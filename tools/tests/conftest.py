"""Shared pytest fixtures for the tinychaos test suite.

The fixtures here build synthetic byte streams so the parser and framer can be
fully exercised before any firmware exists. They are deliberately deterministic
(seeded RNG) so test failures are reproducible.
"""

from __future__ import annotations

import random
from dataclasses import dataclass
from typing import Iterable

import numpy as np
import pytest

from tinychaos.protocol import encode_packet


@pytest.fixture
def synthetic_samples():
    """Return a callable that produces deterministic 12-bit ADC samples.

    Usage:
        samples = synthetic_samples(count=256, seed=42)
    """

    def make(count: int, *, seed: int = 0) -> np.ndarray:
        rng = np.random.default_rng(seed)
        # 12-bit ADC range. Random uniform integers 0..4095.
        return rng.integers(0, 4096, size=count, dtype=np.uint16)

    return make


@pytest.fixture
def valid_packet_bytes(synthetic_samples):
    """Return a callable that builds a single valid packet."""

    def make(seq: int, time_us: int, *, count: int = 256, seed: int = 0) -> bytes:
        samples = synthetic_samples(count, seed=seed)
        return encode_packet(seq, time_us, samples.tolist())

    return make


@dataclass
class StreamSpec:
    """Description of a synthetic byte stream for framer tests."""

    packets: tuple[bytes, ...]
    expected_packet_count: int
    expected_bad_crc_count: int
    expected_drop_count: int
    expected_resync_skipped_bytes: int


@pytest.fixture
def make_stream(valid_packet_bytes):
    """Build a synthetic byte stream with controlled corruption.

    The ``kind`` argument selects the corruption pattern:

      ``"clean"``           N back-to-back valid packets.
      ``"leading_garbage"`` N random bytes before the first valid packet.
      ``"bad_crc"``         One packet has its sample-region byte flipped.
      ``"bad_magic"``       One packet has its magic byte flipped (forces resync).
      ``"seq_gap"``         One packet is dropped from the middle.
      ``"truncated"``       The last packet is cut in half.
    """

    def make(kind: str, n: int = 5, *, seed: int = 0) -> tuple[bytes, StreamSpec]:
        rng = random.Random(seed)
        packets = [
            valid_packet_bytes(seq=i, time_us=i * 1000, count=64, seed=i)
            for i in range(n)
        ]

        if kind == "clean":
            stream = b"".join(packets)
            spec = StreamSpec(
                packets=tuple(packets),
                expected_packet_count=n,
                expected_bad_crc_count=0,
                expected_drop_count=0,
                expected_resync_skipped_bytes=0,
            )
            return stream, spec

        if kind == "leading_garbage":
            garbage = bytes(rng.randrange(256) for _ in range(73))
            # Make sure no accidental valid magic appears in the garbage.
            garbage = garbage.replace(b"\xDA\x7A", b"\xDA\x00")
            stream = garbage + b"".join(packets)
            spec = StreamSpec(
                packets=tuple(packets),
                expected_packet_count=n,
                expected_bad_crc_count=0,
                expected_drop_count=0,
                expected_resync_skipped_bytes=len(garbage),
            )
            return stream, spec

        if kind == "bad_crc":
            mid = n // 2
            corrupted = bytearray(packets[mid])
            # Flip a bit inside the samples region (after the header).
            corrupted[20] ^= 0x01
            corrupted_packets = list(packets)
            corrupted_packets[mid] = bytes(corrupted)
            stream = b"".join(corrupted_packets)
            spec = StreamSpec(
                packets=tuple(corrupted_packets),
                expected_packet_count=n - 1,
                expected_bad_crc_count=1,
                expected_drop_count=0,
                expected_resync_skipped_bytes=0,
            )
            return stream, spec

        if kind == "bad_magic":
            mid = n // 2
            corrupted = bytearray(packets[mid])
            corrupted[0] ^= 0x01  # break the first magic byte
            corrupted_packets = list(packets)
            corrupted_packets[mid] = bytes(corrupted)
            stream = b"".join(corrupted_packets)
            # The corrupted packet's bytes get scanned during resync, so the
            # framer skips its entire length looking for the next magic.
            spec = StreamSpec(
                packets=tuple(corrupted_packets),
                expected_packet_count=n - 1,
                expected_bad_crc_count=0,
                expected_drop_count=0,
                expected_resync_skipped_bytes=len(corrupted),
            )
            return stream, spec

        if kind == "seq_gap":
            mid = n // 2
            without_mid = packets[:mid] + packets[mid + 1 :]
            stream = b"".join(without_mid)
            spec = StreamSpec(
                packets=tuple(without_mid),
                expected_packet_count=n - 1,
                expected_bad_crc_count=0,
                expected_drop_count=1,
                expected_resync_skipped_bytes=0,
            )
            return stream, spec

        if kind == "truncated":
            *prefix, last = packets
            stream = b"".join(prefix) + last[: len(last) // 2]
            spec = StreamSpec(
                packets=tuple(prefix),
                expected_packet_count=n - 1,
                expected_bad_crc_count=0,
                expected_drop_count=0,
                expected_resync_skipped_bytes=0,
            )
            return stream, spec

        raise ValueError(f"unknown corruption kind: {kind}")

    return make


def chunked(data: bytes, sizes: Iterable[int]) -> list[bytes]:
    """Split ``data`` into chunks of the given sizes, cycling if needed."""
    sizes = list(sizes)
    if not sizes:
        return [data]
    out: list[bytes] = []
    i = 0
    j = 0
    while i < len(data):
        size = sizes[j % len(sizes)]
        out.append(data[i : i + size])
        i += size
        j += 1
    return out
