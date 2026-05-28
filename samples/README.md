# Sample captures

Small synthetic `.bin` files in the on-wire format (see [../docs/ENTROPY_CAPTURE_PIPELINE.md](../docs/ENTROPY_CAPTURE_PIPELINE.md) section 8). They exist so the GUI, the Python CLI, and the C# CLI can be exercised end to end without any STM32 attached.

Three ways to add more `.bin` files to this folder:

- **Avalonia GUI Record button**: connect to a live capture, click **Record** in the connection toolbar. The GUI writes a timestamped file here (e.g., `zener-20260528-153012.bin`) until you click **Stop** or disconnect. The new file shows up in the Samples tab immediately.
- **Python CLI**: `python -m tinychaos.cli --port <PORT> --raw-bin samples/my-capture.bin`
- **C# CLI**: not yet wired (no `--raw-bin` equivalent), but the GUI's Record button uses the same C# library so the result is identical on the wire.

Any `.bin` you drop into this folder will appear automatically in the GUI's Samples tab.

## What is in each file

| File                  | Channel 0 (zener)                                    | Channel 1 (baseline) | Use it to see                                       |
|-----------------------|------------------------------------------------------|----------------------|-----------------------------------------------------|
| `zener_synthetic.bin` | Gaussian noise, mean ~2048, std ~400, broadband      | tight noise around 2048 (std ~4) | What clean avalanche noise looks like: bell histogram, scrolling broadband trace |
| `sine_1khz.bin`       | 1 kHz sine, amplitude ~800 around mid-rail           | constant 2048        | Sanity check: a known-frequency signal should produce a clean sine on the waveform and a bimodal histogram |
| `floating_50hz.bin`   | Big 50 Hz sine (1200 amplitude) + small Gaussian     | constant 2048        | What floating-input mains pickup looks like in the host tools |

All files were produced by the Python encoder at 10 kHz two-channel sample rate, 256 samples per packet, with the same byte-for-byte protocol as the firmware.

## Regenerating

The generator lives in the [tools/](../tools/) Python venv. From the repo root:

```
cd tools
source .venv/bin/activate
python - <<'PY'
import math, random
from tinychaos.protocol import encode_packet
random.seed(42)

with open('../samples/zener_synthetic.bin', 'wb') as f:
    for seq in range(200):
        s = []
        for n in range(256):
            ch = n % 2
            v = (int(max(0, min(4095, 2048 + random.gauss(0, 400)))) if ch == 0
                 else int(max(0, min(4095, 2048 + random.gauss(0, 4)))))
            s.append(v)
        f.write(encode_packet(seq=seq, time_us=seq*25600, samples=s))

# sine_1khz.bin and floating_50hz.bin follow the same pattern.
PY
```

## Why this folder is git-tracked despite the `*.bin` rule

The top-level `.gitignore` has a broad `*.bin` rule (originally for STM32 firmware build outputs). Without an explicit exception every sample under `samples/` would be silently ignored. The relevant lines at the bottom of `.gitignore` are:

```
!samples/
!samples/*.bin
```

`git check-ignore -v samples/<file>` should report `!samples/*.bin` as the matched rule.
