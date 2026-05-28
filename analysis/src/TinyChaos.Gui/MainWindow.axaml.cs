using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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
}
