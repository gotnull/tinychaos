using System;
using System.IO;

namespace TinyChaos.Gui;

/// <summary>
/// One captured `.bin` file in the samples directory.
/// </summary>
public sealed class SampleEntry
{
    public string FullPath { get; }
    public string FileName { get; }
    public long SizeBytes { get; }
    public DateTime ModifiedUtc { get; }

    public SampleEntry(FileInfo info)
    {
        FullPath = info.FullName;
        FileName = info.Name;
        SizeBytes = info.Length;
        ModifiedUtc = info.LastWriteTimeUtc;
    }

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
