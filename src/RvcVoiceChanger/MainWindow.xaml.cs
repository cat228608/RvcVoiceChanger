using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using RvcVoiceChanger.Audio;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Models;
using RvcVoiceChanger.Runtime;
using RvcVoiceChanger.Views;

namespace RvcVoiceChanger;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ModelLibrary _library;
    private readonly PythonBridge _bridge;
    private readonly AudioEngine _engine;

    private readonly VoiceChangerView _voiceView;
    private readonly ModelsView _modelsView;
    private readonly ParserView _parserView;
    private readonly SettingsView _settingsView;
    private readonly LogsView _logsView;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();

        // Размер под конкретный экран: на 1366x768 и при масштабе 125-150 % окно
        // 1240x820 не влезает, и часть интерфейса оказывается за краем.
        WindowSizing.FitToWorkArea(this, 1240, 820, 900, 560);

        _settings = settings;

        _library = new ModelLibrary();
        _library.Load();

        _bridge = new PythonBridge(_settings);
        _engine = new AudioEngine(_settings, _bridge);

        _voiceView = new VoiceChangerView(_settings, _library, _engine, _bridge);
        _modelsView = new ModelsView(_settings, _library);
        _parserView = new ParserView(_settings, _library);
        _settingsView = new SettingsView(_settings, _bridge);
        _logsView = new LogsView();

        VersionText.Text = "версия 1.0 · Windows x64";
        Host.Content = _voiceView;
        UpdateThemeButton();

        _engine.StateChanged += (state, message) => Dispatcher.Invoke(() =>
        {
            EngineBadge.Text = state switch
            {
                EngineState.Running => "Работает",
                EngineState.Starting => "Запуск...",
                EngineState.Error => "Ошибка: " + message,
                _ => "Остановлено"
            };

            // SetResourceReference вместо FindResource: цвет точки перекрашивается
            // и при смене темы, а не только при смене состояния движка.
            EngineDot.SetResourceReference(Shape.FillProperty, state switch
            {
                EngineState.Running => "Ok",
                EngineState.Starting => "Warn",
                EngineState.Error => "Err",
                _ => "Muted"
            });
        });

        DeviceBadge.Text = _settings.Device switch
        {
            ComputeDevice.Cuda => "Вычисления: NVIDIA CUDA",
            ComputeDevice.Cpu => "Вычисления: CPU",
            _ => "Вычисления: автовыбор"
        };

        // Модель добавили в парсере — сразу обновляем списки в других вкладках.
        _parserView.ModelAdded += () => Dispatcher.Invoke(() =>
        {
            _modelsView.Refresh();
            _voiceView.RefreshModels();
        });

        _modelsView.ModelsChanged += () => Dispatcher.Invoke(() => _voiceView.RefreshModels());

        Closing += async (_, e) =>
        {
            SettingsStore.Save(_settings);
            await _engine.StopAsync();
            Log.Flush();
        };
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Host == null) return;

        Host.Content = Nav.SelectedIndex switch
        {
            0 => _voiceView,
            1 => _modelsView,
            2 => _parserView,
            3 => _settingsView,
            4 => _logsView,
            _ => Host.Content
        };
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeManager.Current == ThemeManager.Dark ? ThemeManager.Light : ThemeManager.Dark;
        ThemeManager.Apply(_settings.Theme);
        SettingsStore.Save(_settings);
        UpdateThemeButton();
    }

    private void UpdateThemeButton()
    {
        ThemeButton.Content = ThemeManager.Current == ThemeManager.Dark
            ? "\u2600 Светлая тема"
            : "\ud83c\udf19 Тёмная тема";
    }
}
