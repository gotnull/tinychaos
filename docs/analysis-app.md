# C# Analysis Application Notes

The host-side application reads the STM32 sample stream over USB CDC, decodes it, and turns raw ADC counts into useful diagnostics: histogram, FFT, statistics, entropy estimates, whitened random bytes.

This document is the plan. The actual code will live in `analysis/` once written.

## Goals

1. Open the ST-LINK virtual COM port (typically COM3 to COM10 on Windows, /dev/cu.usbmodemNNNN on macOS, /dev/ttyACMx on Linux) and read the binary frame stream defined in [firmware-notes.md](firmware-notes.md).
2. Reconstruct interleaved zener-channel and baseline-channel samples.
3. Show live histogram and live FFT.
4. Compute running statistics: mean, std, kurtosis, autocorrelation.
5. Estimate entropy (Shannon, min-entropy, NIST SP 800-90B style).
6. Optionally apply whitening (von Neumann debias) and produce a stream of random bytes that can be saved to disk or piped to /dev/random.

## Stack and dependencies

| Concern           | Choice                                                        |
|-------------------|---------------------------------------------------------------|
| Runtime           | .NET 8 (or .NET 9 if you keep current)                        |
| UI                | Avalonia (cross-platform) or WPF (Windows only). Avalonia preferred so this runs on macOS and Linux too |
| Serial            | System.IO.Ports.SerialPort, or SerialPortStream for higher robustness |
| FFT               | MathNet.Numerics                                              |
| Charting          | ScottPlot for fast streaming charts                           |
| Statistics        | MathNet.Numerics.Statistics                                   |

All available on NuGet.

## Project layout

```
analysis/
  TinyChaos.sln
  TinyChaos.Core/                  (no UI, pure logic, unit tested)
    FrameReader.cs                 (deframer, CRC, seq tracking)
    SampleBuffer.cs                (ring buffer, channel demux)
    Statistics.cs                  (mean, std, kurt, autocorr)
    Spectrum.cs                    (FFT, PSD, peak detect)
    Entropy.cs                     (Shannon, min-entropy, compression)
    Whitener.cs                    (von Neumann, SHA-256 squeeze)
  TinyChaos.Ui/                    (Avalonia app)
    MainWindow.axaml + .axaml.cs
    HistogramView.axaml
    SpectrumView.axaml
    StatsPanel.axaml
  TinyChaos.Tests/                 (xUnit)
```

## Live data flow

```
ST-LINK virtual COM  --binary frames-->  FrameReader
                                              |
                                              v
                                       SampleBuffer
                                       (separates zener and baseline channels)
                                              |
              +-------------+------------------+------------------+
              v             v                  v                  v
       Statistics     Histogram        Spectrum (FFT,         Whitener
       (running)      (live update)    rolling window)        (optional)
                                                                  |
                                                                  v
                                                          bytes to disk
                                                          or to /dev/random
```

The frame reader runs on a background thread feeding a thread-safe ring buffer. The UI samples the ring buffer at 30 to 60 Hz to update charts.

## Statistics to display

Standard noise-source diagnostics, all live and per-channel:

- Mean (the DC bias point of the signal). For the zener channel after the amplifier, expect about half of VCC, i.e. 1.65 V or about 2048 in 12-bit ADC counts.
- Standard deviation (RMS noise as ADC counts and as input-referred voltage).
- Kurtosis (excess). Pure Gaussian noise has excess kurtosis 0. Avalanche noise is approximately Gaussian, with possible deviation toward heavy tails if you bias the zener into a high-current regime.
- Skewness. Should be near zero.
- Autocorrelation at lag 1 to 100. Should fall off rapidly. A persistent non-zero autocorrelation means either insufficient bandwidth (filter the post-amp signal too narrowly), or coupled hum.
- Visible peaks in the FFT at 50 Hz, 100 Hz, 150 Hz: mains hum coupling. Document and improve grounding.

## Entropy estimation

Three estimates, all computed continuously:

1. Shannon entropy: per-byte Shannon entropy of the 8 LSBs of each sample. Pure noise should give close to 8 bits per byte. The avalanche signal after amplification should give 7 to 8 depending on amplifier gain settings.

2. Min-entropy: estimated from the most common byte value in a recent window. Lower than Shannon entropy; the practical lower bound for cryptographic use.

3. Compression-based: pipe a window of bytes through a fast LZ4 or zlib compressor and measure compression ratio. Incompressible output approximates the min-entropy.

For a hardware RNG that will be used cryptographically, follow NIST SP 800-90B test methodology. The C# app should expose the raw byte stream so external tools (rngtest, ent, dieharder) can be run against it.

## Whitening

The raw ADC samples are biased and correlated. To produce a uniform random byte stream:

Option A. von Neumann debiasing of the LSB stream. Take pairs of consecutive LSB bits. 00 and 11 are discarded. 01 emits 0. 10 emits 1. This removes any constant bias.

Option B. cryptographic squeeze. Hash N samples (e.g., 256 samples) with SHA-256 and output the 32 result bytes. The output is uniform if the input has at least 256 bits of entropy, which is easily satisfied if the ADC samples have even 1 bit of entropy each.

Option C. multi-tap XOR of several independent zener channels. Not in scope until we have a second physical channel.

Default the app to option B with a configurable sample count per output byte.

## Output format

The app can write:

- Raw 12-bit samples as 16-bit little-endian binary, with frame headers. Use this for offline analysis.
- 8-bit samples (the lower 8 bits of the 12-bit ADC value, where the noise lives) as raw bytes.
- Whitened random bytes (post-whitener). Write to a file or stream to stdout for piping.
- Statistics log, JSON lines, one entry per second.

## Manual test plan

1. Open the COM port, confirm framing locks on the 0xA5 0x5A magic.
2. Pull the zener input wire so the input floats. Confirm baseline channel is unchanged and zener channel falls to "open-input" levels.
3. Reconnect. Confirm zener channel shows RMS noise above the baseline channel by at least 10 ADC counts.
4. Run FFT for 30 seconds. Confirm the spectrum is roughly flat from the highpass corner to the lowpass corner, with no large peaks except possibly at mains harmonics.
5. Tune zener bias trimpot from minimum to maximum current. Confirm RMS noise rises with current up to a point, then plateaus.
6. Run rngtest against the whitened output. Should pass.

## Streaming targets

Once basic capture works, two stretch goals:

1. Stream whitened bytes to /dev/urandom (Linux) or to a named pipe on macOS, providing a hardware entropy contribution to the OS.
2. Implement an EGD (Entropy Gathering Daemon) protocol server on a TCP port so any consumer of EGD can pull from this device.

Neither is required for the project to be useful. They are nice extensions for later.
