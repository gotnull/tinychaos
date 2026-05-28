# Bill of Materials

Sourced from **element14 Australia** ([au.element14.com](https://au.element14.com/)). All order codes verified individually for small order quantities (Min 1, 5, or 10 per line, not 5000).

Currency: AUD. Approximate total at small-MOQ pricing: **~$160 ex GST** (versus the $1085 a first-pass MPN-only upload produced because of cut-tape reel minimums on the most common 1N47xx Multicomp Pro SKUs).

The STM32 NUCLEO-H753ZI is not included. You already have one.

## Quickest path: BOM Upload

Element14 ships a BOM upload tool that takes a CSV with order codes and resolves them to small-MOQ line items in one shot.

1. Open [au.element14.com/bom-tool](https://au.element14.com/bom-tool).
2. Click "Select a file" and upload [hardware/element14-bom.csv](hardware/element14-bom.csv).
3. Map column A as Quantity, column B as Customer Reference (the element14 order code), column C as Manufacturer Part Number (used for verification only).
4. Review and click "Add all to basket". Every line resolves to a Min-1/Min-5/Min-10 SKU; nothing forces a 5000-piece minimum.

If you would rather check each part by hand first, the table below has direct order-code links.

## Master table

### 1. Zener diodes (entropy source)

Nexperia BZV85 series. Through-hole DO-41, 1W (5.6V variant is 1.3W). All Min 5 / Mult 5.

| Order Code  | MPN              | Vz    | Price each | Qty | Role                                          |
|-------------|------------------|-------|------------|-----|-----------------------------------------------|
| [3439775](https://au.element14.com/nexperia/bzv85-c4v7-113/dp/3439775)   | BZV85-C4V7,113   | 4.7 V | $0.294     | 5   | Pure Zener regime, quiet baseline             |
| [109726301](https://au.element14.com/search?st=109726301) | BZV85-C5V1,113   | 5.1 V | $0.722     | 5   | Transition, useful intermediate               |
| [109726401](https://au.element14.com/search?st=109726401) | BZV85-C5V6,113   | 5.6 V | $0.418     | 5   | Late transition                               |
| [2777501](https://au.element14.com/nexperia/bzv85-c6v2-113/dp/2777501)   | BZV85-C6V2,113   | 6.2 V | $0.600     | 5   | **Primary avalanche source**                  |

BZV85 is functionally equivalent to 1N47xxA (same package, same nominal voltages, ~5% tolerance). The reason we are not using the 1N47xxA Multicomp Pro SKUs is that three of the four variants (4.7 V, 5.1 V, 5.6 V) are reel-only at element14: Min 5000, Mult 5000, ~$245 per voltage. The Nexperia BZV85 line covers the same voltages at sensible quantities.

### 2. Op-amps (DIP-8 through-hole)

| Order Code  | MPN     | Brand            | Min | Each   | Qty | Role                                  |
|-------------|---------|------------------|-----|--------|-----|---------------------------------------|
| [3117314](https://au.element14.com/texas-instruments/ne5532p/dp/3117314) | NE5532P | Texas Instruments | 1   | $1.060 | 2   | Dual low-noise (replaces NLM LM833N). Equivalent specs. |
| [3117788](https://au.element14.com/texas-instruments/tl072cp/dp/3117788) | TL072CP | Texas Instruments | 1   | $1.580 | 2   | JFET dual, high-Z option              |
| [3117074](https://au.element14.com/texas-instruments/lm358p/dp/3117074)  | LM358P  | Texas Instruments | 5   | $0.408 | 5   | General-purpose dual fallback         |

Note on NE5534P (single ultra-low-noise): the original plan called for NE5534AP for the first stage. Element14's onsemi NE5534ANG (order code 1426386) is marked No Longer Manufactured; the TI single-channel NE5534P SKU varies in stock. **The CSV uses NE5532P (dual) for both stages.** That is the cleanest currently-available option. One NE5532 supplies one stage; spec is ~5 nV/sqrt(Hz) input-referred, two op-amps per package, internally unity-gain compensated. Each gain stage uses one half; the spare half stays unused or becomes a buffer.

### 3. IC socket

| Order Code  | MPN          | Min | Each   | Qty | Role                |
|-------------|--------------|-----|--------|-----|---------------------|
| [2445625](https://au.element14.com/te-connectivity/1-2199298-8/dp/2445625) | 1-2199298-8 | 10  | $1.210 | 10  | TE Connectivity DIP-8 dual-wipe socket |

Six are used by the project (two amplifier stages plus spares); the Min-10 pack covers it.

### 4. Protection diodes

| Order Code  | MPN     | Min | Each   | Qty | Role                                       |
|-------------|---------|-----|--------|-----|--------------------------------------------|
| [9801456](https://au.element14.com/stmicroelectronics/bat46/dp/9801456)  | BAT46  | 5   | $0.204 | 5   | Schottky DO-35, ADC clamp diodes           |
| [2306361](https://au.element14.com/multicomp-pro/1n4148/dp/2306361)      | 1N4148 | 1   | $0.028 | 10  | Multicomp Pro 1N4148 signal diode DO-35    |

Two BAT46 per ADC input (cathode to +3.3V rail, anode to GND). See [docs/adc-protection.md](docs/adc-protection.md).

### 5. Multiturn trimpots (Bourns 3296W, 25-turn, top adjust)

| Order Code  | MPN             | Value     | Each   | Qty | Role                                |
|-------------|-----------------|-----------|--------|-----|-------------------------------------|
| [9353178](https://au.element14.com/bourns/3296w-1-102lf/dp/9353178) | 3296W-1-102LF  | 1 kohm    | $3.150 | 1   | Fine low-Z trim                     |
| [9353186](https://au.element14.com/bourns/3296w-1-103lf/dp/9353186) | 3296W-1-103LF  | 10 kohm   | $3.150 | 2   | Mid-rail or gain trim               |
| [9353194](https://au.element14.com/bourns/3296w-1-104lf/dp/9353194) | 3296W-1-104LF  | 100 kohm  | $3.150 | 2   | **Primary zener bias-current trim** |

### 6. Resistors (Vishay MRS25, 1% metal film, 0.6W, axial)

All Min 10 / Mult 10. Note the Vishay MRS25 part-number value codes use a three-figure-plus-multiplier convention: `1000F` is 100Ω, `1001F` is 1 kΩ, `1003F` is 100 kΩ, etc.

| Order Code  | MPN                  | Value     | Each   | Qty | Use                                       |
|-------------|----------------------|-----------|--------|-----|-------------------------------------------|
| [9463909](https://au.element14.com/vishay/mrs25000c1000fct00/dp/9463909) | MRS25000C1000FCT00 | 100 Ω    | $0.211 | 10  | ADC series protection candidate           |
| [9465170](https://au.element14.com/vishay/mrs25000c1001fct00/dp/9465170) | MRS25000C1001FCT00 | 1 kΩ     | $0.179 | 10  | ADC series protection (primary), stage 2 gain |
| [9468692](https://au.element14.com/vishay/mrs25000c4701fct00/dp/9468692) | MRS25000C4701FCT00 | 4.7 kΩ   | $0.218 | 10  | Feedback / bias                           |
| [9468498](https://au.element14.com/vishay/mrs25000c4702fct00/dp/9468498) | MRS25000C4702FCT00 | 47 kΩ    | $0.191 | 10  | Stage 1 R_F (gain ~100)                   |
| [9463976](https://au.element14.com/vishay/mrs25000c1002fct00/dp/9463976) | MRS25000C1002FCT00 | 10 kΩ    | $0.192 | 10  | Mid-rail dividers, feedback               |
| [9463895](https://au.element14.com/vishay/mrs25000c1003fct00/dp/9463895) | MRS25000C1003FCT00 | 100 kΩ   | $0.218 | 10  | Stage 1 high-Z, mid-rail bias             |

For the stage-1 gain divider (47 kΩ over 470 Ω) you also need 470 Ω. The Vishay MRS25 code is `4700F`; if you want it on the same order, search `MRS25000C4700FCT00` directly in element14 and add it to your cart. Same for 220 Ω (`MRS25000C2200FCT00`) and 1 MΩ (`MRS25000C1004FCT00`). They are not in the CSV but are one click each.

### 7. Capacitors

#### Decoupling (X7R MLCC, radial through-hole)

| Order Code  | MPN              | Value | Each   | Qty | Role                                  |
|-------------|------------------|-------|--------|-----|---------------------------------------|
| [1141777](https://au.element14.com/vishay/k104k15x7rf53h5/dp/1141777) | K104K15X7RF53H5 | 100 nF 50 V | $0.175 | 100 | Decoupling everywhere; lifetime supply |

#### Film (WIMA MKS2, MKT polyester, 100 V DC / 63 V AC)

For the analogue signal path. Use these in any cap that carries the noise signal directly.

| Order Code  | MPN                       | Value     | Min | Each   | Qty | Role                              |
|-------------|---------------------------|-----------|-----|--------|-----|-----------------------------------|
| [1006017](https://au.element14.com/wima/mks2d021001a00kssd/dp/1006017)  | MKS2D021001A00KSSD       | 10 nF     | 10  | $0.587 | 10  | Filter caps, 1 to 10 kHz band     |
| [1006024](https://au.element14.com/wima/mks2d024701a00kssd/dp/1006024)  | MKS2D024701A00KSSD       | 47 nF     | 10  | $0.388 | 10  | Mid-frequency filter              |
| [1006031](https://au.element14.com/wima/mks2d031001a00kssd/dp/1006031)  | MKS2D031001A00KSSD       | 100 nF    | 10  | $0.388 | 10  | Film decoupling, mid-band         |
| [1890146](https://au.element14.com/wima/mks2d032201c00kssd/dp/1890146)  | MKS2D032201C00KSSD       | 220 nF    | 1   | $1.010 | 5   | Mid-corner coupling option        |
| [1890147](https://au.element14.com/wima/mks2d041001k00kssd/dp/1890147)  | MKS2D041001K00KSSD       | 1 µF      | 1   | $1.380 | 5   | **Primary AC coupling cap**       |

The 1 nF film cap that the design doc suggests for the high-pass corner is not stocked at element14 in the WIMA MKS2 100 V series (smallest is 10 nF). A ceramic 1 nF X7R works for filter corners (it does not carry the signal directly); add `1nF X7R MLCC radial through-hole` to your order separately if you want to experiment with higher highpass corners.

#### Electrolytic (Panasonic FC, low-ESR / low-leakage radial)

| Order Code  | MPN          | Value          | Min | Each   | Qty | Role                                  |
|-------------|--------------|----------------|-----|--------|-----|---------------------------------------|
| [1855182](https://au.element14.com/panasonic/eeufc1h100l/dp/1855182)   | EEUFC1H100L  | 10 µF 50 V    | 1   | $0.683 | 10  | Op-amp Vcc local bulk (replaces NLM EEU-FC1H100) |
| [1848449](https://au.element14.com/panasonic/eeufc1v101/dp/1848449)    | EEUFC1V101   | 100 µF 35 V   | 1   | $0.817 | 1   | Local plus bias-supply bulk           |
| [9692550](https://au.element14.com/panasonic/eeufc1j471/dp/9692550)    | EEUFC1J471   | 470 µF 63 V   | 1   | $2.880 | 1   | Main rail bulk smoothing              |

### 8. Prototyping

| Order Code  | MPN            | Description                                        | Each    | Qty |
|-------------|----------------|----------------------------------------------------|---------|-----|
| [2213346](https://au.element14.com/twin-industries/tw-e40-1020/dp/2213346) | TW-E40-1020   | Twin Industries 830-tie-point solderless breadboard | $14.260 | 2   |
| [2503760](https://au.element14.com/kemo-electronic/e003/dp/2503760)       | E003          | Kemo Electronic stripboard FR2 epoxy, 100×160 mm   | $6.310  | 1   |
| [2827888](https://au.element14.com/wurth-elektronik/61300411821/dp/2827888) | 61300411821 | Wurth 40-pin female 2.54 mm socket header (Min 10) | $0.625  | 10  |

## Things to buy elsewhere

Element14 does not stock these in hobbyist-friendly quantities or formats. The list is small and inexpensive; any of Jaycar, Core Electronics, AliExpress, or Sparkfun will have all of them.

| Item                                | Why                                                                      |
|-------------------------------------|--------------------------------------------------------------------------|
| 40-pin male header strip (single)   | Element14's Wurth equivalent is Min 100 strips ($15.50 for 100). You only need two strips, and Core Electronics or Jaycar sell a single 40-pin male break-away strip for under $1. |
| Hookup wire (multi-colour 22 AWG)   | Element14 stocks individual spools but no hobbyist 6-colour kit at a sensible price. Pick up a kit from any hobby electronics shop. |
| Premade jumper wires (M-M, M-F, F-F) | Same: element14 sells in bulk packs only. Get a 100-piece mixed pack from any hobby shop. |
| Alligator-clip test leads (optional) | Same; commodity item. |
| Ferrite bead (through-hole / axial)  | The Murata BLM18AG601SN1D in the original plan is SMD 0603. Through-hole ferrite beads are uncommon. Either substitute a small inductor (10 to 100 uH) on the op-amp Vcc, or buy an axial-leaded ferrite from Jaycar or AliExpress. |

## Summary of what changed since the first CSV

If you uploaded the earlier `hardware/element14-bom.csv` and saw a total over $1000 with several lines at 5000-piece minimums, here is what shifted:

1. **Zener replacements**: 1N4732A/1N4733A/1N4734A (Multicomp Pro, reel-only Min 5000 each) replaced by **Nexperia BZV85-C4V7/5V1/5V6**, all Min 5. The 6.2V was already Min 1 so it carries over conceptually but the new CSV uses the Nexperia variant for consistency.
2. **LM833N replaced** by NE5532P (the LM833NG Onsemi suggestion was No Longer Manufactured).
3. **EEU-FC1H100 replaced** by EEUFC1H100L (the original was No Longer Manufactured).
4. **Stripboard fixed**: the original CSV's bare "3779" matched a $254 hand tool. The new entry uses **Kemo E003 order code 2503760** ($6.31).
5. **40-pin male header dropped** from the CSV (Wurth's variant was Min 100); recommended as a buy-locally item.
6. **Hookup wire and jumper kits dropped** (no good element14 hobbyist option); recommended as buy-locally items.
7. **Resistor value labels corrected**: my earlier labels were off by one decade. The order codes themselves were always right; only the descriptions had been mislabeled.
8. **CSV now uses Order Code as the primary identifier** rather than relying on MPN matching, which removes ambiguity in the BOM tool.
