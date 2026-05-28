"""Tests for stats, drop tracking, and rate estimation."""

from __future__ import annotations

import math

import numpy as np
import pytest

from tinychaos.stats import ChannelStats, DropTracker, RateEstimator, RollingStats


# ---- RollingStats ----------------------------------------------------------


def test_rolling_stats_matches_numpy():
    rng = np.random.default_rng(123)
    data = rng.normal(2000.0, 50.0, size=10_000)
    s = RollingStats()
    for v in data:
        s.add(float(v))
    np.testing.assert_allclose(s.mean, float(np.mean(data)), rtol=0, atol=1e-9)
    np.testing.assert_allclose(s.std, float(np.std(data, ddof=1)), rtol=0, atol=1e-9)
    assert s.min == float(data.min())
    assert s.max == float(data.max())
    assert s.count == len(data)


def test_rolling_stats_zero_samples():
    s = RollingStats()
    assert s.count == 0
    assert math.isnan(s.min)
    assert math.isnan(s.max)
    assert s.mean == 0.0
    assert s.variance == 0.0
    assert s.std == 0.0


def test_rolling_stats_single_sample():
    s = RollingStats()
    s.add(7.5)
    assert s.count == 1
    assert s.mean == 7.5
    assert s.min == 7.5
    assert s.max == 7.5
    assert s.variance == 0.0  # n-1 = 0, defined as 0 by convention here


# ---- DropTracker -----------------------------------------------------------


def test_drop_tracker_no_drops():
    d = DropTracker()
    for seq in range(0, 100):
        assert d.observe(seq) == 0
    assert d.drops == 0
    assert d.packets == 100


def test_drop_tracker_simple_gap():
    d = DropTracker()
    d.observe(0)
    d.observe(1)
    new = d.observe(5)  # skipped 2, 3, 4
    assert new == 3
    assert d.drops == 3
    d.observe(6)
    assert d.drops == 3


def test_drop_tracker_wraparound():
    d = DropTracker()
    d.observe(0xFFFFFFFE)
    d.observe(0xFFFFFFFF)
    new = d.observe(0)  # wrap
    assert new == 0
    assert d.drops == 0
    new = d.observe(2)  # skipped 1
    assert new == 1


def test_drop_tracker_ignores_out_of_order():
    d = DropTracker()
    d.observe(10)
    new = d.observe(5)  # backwards, treat as out-of-order, do not count drops
    assert new == 0
    assert d.drops == 0


def test_drop_tracker_rejects_out_of_range():
    d = DropTracker()
    with pytest.raises(ValueError):
        d.observe(-1)
    with pytest.raises(ValueError):
        d.observe(0x1_0000_0000)


# ---- RateEstimator ---------------------------------------------------------


def test_rate_estimator_constant_rate():
    """At 10 kHz with 256 samples per packet, time_us delta is 25600 us."""
    r = RateEstimator(window=64)
    base_time = 1_000_000_000.0
    t_us = 0
    host_t = base_time
    sample_count = 256
    delta_us = 25600
    host_delta = 0.0256
    for _ in range(32):
        r.observe(t_us, sample_count, host_time=host_t)
        t_us += delta_us
        host_t += host_delta
    stm = r.stm32_rate_hz()
    host = r.host_rate_hz()
    assert stm == pytest.approx(10_000.0, rel=1e-6)
    assert host == pytest.approx(10_000.0, rel=1e-3)


def test_rate_estimator_handles_time_us_wraparound():
    r = RateEstimator(window=10)
    base = 1.0
    t_us = (1 << 32) - 100_000  # near wrap
    for i in range(10):
        r.observe(t_us & 0xFFFFFFFF, 256, host_time=base)
        t_us += 25600
        base += 0.0256
    stm = r.stm32_rate_hz()
    assert stm == pytest.approx(10_000.0, rel=1e-6)


def test_rate_estimator_returns_none_with_too_few_samples():
    r = RateEstimator()
    assert r.stm32_rate_hz() is None
    assert r.host_rate_hz() is None
    r.observe(0, 256, host_time=1.0)
    assert r.stm32_rate_hz() is None
    assert r.host_rate_hz() is None


# ---- ChannelStats ----------------------------------------------------------


def test_channel_stats_per_channel():
    cs = ChannelStats(channel_count=2)
    # Channel 0 values rise from 0 to 99, channel 1 stays at 2000.
    for i in range(100):
        cs.add_sample(0, i)
        cs.add_sample(1, 2000)
    s0 = cs.get(0)
    s1 = cs.get(1)
    assert s0.mean == pytest.approx(49.5)
    assert s1.mean == pytest.approx(2000.0)
    assert s0.std > 0
    assert s1.std == 0.0
    assert s0.min == 0.0
    assert s0.max == 99.0
