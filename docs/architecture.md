# Architecture

This document gives the short mental model and points at the deeper docs for each stage. Read this first.

## End-to-end pipeline

```
+12V to +18V clean bias supply (battery or DC-DC boost)
    |
    v
zener noise source (1N4735, reverse biased into avalanche)
    |
    v   noise voltage rides on DC breakdown voltage
    |
AC coupling cap (1uF MKT, removes DC, passes noise)
    |
    v
stage 1 amplifier (NE5534 single low-noise op-amp, gain ~100)
    |
    v
stage 2 amplifier (LM833 dual low-noise op-amp, gain ~10, with bandpass)
    |
    v   signal now ~1Vrms, biased at mid-rail (~1.65V)
    |
ADC protection (1k series + BAT46 Schottky to 3.3V and GND + 100nF)
    |
    v
STM32 NUCLEO-H753ZI ADC1 (12-bit, DMA into circular buffer)
    |
    v
UART transmit over ST-LINK virtual COM port (USB CDC, 921600 baud)
    |
    v
host PC, C# analysis app (histogram, FFT, whitening, entropy estimate)
```

## The five claims that justify the design

1. The entropy is physical, not algorithmic. Avalanche breakdown is a quantum-mechanical impact-ionisation process. There is no PRNG state to compromise. See [hardware-design.md](hardware-design.md).

2. The signal is small. Zener avalanche noise is typically 10 uV to 1 mV RMS depending on bias current and Vz. The amplifier chain must add minimal noise of its own. The NE5534 first stage at about 3.5 nV/sqrt(Hz) dominates and sets the input-referred noise floor. See [hardware-design.md](hardware-design.md).

3. The bandwidth must be band-limited to avoid aliasing into the ADC samples. Anti-aliasing low-pass before the ADC plus matched ADC sample rate. See [filtering-and-power.md](filtering-and-power.md).

4. The ADC input must survive. STM32 absolute-max is 3.3 V plus about 0.3 V. Series resistance plus Schottky clamps to 3.3 V and GND plus a small cap to GND form a safe input network even under op-amp saturation. See [adc-protection.md](adc-protection.md).

5. The raw samples are biased (mean not zero, possibly Gaussian). Extracting uniform random bits requires whitening: von Neumann debiasing or cryptographic hashing of the LSBs. Done in software, after capture. See [analysis-app.md](analysis-app.md).

## What is on the breadboard

Two physically separated zones:

```
zone A: zener noise and analogue amplifier
    bias supply input (+12V to +18V)
    100k multiturn bias trimpot
    1N4735 zener (or 4.7V/5.1V/5.6V for comparison)
    AC coupling cap (1uF MKT)
    NE5534 first stage in 8-pin DIP socket
    LM833 second stage in 8-pin DIP socket
    100nF + 10uF + ferrite at each op-amp Vcc pin

zone B: 3.3V digital
    STM32 NUCLEO-H753ZI
    USB cable to host PC
    3.3V rail (from the NUCLEO or from the breadboard PSU)

bridge between zones (one wire and one ground)
    signal: through the ADC protection network into PA0 (ADC1_IN0)
    ground: single point at the NUCLEO AGND pin
```

A single ground reference between the analogue and digital zones lives at the STM32 analogue ground pin (or the breadboard rail directly adjacent to it). Multiple ground paths create loops that pick up mains hum at exactly the worst place: right before the ADC.

## Where each major part lives

This table maps each major BOM line to where it appears in the signal chain. Use it as a wiring index.

| Component                       | Position                                                |
|---------------------------------|---------------------------------------------------------|
| 6.2 V zener 1N4735              | Noise source, reverse biased into avalanche             |
| 100 kohm 25-turn cermet trimpot | Series with zener cathode, sets reverse current         |
| 1 uF MKT film                   | AC coupling cap, zener cathode to stage-1 input         |
| 100 kohm metal film, two off    | Stage-1 input bias to mid-rail (3.3 / 2 = 1.65 V)       |
| NE5534                          | Stage-1 op-amp, non-inverting gain 100                  |
| 47 kohm + 470 ohm               | Stage-1 feedback divider (G = 1 + 47k / 470 ~= 100)     |
| 100 nF + 10 uF + ferrite        | Stage-1 Vcc decoupling, immediately next to Vcc pin     |
| LM833                           | Stage-2 dual op-amp, gain 10 with bandpass shaping      |
| 10 nF and 1 uF MKT              | Stage-2 highpass and lowpass corner caps                |
| 1 kohm metal film               | ADC series protection resistor                          |
| Two BAT46 Schottky              | ADC clamps to +3.3 V rail and GND                       |
| 100 nF anti-alias               | ADC pin to GND, forms lowpass with R_series             |

Schematic and component values in [hardware-design.md](hardware-design.md).
