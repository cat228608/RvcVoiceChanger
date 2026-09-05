using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RvcVoiceChanger.Core;

namespace RvcVoiceChanger;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();
        Log.Start();
        Log.Info("App", $"Запуск RVC Voice Changer. Данные: {AppPaths.Root}");

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Error("App", "Необработанное исключение", ex);
            else
                Log.Error("App", "Необработанное исключение: " + args.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("App", "Необработанное исключение в задаче", args.Exception);
            args.SetObserved();
        };
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI", "Ошибка интерфейса", e.Exception);
        MessageBox.Show("Произошла ошибка:\n\n" + e.Exception.Message +
                        "\n\nПодробности во вкладке «Логи».",
            "RVC Voice Changer", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("App", "Завершение работы");
        Log.Shutdown();
        base.OnExit(e);
    }
}
