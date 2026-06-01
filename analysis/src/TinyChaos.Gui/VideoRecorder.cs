using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace TinyChaos.Gui;

/// <summary>
/// Records an Avalonia control (e.g. the whole app content) to a high-quality
/// H.264 MP4 by piping raw frames to ffmpeg. Unlike GIF (capped at 256 colours
/// per frame), this keeps full 24-bit colour, so the captured waveform is
/// faithful, and the resulting MP4 (yuv420p + faststart) plays inline in
/// Telegram, Messages, QuickTime, browsers, etc. without re-encoding.
///
/// Design goals, in priority order:
///   1. Never freeze the UI. <see cref="RenderTargetBitmap"/> must run on the
///      UI thread, so we capture one frame per <see cref="DispatcherTimer"/>
///      tick - the dispatcher keeps pumping between ticks, so the live waveform
///      animates and input stays responsive.
///   2. Bounded memory. A background consumer drains captured frames into
///      ffmpeg's stdin concurrently with capture, so we never hold the whole
///      clip in RAM (frames are encoded as fast as they arrive).
///   3. Quality. libx264 at crf 14 is visually lossless; we avoid any downscale
///      unless the source is enormous.
///
/// Avalonia's render target is BGRA8888, which we feed to ffmpeg as
/// "-pixel_format bgra" raw video. ffmpeg converts to yuv420p for playback.
/// </summary>
public static class VideoRecorder
{
    private static string? s_ffmpegPath;
    private static bool s_probed;

    /// <summary>Absolute path to ffmpeg, or null if it cannot be found. Cached.</summary>
    public static string? FfmpegPath
    {
        get
        {
            if (s_probed)
            {
                return s_ffmpegPath;
            }
            s_probed = true;
            s_ffmpegPath = LocateFfmpeg();
            return s_ffmpegPath;
        }
    }

    /// <summary>
    /// Capture <paramref name="target"/> for <paramref name="duration"/> at
    /// <paramref name="fps"/> and write an MP4 to <paramref name="outputPath"/>.
    /// Returns the path on success, or null if the control has no rendered size.
    /// Throws if ffmpeg is missing (check <see cref="FfmpegPath"/> first) or if
    /// ffmpeg exits non-zero. Must be called from the UI thread.
    /// </summary>
    /// <param name="maxWidth">Downscale (preserving aspect) only if the source
    /// is wider than this. Default keeps typical window sizes at native res.</param>
    /// <param name="onProgress">Optional 0..1 capture-progress callback (UI thread).</param>
    public static async Task<string?> RecordAsync(
        Control target,
        TimeSpan duration,
        int fps,
        string outputPath,
        int maxWidth = 1920,
        Action<double>? onProgress = null)
    {
        string ffmpeg = FfmpegPath
            ?? throw new FileNotFoundException("ffmpeg not found on PATH or in common locations.");

        // Capture at the display's render scaling (2x on Retina) so the clip is
        // crisp and, crucially, so the bitmap is big enough to hold the whole
        // control - sizing to logical points on a Retina screen renders the 2x
        // content into a half-size buffer and clips the bottom/right.
        var topLevel = TopLevel.GetTopLevel(target);
        double scaling = topLevel?.RenderScaling ?? 1.0;

        // For a window, ClientSize is the renderable area; for a child control
        // its Bounds size. Either way, multiply by scaling for device pixels.
        Size logical = topLevel is { } tl && ReferenceEquals(tl, target)
            ? tl.ClientSize
            : target.Bounds.Size;
        int width = (int)Math.Ceiling(logical.Width * scaling);
        int height = (int)Math.Ceiling(logical.Height * scaling);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // Avalonia's render-target pixel order is platform-dependent (BGRA on
        // Windows/Linux, RGBA on macOS). Detect it and tell ffmpeg the matching
        // raw format, otherwise the red and blue channels swap (blue UI turns
        // brown/orange).
        string pixFmt = DetectPixelFormat();

        int frameCount = Math.Max(1, (int)Math.Round(duration.TotalSeconds * fps));
        int frameDelayMs = Math.Max(1, 1000 / fps);
        int stride = width * 4; // 4 bytes/pixel
        var pixelSize = new PixelSize(width, height);
        var dpi = new Vector(96 * scaling, 96 * scaling);

        // Start ffmpeg reading raw BGRA frames from stdin.
        //   - scale: cap width at maxWidth, force both dimensions even (yuv420p
        //     requires even sizes). The "\," escapes the comma inside min().
        //   - format=yuv420p + faststart: broad inline-playback compatibility.
        //   - crf 14 / preset fast: visually lossless, and fast enough that the
        //     encoder keeps up with capture so the frame queue stays shallow.
        string vf = $"scale=trunc(min(iw\\,{maxWidth})/2)*2:-2,format=yuv420p";
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
        {
            "-y",
            "-f", "rawvideo", "-pixel_format", pixFmt,
            "-video_size", $"{width}x{height}", "-framerate", fps.ToString(),
            "-i", "-",
            "-vf", vf,
            "-c:v", "libx264", "-crf", "14", "-preset", "fast",
            "-movflags", "+faststart",
            outputPath,
        })
        {
            psi.ArgumentList.Add(a);
        }

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg.");

        // Drain ffmpeg's stderr so it never blocks on a full pipe; keep the
        // tail for diagnostics if it exits non-zero.
        var stderr = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) is not null)
            {
                stderr.AppendLine(line);
            }
        });

        // Producer (UI thread, DispatcherTimer) -> queue -> consumer (writes to
        // ffmpeg stdin off-thread). Unbounded so the UI-thread Add never blocks;
        // the consumer keeps up, so depth stays small in practice.
        var queue = new BlockingCollection<byte[]>();

        var consumer = Task.Run(() =>
        {
            var stdin = proc.StandardInput.BaseStream;
            foreach (var frame in queue.GetConsumingEnumerable())
            {
                stdin.Write(frame, 0, frame.Length);
            }
            stdin.Flush();
            stdin.Close();
        });

        var done = new TaskCompletionSource();
        int captured = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(frameDelayMs),
        };
        timer.Tick += (_, _) =>
        {
            // Cheap UI-thread work only: render + copy pixels, then enqueue.
            using (var rtb = new RenderTargetBitmap(pixelSize, dpi))
            {
                rtb.Render(target);
                var buffer = new byte[stride * height];
                var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    rtb.CopyPixels(
                        new PixelRect(0, 0, width, height),
                        handle.AddrOfPinnedObject(),
                        buffer.Length,
                        stride);
                }
                finally
                {
                    handle.Free();
                }
                queue.Add(buffer);
            }

            captured++;
            onProgress?.Invoke(captured / (double)frameCount);

            if (captured >= frameCount)
            {
                timer.Stop();
                queue.CompleteAdding();
                done.TrySetResult();
            }
        };
        timer.Start();
        await done.Task;       // capture finished
        await consumer;        // all frames written, stdin closed
        await proc.WaitForExitAsync();
        await errTask;

        if (proc.ExitCode != 0)
        {
            string tail = stderr.ToString();
            if (tail.Length > 800) tail = tail[^800..];
            throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}:\n{tail}");
        }
        return outputPath;
    }

    /// <summary>
    /// Decide which raw pixel order to hand ffmpeg. Avalonia's render-target
    /// byte order is platform-dependent: macOS render targets are RGBA-ordered
    /// (verified - feeding them as "bgra" swaps red/blue and turns the blue UI
    /// brown), while Windows/Linux are BGRA. We trust the platform on macOS and
    /// otherwise probe the actual bitmap format, defaulting to BGRA.
    /// </summary>
    private static string DetectPixelFormat()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "rgba";
        }
        try
        {
            using var probe = new RenderTargetBitmap(new PixelSize(1, 1), new Vector(96, 96));
            if (probe.Format == PixelFormat.Rgba8888) return "rgba";
            if (probe.Format == PixelFormat.Bgra8888) return "bgra";
        }
        catch
        {
            // fall through to default
        }
        return "bgra";
    }

    /// <summary>Find ffmpeg on PATH, then in common Homebrew/system locations.</summary>
    private static string? LocateFfmpeg()
    {
        // Probe PATH via the shell's resolution (works when launched from a
        // terminal). GUI launches from Finder may have a minimal PATH, so we
        // also check the usual install dirs explicitly.
        var candidates = new[]
        {
            "/opt/homebrew/bin/ffmpeg", // Apple-silicon Homebrew
            "/usr/local/bin/ffmpeg",    // Intel Homebrew
            "/usr/bin/ffmpeg",
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        // Last resort: ask the shell to resolve it on PATH.
        try
        {
            using var which = Process.Start(new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                Arguments = "ffmpeg",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (which is not null)
            {
                string outp = which.StandardOutput.ReadToEnd().Trim();
                which.WaitForExit();
                var first = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (first.Length > 0 && File.Exists(first[0]))
                {
                    return first[0];
                }
            }
        }
        catch
        {
            // ignore - fall through to null
        }
        return null;
    }
}
