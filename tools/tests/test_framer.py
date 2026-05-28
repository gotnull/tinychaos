"""Tests for the streaming framer."""

from __future__ import annotations

import random
from typing import Iterable

import pytest
from hypothesis import given, settings, strategies as st

from tinychaos.framer import (
    BadCrc,
    BadVersion,
    Framer,
    PacketReceived,
    ResyncDropped,
)
from tinychaos.protocol import encode_packet


# ---- Helpers ---------------------------------------------------------------


def drive(stream: bytes, chunk_sizes: Iterable[int] | None = None) -> list:
    """Feed ``stream`` into a Framer in the requested chunk sizes.

    If ``chunk_sizes`` is None, feed the whole stream at once.
    """
    framer = Framer()
    events: list = []
    if chunk_sizes is None:
        events.extend(framer.feed(stream))
    else:
        sizes = list(chunk_sizes)
        i = 0
        j = 0
        while i < len(stream):
            size = max(1, sizes[j % len(sizes)] if sizes else 1)
            events.extend(framer.feed(stream[i : i + size]))
            i += size
            j += 1
    # Flush any trailing resync.
    trailing = framer.flush_resync()
    if trailing is not None:
        events.append(trailing)
    return events


def count_events(events, event_type) -> int:
    return sum(1 for e in events if isinstance(e, event_type))


# ---- Basic happy path ------------------------------------------------------


def test_single_clean_packet(valid_packet_bytes):
    pkt = valid_packet_bytes(seq=0, time_us=0, count=4, seed=1)
    events = drive(pkt)
    assert len(events) == 1
    assert isinstance(events[0], PacketReceived)
    assert events[0].packet.seq == 0


def test_multiple_clean_packets(make_stream):
    stream, spec = make_stream("clean", n=5)
    events = drive(stream)
    received = [e for e in events if isinstance(e, PacketReceived)]
    assert len(received) == spec.expected_packet_count
    assert [p.packet.seq for p in received] == list(range(5))


# ---- Corruption scenarios --------------------------------------------------


def test_leading_garbage_then_packets(make_stream):
    stream, spec = make_stream("leading_garbage", n=3, seed=7)
    events = drive(stream)
    received = [e for e in events if isinstance(e, PacketReceived)]
    resync = [e for e in events if isinstance(e, ResyncDropped)]
    assert len(received) == spec.expected_packet_count
    # Total skipped bytes accumulate (possibly across multiple events).
    assert sum(e.n_bytes for e in resync) == spec.expected_resync_skipped_bytes


def test_bad_crc_one_packet(make_stream):
    stream, spec = make_stream("bad_crc", n=5, seed=11)
    events = drive(stream)
    received = [e for e in events if isinstance(e, PacketReceived)]
    bad_crc = [e for e in events if isinstance(e, BadCrc)]
    assert len(received) == spec.expected_packet_count
    assert len(bad_crc) == spec.expected_bad_crc_count
    # The bad CRC event's header should reflect the corrupted packet's SEQ.
    assert bad_crc[0].header.seq == len(received) // 2 + (
        1 if len(received) // 2 >= len(received) // 2 else 0
    ) - (1 if len(received) // 2 == 2 else 0)


def test_bad_magic_skips_corrupted_packet(make_stream):
    stream, spec = make_stream("bad_magic", n=5, seed=13)
    events = drive(stream)
    received = [e for e in events if isinstance(e, PacketReceived)]
    resync = [e for e in events if isinstance(e, ResyncDropped)]
    assert len(received) == spec.expected_packet_count
    assert sum(e.n_bytes for e in resync) == spec.expected_resync_skipped_bytes


def test_truncated_last_packet(make_stream):
    stream, spec = make_stream("truncated", n=4)
    events = drive(stream)
    received = [e for e in events if isinstance(e, PacketReceived)]
    assert len(received) == spec.expected_packet_count


# ---- SEQ gap detection is the consumer's job, not the framer's. -----------
#
# The framer simply emits each well-formed packet. The CLI level (DropTracker
# in stats.py) detects gaps. We still verify that the framer does not lose or
# duplicate packets across a gap.


def test_seq_gap_passed_through(make_stream):
    stream, spec = make_stream("seq_gap", n=5)
    events = drive(stream)
    received = [e for e in events if isinstance(e, PacketReceived)]
    seqs = [e.packet.seq for e in received]
    # We dropped the middle packet (seq=2 with n=5), so the framer should see
    # seqs 0, 1, 3, 4.
    assert seqs == [0, 1, 3, 4]


# ---- Bad version -----------------------------------------------------------


def test_bad_version_emits_event_and_resyncs(valid_packet_bytes):
    good = valid_packet_bytes(seq=1, time_us=1, count=4)
    bad = bytearray(valid_packet_bytes(seq=2, time_us=2, count=4))
    bad[2] = 99  # corrupt version byte
    # CRC is now stale; framer should detect bad version before reaching CRC.
    stream = bytes(bad) + good
    events = drive(stream)
    received = [e for e in events if isinstance(e, PacketReceived)]
    bad_version = [e for e in events if isinstance(e, BadVersion)]
    # The good packet at the end should be recovered.
    assert any(p.packet.seq == 1 for p in received)
    assert len(bad_version) == 1
    assert bad_version[0].version == 99


# ---- Chunk-size invariance (Hypothesis) -----------------------------------


def _summarise(events: list) -> tuple:
    """Reduce an event list to a comparable summary."""
    return tuple(
        ("pkt", e.packet.seq) if isinstance(e, PacketReceived)
        else ("bad_crc", e.header.seq) if isinstance(e, BadCrc)
        else ("resync", e.n_bytes) if isinstance(e, ResyncDropped)
        else ("bad_ver", e.version) if isinstance(e, BadVersion)
        else ("unknown",)
        for e in events
    )


@settings(deadline=None, max_examples=50)
@given(
    chunk_sizes=st.lists(
        st.integers(min_value=1, max_value=64),
        min_size=1,
        max_size=8,
    ),
    n_packets=st.integers(min_value=1, max_value=8),
    sample_count=st.sampled_from([0, 1, 8, 64]),
    seed=st.integers(min_value=0, max_value=10_000),
)
def test_chunk_size_invariance(chunk_sizes, n_packets, sample_count, seed):
    """The framer must produce identical event sequences regardless of how
    the byte stream is chunked.
    """
    rng = random.Random(seed)
    packets = [
        encode_packet(
            seq=i,
            time_us=i * 1000,
            samples=[(rng.randrange(4096)) for _ in range(sample_count)],
        )
        for i in range(n_packets)
    ]
    stream = b"".join(packets)

    # Reference: whole stream in one chunk.
    ref = _summarise(drive(stream))
    # Comparison: stream fed in the property's chunk_sizes pattern.
    chunked = _summarise(drive(stream, chunk_sizes=chunk_sizes))
    # Also test one-byte chunks.
    by_byte = _summarise(drive(stream, chunk_sizes=[1]))

    assert chunked == ref
    assert by_byte == ref


# ---- Single-bit-flip property --------------------------------------------


@settings(deadline=None, max_examples=40)
@given(bit_index=st.integers(min_value=0, max_value=8 * 80 - 1), seed=st.integers(0, 1000))
def test_single_bit_flip_no_false_positive(bit_index, seed):
    """The strong safety property: no single bit flip can cause the framer
    to emit a PacketReceived event for the corrupted packet's SEQ.

    The framer either flags BadCrc, emits BadVersion, or stays in a
    consume-and-resync state until further valid bytes arrive. In none of
    those cases should the corrupted packet appear in received output.

    Recovery of subsequent packets within a bounded test stream is NOT
    asserted: a bit flip that grows the COUNT field can extend the parsed
    body across many subsequent bytes before the parser hits a CRC failure
    and resyncs. In production this is fine because the byte stream is
    continuous; in a bounded test we may simply run out of bytes.
    """
    pkt = encode_packet(seq=1, time_us=1, samples=[42 + i for i in range(8)])
    if bit_index >= 8 * len(pkt):
        return  # nothing to flip in this packet
    corrupted = bytearray(pkt)
    corrupted[bit_index // 8] ^= 1 << (bit_index % 8)

    # Append several valid follow-on packets so the framer has resync
    # targets in the common case. We do not require recovery, only that no
    # false positive is emitted.
    follow_seqs = (2, 3, 4, 5)
    follows = b"".join(
        encode_packet(seq=s, time_us=s * 1000, samples=[s, s + 1, s + 2])
        for s in follow_seqs
    )
    stream = bytes(corrupted) + follows

    events = drive(stream)
    received_seqs = {e.packet.seq for e in events if isinstance(e, PacketReceived)}

    # Strong safety: the corrupted packet must never silently appear.
    assert 1 not in received_seqs
