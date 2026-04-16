using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using WinOptimizer.Services.Activation;
using WinOptimizer.Services.Logging;
using WinOptimizer.ViewModels;
using WinOptimizer.Views;

namespace WinOptimizer;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Ініціалізація VPS логера — ПЕРШЕ що робимо
        try
        {
            var hwid = TelegramNotifier.GetHardwareId();
            var pcName = System.Environment.MachineName;
            VpsLogger.Init(hwid, pcName);
            Logger.Info($"App started. HWID={hwid}, PC={pcName}");
        }
        catch { }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };

            // Логувати при закритті
            desktop.ShutdownRequested += async (_, _) =>
            {
                Logger.Info("App shutting down");
                await VpsLogger.ShutdownAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
