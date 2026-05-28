"""Tests for the offline analysis helpers."""

from __future__ import annotations

import numpy as np
import pytest

from tinychaos.analysis import (
    fft_psd,
    histogram,
    rolling_mean_std,
    z_score_candidates,
)


# ---- rolling_mean_std ------------------------------------------------------


def test_rolling_mean_std_matches_full_window_numpy():
    rng = np.random.default_rng(42)
    samples = rng.normal(2000, 50, size=512)
    window = 32
    mean, std = rolling_mean_std(samples, window)
    # Spot-check against a direct numpy slice at a few indices.
    for i in (window, window + 17, len(samples) - 1):
        chunk = samples[i + 1 - window : i + 1]
        np.testing.assert_allclose(mean[i], chunk.mean(), atol=1e-9)
        np.testing.assert_allclose(std[i], chunk.std(ddof=0), atol=1e-9)


def test_rolling_mean_std_short_window_returns_global():
    samples = np.array([1.0, 2.0, 3.0, 4.0, 5.0])
    mean, std = rolling_mean_std(samples, window=20)
    assert np.allclose(mean, samples.mean())
    assert np.allclose(std, samples.std(ddof=0))


def test_rolling_mean_std_rejects_invalid_window():
    with pytest.raises(ValueError):
        rolling_mean_std(np.array([1.0, 2.0]), window=0)


# ---- z_score_candidates ----------------------------------------------------


def test_z_score_candidates_finds_known_spike():
    samples = np.full(1024, 2000.0)
    samples += np.random.default_rng(1).normal(0, 1.0, size=1024)
    samples[500] = 2100.0  # huge spike well above 1 sigma
    candidates = z_score_candidates(samples, window=64, threshold=5.0)
    assert 500 in candidates


def test_z_score_candidates_quiet_input_returns_empty():
    samples = np.full(1024, 2000.0)
    candidates = z_score_candidates(samples, window=64, threshold=3.0)
    assert candidates.size == 0


# ---- histogram -------------------------------------------------------------


def test_histogram_shape_and_counts():
    samples = np.array([0, 1, 1, 2, 2, 2, 4095])
    h = histogram(samples, bits=12)
    assert h.size == 4096
    assert h[0] == 1
    assert h[1] == 2
    assert h[2] == 3
    assert h[4095] == 1


def test_histogram_clips_too_large_values():
    # bincount will allocate up to max(value)+1 bins; we truncate to 2^bits.
    samples = np.array([0, 1, 1])
    h = histogram(samples, bits=8)
    assert h.size == 256


# ---- fft_psd ---------------------------------------------------------------


def test_fft_psd_peaks_at_input_frequency():
    sample_rate = 10_000.0
    duration = 1.0
    n = int(sample_rate * duration)
    t = np.arange(n) / sample_rate
    freq = 1234.0
    samples = np.sin(2 * np.pi * freq * t)
    freqs, psd = fft_psd(samples, sample_rate)
    # Peak bin should correspond to the input frequency to within bin width.
    peak_idx = int(np.argmax(psd))
    peak_freq = freqs[peak_idx]
    bin_width = sample_rate / n
    assert abs(peak_freq - freq) <= bin_width


def test_fft_psd_freq_axis_runs_0_to_nyquist():
    samples = np.zeros(1024) + 1e-9
    freqs, psd = fft_psd(samples, 10_000.0)
    assert freqs[0] == 0.0
    assert pytest.approx(freqs[-1], rel=1e-6) == 5000.0
    assert psd.size == freqs.size


def test_fft_psd_rejects_short_input():
    with pytest.raises(ValueError):
        fft_psd(np.array([1.0]), 1000.0)


def test_fft_psd_rejects_nonpositive_rate():
    with pytest.raises(ValueError):
        fft_psd(np.zeros(64), 0.0)


def test_fft_psd_rejects_unknown_window():
    with pytest.raises(ValueError):
        fft_psd(np.zeros(64), 1000.0, window="not-a-window")
