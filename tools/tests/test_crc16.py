"""Tests for the CRC-16/CCITT-FALSE implementation."""

from __future__ import annotations

import pytest

from tinychaos.protocol import crc16_ccitt_false


def test_known_answer_123456789():
    """The canonical CRC-16/CCITT-FALSE known-answer."""
    assert crc16_ccitt_false(b"123456789") == 0x29B1


@pytest.mark.parametrize(
    "data, expected",
    [
        # Reference vectors. See e.g. https://reveng.sourceforge.io/crc-catalogue/
        (b"", 0xFFFF),  # empty input returns the init value
        (b"\x00", 0xE1F0),
        (b"\xFF", 0xFF00),
        (b"A", 0xB915),
        (b"AB", 0x4B74),
    ],
)
def test_parametrised_vectors(data, expected):
    assert crc16_ccitt_false(data) == expected


def test_crc_is_deterministic():
    payload = bytes(range(256)) * 4
    assert crc16_ccitt_false(payload) == crc16_ccitt_false(payload)


def test_crc_changes_with_single_bit_flip():
    """Any single bit flip should change the CRC. Strong property in 16-bit."""
    payload = b"the quick brown fox jumps over the lazy dog"
    base = crc16_ccitt_false(payload)
    for i in range(len(payload) * 8):
        flipped = bytearray(payload)
        flipped[i // 8] ^= 1 << (i % 8)
        assert crc16_ccitt_false(bytes(flipped)) != base
