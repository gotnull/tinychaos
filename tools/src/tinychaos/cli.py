"""tinychaos capture CLI.

Driven by argparse. Two source modes (serial and replay) and two exporter
modes (CSV and raw binary). Optional live plotting is gated behind the
``plot`` extra; if matplotlib is not installed the CLI prints a friendly
diagnostic and exits cleanly.

Examples:

    python -m tinychaos.cli --port /dev/tty.usbmodem... --csv out.csv
    python -m tinychaos.cli --port /dev/tty.usbmodem... --duration 60 \
        --csv labels/zener.csv --validation-label zener
    python -m tinychaos.cli --replay capture.bin --csv replay.csv
"""

from __future__ import annotations

import argparse
import sys
import time
from contextlib import ExitStack
from typing import Iterator, Optional

from .exporters import CsvExporter, RawBinaryExporter
from .framer import (
    BadCrc,
    BadVersion,
    Framer,
    PacketReceived,
    ResyncDropped,
)
from .io_serial import FileSource
from .protocol import encode_packet
from .stats import ChannelStats, DropTracker, RateEstimator


def build_argparser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="tinychaos",
        description=(
            "Read framed binary packets from the tinychaos firmware over USB CDC or UART, "
            "validate CRCs, track drops, and export to CSV or raw binary."
        ),
    )
    src = parser.add_mutually_exclusive_group(required=True)
    src.add_argument(
        "--port",
        help="Serial port path, e.g. /dev/tty.usbmodemXXXX",
    )
    src.add_argument(
        "--replay",
        help="Replay a previously captured raw binary file instead of opening a serial port.",
    )
    parser.add_argument(
        "--baud",
        type=int,
        default=921600,
        help=(
            "Baud rate. Ignored over USB CDC; used by the UART fallback. "
            "Default 921600."
        ),
    )
    parser.add_argument("--csv", help="Write decoded samples to this CSV file.")
    parser.add_argument(
        "--raw-bin",
        help="Append each accepted packet's bytes to this file.",
    )
    parser.add_argument(
        "--validation-label",
        default="",
        help="Label written to the validation_label CSV column (e.g. shorted, divider, zener).",
    )
    parser.add_argument(
        "--duration",
        type=float,
        default=None,
        help="Stop after this many seconds of host wall-clock time.",
    )
    parser.add_argument(
        "--channels",
        type=int,
        default=2,
        help="Number of interleaved channels in each packet. Default 2.",
    )
    parser.add_argument(
        "--quiet",
        action="store_true",
        help="Suppress periodic per-second progress lines.",
    )
    parser.add_argument(
        "--plot",
        action="store_true",
        help="Open a live matplotlib panel. Requires the 'plot' extra.",
    )
    parser.add_argument(
        "--fft",
        action="store_true",
        help="At end of capture, compute and display an FFT PSD.",
    )
    parser.add_argument(
        "--read-size",
        type=int,
        default=4096,
        help="Bytes per read from the source. Default 4096.",
    )
    return parser


def open_source(args: argparse.Namespace) -> Iterator[bytes]:
    if args.replay:
        return iter(FileSource(args.replay, read_size=args.read_size))
    # Lazy import to avoid pyserial being required on the replay path.
    from .io_serial import SerialSource

    return iter(SerialSource(args.port, baudrate=args.baud, read_size=args.read_size))


def open_plotter(args: argparse.Namespace):
    """Open a LivePlotter or return None. Prints a friendly message on failure."""
    if not args.plot:
        return None
    try:
        from .plotting import LivePlotter
    except ImportError as e:
        print(
            f"warning: --plot requested but matplotlib is not installed ({e}). "
            "Install with: pip install -e .[plot]",
            file=sys.stderr,
        )
        return None
    return LivePlotter(channel_count=args.channels)


def run(args: argparse.Namespace) -> int:
    framer = Framer()
    drops = DropTracker()
    rate = RateEstimator()
    channel_stats = ChannelStats(channel_count=args.channels)

    # For --fft we keep a bounded ring of samples for the zener channel
    # (channel 0). 65536 samples is plenty for a meaningful PSD without
    # blowing host memory.
    fft_buffer_size = 65_536
    fft_buffers: dict[int, list[int]] = {i: [] for i in range(args.channels)}

    counters = {
        "packets": 0,
        "bad_crc": 0,
        "bad_version": 0,
        "resync_bytes": 0,
        "samples": 0,
    }

    start = time.monotonic()
    last_progress = start

    with ExitStack() as stack:
        csv_writer: Optional[CsvExporter] = None
        raw_writer: Optional[RawBinaryExporter] = None
        if args.csv:
            csv_writer = stack.enter_context(
                CsvExporter(
                    args.csv,
                    channel_count=args.channels,
                    validation_label=args.validation_label,
                )
            )
        if args.raw_bin:
            raw_writer = stack.enter_context(RawBinaryExporter(args.raw_bin))

        plotter = open_plotter(args)
        if plotter is not None:
            stack.callback(plotter.close)

        # For --replay, eagerly verify the file exists so we can report a
        # clean error code rather than a partial summary.
        if args.replay:
            from pathlib import Path

            if not Path(args.replay).exists():
                print(f"error: replay file not found: {args.replay}", file=sys.stderr)
                return 2

        try:
            source = open_source(args)
        except FileNotFoundError as e:
            print(f"error: source not found: {e}", file=sys.stderr)
            return 2
        except Exception as e:  # pyserial errors etc.
            print(f"error: failed to open source: {e}", file=sys.stderr)
            return 2

        for chunk in source:
            for event in framer.feed(chunk):
                if isinstance(event, PacketReceived):
                    p = event.packet
                    drops.observe(p.header.seq)
                    rate.observe(p.header.time_us, p.header.count, host_time=time.monotonic())
                    counters["packets"] += 1
                    counters["samples"] += p.header.count
                    for idx, value in enumerate(p.samples):
                        ch = idx % args.channels
                        channel_stats.add_sample(ch, value)
                        if args.fft and len(fft_buffers[ch]) < fft_buffer_size:
                            fft_buffers[ch].append(int(value))
                    if csv_writer is not None:
                        csv_writer.write_packet(p)
                    if raw_writer is not None:
                        raw_writer.write_packet_bytes(_reencode(p))
                    if plotter is not None:
                        plotter.feed_packet(p, channel_count=args.channels)
                elif isinstance(event, BadCrc):
                    counters["bad_crc"] += 1
                elif isinstance(event, ResyncDropped):
                    counters["resync_bytes"] += event.n_bytes
                elif isinstance(event, BadVersion):
                    counters["bad_version"] += 1

            # Periodic progress
            now = time.monotonic()
            if not args.quiet and now - last_progress >= 1.0:
                print(_progress_line(now - start, counters, drops, rate), file=sys.stderr)
                last_progress = now

            if args.duration is not None and (time.monotonic() - start) >= args.duration:
                break

        # Trailing resync flush
        trailing = framer.flush_resync()
        if trailing is not None:
            counters["resync_bytes"] += trailing.n_bytes

    elapsed = time.monotonic() - start
    print(_summary(elapsed, counters, drops, rate, channel_stats))

    if args.fft:
        try:
            import numpy as np

            from .analysis import fft_psd
        except ImportError as e:
            print(
                f"warning: --fft requested but numpy is missing ({e}).",
                file=sys.stderr,
            )
            return 0

        stm_hz = rate.stm32_rate_hz()
        if stm_hz is None:
            print(
                "warning: --fft requested but sample rate could not be estimated.",
                file=sys.stderr,
            )
            return 0

        # The packets carry samples for ALL channels combined. The effective
        # per-channel sample rate is stm_hz / channels.
        per_channel_hz = stm_hz / args.channels if args.channels > 0 else stm_hz

        for ch, buf in fft_buffers.items():
            if len(buf) < 2:
                continue
            freqs, psd = fft_psd(np.asarray(buf, dtype=np.float64), per_channel_hz)
            peak_idx = int(np.argmax(psd))
            print(
                f"channel {ch} FFT: n={len(buf)} fs={per_channel_hz:.1f}Hz "
                f"peak={freqs[peak_idx]:.1f}Hz peak_psd={psd[peak_idx]:.3e}"
            )
            try:
                from .plotting import plot_fft

                plot_fft(freqs, psd, title=f"channel {ch} PSD")
            except ImportError:
                # matplotlib not installed; the numeric summary above is
                # already useful on its own.
                pass

    return 0


def _reencode(packet) -> bytes:
    """Re-encode a Packet as bytes for raw binary archiving.

    Round-trips through the protocol module, which guarantees the same on-
    wire layout as the firmware produced.
    """
    return encode_packet(
        seq=packet.header.seq,
        time_us=packet.header.time_us,
        samples=packet.samples,
        version=packet.header.version,
        flags=packet.header.flags,
    )


def _progress_line(elapsed: float, counters, drops, rate) -> str:
    stm_hz = rate.stm32_rate_hz() or 0.0
    host_hz = rate.host_rate_hz() or 0.0
    return (
        f"[{elapsed:6.1f}s] pkts={counters['packets']:6d} "
        f"bad_crc={counters['bad_crc']:3d} "
        f"drops={drops.drops:4d} resync={counters['resync_bytes']:5d}B "
        f"stm32={stm_hz:7.1f}Hz host={host_hz:7.1f}Hz"
    )


def _summary(elapsed, counters, drops, rate, channel_stats) -> str:
    lines = [
        "",
        "Capture summary",
        "---------------",
        f"  elapsed                 : {elapsed:.2f} s",
        f"  packets received        : {counters['packets']}",
        f"  bad CRC                 : {counters['bad_crc']}",
        f"  bad version             : {counters['bad_version']}",
        f"  dropped packets         : {drops.drops}",
        f"  resync bytes skipped    : {counters['resync_bytes']}",
        f"  samples received        : {counters['samples']}",
    ]
    stm_hz = rate.stm32_rate_hz()
    host_hz = rate.host_rate_hz()
    lines.append(
        f"  STM32-derived rate      : {stm_hz:.1f} Hz" if stm_hz else "  STM32-derived rate      : (insufficient data)"
    )
    lines.append(
        f"  host-derived rate       : {host_hz:.1f} Hz" if host_hz else "  host-derived rate       : (insufficient data)"
    )
    lines.append("")
    lines.append("Per-channel statistics")
    lines.append("----------------------")
    for ch in range(channel_stats.channel_count):
        s = channel_stats.get(ch)
        if s.count == 0:
            lines.append(f"  channel {ch}: no samples")
            continue
        lines.append(
            f"  channel {ch}: n={s.count} min={s.min:.1f} max={s.max:.1f} "
            f"mean={s.mean:.2f} std={s.std:.3f}"
        )
    return "\n".join(lines)


def main(argv: Optional[list[str]] = None) -> int:
    parser = build_argparser()
    args = parser.parse_args(argv)
    try:
        return run(args)
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
