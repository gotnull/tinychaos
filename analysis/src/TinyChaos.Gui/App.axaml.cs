using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace TinyChaos.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>App-menu "About tinychaos": show the branded About window.</summary>
    private void OnAbout(object? sender, EventArgs e)
    {
        var about = new AboutWindow();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } owner)
        {
            about.ShowDialog(owner);
        }
        else
        {
            about.Show();
        }
    }

    /// <summary>App-menu "Quit tinychaos".</summary>
    private void OnQuit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => vm.Dispose();

            // macOS: Window.Icon does not drive the Dock icon (that normally
            // comes from the .app bundle). When launched via `dotnet run` there
            // is no bundle, so set the Dock icon from our app icon at runtime.
            // No-op on Windows/Linux.
            MacDockIcon.TrySet(new Uri("avares://tinychaos-gui/Assets/tinychaos-icon.png"));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
