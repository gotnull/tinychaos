using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
// 'Image' is ambiguous (Avalonia.Controls.Image vs SixLabors.ImageSharp.Image);
// alias the ImageSharp one so the encode calls below are unambiguous.
using IsImage = SixLabors.ImageSharp.Image;

namespace TinyChaos.Gui;

/// <summary>
/// Records an Avalonia control (e.g. the whole app content) to an animated GIF
/// so the user can capture and share a clip of the moving UI without an
/// external screen recorder.
///
/// Keeping the UI responsive is the whole game here. <see cref="RenderTargetBitmap"/>
/// can only render a visual from the UI thread, so frame capture is inherently
/// UI-thread work. To avoid the app appearing frozen during a recording we:
///   1. Drive capture from a <see cref="DispatcherTimer"/>, one render per tick.
///      Between ticks the dispatcher message loop runs normally, so the live
///      waveform keeps animating and input stays responsive. (A tight
///      render/await loop can starve the pump if a render overruns the delay.)
///   2. Do only the cheap part on the UI thread - render + copy raw pixels.
///   3. Push everything expensive (downscale, colour quantisation, LZW encode)
///      onto a background thread after capture finishes.
///
/// Avalonia's render target is BGRA8888; ImageSharp's <see cref="Bgra32"/> uses
/// the same byte order, so frames map across without a channel swap. The UI is
/// opaque (alpha = 255) so premultiplied-alpha differences are moot.
/// </summary>
public static class GifRecorder
{
    /// <summary>
    /// Capture <paramref name="target"/> for <paramref name="duration"/> at
    /// <paramref name="fps"/> frames per second and write an animated GIF to
    /// <paramref name="outputPath"/>. Returns the path on success, or null if
    /// the control has no rendered size yet. Must be called from the UI thread.
    /// </summary>
    /// <param name="maxWidth">If the captured frames are wider than this, they
    /// are scaled down (preserving aspect) during encode so the GIF stays a
    /// sharable size. Scaling happens off the UI thread.</param>
    /// <param name="onProgress">Optional 0..1 progress callback (capture phase),
    /// invoked on the UI thread.</param>
    public static async Task<string?> RecordAsync(
        Control target,
        TimeSpan duration,
        int fps,
        string outputPath,
        int maxWidth = 900,
        Action<double>? onProgress = null)
    {
        // The control must be laid out and have a non-zero size to render.
        int width = (int)Math.Round(target.Bounds.Width);
        int height = (int)Math.Round(target.Bounds.Height);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        int frameCount = Math.Max(1, (int)Math.Round(duration.TotalSeconds * fps));
        int frameDelayMs = Math.Max(1, 1000 / fps);
        int stride = width * 4; // BGRA = 4 bytes/pixel
        var pixelSize = new PixelSize(width, height);
        var dpi = new Vector(96, 96);

        // Raw BGRA frames, captured on the UI thread.
        var frames = new List<byte[]>(frameCount);

        // Capture one frame per timer tick so the dispatcher keeps pumping
        // (UI stays live) between grabs. A TaskCompletionSource lets us await
        // the whole capture as a single async operation.
        var done = new TaskCompletionSource();
        int captured = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(frameDelayMs),
        };
        timer.Tick += (_, _) =>
        {
            // Render the live control into an off-screen bitmap and pull pixels.
            // This is the only UI-thread cost per frame; keep it minimal.
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
                frames.Add(buffer);
            }

            captured++;
            onProgress?.Invoke(captured / (double)frameCount);

            if (captured >= frameCount)
            {
                timer.Stop();
                done.TrySetResult();
            }
        };
        timer.Start();
        await done.Task;

        // Encode off the UI thread (downscale + GIF quantisation + LZW are all
        // CPU-bound). The UI is fully free during this phase.
        await Task.Run(() =>
            EncodeGif(frames, width, height, frameDelayMs, maxWidth, outputPath));
        return outputPath;
    }

    /// <summary>Encode the captured BGRA frames into a looping animated GIF,
    /// downscaling to <paramref name="maxWidth"/> if needed.</summary>
    private static void EncodeGif(
        List<byte[]> frames, int width, int height,
        int frameDelayMs, int maxWidth, string outputPath)
    {
        // GIF frame delay is in centiseconds (1/100 s); round our ms cadence.
        int delayCs = Math.Max(1, frameDelayMs / 10);

        // Target size: scale down (preserving aspect) only if wider than max.
        int outW = width, outH = height;
        if (maxWidth > 0 && width > maxWidth)
        {
            outW = maxWidth;
            outH = Math.Max(1, (int)Math.Round(height * (maxWidth / (double)width)));
        }
        bool resize = outW != width || outH != height;

        // Build the animation around the first frame, then append the rest.
        using var gif = LoadFrame(frames[0], width, height, resize, outW, outH);
        gif.Metadata.GetGifMetadata().RepeatCount = 0; // 0 = loop forever

        var rootMeta = gif.Frames.RootFrame.Metadata.GetGifMetadata();
        rootMeta.FrameDelay = delayCs;
        rootMeta.DisposalMethod = GifDisposalMethod.RestoreToBackground;

        for (int i = 1; i < frames.Count; i++)
        {
            using var frameImg = LoadFrame(frames[i], width, height, resize, outW, outH);
            var added = gif.Frames.AddFrame(frameImg.Frames.RootFrame);
            var meta = added.Metadata.GetGifMetadata();
            meta.FrameDelay = delayCs;
            meta.DisposalMethod = GifDisposalMethod.RestoreToBackground;
        }

        gif.SaveAsGif(outputPath);
    }

    /// <summary>Load one BGRA frame into an ImageSharp image, optionally resized.</summary>
    private static Image<Bgra32> LoadFrame(
        byte[] bgra, int width, int height, bool resize, int outW, int outH)
    {
        var img = IsImage.LoadPixelData<Bgra32>(bgra, width, height);
        if (resize)
        {
            img.Mutate(x => x.Resize(outW, outH));
        }
        return img;
    }
}
