"""The HAL refusal cycler must never repeat consecutively and must use every
line before any repeat (the user explicitly required non-repeating replies)."""


def test_hal_returns_a_known_line(bot):
    assert bot._next_hal() in bot.HAL_DENY


def test_hal_cycles_all_before_repeat(bot):
    n = len(bot.HAL_DENY)
    first_cycle = [bot._next_hal() for _ in range(n)]
    assert set(first_cycle) == set(bot.HAL_DENY)  # every line used once


def test_hal_never_repeats_consecutively(bot):
    prev = None
    for _ in range(200):
        cur = bot._next_hal()
        assert cur != prev, "HAL repeated the same refusal twice in a row"
        prev = cur


def test_hal_multiple_cycles_cover_all_each_time(bot):
    n = len(bot.HAL_DENY)
    for _ in range(5):
        cycle = [bot._next_hal() for _ in range(n)]
        assert sorted(cycle) == sorted(bot.HAL_DENY)
