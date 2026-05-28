"""Optional live plotting via matplotlib.

The CLI imports this module lazily. If matplotlib is not installed,
``import tinychaos.plotting`` raises ``ImportError`` and the CLI catches
that and prints a friendly diagnostic instead of crashing.

Public surface:

- ``PlottingUnavailableError``    raised by every helper when the optional
                                  matplotlib dependency cannot be loaded.
- ``LivePlotter``                  three-panel live view: waveform per
                                  channel, histogram, rolling stats.
- ``plot_fft(freqs, psd)``         offline PSD plot.

Both ``LivePlotter`` and ``plot_fft`` import matplotlib at the top of this
module. The lazy gate is: if you can ``import tinychaos.plotting``, the rest
is available; if matplotlib is missing, the import fails and callers must
handle ``ImportError`` (the CLI does).
"""

from __future__ import annotations

import sys
from collections import deque
from typing import Deque, Iterable, Optional

import numpy as np

try:
    import matplotlib

    matplotlib.use("TkAgg")  # interactive backend on macOS by default
    import matplotlib.pyplot as plt
except ImportError as e:  # pragma: no cover - exercised by tests via patching
    raise ImportError(
        "matplotlib is required for tinychaos.plotting. "
        "Install the 'plot' extra: pip install -e .[plot]"
    ) from e


class PlottingUnavailableError(RuntimeError):
    """Kept for API symmetry. Currently unused because ImportError is the
    canonical signal that the optional dep is missing.
    """


class LivePlotter:
    """A small live three-panel matplotlib view.

    Panels:
      1. Waveform: a rolling N-sample window per channel.
      2. Histogram: a running histogram of all samples so far (per channel).
      3. Stats: a text panel showing per-channel mean and std.

    The plotter expects to be fed packets via ``feed_packet``. It throttles
    redraws to avoid swamping the matplotlib event loop.
    """

    def __init__(
        self,
        *,
        channel_count: int = 2,
        waveform_window: int = 4096,
        histogram_bits: int = 12,
        update_every_n_packets: int = 4,
    ) -> None:
        self._channel_count = int(channel_count)
        self._waveform_window = int(waveform_window)
        self._bits = int(histogram_bits)
        self._packets_since_update = 0
        self._update_every = int(update_every_n_packets)
        self._waveforms: list[Deque[int]] = [
            deque(maxlen=self._waveform_window) for _ in range(self._channel_count)
        ]
        self._histograms: list[np.ndarray] = [
            np.zeros(1 << self._bits, dtype=np.int64) for _ in range(self._channel_count)
        ]
        self._sample_count = [0 for _ in range(self._channel_count)]
        self._sample_sum = [0 for _ in range(self._channel_count)]
        self._sample_sumsq = [0 for _ in range(self._channel_count)]

        self._fig, self._axes = plt.subplots(3, 1, figsize=(9, 7))
        self._fig.suptitle("tinychaos live capture")
        self._lines_wave = [
            self._axes[0].plot([], [], label=f"channel {i}")[0]
            for i in range(self._channel_count)
        ]
        self._axes[0].set_title("waveform")
        self._axes[0].set_xlabel("sample index")
        self._axes[0].set_ylabel("ADC code")
        self._axes[0].set_ylim(0, 1 << self._bits)
        self._axes[0].legend(loc="upper right")

        self._lines_hist = [
            self._axes[1].plot([], [], label=f"channel {i}")[0]
            for i in range(self._channel_count)
        ]
        self._axes[1].set_title("histogram (cumulative)")
        self._axes[1].set_xlabel("ADC code")
        self._axes[1].set_ylabel("count")
        self._axes[1].legend(loc="upper right")

        self._axes[2].axis("off")
        self._stats_text = self._axes[2].text(
            0.01,
            0.95,
            "",
            va="top",
            family="monospace",
            transform=self._axes[2].transAxes,
        )

        plt.tight_layout(rect=(0, 0, 1, 0.96))
        plt.show(block=False)

    def feed_packet(self, packet, *, channel_count: Optional[int] = None) -> None:
        cc = channel_count if channel_count is not None else self._channel_count
        for idx, value in enumerate(packet.samples):
            ch = idx % cc
            if ch >= self._channel_count:
                continue
            v = int(value)
            self._waveforms[ch].append(v)
            self._histograms[ch][v] += 1
            self._sample_count[ch] += 1
            self._sample_sum[ch] += v
            self._sample_sumsq[ch] += v * v

        self._packets_since_update += 1
        if self._packets_since_update >= self._update_every:
            self._packets_since_update = 0
            self._redraw()

    def _redraw(self) -> None:
        max_y_count = 1
        for ch in range(self._channel_count):
            arr = np.fromiter(self._waveforms[ch], dtype=np.int32)
            x = np.arange(arr.size)
            self._lines_wave[ch].set_data(x, arr)
            if arr.size:
                self._axes[0].set_xlim(0, max(arr.size, 1))
            counts = self._histograms[ch]
            self._lines_hist[ch].set_data(np.arange(counts.size), counts)
            if counts.max() > max_y_count:
                max_y_count = int(counts.max())
        self._axes[1].set_xlim(0, 1 << self._bits)
        self._axes[1].set_ylim(0, max(max_y_count, 1) * 1.1)

        # Stats panel
        lines = []
        for ch in range(self._channel_count):
            n = self._sample_count[ch]
            if n == 0:
                lines.append(f"channel {ch}: no samples yet")
                continue
            mean = self._sample_sum[ch] / n
            var = max(self._sample_sumsq[ch] / n - mean * mean, 0.0)
            std = float(np.sqrt(var))
            lines.append(
                f"channel {ch}: n={n:>10d}  mean={mean:8.2f}  std={std:8.3f}"
            )
        self._stats_text.set_text("\n".join(lines))

        try:
            self._fig.canvas.draw_idle()
            self._fig.canvas.flush_events()
        except Exception as e:  # pragma: no cover - protective
            print(f"warning: plot redraw failed: {e}", file=sys.stderr)

    def close(self) -> None:
        try:
            plt.close(self._fig)
        except Exception:
            pass


def plot_fft(freqs: np.ndarray, psd: np.ndarray, *, title: str = "PSD") -> None:
    """Show an offline PSD plot in a blocking window."""
    fig, ax = plt.subplots(figsize=(9, 4))
    ax.semilogy(freqs, psd)
    ax.set_xlabel("frequency (Hz)")
    ax.set_ylabel("PSD")
    ax.set_title(title)
    ax.grid(True, which="both", alpha=0.3)
    plt.tight_layout()
    plt.show()
