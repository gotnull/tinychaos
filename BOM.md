# Bill of Materials

All parts sourced from **element14 Australia** ([au.element14.com](https://au.element14.com/)).

Currency: AUD. Manufacturer part numbers (MPN) are the canonical reference. The element14 order code (7-digit) for each line is resolved by element14's BOM upload tool when you submit [hardware/element14-bom.csv](hardware/element14-bom.csv).

The STM32 NUCLEO-H753ZI is **not** in this BOM. You already have one.

## Quickest way to add everything to your cart

Element14 has a BOM upload tool that takes a CSV of part numbers and quantities and resolves them against its catalogue, then lets you push the whole list into the cart.

1. Open [au.element14.com/bom-tool](https://au.element14.com/bom-tool).
2. Click "Select a file" and choose [hardware/element14-bom.csv](hardware/element14-bom.csv) from this repo.
3. Map the columns when prompted: column A is Quantity, column B is Manufacturer Part Number.
4. Review the matched parts. Some manufacturer part numbers may need adjusting; the tool flags any that did not match.
5. Click "Add all to cart" once the matches look right.

If you would rather check each part by hand first, use the table below. Every row has a search-URL link straight to that part in the element14 catalogue.

## Summary of categories

| Section                 | Items |
|-------------------------|-------|
| Entropy source (zeners) | 4 distinct, quantity 14 total |
| Resistors and trimpots  | 11 line items |
| Capacitors              | 12 line items |
| Op-amps and sockets     | 6 line items |
| Diodes (protection)     | 2 line items |
| Ferrites                | 1 line item |
| Breadboard and proto    | 6 line items |
| Total estimated cost    | ~$120 to $160 AUD (depending on assortment kits chosen) |

## 1. Core entropy source

The 1N47xx family of 1 W axial DO-41 zeners spans the Zener-to-avalanche transition. Buying all four lets you compare noise across the regime.

| MPN     | Vz    | Qty | Role                                      | Search link |
|---------|-------|-----|-------------------------------------------|-------------|
| 1N4732A | 4.7 V | 3   | Pure Zener regime, quiet baseline         | [search](https://au.element14.com/search?st=1N4732A) |
| 1N4733A | 5.1 V | 3   | Transition, useful intermediate           | [search](https://au.element14.com/search?st=1N4733A) |
| 1N4734A | 5.6 V | 3   | Late transition                           | [search](https://au.element14.com/search?st=1N4734A) |
| 1N4735A | 6.2 V | 5   | Primary avalanche source (must-buy)       | [search](https://au.element14.com/search?st=1N4735A) |

If 1N47xxA series is out of stock, the bare 1N47xx (no A suffix) and equivalent BZX55C-x.xV (Vishay) are direct replacements.

## 2. Resistors

### 2a. Precision metal-film 1% 0.5 W (signal path)

| MPN                | Value     | Qty | Role                                  | Search link |
|--------------------|-----------|-----|---------------------------------------|-------------|
| MRS25000C1000F100  | 100 ohm   | 50  | ADC series-protection candidate       | [search](https://au.element14.com/search?st=MRS25+100R) |
| MRS25000C2200F100  | 220 ohm   | 50  | Stage 1 input series                  | [search](https://au.element14.com/search?st=MRS25+220R) |
| MRS25000C4700F100  | 470 ohm   | 50  | Stage 1 R_G (gain set, 47k/470 = 100) | [search](https://au.element14.com/search?st=MRS25+470R) |
| MRS25000C1000FCT00 | 1 kohm    | 50  | ADC series-protection (primary)       | [search](https://au.element14.com/search?st=MRS25+1K) |
| MRS25000C4701FCT00 | 4.7 kohm  | 50  | Op-amp feedback / bias                | [search](https://au.element14.com/search?st=MRS25+4K7) |
| MRS25000C1001FCT00 | 10 kohm   | 50  | Mid-rail dividers, feedback           | [search](https://au.element14.com/search?st=MRS25+10K) |
| MRS25000C4702FCT00 | 47 kohm   | 50  | Stage 1 R_F (gain ~100)               | [search](https://au.element14.com/search?st=MRS25+47K) |
| MRS25000C1002FCT00 | 100 kohm  | 50  | Stage 1 high-Z, mid-rail bias         | [search](https://au.element14.com/search?st=MRS25+100K) |
| MRS25000C1003FCT00 | 1 Mohm    | 50  | Input pull, very-high-Z filter        | [search](https://au.element14.com/search?st=MRS25+1M) |

The MRS25 is Vishay's standard 1 % metal film 0.6 W axial (sometimes labelled 0.5 W in datasheets, the package is the same). 50-piece reels at element14 are typically $3 to $5 each.

### 2b. Multiturn cermet trimpots (Bourns 3296W series, top-adjust)

| MPN            | Value     | Qty | Role                                | Search link |
|----------------|-----------|-----|-------------------------------------|-------------|
| 3296W-1-102LF  | 1 kohm    | 1   | Fine low-Z trim                     | [search](https://au.element14.com/search?st=3296W-1-102LF) |
| 3296W-1-103LF  | 10 kohm   | 2   | Mid-rail / gain trim                | [search](https://au.element14.com/search?st=3296W-1-103LF) |
| 3296W-1-104LF  | 100 kohm  | 2   | Primary zener bias-current trim     | [search](https://au.element14.com/search?st=3296W-1-104LF) |

### 2c. Resistor assortment kit (optional convenience)

For non-critical positions (timing, dividers far from the signal path) it can be quicker to grab a kit:

| MPN          | Description                                | Qty | Search link |
|--------------|--------------------------------------------|-----|-------------|
| RKE5KIT      | Vellement E12 resistor assortment kit      | 1   | [search](https://au.element14.com/search?st=resistor+kit) |

If the Vellement kit is not in stock, search for "resistor assortment kit" and pick any 1 % 0.25 W axial kit covering the E12 or E24 range.

## 3. Capacitors

### 3a. Decoupling (ceramic 100 nF MLCC, X7R)

| MPN              | Value          | Qty  | Role                              | Search link |
|------------------|----------------|------|-----------------------------------|-------------|
| K104K15X7RF53H5  | 100 nF 50 V    | 100  | Decoupling at every IC Vcc pin    | [search](https://au.element14.com/search?st=100nF+X7R+50V+radial) |
| K105K15X7RF5UH5  | 1 uF 50 V      | 20   | Non-polarised mid-value bulk      | [search](https://au.element14.com/search?st=1uF+X7R+50V+radial) |

The MPNs above are Vishay K-series radial X7R. Element14 stocks equivalent Murata, Kemet, AVX. Any X7R is fine.

### 3b. Film (MKT polyester, 100 V, WIMA MKS series)

For the analogue signal path. Use these in any cap that carries the noise signal directly.

| MPN                | Value     | Qty | Role                          | Search link |
|--------------------|-----------|-----|-------------------------------|-------------|
| MKS21NF10100       | 1 nF      | 10  | High-pass corner caps         | [search](https://au.element14.com/search?st=WIMA+MKS2+1nF+100V) |
| MKS210NF100100     | 10 nF     | 10  | Filter caps, 1 to 10 kHz band | [search](https://au.element14.com/search?st=WIMA+MKS2+10nF+100V) |
| MKS247NF100100     | 47 nF     | 10  | Mid-frequency filter          | [search](https://au.element14.com/search?st=WIMA+MKS2+47nF+100V) |
| MKS2100NF100100    | 100 nF    | 10  | Film decoupling, mid-band     | [search](https://au.element14.com/search?st=WIMA+MKS2+100nF+100V) |
| MKS2220NF100100    | 220 nF    | 10  | Mid-corner coupling option    | [search](https://au.element14.com/search?st=WIMA+MKS2+220nF+100V) |
| MKS41UF100100      | 1 uF      | 5   | Primary AC coupling cap       | [search](https://au.element14.com/search?st=WIMA+MKS4+1uF+100V) |

WIMA naming is value-then-voltage with no separator. If the exact MPN does not resolve, search by value and voltage and any WIMA MKS/MKP family is acceptable.

### 3c. Electrolytic (Panasonic FC low-ESR, radial)

| MPN          | Value         | Qty | Role                                 | Search link |
|--------------|---------------|-----|--------------------------------------|-------------|
| EEU-FC1H100  | 10 uF 50 V    | 10  | Op-amp Vcc local bulk                | [search](https://au.element14.com/search?st=EEU-FC1H100) |
| EEU-FC1V101  | 100 uF 35 V   | 10  | Local plus bias-supply bulk          | [search](https://au.element14.com/search?st=EEU-FC1V101) |
| EEU-FC1J471  | 470 uF 63 V   | 5   | Main rail bulk smoothing             | [search](https://au.element14.com/search?st=EEU-FC1J471) |

## 4. Op-amps and sockets

### 4a. Op-amps (DIP-8 through-hole, low-noise selection)

| MPN       | Noise (typ)         | Qty | Role                                    | Search link |
|-----------|---------------------|-----|-----------------------------------------|-------------|
| NE5534P   | ~3.5 nV/sqrt(Hz)    | 2   | Stage 1 low-noise gain                  | [search](https://au.element14.com/search?st=NE5534P) |
| LM833N    | ~4.5 nV/sqrt(Hz)    | 3   | Stage 2 dual low-noise (and spare)      | [search](https://au.element14.com/search?st=LM833N) |
| TL072CP   | ~18 nV/sqrt(Hz)     | 2   | JFET-input dual (high-Z buffer option)  | [search](https://au.element14.com/search?st=TL072CP) |
| LM358P    | ~40 nV/sqrt(Hz)     | 2   | General-purpose dual (fallback)         | [search](https://au.element14.com/search?st=LM358P) |

Notes on availability: TI is the canonical maker for all four. ON Semi has marked some NE5534 variants as "No Longer Manufactured". If NE5534P from TI is unavailable, equivalent stocked options on element14 include OPA1611 (single, lower noise, +$$) or LM4562 (dual, very low noise, +$). The BOM upload tool will surface alternatives automatically.

### 4b. DIP-8 sockets

| MPN          | Type                                     | Qty | Role                              | Search link |
|--------------|------------------------------------------|-----|-----------------------------------|-------------|
| 1-2199298-8  | TE Connectivity 8-way dual-wipe socket   | 6   | Standard DIP socket each op-amp   | [search](https://au.element14.com/search?st=DIP-8+socket) |
| 540-AG10D    | Mill-Max gold machined-pin 8-way         | 2   | Premium for noise-critical stage  | [search](https://au.element14.com/search?st=Mill-Max+DIP-8+gold) |

If the exact MPN does not match, any 8-pin 2.54 mm DIP socket is fine for general use. The gold machined-pin is a luxury for the first stage only.

## 5. Protection diodes

| MPN     | Type                              | Qty | Role                                  | Search link |
|---------|-----------------------------------|-----|---------------------------------------|-------------|
| BAT46   | Schottky DO-35, Vf ~ 0.3 V         | 4   | ADC clamps (two per channel)          | [search](https://au.element14.com/search?st=BAT46) |
| 1N4148  | Standard signal diode DO-35       | 20  | General-purpose clamping and logic    | [search](https://au.element14.com/search?st=1N4148) |

Two BAT46 per ADC input: cathode to +3.3 V rail clamp, anode to GND clamp. See [docs/adc-protection.md](docs/adc-protection.md) for the network.

## 6. Ferrites and EMI

| MPN              | Description                              | Qty | Role                          | Search link |
|------------------|------------------------------------------|-----|-------------------------------|-------------|
| BLM18AG601SN1D   | Murata 600 ohm ferrite bead, leaded      | 10  | Series with op-amp Vcc        | [search](https://au.element14.com/search?st=ferrite+bead+leaded+600+ohm) |

Through-hole ferrite beads are harder to find than SMD on element14. If only SMD is in stock, alternatives include axial-leaded Wurth WE-CBF (74279xxx series) or Fair-Rite leaded.

## 7. Breadboard and prototyping

| MPN              | Description                                    | Qty | Search link |
|------------------|------------------------------------------------|-----|-------------|
| TW-E40-1020      | Twin Industries 830-tie-point breadboard       | 2   | [search](https://au.element14.com/search?st=830+tie+point+breadboard) |
| 1568-1130-ND     | Adafruit M-M / M-F / F-F jumper kit (or equiv) | 1   | [search](https://au.element14.com/search?st=jumper+wire+kit) |
| WJW-022-12       | 22 AWG solid-core hook-up wire 6-colour kit    | 1   | [search](https://au.element14.com/search?st=hook+up+wire+22AWG+kit) |
| 3779             | Vero stripboard 100x160 mm                     | 1   | [search](https://au.element14.com/search?st=Vero+stripboard) |
| 61300411121      | Wurth 40-pin male header 2.54 mm pitch         | 2   | [search](https://au.element14.com/search?st=Wurth+40+pin+header+male) |
| 61300411821      | Wurth 40-pin female socket header 2.54 mm      | 1   | [search](https://au.element14.com/search?st=Wurth+40+pin+header+female) |

Alligator clip jumper leads and screw terminals are not strictly required. If you want them, element14 stocks Pomona / Mueller alligator jumper sets and Phoenix Contact MKDS screw terminals.

## 8. Measurement and debug

The bench instruments below are useful but not core. The C# host app does most of the characterisation in software.

| Tool                  | Status                                | Source notes                                  |
|-----------------------|---------------------------------------|-----------------------------------------------|
| Bench DMM             | You probably already own one          | Any Fluke or Brymen or Uni-T 4000-count model |
| USB logic analyser    | Useful for STM32 UART debug           | Cheap "Saleae clone" from AliExpress, $15     |
| Oscilloscope          | Useful but not required               | Buy separately or rely on the C# app          |
| Function generator    | Useful for verifying op-amp bandwidth | AD9833 DDS module from element14 or AliExpress, $10 to $30 |
| USB-serial adapter    | Not needed                            | NUCLEO ST-LINK exposes a virtual COM port     |

## What deliberately did not make it in

- NE5534 non-A grade: the A grade is the same package and is only marginally more expensive. Use the A grade if available; the BOM falls back to the base part if not.
- LM741: too noisy and too slow. Included historically in many noise-source designs, but the NE5534 is strictly better.
- Quad op-amp packages (TL074, TL084, LM324): worse routing on a breadboard and more crosstalk than dual or single packages. Stick to DIP-8 dual or single.
- 5 W zeners: overkill power dissipation; the 1 W parts are quieter at the same operating current and we never get near 1 W of dissipation.
- Carbon film resistors in the signal path: higher excess (1/f) noise than metal film. Used only in non-critical positions if you grab a kit.

## How to verify substitutions

If element14 flags a part as unavailable when you upload the CSV:

1. Click the "Show similar" or "Cross-reference" link in the BOM tool output.
2. Confirm the alternative is the same package (DIP-8 for ICs, DO-35 or DO-41 for diodes, axial leaded for resistors and caps).
3. For op-amps, confirm noise spec is comparable or better (in nV/sqrt(Hz) input-referred).
4. For caps in the signal path (the WIMA MKS parts), only accept a metalised-polyester (MKT), polypropylene (MKP/FKP), or PPS film. Reject anything labelled ceramic, electrolytic, or tantalum.
5. For zeners, the same 1N47xx number from a different manufacturer is always acceptable.

The hardware design doc gives the rationale per component in case you need to make a substitution under stock pressure: see [docs/hardware-design.md](docs/hardware-design.md).
