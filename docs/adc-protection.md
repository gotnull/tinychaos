# ADC Input Protection

The STM32H753ZI ADC absolute-maximum input is Vdda + 0.3 V. With Vdda = 3.3 V that is 3.6 V. Below GND minus 0.3 V (so below -0.3 V) is also out of spec. Exceed either rail by enough current and the pin's internal ESD diodes start clamping. Exceed them by more current and the pin is damaged permanently.

The amplifier output can easily go to either supply rail under saturation. So we cannot rely on the amplifier alone to stay in range. The ADC input therefore needs its own protection network, independent of the amplifier.

## The protection network

```
amplifier output  ----[R_series 1k]----+----[ADC pin PA0]
                                       |
                                       +--[BAT46]--> +3.3V (cathode to rail)
                                       |
                                       +--[BAT46]--> GND   (anode to GND)
                                       |
                                       +---[C_aa 100nF]--- GND
```

Three things happening in parallel at the ADC pin:

1. R_series limits current. If the amplifier output goes to a supply rail and the clamp diode fires, the current is bounded by R_series. With 1 kohm and an amplifier supply at 5 V trying to push the input to 5 V, the clamp diode at +3.3 V sees about (5 - 3.6) / 1k = 1.4 mA. The BAT46 is rated 150 mA continuous. Very safe.

2. BAT46 Schottky clamps the voltage. Forward voltage is about 0.3 V at low current. So with the upper clamp (cathode to +3.3 V rail), the input is clamped at 3.3 + 0.3 = 3.6 V. That is within the STM32 absolute-max envelope. With the lower clamp (anode to GND), the input is clamped at 0 - 0.3 = -0.3 V. Also within absolute-max.

3. C_aa forms the anti-alias lowpass with R_series. With R = 1k and C = 100 nF the corner is 1.6 kHz. Adjust C for your target ADC sample rate. See [filtering-and-power.md](filtering-and-power.md).

## Why Schottky and not 1N4148

Standard silicon diodes (1N4148) clamp at about 0.7 V. With cathode to +3.3 V that puts the clamped input at 3.3 + 0.7 = 4.0 V, which exceeds the STM32 absolute-max of 3.6 V. Use Schottky.

If you have only 1N4148 on hand, you can clamp to a lower supply (for example, clamp to +3.0 V or to a precision 2.5 V Zener reference) and use the slightly higher Vf safely. But the cleanest solution is BAT46 to the 3.3 V rail directly.

## Why we still need R_series even with the clamp

The ADC internal sample-and-hold cap charges through whatever source impedance you give it. A high source impedance plus a fast sample window can cause the SAR to settle on the wrong code. STM32H7 with default settings is happy with source impedance up to a few kohm, depending on sample time. 1 kohm series is well within tolerance and gives the clamps room to do their job. See the STM32H753ZI datasheet section "ADC input impedance" for the exact relationship between source impedance and sample cycles.

## Layout

Run R_series and the clamp diodes physically close to the STM32 ADC pin, not the amplifier output. The protection job is to protect the *pin*, so the diodes need a short path to the pin and a short path to the local +3.3 V and GND.

```
ideal layout

amplifier out  -------------- 5 cm or more ------------+
                                                       |
                                                      Rs
                                                       |
                                       short  +--------+--------+
                                              |        |        |
                                            Dclmp+   Dclmp-   Caa
                                              |        |        |
                                            +3V3      GND      GND
                                              |        |        |
                                            short    short    short
                                                       |
                                                      pin PA0
```

The protection network sits a few mm from the ADC pin. The path from amplifier to that network can be longer; what matters is short distance from the diode to the rail and to the pin.

## Limits and what this does NOT protect against

This network protects against:

- Slow over-voltage at the amplifier output. Steady-state up to about 30 V at the amplifier output is bounded by R_series at 30 mA, well below the BAT46 limit.
- Power-up transients where the amplifier rail comes up before the +3.3 V rail.
- Static discharge on the breadboard from finger-touch (small joule).

It does NOT protect against:

- Direct contact with mains voltage. If you accidentally connect 230 V AC to the ADC pin, the 1 kohm sees over 200 mA peak and the diode dies. Use isolation if you go anywhere near mains.
- Inductive flyback from a large coil. Add a TVS or freewheel diode at the source, not at the ADC.
- ESD from a charged dry-finger touch directly to the pin (~kV pulse). Adding an ESD-rated TVS (for example PESD3V3 from element14) parallel to the input is sensible if you handle the board a lot. Specify this in the BOM nice-to-have section.
