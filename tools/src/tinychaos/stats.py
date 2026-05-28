"""Stats helpers for the live capture loop.

Three small components:

- RollingStats        a Welford-style mean and variance over a windowed
                      stream of samples. O(1) update per sample.
- DropTracker         observes SEQ values and counts gaps, with uint32
                      wraparound handling.
- RateEstimator       computes two independent sample-rate estimates from
                      the per-packet TIME_US and host wall-clock time.
- ChannelStats        per-channel min, max, mean, std maintained from the
                      RollingStats primitive.

These are intentionally simple. The plotting layer and the CLI summary read
from them.
"""

from __future__ import annotations

import math
import time
from dataclasses import dataclass, field
from typing import Optional

import numpy as np


# ---- RollingStats ----------------------------------------------------------


class RollingStats:
    """Numerically stable running mean and variance using Welford's algorithm.

    Tracks a global running statistic over an unbounded number of samples.
    For windowed views, instantiate one per window or feed a sliding subset.
    """

    __slots__ = ("_n", "_mean", "_m2", "_min", "_max")

    def __init__(self) -> None:
        self._n: int = 0
        self._mean: float = 0.0
        self._m2: float = 0.0
        self._min: float = math.inf
        self._max: float = -math.inf

    def add(self, value: float) -> None:
        self._n += 1
        delta = value - self._mean
        self._mean += delta / self._n
        delta2 = value - self._mean
        self._m2 += delta * delta2
        if value < self._min:
            self._min = float(value)
        if value > self._max:
            self._max = float(value)

    def add_array(self, values: np.ndarray) -> None:
        # Per-element loop for simplicity. Hot paths can be optimised later.
        for v in values:
            self.add(float(v))

    @property
    def count(self) -> int:
        return self._n

    @property
    def mean(self) -> float:
        return self._mean

    @property
    def variance(self) -> float:
        if self._n < 2:
            return 0.0
        return self._m2 / (self._n - 1)

    @property
    def std(self) -> float:
        return math.sqrt(self.variance)

    @property
    def min(self) -> float:
        return self._min if self._n else math.nan

    @property
    def max(self) -> float:
        return self._max if self._n else math.nan


# ---- DropTracker -----------------------------------------------------------


class DropTracker:
    """Track packet sequence drops in a uint32 stream.

    On the first call, the next expected SEQ is set to ``seq + 1``. On
    subsequent calls, the difference between the observed SEQ and the
    expected SEQ (mod 2^32) is added to the drop count. The "expected" is
    then updated to ``seq + 1``.

    A wraparound of 2^32 is handled correctly because all arithmetic is
    performed modulo 2^32 on integers.
    """

    _MASK = 0xFFFFFFFF

    def __init__(self) -> None:
        self._expected: Optional[int] = None
        self._drops: int = 0
        self._packets: int = 0

    def observe(self, seq: int) -> int:
        """Observe a packet sequence number.

        Returns the number of newly detected drops attributable to THIS
        observation (i.e. ``seq - expected`` when positive, else 0).
        """
        if not 0 <= seq <= self._MASK:
            raise ValueError(f"seq out of uint32 range: {seq}")
        self._packets += 1
        if self._expected is None:
            self._expected = (seq + 1) & self._MASK
            return 0
        gap = (seq - self._expected) & self._MASK
        # If gap is large (more than 2^31), interpret as out-of-order or
        # replayed packet, not a drop. We ignore those rather than treat
        # them as catastrophic.
        if gap > 0x7FFFFFFF:
            return 0
        self._drops += gap
        self._expected = (seq + 1) & self._MASK
        return gap

    @property
    def drops(self) -> int:
        return self._drops

    @property
    def packets(self) -> int:
        return self._packets


# ---- RateEstimator ---------------------------------------------------------


@dataclass
class RateEstimator:
    """Estimate sample rate from STM32 timestamps and from host wall clock.

    Both numbers are computed over a sliding window of (TIME_US, COUNT,
    host_time) observations. The window size in observations is configurable
    via ``window``.
    """

    window: int = 256

    _stm_times: list = field(default_factory=list)
    _host_times: list = field(default_factory=list)
    _counts: list = field(default_factory=list)

    _MASK = 0xFFFFFFFF

    def observe(self, time_us: int, sample_count: int, host_time: Optional[float] = None) -> None:
        if host_time is None:
            host_time = time.monotonic()
        self._stm_times.append(int(time_us) & self._MASK)
        self._counts.append(int(sample_count))
        self._host_times.append(float(host_time))
        if len(self._stm_times) > self.window:
            self._stm_times.pop(0)
            self._counts.pop(0)
            self._host_times.pop(0)

    def stm32_rate_hz(self) -> Optional[float]:
        if len(self._stm_times) < 2:
            return None
        # Sum sample counts from second observation onwards: each observation
        # represents the samples since the previous TIME_US tick.
        samples = sum(self._counts[1:])
        # Walk the deltas, summing in microseconds with wraparound handling.
        total_us = 0
        for prev, curr in zip(self._stm_times, self._stm_times[1:]):
            delta = (curr - prev) & self._MASK
            # Reject implausibly large deltas (treated as bogus, skip)
            if delta > 0x7FFFFFFF:
                return None
            total_us += delta
        if total_us == 0:
            return None
        return samples * 1_000_000.0 / total_us

    def host_rate_hz(self) -> Optional[float]:
        if len(self._host_times) < 2:
            return None
        samples = sum(self._counts[1:])
        elapsed = self._host_times[-1] - self._host_times[0]
        if elapsed <= 0:
            return None
        return samples / elapsed


# ---- ChannelStats ----------------------------------------------------------


class ChannelStats:
    """Maintain rolling per-channel statistics.

    Channels are addressed by integer index. The stats are computed over the
    entire stream observed so far (not windowed); a windowed variant can be
    added if needed.
    """

    def __init__(self, channel_count: int) -> None:
        self._stats = [RollingStats() for _ in range(channel_count)]

    @property
    def channel_count(self) -> int:
        return len(self._stats)

    def add_sample(self, channel_index: int, value: int) -> None:
        self._stats[channel_index].add(float(value))

    def add_packet_samples(self, samples_per_channel: dict[int, np.ndarray]) -> None:
        for idx, arr in samples_per_channel.items():
            self._stats[idx].add_array(arr)

    def get(self, channel_index: int) -> RollingStats:
        return self._stats[channel_index]
