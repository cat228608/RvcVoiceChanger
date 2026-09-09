using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RvcVoiceChanger.Audio;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Models;
using RvcVoiceChanger.Runtime;

namespace RvcVoiceChanger.Views;

public partial class VoiceChangerView : UserControl
{
    private readonly AppSettings _settings;
    private readonly ModelLibrary _library;
    private readonly AudioEngine _engine;
    private readonly PythonBridge _bridge;
    private bool _loading = true;

    // Та же история, что и в настройках: ползунки дают ValueChanged прямо во время разбора XAML,
    // когда ни полей класса, ни остальных элементов ещё нет.
    private bool _ready;

    // Debounce для ползунков: каждый тик ValueChanged не должен слать кадр воркеру
    // и писать settings.json — ждём паузу в движении ползунка.
    private DispatcherTimer? _pushDebounce;
    private DispatcherTimer? _saveDebounce;

    // Тикает, пока движок запускается и прогревается: показываем «Инициализация... N с»,
    // чтобы было видно, что программа не зависла, и когда уже можно говорить.
    private DispatcherTimer? _initTimer;
    private bool _initTimerHooked;

    private void SchedulePushSettings()
    {
        _pushDebounce ??= CreateDebounce(async () =>
        {
            try
            {
                await _bridge.PushSettingsAsync();
            }
            catch (Exception ex)
            {
                Log.Debug("UI", "Не удалось отправить настройки воркеру: " + ex.Message);
            }
        });

        _pushDebounce.Stop();
        _pushDebounce.Start();
    }

    private void ScheduleSaveSettings()
    {
        _saveDebounce ??= CreateDebounce(() =>
        {
            SettingsStore.Save(_settings);
            return Task.CompletedTask;
        });

        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private DispatcherTimer CreateDebounce(Func<Task> action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await action();
        };
        return timer;
    }

    public VoiceChangerView(AppSettings settings, ModelLibrary library, AudioEngine engine, PythonBridge bridge)
    {
        InitializeComponent();
        _ready = true;

        _settings = settings;
        _library = library;
        _engine = engine;
        _bridge = bridge;

        PitchSlider.Value = _settings.Pitch;
        InGain.Value = _settings.InputGainDb;
        OutGain.Value = _settings.OutputGainDb;
        MonitorEnabled.IsChecked = _settings.MonitorEnabled;

        RefreshModels();
        RefreshDevices();
        UpdateLabels();

        _engine.InputLevel += level => Dispatcher.BeginInvoke(new Action(() => InputMeter.Value = level));
        _engine.OutputLevel += (level, ms) => Dispatcher.BeginInvoke(new Action(() =>
        {
            OutputMeter.Value = level;
            LatencyText.Text = $"Оценка задержки: ~{_engine.LatencyEstimateMs:F0} мс · обработка блока: {ms:F0} мс";
        }));

        _engine.StateChanged += (state, message) => Dispatcher.BeginInvoke(new Action(() =>
        {
            StartButton.IsEnabled = state is EngineState.Stopped or EngineState.Error;
            StopButton.IsEnabled = state is EngineState.Running or EngineState.Starting or EngineState.Warmup;

            StatusText.Text = state switch
            {
                EngineState.Running => "Работает",
                EngineState.Starting => "Запуск...",
                EngineState.Warmup => "Инициализация...",
                EngineState.Error => "Ошибка: " + message,
                _ => "Остановлено"
            };

            UpdateReadyBanner(state, message);
        }));

        _loading = false;
    }

    /// <summary>
    /// Нижняя плашка стадии запуска: «Инициализация... N с», пока грузится модель
    /// и идёт прогрев ядра, затем «Можно говорить». Раньше момент готовности
    /// приходилось ловить глазами по логам и строке обработки блока.
    /// </summary>
    private void UpdateReadyBanner(EngineState state, string? message)
    {
        if (state is EngineState.Starting or EngineState.Warmup)
        {
            ReadyBanner.Visibility = Visibility.Visible;
            ReadyText.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            ReadyProgress.SetResourceReference(ProgressBar.ForegroundProperty, "Warn");

            RenderInitText();
            StartInitTimer();
            return;
        }

        StopInitTimer();

        switch (state)
        {
            case EngineState.Running:
                ReadyBanner.Visibility = Visibility.Visible;
                ReadyText.SetResourceReference(TextBlock.ForegroundProperty, "Ok");
                ReadyProgress.SetResourceReference(ProgressBar.ForegroundProperty, "Ok");
                ReadyProgress.IsIndeterminate = false;
                ReadyProgress.Value = 1;
                ReadyText.Text = $"Можно говорить · инициализация заняла {_engine.StartupSeconds:F0} с";
                break;

            case EngineState.Error:
                ReadyBanner.Visibility = Visibility.Visible;
                ReadyText.SetResourceReference(TextBlock.ForegroundProperty, "Err");
                ReadyProgress.IsIndeterminate = false;
                ReadyProgress.Value = 0;
                ReadyText.Text = "Не запустилось: " + (message ?? "подробности во вкладке «Логи»");
                break;

            default:
                ReadyBanner.Visibility = Visibility.Collapsed;
                ReadyProgress.IsIndeterminate = false;
                ReadyProgress.Value = 0;
                ReadyText.Text = string.Empty;
                break;
        }
    }

    /// <summary>Две стадии запуска видны по-разному: загрузка модели — бегущая полоса, прогрев — реальный прогресс.</summary>
    private void RenderInitText()
    {
        var seconds = _engine.StartupSeconds;

        if (_engine.State == EngineState.Warmup)
        {
            ReadyProgress.IsIndeterminate = false;
            ReadyProgress.Value = _engine.WarmupProgress;
            ReadyText.Text = $"Инициализация... {seconds:F0} с · прогрев модели, говорить пока не нужно";
        }
        else
        {
            ReadyProgress.IsIndeterminate = true;
            ReadyText.Text = $"Инициализация... {seconds:F0} с · загружаю модель на устройство";
        }
    }

    private void StartInitTimer()
    {
        _initTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        // Обработчик подписываем ровно один раз: иначе каждый старт добавлял бы лишнюю подписку.
        if (!_initTimerHooked)
        {
            _initTimerHooked = true;
            _initTimer.Tick += (_, _) =>
            {
                if (_engine.State is EngineState.Starting or EngineState.Warmup) RenderInitText();
                else StopInitTimer();
            };
        }

        _initTimer.Start();
    }

    private void StopInitTimer() => _initTimer?.Stop();

    public void RefreshModels()
    {
        var previous = _settings.ActiveModelId;

        ModelBox.ItemsSource = null;
        ModelBox.ItemsSource = _library.Items;
        ModelBox.SelectedItem = _library.Find(previous) ?? _library.Items.FirstOrDefault();

        UpdateModelInfo();
    }

    private void RefreshDevices()
    {
        var inputs = AudioDevices.Inputs();
        var outputs = AudioDevices.Outputs();

        InputBox.ItemsSource = inputs;
        OutputBox.ItemsSource = outputs;
        MonitorBox.ItemsSource = outputs;

        InputBox.SelectedItem = inputs.FirstOrDefault(d => d.Id == _settings.InputDeviceId)
                                ?? inputs.FirstOrDefault(d => d.IsDefault && !d.LooksLikeVirtualCable)
                                ?? inputs.FirstOrDefault();

        OutputBox.SelectedItem = outputs.FirstOrDefault(d => d.Id == _settings.OutputDeviceId)
                                 ?? outputs.FirstOrDefault(d => d.LooksLikeVirtualCable)
                                 ?? outputs.FirstOrDefault();

        MonitorBox.SelectedItem = outputs.FirstOrDefault(d => d.Id == _settings.MonitorDeviceId)
                                  ?? outputs.FirstOrDefault(d => d.IsDefault && !d.LooksLikeVirtualCable)
                                  ?? outputs.FirstOrDefault();

        var installed = VbCable.IsInstalled();
        CableStatus.Text = installed
            ? "Виртуальный микрофон найден ✓"
            : "Виртуальный микрофон не установлен";
        CableButton.IsEnabled = !installed;
    }

    private void UpdateModelInfo()
    {
        if (ModelBox.SelectedItem is ModelEntry entry)
        {
            ModelInfo.Text = $"{entry.StatusText} · {entry.SizeText}";
            _settings.ActiveModelId = entry.Id;
        }
        else
        {
            ModelInfo.Text = "Моделей нет — добавьте их во вкладках «Модели» или «Парсер моделей»";
        }
    }

    private void UpdateLabels()
    {
        if (!_ready || _settings == null) return;

        if (PitchText != null) PitchText.Text = $"{_settings.Pitch:+0;-0;0} полутона";
        if (InGainText != null) InGainText.Text = $"{_settings.InputGainDb:+0.0;-0.0;0.0} дБ";
        if (OutGainText != null) OutGainText.Text = $"{_settings.OutputGainDb:+0.0;-0.0;0.0} дБ";
        if (LatencyText != null && _engine != null)
            LatencyText.Text = $"Оценка задержки: ~{_engine.LatencyEstimateMs:F0} мс";
    }

    private async void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading) return;

        UpdateModelInfo();
        SettingsStore.Save(_settings);

        if (_engine.State is not (EngineState.Running or EngineState.Warmup)
            || ModelBox.SelectedItem is not ModelEntry entry) return;

        try
        {
            ModelInfo.Text = "Смена модели...";
            await _bridge.SwitchModelAsync(entry.PthPath, entry.IndexPath);
            UpdateModelInfo();
        }
        catch (Exception ex)
        {
            Log.Error("UI", "Не удалось сменить модель", ex);
            MessageBox.Show("Не удалось сменить модель:\n" + ex.Message, "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Pitch_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _settings == null) return;

        _settings.Pitch = (int)(PitchSlider?.Value ?? 0);
        UpdateLabels();

        if (_loading || _bridge == null) return;

        // Debounce: ползунок генерирует десятки событий в секунду.
        SchedulePushSettings();
        ScheduleSaveSettings();
    }

    private void PitchUp_Click(object sender, RoutedEventArgs e) => PitchSlider.Value = 12;
    private void PitchDown_Click(object sender, RoutedEventArgs e) => PitchSlider.Value = -12;
    private void PitchReset_Click(object sender, RoutedEventArgs e) => PitchSlider.Value = 0;

    private void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _settings == null) return;

        _settings.InputGainDb = InGain?.Value ?? 0;
        _settings.OutputGainDb = OutGain?.Value ?? 0;
        UpdateLabels();

        if (_loading) return;
        // Громкость тоже сохраняем — раньше она терялась при перезапуске программы.
        ScheduleSaveSettings();
    }

    private void Device_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading || _settings == null) return;

        _settings.InputDeviceId = (InputBox.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.OutputDeviceId = (OutputBox.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.MonitorDeviceId = (MonitorBox.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.MonitorEnabled = MonitorEnabled.IsChecked == true;

        SettingsStore.Save(_settings);
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        RefreshDevices();
        _loading = false;
    }

    private async void InstallCable_Click(object sender, RoutedEventArgs e)
    {
        // Установка стороннего аудиодрайвера — серьёзное действие, требуем явного согласия.
        var confirm = MessageBox.Show(
            "Сейчас будет скачан и установлен аудиодрайвер VB-CABLE (производитель — VB-Audio Software).\n\n" +
            "• Потребуется подтверждение UAC (права администратора).\n" +
            "• В системе появятся виртуальные устройства «CABLE Input/Output».\n" +
            "• После установки может потребоваться перезагрузка Windows.\n\nПродолжить?",
            "Установка виртуального микрофона",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        CableButton.IsEnabled = false;

        var progress = new Progress<string>(s => CableStatus.Text = s);
        var ok = await VbCable.InstallAsync(_settings, progress);

        if (!ok)
        {
            MessageBox.Show("Драйвер установлен не полностью. Обычно помогает перезагрузка Windows.",
                "Виртуальный микрофон", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshDevices_Click(sender, e);
    }

    /// <summary>Общая логика запуска для кнопок «Старт» и «Проверка». Возвращает true при успехе.</summary>
    private async Task<bool> StartEngineAsync()
    {
        if (ModelBox.SelectedItem is not ModelEntry entry)
        {
            MessageBox.Show("Сначала добавьте и выберите модель голоса.", "Модель не выбрана",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (!entry.Exists)
        {
            MessageBox.Show("Файл модели не найден:\n" + entry.PthPath, "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        _settings.SendToVirtualMic = true;
        Device_Changed(this, new RoutedEventArgs());

        try
        {
            await _engine.StartAsync(entry.PthPath, entry.IndexPath);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить:\n\n" + ex.Message + "\n\nПодробности во вкладке «Логи».",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartEngineAsync();

    private async void Stop_Click(object sender, RoutedEventArgs e) => await _engine.StopAsync();

    /// <summary>Режим проверки: говоришь в микрофон �� сразу слышишь результат в наушниках.</summary>
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        MonitorEnabled.IsChecked = true;
        _settings.MonitorEnabled = true;
        Device_Changed(sender, e);

        if (_engine.State is EngineState.Running or EngineState.Warmup or EngineState.Starting)
        {
            await _engine.StopAsync();
        }

        // Ждём реального запуска: раньше MessageBox появлялся до старта (и даже при ошибке).
        var started = await StartEngineAsync();
        if (!started) return;

        MessageBox.Show("Режим проверки включён.\n\nГоворите в микрофон — изменённый голос идёт в выбранные наушники.\nЧтобы не было эха, не используйте колонки рядом с микрофоном.",
            "Проверка", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
