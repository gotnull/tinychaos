"""Wire-format protocol for tinychaos entropy capture packets.

Packet layout (little-endian throughout):

    Offset  Field      Size       Notes
    ------  ---------  ---------  -----------------------------------------------
    0       MAGIC      2 bytes    Literal 0xDA 0x7A, not endian-swapped
    2       VERSION    1 byte     Protocol version, currently 1
    3       FLAGS      1 byte     Reserved, currently 0
    4       SEQ        4 bytes    uint32 LE, monotonically increasing
    8       TIME_US    4 bytes    uint32 LE, STM32 microsecond timestamp
    12      COUNT      2 bytes    uint16 LE, number of uint16 samples
    14      SAMPLES    2*COUNT    uint16 LE ADC samples
    14+2N   CRC16      2 bytes    uint16 LE, CRC-16/CCITT-FALSE

The CRC covers VERSION through the last sample, that is 12 + 2*COUNT bytes.
The CRC does not cover MAGIC or itself.

This module is the host-side authority for the protocol. The firmware has a
parallel implementation in C; both sides assert the same CRC known-answer.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from typing import Sequence

# ---- Constants -------------------------------------------------------------

MAGIC: bytes = b"\xDA\x7A"
MAGIC_0: int = 0xDA
MAGIC_1: int = 0x7A

PROTOCOL_VERSION: int = 1
DEFAULT_FLAGS: int = 0

#: Bytes from start of MAGIC through end of COUNT (header), exclusive of CRC.
HEADER_SIZE: int = 14

#: Bytes consumed by the trailing CRC field.
CRC_SIZE: int = 2

#: Minimum valid packet size (zero samples). Used for the framer fast-fail.
MIN_PACKET_SIZE: int = HEADER_SIZE + CRC_SIZE  # = 16


# ---- Exceptions ------------------------------------------------------------

class ProtocolError(Exception):
    """Base for any protocol-level decoding failure."""


class ShortPacketError(ProtocolError):
    """Buffer is shorter than the minimum or claimed packet length."""


class BadMagicError(ProtocolError):
    """The first two bytes did not match the MAGIC sequence."""


class BadCrcError(ProtocolError):
    """The trailing CRC did not match the computed CRC over the payload."""

    def __init__(self, expected: int, actual: int):
        super().__init__(f"CRC mismatch: expected 0x{expected:04X}, got 0x{actual:04X}")
        self.expected = expected
        self.actual = actual


class UnsupportedVersionError(ProtocolError):
    """The VERSION byte does not match a version this module knows about."""

    def __init__(self, version: int):
        super().__init__(f"Unsupported protocol version: {version}")
        self.version = version


# ---- CRC-16/CCITT-FALSE ----------------------------------------------------

_CRC16_POLY = 0x1021
_CRC16_INIT = 0xFFFF


def crc16_ccitt_false(data: bytes) -> int:
    """Compute CRC-16/CCITT-FALSE over ``data``.

    Polynomial 0x1021, initial value 0xFFFF, no input reflection, no output
    reflection, no final XOR. Known-answer on b"123456789" is 0x29B1.

    A bytewise (no table) implementation. Performance is acceptable for the
    expected packet sizes (under 1 KB per packet at default config); a table
    lookup version is straightforward if needed later.
    """
    crc = _CRC16_INIT
    for byte in data:
        crc ^= byte << 8
        for _ in range(8):
            if crc & 0x8000:
                crc = ((crc << 1) ^ _CRC16_POLY) & 0xFFFF
            else:
                crc = (crc << 1) & 0xFFFF
    return crc


# ---- Data classes ----------------------------------------------------------

@dataclass(frozen=True)
class PacketHeader:
    """Decoded header fields, in the order they appear on the wire."""

    version: int
    flags: int
    seq: int
    time_us: int
    count: int


@dataclass(frozen=True)
class Packet:
    """A fully decoded, CRC-validated packet."""

    header: PacketHeader
    samples: tuple[int, ...]

    @property
    def seq(self) -> int:
        return self.header.seq

    @property
    def time_us(self) -> int:
        return self.header.time_us

    @property
    def count(self) -> int:
        return self.header.count


# ---- Encode and decode -----------------------------------------------------

# struct format for the post-MAGIC header (12 bytes: B B I I H).
# Little-endian, no padding (the leading '<' disables alignment).
_HEADER_STRUCT = struct.Struct("<BBIIH")
assert _HEADER_STRUCT.size == HEADER_SIZE - 2, "header struct size mismatch"


def encode_packet(
    seq: int,
    time_us: int,
    samples: Sequence[int],
    *,
    version: int = PROTOCOL_VERSION,
    flags: int = DEFAULT_FLAGS,
) -> bytes:
    """Encode a packet to bytes.

    The ``samples`` sequence is encoded as little-endian uint16 in order.
    Raises ``ValueError`` if any sample is out of range or if seq/time_us
    exceed uint32.
    """
    if not 0 <= version <= 0xFF:
        raise ValueError(f"version out of range: {version}")
    if not 0 <= flags <= 0xFF:
        raise ValueError(f"flags out of range: {flags}")
    if not 0 <= seq <= 0xFFFFFFFF:
        raise ValueError(f"seq out of range: {seq}")
    if not 0 <= time_us <= 0xFFFFFFFF:
        raise ValueError(f"time_us out of range: {time_us}")

    count = len(samples)
    if not 0 <= count <= 0xFFFF:
        raise ValueError(f"sample count out of range: {count}")

    header = _HEADER_STRUCT.pack(version, flags, seq, time_us, count)
    # Build samples block. struct.pack with explicit format is faster than a
    # generator on the hot path; we accept Sequence[int] here so a list, tuple,
    # or numpy uint16 array all work.
    sample_fmt = f"<{count}H"
    try:
        samples_bytes = struct.pack(sample_fmt, *samples)
    except struct.error as e:
        raise ValueError(f"sample out of uint16 range: {e}") from e

    crc_input = header + samples_bytes
    crc = crc16_ccitt_false(crc_input)
    crc_bytes = struct.pack("<H", crc)

    return MAGIC + crc_input + crc_bytes


def decode_packet(buf: bytes) -> Packet:
    """Decode a complete packet from ``buf``.

    ``buf`` must be the bytes from MAGIC through CRC inclusive, with no
    leading or trailing junk. The streaming framer in framer.py is the right
    way to get well-aligned slices; this function does not search for MAGIC.

    Raises one of the ProtocolError subclasses on any decoding failure.
    """
    if len(buf) < MIN_PACKET_SIZE:
        raise ShortPacketError(
            f"packet too short: {len(buf)} bytes, need at least {MIN_PACKET_SIZE}"
        )

    if buf[0] != MAGIC_0 or buf[1] != MAGIC_1:
        raise BadMagicError(
            f"bad magic: got {buf[0]:#04x} {buf[1]:#04x}, "
            f"expected {MAGIC_0:#04x} {MAGIC_1:#04x}"
        )

    version, flags, seq, time_us, count = _HEADER_STRUCT.unpack_from(buf, 2)

    if version != PROTOCOL_VERSION:
        raise UnsupportedVersionError(version)

    expected_len = HEADER_SIZE + 2 * count + CRC_SIZE
    if len(buf) < expected_len:
        raise ShortPacketError(
            f"truncated: have {len(buf)} bytes, need {expected_len} "
            f"(count={count})"
        )

    samples_start = HEADER_SIZE
    samples_end = samples_start + 2 * count
    samples_bytes = buf[samples_start:samples_end]
    samples = struct.unpack(f"<{count}H", samples_bytes)

    # CRC covers VERSION through last sample (12 + 2*count bytes).
    crc_input = buf[2:samples_end]
    crc_computed = crc16_ccitt_false(crc_input)

    (crc_received,) = struct.unpack_from("<H", buf, samples_end)
    if crc_received != crc_computed:
        raise BadCrcError(expected=crc_computed, actual=crc_received)

    header = PacketHeader(
        version=version,
        flags=flags,
        seq=seq,
        time_us=time_us,
        count=count,
    )
    return Packet(header=header, samples=samples)
