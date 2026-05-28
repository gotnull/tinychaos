"""Tests for the protocol encode/decode and packet validation."""

from __future__ import annotations

import struct

import pytest

from tinychaos.protocol import (
    BadCrcError,
    BadMagicError,
    HEADER_SIZE,
    MAGIC,
    MIN_PACKET_SIZE,
    PROTOCOL_VERSION,
    Packet,
    ShortPacketError,
    UnsupportedVersionError,
    crc16_ccitt_false,
    decode_packet,
    encode_packet,
)


# ---- Round-trip ------------------------------------------------------------


@pytest.mark.parametrize("count", [0, 1, 8, 256, 1024])
def test_roundtrip_various_counts(count, synthetic_samples):
    samples = synthetic_samples(count, seed=count).tolist()
    seq = 42
    time_us = 12_345_678

    encoded = encode_packet(seq, time_us, samples)
    packet = decode_packet(encoded)

    assert packet.header.version == PROTOCOL_VERSION
    assert packet.header.flags == 0
    assert packet.header.seq == seq
    assert packet.header.time_us == time_us
    assert packet.header.count == count
    assert list(packet.samples) == samples


def test_roundtrip_max_uint32_seq_and_time(synthetic_samples):
    samples = synthetic_samples(4, seed=1).tolist()
    seq = 0xFFFFFFFF
    time_us = 0xFFFFFFFF
    packet = decode_packet(encode_packet(seq, time_us, samples))
    assert packet.header.seq == seq
    assert packet.header.time_us == time_us


# ---- Byte-layout assertions ------------------------------------------------


def test_explicit_byte_layout():
    """Confirm bytes are in the order specified in the docs."""
    samples = [0x0102, 0x0304]
    seq = 0x11223344
    time_us = 0x55667788

    encoded = encode_packet(seq, time_us, samples, version=1, flags=0)

    assert encoded[0:2] == MAGIC
    assert encoded[2] == 1  # version
    assert encoded[3] == 0  # flags
    # SEQ little-endian: 0x11223344 -> 44 33 22 11
    assert encoded[4:8] == bytes([0x44, 0x33, 0x22, 0x11])
    # TIME_US little-endian: 0x55667788 -> 88 77 66 55
    assert encoded[8:12] == bytes([0x88, 0x77, 0x66, 0x55])
    # COUNT little-endian: 2 -> 02 00
    assert encoded[12:14] == bytes([0x02, 0x00])
    # SAMPLES little-endian: 0x0102 0x0304 -> 02 01 04 03
    assert encoded[14:18] == bytes([0x02, 0x01, 0x04, 0x03])
    # CRC at offset 18 (header 14 + 2*2 samples = 18). Bytes 18..20.
    assert len(encoded) == 20
    crc = struct.unpack("<H", encoded[18:20])[0]
    assert crc == crc16_ccitt_false(encoded[2:18])


def test_packet_is_immutable():
    samples = [1, 2, 3]
    packet = decode_packet(encode_packet(0, 0, samples))
    with pytest.raises(AttributeError):
        packet.header = None  # type: ignore[misc]


# ---- Rejection -------------------------------------------------------------


def test_reject_short_buffer():
    with pytest.raises(ShortPacketError):
        decode_packet(b"")
    with pytest.raises(ShortPacketError):
        decode_packet(b"\xDA\x7A")
    with pytest.raises(ShortPacketError):
        decode_packet(b"\xDA\x7A" + bytes(MIN_PACKET_SIZE - 3))


def test_reject_bad_magic():
    payload = encode_packet(1, 1, [0, 1, 2, 3])
    corrupted = bytes([0x00]) + payload[1:]
    with pytest.raises(BadMagicError):
        decode_packet(corrupted)


def test_reject_unsupported_version():
    payload = bytearray(encode_packet(1, 1, [0, 1, 2, 3]))
    payload[2] = 99  # version
    # CRC will now be wrong but version check fires first.
    with pytest.raises(UnsupportedVersionError) as exc_info:
        decode_packet(bytes(payload))
    assert exc_info.value.version == 99


def test_reject_bad_crc():
    payload = bytearray(encode_packet(1, 1, [0, 1, 2, 3]))
    payload[-1] ^= 0x01
    with pytest.raises(BadCrcError) as exc_info:
        decode_packet(bytes(payload))
    assert exc_info.value.expected != exc_info.value.actual


def test_reject_truncated_at_samples():
    payload = encode_packet(1, 1, [0, 1, 2, 3, 4, 5])
    # Cut off in the middle of the samples region.
    short = payload[: HEADER_SIZE + 4]
    with pytest.raises(ShortPacketError):
        decode_packet(short)


# ---- Encode validation -----------------------------------------------------


@pytest.mark.parametrize(
    "kwargs",
    [
        {"seq": -1, "time_us": 0},
        {"seq": 0, "time_us": -1},
        {"seq": 0x1_0000_0000, "time_us": 0},
        {"seq": 0, "time_us": 0x1_0000_0000},
    ],
)
def test_encode_rejects_out_of_range(kwargs):
    with pytest.raises(ValueError):
        encode_packet(samples=[1, 2], **kwargs)


def test_encode_rejects_out_of_range_sample():
    with pytest.raises(ValueError):
        encode_packet(0, 0, [0, 1, 0x1_0000])


def test_encode_rejects_too_many_samples():
    with pytest.raises(ValueError):
        encode_packet(0, 0, [0] * (0x1_0000))


# ---- Sanity ----------------------------------------------------------------


def test_packet_dataclasses_export_seq_time_count():
    packet = decode_packet(encode_packet(7, 8_000_000, [0, 1, 2]))
    assert packet.seq == 7
    assert packet.time_us == 8_000_000
    assert packet.count == 3
    assert isinstance(packet, Packet)
