using System.Collections.Specialized;
using Avalonia.Controls;
using SkuaSim.UI.ViewModels;

namespace SkuaSim.UI.Views;

public partial class DiagnosticsView : UserControl
{
    private DiagnosticsViewModel? _subscribedVm;

    public DiagnosticsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.FilteredLogs.CollectionChanged -= OnLogsCollectionChanged;

        _subscribedVm = DataContext as DiagnosticsViewModel;
        if (_subscribedVm != null)
            _subscribedVm.FilteredLogs.CollectionChanged += OnLogsCollectionChanged;
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_subscribedVm is { AutoScrollLogs: true } vm &&
            e.Action == NotifyCollectionChangedAction.Add &&
            vm.FilteredLogs.Count > 0)
        {
            LogsListBox.ScrollIntoView(vm.FilteredLogs[^1]);
        }
    }
}
