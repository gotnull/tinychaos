"""End-to-end smoke test of the CLI via --replay against a generated file.

No hardware required. We build a synthetic packet stream, write it to a temp
file, run the CLI in replay mode with --csv, and verify both that the CLI
exits 0 and that the CSV contains the expected rows.
"""

from __future__ import annotations

import csv
from pathlib import Path

from tinychaos.cli import main as cli_main
from tinychaos.protocol import encode_packet


def _build_stream(packets):
    return b"".join(packets)


def test_cli_replay_writes_csv(tmp_path, capsys):
    # Build 10 packets, 8 samples each, no gaps.
    packets = []
    for i in range(10):
        samples = [(i * 8 + n) & 0xFFF for n in range(8)]
        packets.append(encode_packet(seq=i, time_us=i * 1000, samples=samples))
    bin_path = tmp_path / "synthetic.bin"
    bin_path.write_bytes(_build_stream(packets))

    csv_path = tmp_path / "out.csv"
    rc = cli_main(
        [
            "--replay",
            str(bin_path),
            "--csv",
            str(csv_path),
            "--quiet",
            "--validation-label",
            "test",
            "--channels",
            "2",
        ]
    )
    assert rc == 0
    assert csv_path.exists()

    with csv_path.open(newline="") as f:
        rows = list(csv.reader(f))

    header = rows[0]
    assert header == [
        "host_time",
        "packet_seq",
        "stm32_time_us",
        "sample_index",
        "channel_index",
        "adc_value",
        "validation_label",
    ]
    body = rows[1:]
    # 10 packets * 8 samples = 80 rows.
    assert len(body) == 80
    # validation_label is propagated.
    assert all(r[-1] == "test" for r in body)
    # channel_index alternates 0,1,0,1,...
    for n, row in enumerate(body):
        assert int(row[4]) == n % 2


def test_cli_replay_with_gap_detects_drops(tmp_path, capsys):
    packets = []
    for i in range(5):
        if i == 2:
            continue  # drop one
        samples = [n & 0xFFF for n in range(4)]
        packets.append(encode_packet(seq=i, time_us=i * 1000, samples=samples))
    bin_path = tmp_path / "gap.bin"
    bin_path.write_bytes(_build_stream(packets))

    rc = cli_main(["--replay", str(bin_path), "--quiet", "--channels", "2"])
    assert rc == 0
    captured = capsys.readouterr().out
    assert "packets received        : 4" in captured
    assert "dropped packets         : 1" in captured


def test_cli_handles_missing_file(tmp_path, capsys):
    rc = cli_main(["--replay", str(tmp_path / "does-not-exist.bin"), "--quiet"])
    assert rc == 2


def test_cli_replay_with_leading_garbage_resyncs(tmp_path, capsys):
    # 32 random-looking bytes (avoiding the magic) then a single valid packet.
    garbage = bytes((b * 7 + 3) & 0xFF for b in range(32))
    garbage = garbage.replace(b"\xDA\x7A", b"\xDA\x00")
    pkt = encode_packet(seq=0, time_us=0, samples=[1, 2, 3, 4])
    bin_path = tmp_path / "leading.bin"
    bin_path.write_bytes(garbage + pkt)

    rc = cli_main(["--replay", str(bin_path), "--quiet", "--channels", "2"])
    assert rc == 0
    captured = capsys.readouterr().out
    assert "packets received        : 1" in captured
    assert "resync bytes skipped    : 32" in captured
