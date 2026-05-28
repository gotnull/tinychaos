"""Offline analysis helpers for captured ADC samples.

Numpy-only, no plotting. The matplotlib views in tinychaos.plotting wrap
these where useful. The CLI exposes them via the ``--fft`` flag.

Functions:

- ``rolling_mean_std(samples, window)``        windowed mean and std.
- ``z_score_candidates(samples, window, threshold)``  indices where the
                                                       per-sample Z-score
                                                       exceeds threshold.
- ``histogram(samples, bits)``                  count per ADC code.
- ``fft_psd(samples, sample_rate_hz, window)``  frequency axis and PSD.
"""

from __future__ import annotations

from typing import Tuple

import numpy as np


def rolling_mean_std(samples: np.ndarray, window: int) -> Tuple[np.ndarray, np.ndarray]:
    """Compute rolling mean and std with the given window size.

    The output arrays have the same length as ``samples``. The first
    ``window - 1`` entries are computed from a shorter window
    (partial-window prefix), matching what a streaming consumer would see.
    """
    samples = np.asarray(samples, dtype=np.float64)
    n = samples.size
    if window <= 0:
        raise ValueError("window must be positive")
    if window > n:
        # Single global mean/std for everything.
        mean = np.full(n, samples.mean(), dtype=np.float64)
        std = np.full(n, samples.std(ddof=0), dtype=np.float64)
        return mean, std

    cumsum = np.concatenate([[0.0], np.cumsum(samples)])
    cumsumsq = np.concatenate([[0.0], np.cumsum(samples * samples)])

    # Effective window at each index: min(window, i+1).
    indices = np.arange(1, n + 1)
    eff = np.minimum(indices, window).astype(np.float64)
    starts = np.maximum(indices - window, 0)

    sums = cumsum[indices] - cumsum[starts]
    sumsq = cumsumsq[indices] - cumsumsq[starts]
    mean = sums / eff
    var = np.maximum(sumsq / eff - mean * mean, 0.0)
    std = np.sqrt(var)
    return mean, std


def z_score_candidates(
    samples: np.ndarray, window: int, threshold: float
) -> np.ndarray:
    """Return indices where the per-sample Z-score (relative to a rolling
    window) exceeds ``threshold`` in absolute value.

    Indices into ``samples``. The neutral term "candidate event" is used in
    docs and UI rather than any qualitative judgement about cause.
    """
    samples = np.asarray(samples, dtype=np.float64)
    mean, std = rolling_mean_std(samples, window)
    # Avoid divide-by-zero in regions where std is degenerate.
    safe_std = np.where(std > 0, std, np.inf)
    z = (samples - mean) / safe_std
    return np.where(np.abs(z) >= threshold)[0]


def histogram(samples: np.ndarray, bits: int = 12) -> np.ndarray:
    """Bin ``samples`` into ``2**bits`` counts, indexed by ADC code."""
    n_bins = 1 << bits
    counts = np.bincount(np.asarray(samples, dtype=np.int64), minlength=n_bins)
    if counts.size > n_bins:
        counts = counts[:n_bins]
    return counts


def fft_psd(
    samples: np.ndarray,
    sample_rate_hz: float,
    window: str = "hann",
) -> Tuple[np.ndarray, np.ndarray]:
    """Compute one-sided PSD estimate for ``samples``.

    Returns (freqs, psd) where ``freqs`` runs from 0 to sample_rate_hz / 2.

    Uses a simple periodogram (single window, no segmentation). For longer
    captures consider Welch's method; this function is intentionally minimal
    and stays self-contained on numpy.
    """
    samples = np.asarray(samples, dtype=np.float64)
    n = samples.size
    if n < 2:
        raise ValueError("need at least 2 samples for FFT")
    if sample_rate_hz <= 0:
        raise ValueError("sample_rate_hz must be positive")

    if window == "hann":
        w = np.hanning(n)
    elif window == "hamming":
        w = np.hamming(n)
    elif window in ("rect", "boxcar", "none"):
        w = np.ones(n)
    else:
        raise ValueError(f"unknown window: {window}")

    # Remove DC offset before windowing to keep the DC bin meaningful.
    detrended = samples - samples.mean()
    windowed = detrended * w
    # Window correction: divide by the squared L2 norm of the window.
    norm = (w * w).sum()

    spectrum = np.fft.rfft(windowed)
    psd = (np.abs(spectrum) ** 2) / (sample_rate_hz * norm)
    # One-sided PSD: double interior bins.
    if psd.size > 2:
        psd[1:-1] *= 2.0

    freqs = np.fft.rfftfreq(n, d=1.0 / sample_rate_hz)
    return freqs, psd
