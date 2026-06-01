#!/usr/bin/env python3
"""
Extract decoded ADC samples from a raw tinychaos capture, stripping ALL packet
framing (the 14-byte header AND the trailing 2-byte CRC of every packet). The
output is nothing but the sample values, ready to load in other tools.

Why not just hand-strip bytes? Two traps this avoids:
  1. The sample data can contain the bytes 0xDA 0x7A by chance, so searching for
     the magic to find packet boundaries will eventually mis-split. This decodes
     through the real framer instead, which tracks length and CRC.
  2. Corrupted packets are dropped (CRC-checked) rather than silently fed into
     your analysis as garbage. Resync regions are skipped and reported.

Packet layout (for reference):
    0   MAGIC      DA 7A          2 bytes
    2   VERSION                   1 byte
    3   FLAGS                     1 byte
    4   SEQ        uint32 LE      4 bytes
    8   TIME_US    uint32 LE      4 bytes
    12  COUNT      uint16 LE      2 bytes   (= 256)
    14  SAMPLES    uint16 LE      2*COUNT   (= 512 bytes for 256 samples)
    ..  CRC16      uint16 LE      2 bytes
    => 528 bytes total per packet at COUNT=256.
Samples are channel-interleaved: ch0, ch1, ch0, ch1, ... (default 2 channels).

Usage:
    python extract_samples.py CAPTURE.bin --raw out.bin        # all samples, uint16 LE
    python extract_samples.py CAPTURE.bin --split out          # out_ch0.bin, out_ch1.bin
    python extract_samples.py CAPTURE.bin --csv out.csv        # index,channel,value
    python extract_samples.py CAPTURE.bin --raw out.bin --csv out.csv --channels 2
"""
import argparse
import struct
import sys
from pathlib import Path

# Make the in-tree package importable without installing it.
sys.path.insert(0, str(Path(__file__).parent / "src"))
from tinychaos.framer import Framer, PacketReceived, BadCrc, BadVersion, ResyncDropped  # noqa: E402

READ_CHUNK = 65536


HEADER_SIZE = 14   # MAGIC(2) VERSION(1) FLAGS(1) SEQ(4) TIME_US(4) COUNT(2)
CRC_SIZE = 2       # uint16 LE trailing CRC-16/CCITT-FALSE


def decode(path: Path):
    """Stream the file through the framer; return (interleaved_samples, stats)."""
    framer = Framer()
    samples: list[int] = []
    stats = {"packets": 0, "bad_crc": 0, "bad_version": 0, "resync_bytes": 0,
             "sample_count": None}
    with path.open("rb") as f:
        while True:
            chunk = f.read(READ_CHUNK)
            if not chunk:
                break
            for ev in framer.feed(chunk):
                if isinstance(ev, PacketReceived):
                    samples.extend(ev.packet.samples)
                    stats["packets"] += 1
                    # Record the samples-per-packet (COUNT field) for the report.
                    if stats["sample_count"] is None:
                        stats["sample_count"] = ev.packet.count
                elif isinstance(ev, BadCrc):
                    stats["bad_crc"] += 1
                elif isinstance(ev, BadVersion):
                    stats["bad_version"] += 1
                elif isinstance(ev, ResyncDropped):
                    stats["resync_bytes"] += ev.n_bytes
    trailing = framer.flush_resync()
    if trailing is not None:
        stats["resync_bytes"] += trailing.n_bytes
    return samples, stats


def write_raw(path: Path, values) -> None:
    """All samples, interleaved, as little-endian uint16 (the on-wire value)."""
    path.write_bytes(struct.pack(f"<{len(values)}H", *values))


def write_split(base: Path, values, channels: int) -> None:
    """One little-endian uint16 file per channel: <base>_chN.bin."""
    for ch in range(channels):
        ch_vals = values[ch::channels]
        out = base.with_name(f"{base.name}_ch{ch}.bin")
        out.write_bytes(struct.pack(f"<{len(ch_vals)}H", *ch_vals))
        print(f"  wrote {out}  ({len(ch_vals)} samples)")


def write_csv(path: Path, values, channels: int) -> None:
    """One row per sample: index,channel,value (header-free values, labelled)."""
    with path.open("w") as f:
        f.write("index,channel,value\n")
        for i, v in enumerate(values):
            f.write(f"{i},{i % channels},{v}\n")


def main() -> int:
    ap = argparse.ArgumentParser(description="Strip framing, export raw ADC samples.")
    ap.add_argument("input", type=Path, help="raw .bin capture (as written by Record)")
    ap.add_argument("--raw", type=Path, help="write all samples interleaved, uint16 LE")
    ap.add_argument("--split", type=Path, help="write per-channel files <base>_chN.bin (uint16 LE)")
    ap.add_argument("--csv", type=Path, help="write index,channel,value CSV")
    ap.add_argument("--channels", type=int, default=2, help="channel count for de-interleave (default 2)")
    args = ap.parse_args()

    if not (args.raw or args.split or args.csv):
        ap.error("pick at least one output: --raw, --split, or --csv")

    samples, stats = decode(args.input)

    # ---- Validation report (confirmed structure, not pattern-matching) -------
    count = stats["sample_count"]
    exported_bytes = len(samples) * 2  # uint16 each
    print(f"== {args.input} ==")
    if count is not None:
        payload = 2 * count
        packet_size = HEADER_SIZE + payload + CRC_SIZE
        print(
            f"  packet structure : {packet_size} B = "
            f"{HEADER_SIZE} header + {payload} payload + {CRC_SIZE} CRC "
            f"(overhead {HEADER_SIZE + CRC_SIZE} B)"
        )
        print(f"  samples / packet : {count}  ({payload} payload bytes/packet)")
    print(f"  packets processed: {stats['packets']}")
    print(
        f"  dropped/skipped  : bad_crc={stats['bad_crc']} "
        f"bad_version={stats['bad_version']} resync_bytes={stats['resync_bytes']}"
    )
    print(
        f"  exported         : {len(samples)} samples "
        f"({len(samples) // max(1, args.channels)} per channel x {args.channels}), "
        f"{exported_bytes} bytes payload"
    )
    if not samples:
        print("no valid packets decoded - nothing written.", file=sys.stderr)
        return 1

    if args.raw:
        write_raw(args.raw, samples)
        print(f"  wrote {args.raw}  ({len(samples)} samples, uint16 LE interleaved)")
    if args.split:
        write_split(args.split, samples, args.channels)
    if args.csv:
        write_csv(args.csv, samples, args.channels)
        print(f"  wrote {args.csv}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
