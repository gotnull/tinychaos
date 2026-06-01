using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace TinyChaos.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
