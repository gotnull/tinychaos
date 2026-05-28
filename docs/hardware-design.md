# Hardware Design

This document is the "what the circuit actually is" reference. It includes values, schematics, and back-of-envelope sizing for every node.

## 1. The zener entropy source

### Operating principle

A reverse-biased zener diode breaks down at its rated voltage Vz. How it breaks down depends on Vz:

- Vz below about 5.5 V: Zener breakdown (quantum tunneling across the depletion region). Sharp and quiet.
- Vz above about 5.5 V: avalanche breakdown (carriers gain enough energy between collisions to ionise lattice atoms, creating an electron-hole avalanche). The avalanche multiplication factor varies randomly cycle-to-cycle, producing shot-noise-like wideband random voltage superimposed on the DC breakdown voltage.

A 6.2 V 1N4735 sits comfortably in the avalanche regime. It is quiet enough to bias reliably with a small reverse current (50 to 200 uA), noisy enough to give millivolts of broadband signal.

### Bias network

```
V_BIAS (+12V to +18V clean DC, see filtering-and-power.md)
    |
    |
   R_bias_fixed  10 kohm 0.5W metal film
    |               (sets minimum series resistance,
    |                limits current if trimpot wipes to 0)
    |
   R_bias_trim   100 kohm multiturn cermet
    |               (fine-tunes the reverse current,
    |                I = (V_BIAS - Vz) / (R_fixed + R_trim))
    |
    +-------> noise tap (DC ~6.2V, mV of AC noise on top)
    |             goes to AC coupling cap, then stage 1 input
    |
   D1            1N4735 zener
    |               cathode is the TOP terminal in this drawing
    |               anode at the BOTTOM going to GND
    |               (REVERSE biased: cathode positive)
    |
   GND (analogue side)
```

### Bias current calculation

Target: about 100 uA reverse current. This is high enough to be safely above the knee of the Vz-Iz curve, low enough that the zener does not self-heat into its temperature coefficient.

```
V_BIAS   = 15 V  (e.g. DC-DC boost set to 15 V, or 2x 9V batteries in series)
Vz       = 6.2 V (1N4735)
V_drop   = V_BIAS - Vz = 8.8 V
I_target = 100 uA
R_total  = V_drop / I_target = 88 kohm

Use R_fixed = 10 kohm + R_trim spanning 0 to 100 kohm.
  At R_trim = 100 kohm: I = 8.8V / 110k =  80 uA
  At R_trim =   0 kohm: I = 8.8V /  10k = 880 uA
```

Set the trimpot for the bias current that gives the best noise vs drift trade-off, typically 50 to 200 uA for the 1N4735.

### What you see at the noise tap

| Quantity   | Typical                                                      |
|------------|--------------------------------------------------------------|
| DC level   | ~6.2 V (equal to Vz)                                         |
| AC noise   | 100 uV to 2 mV RMS, broadband, roughly white from a few Hz to over 100 kHz |
| Bandwidth  | Limited by zener junction capacitance and bias source impedance; effective -3 dB corner around 100 kHz to 1 MHz |

This means we need about 1000x gain to get from "small mV" to a full-scale ADC swing.

## 2. AC coupling

The noise tap sits at DC = 6.2 V. The op-amp following it operates from a 0 to 3.3 V (or split supply) rail and has an input bias point at mid-rail (~1.65 V for single 3.3 V supply). We must AC-couple to strip the DC and pass only the noise.

Choose the coupling cap and the next-stage input impedance to put the highpass corner well below the lowest frequency of interest. For HWRNG work, even very low frequencies are useful noise.

```
f_HP = 1 / (2 * pi * R_in * C_couple)

With R_in = 100 kohm to mid-rail and C_couple = 1 uF MKT:
f_HP = 1 / (2 * pi * 100_000 * 1e-6) ~= 1.6 Hz
```

A corner at about 1.6 Hz means we keep essentially all of the noise spectrum and only block DC and mains-frequency drift. Use the 1 uF MKT film cap (not electrolytic) because the cap carries the actual signal and we want zero dielectric absorption or voltage-coefficient effects.

## 3. Amplifier: two stages

### Stage 1: NE5534, gain about 100

Single low-noise bipolar op-amp at about 3.5 nV/sqrt(Hz) input-referred voltage noise. This is the noise-defining stage. Configure as non-inverting.

```
                 +5V supply
                  |
                  |  decoupling: 100 nF MLCC + 10 uF + ferrite,
                  |  all within 1 cm of the Vcc pin
                  |
              +---+---+
   IN  ---->  | + Vcc |
              |       |---->  OUT
              | -     |
              +-------+
                  |
                  |   feedback
                  +--->  R_F = 47 kohm
                            |
                            +---->  closed-loop output node
                            |
                         R_G = 470 ohm
                            |
                           GND analogue

  Gain = 1 + R_F / R_G = 1 + 47000 / 470 ~= 101
```

Input bias network: two 100 kohm resistors, one from +3.3 V and one to GND, meeting at the + input. This sets the DC bias to mid-rail (~1.65 V). The AC coupling cap from the zener tap drives this node.

```
   +3.3V
     |
   R = 100 kohm
     |
     +---->  + input of NE5534
     |
   R = 100 kohm
     |
    GND analogue

  Mid-rail bias node, Thevenin source impedance = 50 kohm
  Combined with 1 uF coupling cap: f_HP = 1 / (2 * pi * 50k * 1uF) ~= 3.2 Hz
```

NE5534 compensation: the NE5534 is not unity-gain stable. It needs a small compensation cap (typically 22 pF) between pins 5 and 8, or its equivalent in the package, for stability below gain of about 3. At gain 100 we are safely above the unity-gain region but still include the cap for margin against ringing.

### Stage 2: LM833, gain about 10, with lowpass shaping

```
  Total chain gain = 100 * 10 = 1000

  Output swing = 1 mV RMS input * 1000 = 1 V RMS ~= 2.8 V peak-to-peak
  This sits comfortably within the 0 to 3.3 V ADC range when biased at 1.65 V.
```

Use the second half of the LM833 as a spare or as an output buffer if needed.

Add a lowpass corner in this stage (a capacitor across R_F) at about 50 kHz to act as the first anti-aliasing pole. This eases the load on the dedicated ADC RC anti-alias filter that follows.

```
  f_LP = 1 / (2 * pi * R_F * C_F)

  With R_F = 10 kohm and C_F = 330 pF:
  f_LP ~= 48 kHz
```

## 4. The reference / baseline divider

The spec calls for comparing the zener signal against a voltage-divider baseline. Wire a second ADC channel to a clean DC reference:

```
   +3.3V
     |
   R = 100 kohm
     |
     +---->  1 kohm series  +  BAT46 clamps  +  100 nF  --->  STM32 PA3 (ADC1_IN3)
     |
   R = 100 kohm
     |
    GND analogue
```

The divider gives a DC reference at exactly 1.65 V. With the ADC sampling both channels alternately:

- The baseline channel shows you the ADC's own noise floor plus any 50 Hz mains hum picked up on the breadboard.
- The zener channel shows that plus the avalanche noise.
- Subtract one from the other and you see only the avalanche-attributable noise.

If you ever wonder "is this real noise or am I just measuring mains hum and ADC quantisation", this channel answers it.

## 5. Component-to-BOM cross reference

Each circuit element above and where it appears in [BOM.md](../BOM.md).

| Symbol           | Value                  | BOM section                           |
|------------------|------------------------|---------------------------------------|
| Zener D1         | 6.2 V 1W, 1N4735       | Entropy source                        |
| R_bias_fixed     | 10 kohm 0.5W MF        | Resistors, precision metal film       |
| R_bias_trim      | 100 kohm 25-turn cermet| Resistors, multiturn trimpots         |
| C_couple         | 1 uF MKT polyester     | Capacitors, film                      |
| R_in (mid-rail)  | 100 kohm MF, two off   | Resistors, precision metal film       |
| U1 (stage 1)     | NE5534 DIP-8           | Op-amps                               |
| R_F1             | 47 kohm MF             | Resistors, precision metal film       |
| R_G1             | 470 ohm                | Resistors, assortment pack            |
| C_decouple       | 100 nF MLCC            | Capacitors, decoupling                |
| C_bulk           | 10 uF electrolytic     | Capacitors, electrolytic              |
| FB_ferrite       | ferrite bead           | Ferrites                              |
| U2 (stage 2)     | LM833 DIP-8            | Op-amps                               |
| R_F2             | 10 kohm                | Resistors, precision metal film       |
| R_G2             | 1 kohm                 | Resistors, precision metal film       |
| C_F2             | 330 pF                 | Capacitors, ceramic assortment        |
| R_series (ADC)   | 1 kohm                 | Resistors, precision metal film       |
| D_clamp two off  | BAT46 Schottky DO-35   | Diodes, Schottky                      |
| C_aa (ADC)       | 100 nF MLCC            | Capacitors, decoupling                |
| Socket U1        | DIP-8                  | Op-amps, sockets                      |
| Socket U2        | DIP-8                  | Op-amps, sockets                      |

## 6. Layout discipline

For a breadboard build to work as analogue, you have to make a few choices that ordinary "shove jumpers in" breadboarding ignores:

1. Star ground at the NUCLEO AGND pin. All "GND" returns from the analogue zone go to that one point via a dedicated breadboard rail. No daisy-chained grounds through random jumpers.
2. Op-amp Vcc decoupling lives within 1 cm of the IC. 100 nF MLCC plus 10 uF electrolytic plus ferrite bead, immediately adjacent to the Vcc pin.
3. Use solid-core jumpers for the signal-path nets (zener tap, op-amp inputs, ADC line). Flexible flying jumpers act as antennas.
4. Isolate the +12 V to +18 V bias section from the +3.3 V or +5 V section. Only the coupling cap and a single GND wire cross between them.
5. Keep the ADC line as short as possible and away from the bias rail. The zener cathode swings only millivolts but the bias rail can pick up much more.

When you graduate to a stripboard or PCB version, you can add a real ground plane or at least a thick ground bus. That alone halves your noise floor.
