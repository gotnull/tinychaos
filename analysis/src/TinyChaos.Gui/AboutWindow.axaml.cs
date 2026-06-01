using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace TinyChaos.Gui;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // Show the assembly's informational/file version so the About box stays
        // in sync with the build without a hard-coded string.
        var asm = Assembly.GetExecutingAssembly();
        string version =
            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "0.1.0";
        // Strip any "+<git-hash>" suffix the SDK appends to informational version.
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];

        this.FindControl<TextBlock>("VersionText")!.Text = $"version {version}";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
