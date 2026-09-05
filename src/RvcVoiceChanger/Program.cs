using System;
using System.Threading;
using System.Windows;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Runtime;
using RvcVoiceChanger.Views;

namespace RvcVoiceChanger;

public static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        _singleInstance = new Mutex(true, "RvcVoiceChanger.SingleInstance", out var isNew);
        if (!isNew)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            MessageBox.Show("Программа уже запущена.", "RVC Voice Changer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        try
        {
            return RunApp();
        }
        finally
        {
            try { _singleInstance.ReleaseMutex(); } catch { }
            _singleInstance.Dispose();
            _singleInstance = null;
        }
    }

    private static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();

        // Пока идёт установка, закрытие её окна не должно гасить всё приложение:
        // WPF назначает первое созданное окно главным, и с OnMainWindowClose
        // успешное закрытие BootstrapWindow завершало процесс до показа MainWindow.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppPaths.EnsureCreated();

        // Логгер запускаем ДО окна установки: BootstrapWindow.ShowDialog() идёт до app.Run(),
        // и без этого всё, что пишется во время установки, терялось бы.
        Log.Start();

        var settings = SettingsStore.Load();
        ThemeManager.Apply(settings.Theme);

        // Первый запуск или неполная установка — показываем окно установки.
        if (!RuntimeInstaller.IsInstalled())
        {
            var bootstrap = new BootstrapWindow(settings);
            var result = bootstrap.ShowDialog();

            if (result != true || !bootstrap.InstallSucceeded)
            {
                Log.Warn("App", "Установка не завершена — выход");
                Log.Shutdown();
                return 1;
            }

            settings = SettingsStore.Load();
        }

        var window = new MainWindow(settings);
        app.MainWindow = window;
        // Только теперь закрытие главного окна должно завершать приложение.
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

        return app.Run();
    }
}
