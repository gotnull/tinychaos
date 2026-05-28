using System;
using Avalonia;

namespace TinyChaos.Gui;

internal static class Program
{
    // Entry point. Standard Avalonia 11 boilerplate: build the Avalonia
    // application, run the classic desktop lifetime (Windows / macOS / Linux).
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
