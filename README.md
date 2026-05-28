# tinychaos

A hardware random number generator using zener-diode avalanche breakdown noise as the entropy source, captured by an STM32 NUCLEO-H753ZI ADC and analysed by a C# host application.

The goal is not just another HWRNG. It is a measurable, instrumented one. Every stage is broken out so you can probe it on a breadboard, swap zeners across the zener-to-avalanche transition, and watch the noise statistics change in real time.

## Status

| Stage              | State                                                  |
|--------------------|--------------------------------------------------------|
| Bill of materials  | Sourced from element14 Australia (see [BOM.md](BOM.md))|
| element14 cart     | In progress, populated as parts are confirmed in stock |
| Hardware design    | See [docs/hardware-design.md](docs/hardware-design.md) |
| Firmware (STM32)   | Specification only, see [docs/firmware-notes.md](docs/firmware-notes.md) |
| Analysis app (C#)  | Specification only, see [docs/analysis-app.md](docs/analysis-app.md) |

The NUCLEO-H753ZI is excluded from the BOM. You already have one.

## Why zener avalanche noise

A reverse-biased zener diode in avalanche breakdown (typically Vz at or above about 5.5 V) produces wideband, near-white noise from the stochastic impact-ionisation cascade in the depletion region. Below about 5.5 V the same package operates by the Zener (tunneling) mechanism, which is far quieter. The 1N47xx family spans this transition exactly, which makes them ideal teaching parts:

| 1N47xx  | Vz    | Mechanism                | Expected noise        |
|---------|-------|--------------------------|-----------------------|
| 1N4732  | 4.7 V | Pure Zener (tunneling)   | Low                   |
| 1N4733  | 5.1 V | Mixed (transition)       | Medium                |
| 1N4734  | 5.6 V | Mixed (transition)       | Medium-high           |
| 1N4735  | 6.2 V | Pure avalanche           | High (primary source) |

The BOM includes all four so you can characterise the transition directly with the same circuit.

## Signal chain (block view)

```
   +12V to +18V clean bias supply
              |
              v
   [ bias trimpot, 100k multiturn ]
              |
              v
   [ zener 1N4735, reverse biased into avalanche ]
              |
              v  (noise tap, DC ~6.2V with mV of AC noise on top)
              |
   [ AC coupling cap, 1uF MKT film ]
              |
              v
   [ stage 1 amplifier, NE5534, gain ~100 ]
              |
              v
   [ stage 2 amplifier, LM833, gain ~10 ]
              |
              v  (signal now ~1Vrms, biased at mid-rail 1.65V)
              |
   [ ADC protection: 1k series, BAT46 Schottky clamps, 100nF ]
              |
              v
   [ STM32 NUCLEO-H753ZI, ADC1 12-bit, DMA, UART ]
              |
              v  (USB CDC virtual COM port via ST-LINK, 921600 baud)
              |
   [ host PC, C# analysis app: histogram, FFT, whitening ]
```

Each block is documented separately:

- Entropy source and bias: [docs/hardware-design.md](docs/hardware-design.md)
- Filtering, decoupling, power: [docs/filtering-and-power.md](docs/filtering-and-power.md)
- ADC input protection: [docs/adc-protection.md](docs/adc-protection.md)
- STM32 firmware plan: [docs/firmware-notes.md](docs/firmware-notes.md)
- C# analysis app plan: [docs/analysis-app.md](docs/analysis-app.md)
- Architecture overview: [docs/architecture.md](docs/architecture.md)

## Bill of materials

See [BOM.md](BOM.md) for the complete sourced parts list with element14 product links, prices, quantities, rationale, and must-buy vs nice-to-have classification.

## Repo layout

```
tinychaos/
  README.md                  (you are here)
  BOM.md                     (complete bill of materials)
  docs/                      (design docs, theory and decisions)
    architecture.md
    hardware-design.md
    filtering-and-power.md
    adc-protection.md
    firmware-notes.md
    analysis-app.md
  hardware/                  (schematic or KiCad project, future)
  firmware/                  (STM32 firmware, future)
  analysis/                  (C# analysis app, future)
```

## Safety notes

- STM32 ADC inputs must not exceed 3.3 V. See [docs/adc-protection.md](docs/adc-protection.md) for the required series resistor and Schottky clamp network.
- Zener bias supply may be 12 to 18 V. Keep this section isolated from the 3.3 V logic side and only connect through the AC coupling cap and protected ADC front-end.
- Avalanche noise sources are sensitive to mains hum and switching-supply ripple. See [docs/filtering-and-power.md](docs/filtering-and-power.md) for the recommended battery-isolated test setup.
