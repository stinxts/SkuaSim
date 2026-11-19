using Avalonia.Controls;
using SkuaSim.UI.ViewModels;

namespace SkuaSim.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnWindowOpened;
        SizeChanged += OnWindowSizeChanged;
        ScalingChanged += (_, _) => ApplyScreenMetrics(resizeToFit: false);
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        ApplyScreenMetrics(resizeToFit: true);
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen != null && screen.Scaling > 0)
            {
                var screenLogicalW = screen.WorkingArea.Width / screen.Scaling;
                var screenLogicalH = screen.WorkingArea.Height / screen.Scaling;
                vm.UpdateAutoScale(Math.Min(e.NewSize.Width, screenLogicalW), Math.Min(e.NewSize.Height, screenLogicalH));
            }
            else
            {
                vm.UpdateAutoScale(e.NewSize.Width, e.NewSize.Height);
            }
        }
    }

    private void ApplyScreenMetrics(bool resizeToFit)
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen == null || screen.Scaling <= 0)
            return;

        var logicalW = screen.WorkingArea.Width / screen.Scaling;
        var logicalH = screen.WorkingArea.Height / screen.Scaling;

        if (resizeToFit && WindowState == WindowState.Normal)
        {
            // Fit politely on HD (1280x720), Full-HD, 2K, 4K, and 8K displays
            var targetW = Math.Clamp(logicalW * 0.88, MinWidth, Math.Max(MinWidth, logicalW * 0.95));
            var targetH = Math.Clamp(logicalH * 0.88, MinHeight, Math.Max(MinHeight, logicalH * 0.94));
            Width = Math.Round(targetW);
            Height = Math.Round(targetH);
        }

        if (DataContext is MainViewModel vm)
            vm.UpdateAutoScale(logicalW, logicalH);
    }
}
