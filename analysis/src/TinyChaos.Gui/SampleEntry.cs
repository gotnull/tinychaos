using System;
using System.Collections.Generic;
using System.IO;

namespace TinyChaos.Gui;

/// <summary>
/// One captured `.bin` file in the samples directory.
///
/// Samples that match a known-demo file name (see <see cref="DemoFileNames"/>)
/// are flagged as <see cref="IsDemo"/> and the GUI prevents them from being
/// deleted. They are the seeded synthetic captures the repo ships with;
/// recapturing them is non-trivial (the synthesis script lives in
/// samples/README.md) so we protect them.
/// </summary>
public sealed class SampleEntry
{
    /// <summary>
    /// File names that the repo seeds and protects from deletion in the GUI.
    /// Matched case-insensitively against <see cref="FileName"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> DemoFileNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "floating_50hz.bin",
            "sine_1khz.bin",
            "zener_synthetic.bin",
        };

    public string FullPath { get; }
    public string FileName { get; }
    public long SizeBytes { get; }
    public DateTime ModifiedUtc { get; }
    public bool IsDemo { get; }

    public SampleEntry(FileInfo info)
    {
        FullPath = info.FullName;
        FileName = info.Name;
        SizeBytes = info.Length;
        ModifiedUtc = info.LastWriteTimeUtc;
        IsDemo = DemoFileNames.Contains(FileName);
    }

    /// <summary>Pseudo-icon: lock glyph for demo samples, blank otherwise.</summary>
    public string DemoBadge => IsDemo ? "•" : "";

    public string SizeText => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
    };

    public string ModifiedText
    {
        get
        {
            var age = DateTime.UtcNow - ModifiedUtc;
            return age.TotalSeconds < 60
                ? "just now"
                : age.TotalMinutes < 60
                    ? $"{(int)age.TotalMinutes} min ago"
                    : age.TotalHours < 24
                        ? $"{(int)age.TotalHours} h ago"
                        : age.TotalDays < 30
                            ? $"{(int)age.TotalDays} d ago"
                            : ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }
}
