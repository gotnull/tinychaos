# Filtering and Power

This document covers the supply rails, decoupling, ferrites, RC filtering, and the analogue layout choices that determine whether the noise you measure is your zener or the room.

## 1. Power topology

Two independent rails:

```
zener bias rail            +12V to +18V
                           battery preferred for low-noise tests:
                           - 9V battery via clip (gives 9V, marginal for 6.2V zener)
                           - two 9V batteries in series (gives 18V, comfortable)
                           - DC-DC boost module set to 12 to 15V from USB

amplifier and ADC rail     +5V or +3.3V regulated
                           from the breadboard PSU module (with onboard LDO)
                           OR direct from the NUCLEO 3.3V pin

logic and host rail        3.3V from the NUCLEO
                           USB cable from PC supplies the NUCLEO
```

Connect the two analogue grounds at exactly one point. Connect that point to NUCLEO AGND.

## 2. Decoupling at every IC

For each op-amp (NE5534 and LM833):

```
+5V rail ---[ferrite bead]---[+]---> Vcc pin of op-amp
                              |
                          [10 uF electrolytic]
                              |
                          [100 nF ceramic MLCC]
                              |
                             GND
```

Place the 100 nF MLCC physically within 1 cm of the Vcc pin. The 10 uF bulk can be a few cm away. The ferrite bead goes in series before both caps and acts as a high-frequency series impedance, killing HF noise riding on the rail from elsewhere in the breadboard.

## 3. RC filtering targets

Three filter roles in the chain:

### Highpass at the input (sets lowest noise frequency captured)

```
C_couple in series with R_in to mid-rail bias network
f_HP = 1 / (2 * pi * R_in * C_couple)

Default: R_in = 50 kohm (Thevenin of 100k || 100k mid-rail divider)
         C_couple = 1 uF MKT (RM7170 equivalent)
         f_HP ~= 3.2 Hz
```

### Bandpass shaping inside stage 2

```
R_F2 = 10 kohm
C_F2 = 330 pF in parallel with R_F2
f_LP = 1 / (2 * pi * R_F2 * C_F2) ~= 48 kHz
```

This kills band above 48 kHz before the ADC anti-alias filter.

### Anti-alias before ADC

```
R_series in series, then C to GND at the ADC pin
R_series = 1 kohm (also doubles as ADC input current limit)
C        = 100 nF MLCC
f_LP = 1 / (2 * pi * 1k * 100n) ~= 1.6 kHz
```

This is the final pole. Match the ADC sample rate so the Nyquist frequency is well above 1.6 kHz (sampling at 10 kHz gives Nyquist of 5 kHz, comfortable headroom).

If you want a wider noise bandwidth and a higher sample rate, change C downward: 1 nF gives 160 kHz corner, suitable for sampling at 500 kHz or higher.

## 4. Ferrite use

Two ferrite roles:

- Bead in series with op-amp Vcc pin: small SMD or through-hole bead, suppresses HF supply noise. Specified part: through-hole ferrite bead pack (BOM section "Ferrites").
- Clip-on suppression sleeve around the USB cable from PC to NUCLEO: catches common-mode noise from the PC. Optional but cheap insurance.

## 5. Battery-isolated test mode

For the cleanest possible measurement run:

```
1. Disconnect the USB from the NUCLEO. (Saves data only intermittently then.)
2. Power the NUCLEO from its E5V pin via a 9V battery and an LM7805 (or just use
   the on-board 3.3V LDO from a 5V rail derived from a power bank).
3. Power the zener bias from two 9V batteries in series.
4. Take a recording, then reconnect USB to offload data.
```

Or simpler: keep USB connected but add a USB isolator (any cheap ADUM3160 board, sourced from element14 or AliExpress) between PC and NUCLEO. This breaks the PC ground loop without changing the firmware flow.

## 6. What to check with the baseline channel

The ADC's second channel measures the static mid-rail divider. Differences between the zener channel and the baseline channel attribute to:

- 50 Hz / 100 Hz peaks in baseline: mains hum coupling. Improve grounding, shorten leads, move away from switching power adapters.
- Broadband baseline noise above ADC LSB: ADC quantisation plus op-amp noise plus mains-derived junk. Compare to NE5534 expected noise budget.
- Baseline drift over minutes: thermal effects on the resistors or supply. Use 1 percent metal-film resistors throughout (in the BOM).

Anything in the zener channel that is *not* in the baseline channel is your entropy.
