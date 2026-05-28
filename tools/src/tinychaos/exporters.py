"""Output writers for capture sessions.

Two exporters, both context managers so the CLI can compose them naturally:

- CsvExporter        one row per sample with the canonical schema
                     host_time, packet_seq, stm32_time_us, sample_index,
                     channel_index, adc_value, validation_label
- RawBinaryExporter  appends each accepted packet's bytes for replay later

Both are write-only and best-effort: they raise on filesystem errors but
otherwise do not interfere with the capture loop.
"""

from __future__ import annotations

import csv
import io
import time
from contextlib import AbstractContextManager
from typing import IO, Optional

from .protocol import Packet


CSV_COLUMNS = (
    "host_time",
    "packet_seq",
    "stm32_time_us",
    "sample_index",
    "channel_index",
    "adc_value",
    "validation_label",
)


class CsvExporter(AbstractContextManager["CsvExporter"]):
    """Write decoded samples to a CSV file.

    Samples are emitted in packet-arrival order, then by sample index within
    the packet, then by channel index. Because the firmware sends interleaved
    channel samples (zener, baseline, zener, baseline, ...), we treat odd
    sample indices as channel 1 and even as channel 0 unless told otherwise.
    """

    def __init__(
        self,
        path: str,
        *,
        channel_count: int = 2,
        validation_label: str = "",
    ) -> None:
        self._path = path
        self._channel_count = int(channel_count)
        if self._channel_count < 1:
            raise ValueError("channel_count must be >= 1")
        self._validation_label = validation_label
        self._file: Optional[IO[str]] = None
        self._writer: Optional[csv.writer] = None

    def __enter__(self) -> "CsvExporter":
        self._file = open(self._path, "w", newline="", encoding="utf-8")
        self._writer = csv.writer(self._file)
        self._writer.writerow(CSV_COLUMNS)
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        if self._file is not None:
            try:
                self._file.flush()
            finally:
                self._file.close()
            self._file = None
            self._writer = None

    def write_packet(self, packet: Packet, *, host_time: Optional[float] = None) -> None:
        if self._writer is None:
            raise RuntimeError("CsvExporter used outside its context")
        ht = host_time if host_time is not None else time.time()
        seq = packet.header.seq
        time_us = packet.header.time_us
        cc = self._channel_count
        # samples are interleaved across channels. sample_index here is the
        # raw index into the packet; channel_index is (idx % cc).
        for idx, value in enumerate(packet.samples):
            self._writer.writerow(
                [
                    f"{ht:.6f}",
                    seq,
                    time_us,
                    idx,
                    idx % cc,
                    int(value),
                    self._validation_label,
                ]
            )


class RawBinaryExporter(AbstractContextManager["RawBinaryExporter"]):
    """Append the bytes of accepted packets to a file.

    The file format is the on-wire format: concatenated packets, no padding,
    no framing beyond what each packet already contains. This means the same
    framer can later replay the file via FileSource and reproduce the
    PacketReceived sequence exactly.
    """

    def __init__(self, path: str) -> None:
        self._path = path
        self._file: Optional[IO[bytes]] = None

    def __enter__(self) -> "RawBinaryExporter":
        self._file = open(self._path, "ab")
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        if self._file is not None:
            try:
                self._file.flush()
            finally:
                self._file.close()
            self._file = None

    def write_packet_bytes(self, payload: bytes) -> None:
        if self._file is None:
            raise RuntimeError("RawBinaryExporter used outside its context")
        self._file.write(payload)
