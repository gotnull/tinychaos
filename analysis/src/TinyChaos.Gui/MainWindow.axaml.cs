using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace TinyChaos.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Wire up the samples ListBox: mirror multi-selection into the view
        // model, and route DoubleTapped to PlaySample. The view model owns
        // all the logic; the code-behind is glue.
        var list = this.FindControl<ListBox>("SamplesListBox");
        if (list is not null)
        {
            list.SelectionChanged += OnSamplesSelectionChanged;
            list.DoubleTapped += OnSamplesDoubleTapped;
        }
    }

    private void OnSamplesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not ListBox list) return;

        // SelectedItems is the live IList<object> on the ListBox. Reconcile
        // the view-model's ObservableCollection with it so commands and
        // bindings stay in sync.
        var selected = list.SelectedItems;
        vm.SelectedSamples.Clear();
        if (selected is null) return;
        foreach (var item in selected)
        {
            if (item is SampleEntry entry)
            {
                vm.SelectedSamples.Add(entry);
            }
        }

        // Load the just-clicked capture into the saved-data viewer (prefer the
        // newly-added item; fall back to the first selected).
        SampleEntry? toView = null;
        foreach (var item in e.AddedItems)
        {
            if (item is SampleEntry se) { toView = se; break; }
        }
        toView ??= vm.SelectedSamples.Count > 0 ? vm.SelectedSamples[0] : null;
        if (toView is not null) vm.ShowSampleInViewer(toView);
    }

    /// <summary>"Reset view" button: return the saved-data viewer to full scale.</summary>
    private void OnResetViewer(object? sender, RoutedEventArgs e)
        => this.FindControl<SavedWaveformView>("SavedView")?.ResetView();

    /// <summary>
    /// "Browse…" button on the Firmware tab: pick a prebuilt firmware <c>.bin</c>
    /// (e.g. one downloaded from GitHub Releases) and hand its path to the view
    /// model, which can then flash it with st-flash - no build toolchain needed.
    /// The file dialog needs the window's StorageProvider, hence code-behind.
    /// </summary>
    private async void OnBrowseFirmwareBin(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // MainWindow is itself a TopLevel, so its StorageProvider drives the dialog.
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a firmware .bin to flash",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Firmware binary") { Patterns = new[] { "*.bin" } },
                FilePickerFileTypes.All,
            },
        });

        var picked = files.Count > 0 ? files[0] : null;
        var path = picked?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) vm.SelectedFirmwareBin = path;
    }

    /// <summary>
    /// "Export" button: write the loaded capture's samples with all packet
    /// framing stripped - a raw interleaved uint16 .bin and a CSV - next to the
    /// source file, then reveal them. (The same thing tools/extract_samples.py
    /// does, straight from the GUI.)
    /// </summary>
    private void OnExportSamples(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var buf = vm.LoadedCapture;
        if (buf is null || buf.Length == 0) { vm.ViewerStatusText = "nothing to export"; return; }
        try
        {
            string dir = Path.GetDirectoryName(buf.SourcePath) ?? ".";
            string stem = Path.GetFileNameWithoutExtension(buf.SourcePath);
            string rawPath = Path.Combine(dir, $"{stem}-raw.bin");
            string csvPath = Path.Combine(dir, $"{stem}-samples.csv");
            buf.WriteRawInterleaved(rawPath);
            buf.WriteCsv(csvPath);
            vm.ViewerStatusText =
                $"exported {Path.GetFileName(rawPath)} + .csv ({buf.Length:N0} samples/ch, no headers)";
            RevealInFileManager(rawPath);
        }
        catch (Exception ex)
        {
            vm.ViewerStatusText = $"export failed: {ex.Message}";
        }
    }

    private void OnSamplesDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not ListBox list) return;
        // The row the user double-clicked is the currently focused row,
        // which Avalonia exposes as SelectedItem after the click bubbles.
        if (list.SelectedItem is SampleEntry entry)
        {
            vm.PlaySample(entry);
        }
    }

    /// <summary>
    /// "Record MP4" button: capture the whole app content to a shareable clip
    /// on the Desktop and reveal it in Finder/Explorer. Prefers a high-quality
    /// H.264 MP4 via <see cref="VideoRecorder"/> (full colour, plays inline in
    /// Telegram); falls back to an animated GIF (<see cref="GifRecorder"/>) only
    /// if ffmpeg is unavailable. Kept in code-behind because rendering a control
    /// to a bitmap needs the control reference (a View concern). We capture the
    /// whole Window (a TopLevel): it covers the entire app at its full client
    /// size with no margin offset, so nothing is clipped on the edges.
    /// </summary>
    private async void OnRecordVideo(object? sender, RoutedEventArgs e)
    {
        Control content = this; // capture the whole window client area
        var button = this.FindControl<Button>("RecordVideoButton");
        if (button is null) return;

        // Prevent re-entry and show progress on the button itself.
        button.IsEnabled = false;
        var originalContent = button.Content;

        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) desktop = Path.GetTempPath();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            string? saved;
            var duration = TimeSpan.FromSeconds(5);
            void Progress(double p) => button.Content = $"Recording {(int)(p * 100)}%";

            if (VideoRecorder.FfmpegPath is not null)
            {
                // ~5 s at 30 fps - smooth, visually lossless MP4.
                saved = await VideoRecorder.RecordAsync(
                    content, duration, fps: 30,
                    outputPath: Path.Combine(desktop, $"tinychaos-{stamp}.mp4"),
                    onProgress: Progress);
            }
            else
            {
                // No ffmpeg: fall back to a GIF so the feature still works.
                saved = await GifRecorder.RecordAsync(
                    content, duration, fps: 15,
                    outputPath: Path.Combine(desktop, $"tinychaos-{stamp}.gif"),
                    onProgress: Progress);
            }

            if (saved is null)
            {
                button.Content = "Nothing to capture";
                await Task.Delay(1500);
            }
            else
            {
                button.Content = "Saved ✓";
                RevealInFileManager(saved);
                await Task.Delay(1500);
            }
        }
        catch (Exception ex)
        {
            button.Content = "Record failed";
            Debug.WriteLine($"clip record failed: {ex}");
            await Task.Delay(1500);
        }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = true;
        }
    }

    /// <summary>Open a file manager with the saved file selected (best-effort,
    /// per-OS). Failure is non-fatal - the file is on disk regardless.</summary>
    private static void RevealInFileManager(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", $"-R \"{path}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else
            {
                // Linux: open the containing folder.
                Process.Start("xdg-open", $"\"{Path.GetDirectoryName(path)}\"");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"reveal failed: {ex}");
        }
    }
}
