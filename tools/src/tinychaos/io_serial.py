"""Byte-chunk sources for the framer.

Two implementations of the same minimal interface (``__iter__`` yields
``bytes`` chunks):

- ``SerialSource``: live capture from a pyserial port.
- ``FileSource``: replay from a previously captured raw binary file.

Both are iterators; the CLI drives them with a single ``for chunk in source``
loop and feeds each chunk into the framer.
"""

from __future__ import annotations

from typing import Iterator, Optional


class FileSource:
    """Replay a captured raw binary file as a stream of chunks.

    Useful for offline replay and for the CLI smoke test, which exercises the
    end-to-end path without hardware.
    """

    def __init__(self, path: str, *, read_size: int = 4096) -> None:
        self._path = path
        self._read_size = int(read_size)
        if self._read_size <= 0:
            raise ValueError("read_size must be positive")

    def __iter__(self) -> Iterator[bytes]:
        with open(self._path, "rb") as f:
            while True:
                chunk = f.read(self._read_size)
                if not chunk:
                    return
                yield chunk


class SerialSource:
    """Live byte source backed by a pyserial port.

    The ``baudrate`` parameter is forwarded to pyserial but is ignored by the
    underlying transport when the device is a USB CDC virtual serial port. It
    is meaningful for the UART fallback path.
    """

    def __init__(
        self,
        port: str,
        baudrate: int = 921600,
        *,
        read_size: int = 4096,
        timeout: Optional[float] = 0.5,
    ) -> None:
        # Import here so the package does not hard-require pyserial at import
        # time. This makes the FileSource path usable in environments where
        # pyserial is not installed.
        import serial  # type: ignore[import-untyped]

        self._serial = serial.Serial(
            port=port,
            baudrate=int(baudrate),
            timeout=timeout,
        )
        self._read_size = int(read_size)
        if self._read_size <= 0:
            raise ValueError("read_size must be positive")

    def __iter__(self) -> Iterator[bytes]:
        try:
            while True:
                chunk = self._serial.read(self._read_size)
                if chunk:
                    yield chunk
                # If read timed out we just go around again. The caller can
                # break out by stopping iteration externally.
        finally:
            self.close()

    def close(self) -> None:
        try:
            self._serial.close()
        except Exception:
            pass
