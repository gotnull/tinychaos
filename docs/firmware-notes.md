# Firmware notes (superseded)

This file is kept at its original path to preserve any inbound links. The firmware design described in earlier revisions used an older frame format (`0xA5 0x5A` magic, no version or flags field, different CRC scope) and is no longer current.

The authoritative firmware design is now:

- [ENTROPY_CAPTURE_PIPELINE.md](ENTROPY_CAPTURE_PIPELINE.md), sections 7 and 8, for the USB CDC transport and the binary packet format.
- [hardware-design.md](hardware-design.md) for the analogue front-end and ADC pinout.
- [adc-protection.md](adc-protection.md) for the ADC input clamp network.
- `firmware/Core/Inc/entropy_config.h` for the compile-time constants once the firmware lands.

Wire format summary: 2-byte MAGIC `0xDA 0x7A`, 1-byte VERSION, 1-byte FLAGS, 4-byte SEQ, 4-byte TIME_US, 2-byte COUNT, then `2 * COUNT` little-endian uint16 ADC samples, then 2-byte CRC-16/CCITT-FALSE over VERSION through the last sample.
