using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SkuaSim.UI.Services;
using SkuaSim.UI.ViewModels;
using SkuaSim.UI.Views;

namespace SkuaSim.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = vm
            };
            vm.AttachServices(new WindowFileDialogService(() => mainWindow));
            mainWindow.Closing += (_, _) => vm.Shutdown();
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
