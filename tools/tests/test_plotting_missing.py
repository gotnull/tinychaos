"""Verify the CLI does not crash when matplotlib is unavailable.

We simulate "matplotlib not installed" by blocking it from sys.modules and
clearing tinychaos.plotting from the module cache, then exercising the
``open_plotter`` helper that the CLI uses.
"""

from __future__ import annotations

import builtins
import sys

import pytest

import tinychaos.cli as cli


def test_cli_open_plotter_handles_missing_matplotlib(monkeypatch, capsys):
    # Force the import of tinychaos.plotting to fail by intercepting
    # __import__ for matplotlib. We also evict any cached versions of the
    # plotting module and matplotlib from sys.modules.
    for mod in list(sys.modules.keys()):
        if mod == "tinychaos.plotting" or mod.startswith("matplotlib"):
            monkeypatch.delitem(sys.modules, mod, raising=False)

    real_import = builtins.__import__

    def fake_import(name, *args, **kwargs):
        if name == "matplotlib" or name.startswith("matplotlib."):
            raise ImportError("simulated: matplotlib not installed")
        return real_import(name, *args, **kwargs)

    monkeypatch.setattr(builtins, "__import__", fake_import)

    # Construct minimal args namespace as expected by open_plotter.
    class Args:
        plot = True
        channels = 2

    plotter = cli.open_plotter(Args())
    assert plotter is None, "open_plotter should fall back to None when matplotlib is missing"

    captured = capsys.readouterr()
    assert "warning" in captured.err.lower()
    assert "matplotlib" in captured.err.lower()


def test_cli_does_not_open_plotter_when_flag_off():
    class Args:
        plot = False
        channels = 2

    plotter = cli.open_plotter(Args())
    assert plotter is None
