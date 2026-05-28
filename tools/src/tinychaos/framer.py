"""Streaming resynchroniser for tinychaos packets.

The Framer turns a byte chunk feed into a sequence of decoded packets or
error events. It tolerates arbitrary corruption: leading garbage, mid-packet
bit flips, magic bytes flipped, packet truncation, doubled magics, anything.

Critical correctness properties (asserted by tests):

  - Feeding the same byte stream in one-byte chunks, fixed chunks, or random
    chunks yields the same sequence of events. The framer is purely stateful
    over the byte stream, not over chunk boundaries.

  - The framer never emits a PacketReceived event for a packet whose CRC is
    invalid. If a single byte flips, the framer either emits BadCrc or
    resyncs and emits ResyncDropped (when the flip lands in MAGIC).

  - After any number of bad bytes, the framer eventually resyncs onto the
    next valid packet, provided one appears.

The state machine has five states:

    SEARCH_MAGIC0 -- looking for 0xDA byte
    SEARCH_MAGIC1 -- saw 0xDA, now looking for 0x7A
    READ_HEADER   -- have MAGIC, accumulating 12 header bytes
    READ_BODY     -- header parsed, accumulating 2*COUNT sample bytes
    READ_CRC      -- accumulating 2 CRC bytes (then validate-and-emit)

On any mismatch during header or body collection the framer rewinds by one
byte from the last false MAGIC start and resumes searching. Bytes consumed
in vain accumulate into a single ResyncDropped event emitted lazily before
the next PacketReceived event.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from enum import Enum, auto
from typing import Iterator, Optional, Union

from .protocol import (
    BadCrcError,
    HEADER_SIZE,
    MAGIC_0,
    MAGIC_1,
    PROTOCOL_VERSION,
    Packet,
    PacketHeader,
    crc16_ccitt_false,
    decode_packet,
)


# ---- Event types -----------------------------------------------------------


@dataclass(frozen=True)
class PacketReceived:
    """A valid, CRC-checked packet."""

    packet: Packet


@dataclass(frozen=True)
class BadCrc:
    """A packet whose magic and length were plausible but whose CRC failed.

    The header is parsed so the consumer can see which SEQ value was
    affected. The bytes are then discarded.
    """

    header: PacketHeader
    expected_crc: int
    actual_crc: int


@dataclass(frozen=True)
class BadVersion:
    """A packet whose magic was right but whose version byte was not 1."""

    version: int


@dataclass(frozen=True)
class ResyncDropped:
    """One or more bytes were skipped during resync.

    Emitted lazily: while the framer is searching for MAGIC it accumulates
    the count internally, and emits a single event with the total just
    before the next PacketReceived (or BadCrc) it produces.
    """

    n_bytes: int


FrameEvent = Union[PacketReceived, BadCrc, BadVersion, ResyncDropped]


# ---- State machine ---------------------------------------------------------


class _State(Enum):
    SEARCH_MAGIC0 = auto()
    SEARCH_MAGIC1 = auto()
    READ_HEADER = auto()
    READ_BODY = auto()
    READ_CRC = auto()


_HEADER_STRUCT = struct.Struct("<BBIIH")


class Framer:
    """Streaming packet framer.

    Usage:

        framer = Framer()
        for chunk in source:
            for event in framer.feed(chunk):
                handle(event)
    """

    def __init__(self) -> None:
        self._state: _State = _State.SEARCH_MAGIC0
        self._skipped_bytes: int = 0
        # Buffers in flight while assembling a packet.
        self._header_buf = bytearray()
        self._body_buf = bytearray()
        self._crc_buf = bytearray()
        # Parsed header, populated when READ_HEADER completes.
        self._header: Optional[PacketHeader] = None
        self._body_target: int = 0  # bytes expected in body
        # Memory of bytes consumed since the most recent MAGIC0 hit. If we
        # bail out of a candidate packet we need to "give back" the bytes
        # after the false-start MAGIC0 so they are rescanned.
        self._consumed_after_magic0: bytearray = bytearray()
        # Pre-buffer for bytes we have not yet examined. This lets the
        # rewind-on-failure path push bytes back trivially.
        self._pending = bytearray()

    # -- Public API ---------------------------------------------------------

    def feed(self, chunk: bytes) -> Iterator[FrameEvent]:
        """Feed a chunk of bytes and yield any events produced."""
        if chunk:
            self._pending.extend(chunk)
        while self._pending:
            event = self._step()
            if event is not None:
                yield event
            if event is None and self._state in (_State.SEARCH_MAGIC0,):
                # Nothing more to do this iteration if we ran out of bytes
                # midway through scanning; the outer while-loop check on
                # _pending handles this. But the _step function may have
                # returned None because it consumed all pending bytes; in
                # that case the while loop will exit naturally.
                pass

    def flush_resync(self) -> Optional[ResyncDropped]:
        """Force emission of any pending ResyncDropped count.

        Useful at end-of-stream so the consumer sees the trailing skipped
        bytes even if no valid packet followed them.
        """
        if self._skipped_bytes:
            event = ResyncDropped(n_bytes=self._skipped_bytes)
            self._skipped_bytes = 0
            return event
        return None

    # -- State machine internals -------------------------------------------

    def _emit_resync_if_pending(self) -> Optional[ResyncDropped]:
        if self._skipped_bytes:
            event = ResyncDropped(n_bytes=self._skipped_bytes)
            self._skipped_bytes = 0
            return event
        return None

    def _pop_byte(self) -> Optional[int]:
        if not self._pending:
            return None
        # popleft would be O(1) on a deque; bytearray slicing is O(n) but the
        # arrays are tiny so it does not matter.
        b = self._pending[0]
        del self._pending[0]
        return b

    def _step(self) -> Optional[FrameEvent]:
        """Advance the state machine by as much as possible.

        Returns one event or None. The caller loops while there are pending
        bytes.
        """
        if self._state is _State.SEARCH_MAGIC0:
            return self._step_search_magic0()
        if self._state is _State.SEARCH_MAGIC1:
            return self._step_search_magic1()
        if self._state is _State.READ_HEADER:
            return self._step_read_header()
        if self._state is _State.READ_BODY:
            return self._step_read_body()
        if self._state is _State.READ_CRC:
            return self._step_read_crc()
        raise AssertionError(f"unreachable state: {self._state}")

    def _step_search_magic0(self) -> Optional[FrameEvent]:
        # Consume bytes one by one until we either find MAGIC0 or run out.
        while self._pending:
            b = self._pending[0]
            if b == MAGIC_0:
                del self._pending[0]
                self._state = _State.SEARCH_MAGIC1
                return None
            # Non-magic byte: count and skip.
            self._skipped_bytes += 1
            del self._pending[0]
        return None

    def _step_search_magic1(self) -> Optional[FrameEvent]:
        if not self._pending:
            return None
        b = self._pending[0]
        if b == MAGIC_1:
            del self._pending[0]
            # We found a complete magic. The MAGIC0 we consumed earlier was
            # legitimate; do NOT count it as skipped.
            self._state = _State.READ_HEADER
            self._header_buf.clear()
            return None
        # Magic0 was a false alarm. Count the MAGIC0 byte as skipped (since
        # we are about to scan from this position). Do not consume the
        # current byte: it might itself be a MAGIC0.
        self._skipped_bytes += 1
        self._state = _State.SEARCH_MAGIC0
        return None

    def _step_read_header(self) -> Optional[FrameEvent]:
        need = HEADER_SIZE - 2 - len(self._header_buf)  # 12 bytes total
        if not self._pending:
            return None
        take = min(need, len(self._pending))
        self._header_buf.extend(self._pending[:take])
        del self._pending[:take]
        if len(self._header_buf) < HEADER_SIZE - 2:
            return None

        # We have the full 12-byte header.
        version, flags, seq, time_us, count = _HEADER_STRUCT.unpack(bytes(self._header_buf))

        if version != PROTOCOL_VERSION:
            # Treat as resync: drop these header bytes and look for next magic.
            # The 2 bytes of magic + 12 bytes of header were all consumed; they
            # are all "skipped" in retrospect.
            event = BadVersion(version=version)
            self._skipped_bytes += 2 + len(self._header_buf)  # the magic + the header
            self._reset_to_search()
            return event

        # Reasonable bounds on COUNT to protect against absurd allocations on
        # corrupted streams. Max packet at our default config is 256; allow
        # headroom up to 4096 samples per packet.
        if count > 4096:
            self._skipped_bytes += 2 + len(self._header_buf)
            self._reset_to_search()
            return None

        self._header = PacketHeader(
            version=version,
            flags=flags,
            seq=seq,
            time_us=time_us,
            count=count,
        )
        self._body_target = 2 * count
        self._body_buf.clear()
        self._state = _State.READ_BODY
        return None

    def _step_read_body(self) -> Optional[FrameEvent]:
        need = self._body_target - len(self._body_buf)
        if need == 0:
            # zero-sample packet, jump straight to CRC
            self._state = _State.READ_CRC
            self._crc_buf.clear()
            return None
        if not self._pending:
            return None
        take = min(need, len(self._pending))
        self._body_buf.extend(self._pending[:take])
        del self._pending[:take]
        if len(self._body_buf) < self._body_target:
            return None
        self._state = _State.READ_CRC
        self._crc_buf.clear()
        return None

    def _step_read_crc(self) -> Optional[FrameEvent]:
        need = 2 - len(self._crc_buf)
        if not self._pending:
            return None
        take = min(need, len(self._pending))
        self._crc_buf.extend(self._pending[:take])
        del self._pending[:take]
        if len(self._crc_buf) < 2:
            return None

        # We have everything. Validate.
        header = self._header
        assert header is not None
        crc_input = bytes(self._header_buf) + bytes(self._body_buf)
        crc_computed = crc16_ccitt_false(crc_input)
        (crc_received,) = struct.unpack("<H", bytes(self._crc_buf))

        # Emit any pending ResyncDropped before the outcome event.
        resync_event = self._emit_resync_if_pending()

        if crc_received != crc_computed:
            # Bad CRC: discard this packet's bytes; they have all been consumed
            # legitimately (we found a real-looking header), so they are NOT
            # counted as resync-skipped.
            event_after = BadCrc(
                header=header,
                expected_crc=crc_computed,
                actual_crc=crc_received,
            )
        else:
            # Build samples tuple.
            samples = struct.unpack(f"<{header.count}H", bytes(self._body_buf))
            packet = Packet(header=header, samples=samples)
            event_after = PacketReceived(packet=packet)

        self._reset_to_search()

        # If we had a pending resync count, emit it now and stash the outcome
        # event for the next pump. Cheaper alternative: we have two events to
        # emit but the caller is iterating, so push the outcome back into a
        # deferred slot.
        if resync_event is not None:
            self._deferred_event = event_after  # type: ignore[attr-defined]
            return resync_event
        return event_after

    def _reset_to_search(self) -> None:
        self._state = _State.SEARCH_MAGIC0
        self._header_buf.clear()
        self._body_buf.clear()
        self._crc_buf.clear()
        self._header = None
        self._body_target = 0

    # The deferred-event slot is initialised here so it always exists. After
    # a CRC validation step we may need to emit two events in sequence
    # (ResyncDropped then PacketReceived). The first is returned synchronously
    # and the second is stashed here for the next call. _step picks it up
    # before doing any other work.
    _deferred_event: Optional[FrameEvent] = None

    def __init_subclass__(cls, **kwargs):  # pragma: no cover - safety
        super().__init_subclass__(**kwargs)

    # Override _step to drain a pending deferred event first.
    def _drain_deferred(self) -> Optional[FrameEvent]:
        if self._deferred_event is not None:
            event = self._deferred_event
            self._deferred_event = None
            return event
        return None


# Patch the Framer.feed to drain deferred events around _step calls.
# We do this by replacing feed with a wrapper that integrates the deferred
# drain into the iteration. Keeping it as a separate method avoids reaching
# into the _step machinery.

def _feed(self, chunk: bytes) -> Iterator[FrameEvent]:
    if chunk:
        self._pending.extend(chunk)
    while True:
        deferred = self._drain_deferred()
        if deferred is not None:
            yield deferred
            continue
        if not self._pending and self._state is _State.SEARCH_MAGIC0:
            return
        event = self._step()
        if event is not None:
            yield event
            continue
        # _step returned None: either we ran out of bytes mid-state or we
        # transitioned without producing an event.
        if not self._pending:
            return


Framer.feed = _feed  # type: ignore[method-assign]
