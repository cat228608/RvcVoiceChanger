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

    // Размер, который нужен самой широкой вкладке (парсер и таблица моделей):
    // окно сразу открывается так, чтобы ничего не приходилось растягивать руками.
    private const double PreferredWidth = 1320;
    private const double PreferredHeight = 820;

    /// <summary>
    /// Подбирает стартовый размер окна: берём нужный контенту, но никогда
    /// не выходим за рабочую область экрана (иначе часть окна уезжает за границу).
    /// </summary>
    private void ApplyOptimalWindowSize()
    {
        var area = SystemParameters.WorkArea;

        // Небольшой зазор, чтобы окно не прилипало к краям и к панели задач.
        var maxWidth = Math.Max(720, area.Width - 40);
        var maxHeight = Math.Max(520, area.Height - 40);

        var width = Math.Min(PreferredWidth, maxWidth);
        var height = Math.Min(PreferredHeight, maxHeight);

        // На маленьких экранах минимальный размер не должен мешать окну уместиться.
        if (MinWidth > width) MinWidth = width;
        if (MinHeight > height) MinHeight = height;

        Width = width;
        Height = height;
        Left = area.Left + (area.Width - width) / 2;
        Top = area.Top + (area.Height - height) / 2;

        // Экран меньше, чем требует контент — разворачиваем окно на всю область.
        if (PreferredWidth > maxWidth || PreferredHeight > maxHeight)
            WindowState = WindowState.Maximized;
    }

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        ApplyOptimalWindowSize();

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

        VersionText.Text = "версия 1.2 alpha · Windows x64";
        Host.Content = _voiceView;
        UpdateThemeButton();

        _engine.StateChanged += (state, message) => Dispatcher.Invoke(() =>
        {
            EngineBadge.Text = state switch
            {
                EngineState.Running => "Работает",
                EngineState.Starting => "Запуск...",
                EngineState.Warmup => "Инициализация...",
                EngineState.Error => "Ошибка: " + message,
                _ => "Остановлено"
            };

            // SetResourceReference вместо FindResource: цвет точки перекрашивается
            // и при смене темы, а не только при смене состояния движка.
            EngineDot.SetResourceReference(Shape.FillProperty, state switch
            {
                EngineState.Running => "Ok",
                EngineState.Starting => "Warn",
                EngineState.Warmup => "Warn",
                EngineState.Error => "Err",
                _ => "Muted"
            });
        });

        DeviceBadge.Text = _settings.Device switch
        {
            ComputeDevice.Cuda => "Вычисления: NVIDIA CUDA",
            ComputeDevice.Cpu => "Вычисления: CPU",
            ComputeDevice.DirectMl => "Вычисления: DirectML (AMD / Intel)",
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
