using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Net;
using RvcVoiceChanger.Runtime;

namespace RvcVoiceChanger.Views;

public partial class SettingsView : UserControl
{
    private readonly AppSettings _settings;
    private readonly PythonBridge _bridge;
    private bool _loading = true;

    // WPF вызывает обработчики ещё во время разбора XAML: Minimum/Maximum у Slider меняют Value
    // и бьют ValueChanged до того, как созданы остальные элементы. До конца загрузки ничего не трогаем.
    private bool _ready;

    public SettingsView(AppSettings settings, PythonBridge bridge)
    {
        InitializeComponent();
        _ready = true;

        _settings = settings;
        _bridge = bridge;

        LoadFromSettings();
        _loading = false;
    }

    private void LoadFromSettings()
    {
        _loading = true;

        SelectByContent(F0Method, _settings.F0Method);
        SelectByContent(Embedder, _settings.EmbedderModel);
        EmbedderCustom.Text = _settings.EmbedderModelCustom;

        IndexRate.Value = _settings.IndexRate;
        Protect.Value = _settings.Protect;
        VolumeEnvelope.Value = _settings.VolumeEnvelope;

        Autotune.IsChecked = _settings.Autotune;
        AutotuneStrength.Value = _settings.AutotuneStrength;
        ProposedPitch.IsChecked = _settings.ProposedPitch;
        ProposedPitchThreshold.Value = _settings.ProposedPitchThreshold;

        CleanAudio.IsChecked = _settings.CleanAudio;
        CleanStrength.Value = _settings.CleanStrength;
        PostProcess.IsChecked = _settings.PostProcess;

        Reverb.IsChecked = _settings.Reverb;
        ReverbRoom.Value = _settings.ReverbRoomSize;
        ReverbWet.Value = _settings.ReverbWetLevel;
        Compressor.IsChecked = _settings.Compressor;
        CompThreshold.Value = _settings.CompressorThresholdDb;
        CompRatio.Value = _settings.CompressorRatio;
        Limiter.IsChecked = _settings.Limiter;
        LimiterThreshold.Value = _settings.LimiterThresholdDb;

        ChunkSize.Value = _settings.ReadChunkSize;
        CrossFade.Value = _settings.CrossFadeOverlapSize;
        ExtraConvert.Value = _settings.ExtraConvertSize;
        SilentThreshold.Value = _settings.SilentThreshold;

        UsePhaseVocoder.IsChecked = _settings.UsePhaseVocoder;
        VadEnabled.IsChecked = _settings.VadEnabled;
        ExclusiveMode.IsChecked = _settings.ExclusiveMode;
        DeviceBox.SelectedIndex = (int)_settings.Device;
        DirectMlStatus.Text = "Состояние: установлена сборка torch — " + TorchFlavorText()
                              + ". Нажмите «Проверить», чтобы опросить DirectML.";

        PrecisionBox.SelectedIndex = (int)_settings.Precision;
        AllowTf32.IsChecked = _settings.AllowTf32;
        TorchCompile.IsChecked = _settings.TorchCompileEnabled;
        SelectByContent(TorchCompileModeBox, _settings.TorchCompileMode);
        ReduceGpuSync.IsChecked = _settings.ReduceGpuSync;
        UseRingBuffer.IsChecked = _settings.UseRingBuffer;

        VolumeGainModeBox.SelectedIndex = (int)_settings.VolumeGainMode;
        SoftGate.IsChecked = _settings.SoftGateEnabled;
        SoftGateHangover.Value = _settings.SoftGateHangoverMs;
        SoftGateAttack.Value = _settings.SoftGateAttackMs;
        SoftGateRelease.Value = _settings.SoftGateReleaseMs;

        ProxyModeBox.SelectedIndex = (int)_settings.ProxyMode;
        ProxyHost.Text = _settings.ProxyHost;
        ProxyPort.Text = _settings.ProxyPort.ToString();
        ProxyUser.Text = _settings.ProxyUser;
        ProxyPass.Password = _settings.ProxyPassword;
        ProxyForPip.IsChecked = _settings.ProxyForPip;
        ProxyForHf.IsChecked = _settings.ProxyForHuggingFace;

        UpdateTexts();
        _loading = false;
    }

    private static void SelectByContent(ComboBox box, string? value)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }

        box.SelectedIndex = 0;
    }

    /// <summary>Безопасная запись в подпись: элемента может ещё не существовать.</summary>
    private static void SetText(TextBlock? target, string value)
    {
        if (target != null) target.Text = value;
    }

    private void UpdateTexts()
    {
        if (!_ready) return;

        SetText(IndexRateText, $"{IndexRate?.Value ?? 0:F2}");
        SetText(ProtectText, $"{Protect?.Value ?? 0:F2}");
        SetText(VolumeEnvelopeText, $"{VolumeEnvelope?.Value ?? 0:F2}");
        SetText(AutotuneStrengthText, $"{AutotuneStrength?.Value ?? 0:F2}");
        SetText(ProposedPitchThresholdText, $"{ProposedPitchThreshold?.Value ?? 0:F0}");
        SetText(CleanStrengthText, $"{CleanStrength?.Value ?? 0:F2}");

        var chunk = (int)(ChunkSize?.Value ?? 0);
        SetText(ChunkSizeText, $"{chunk} (~{chunk * 128 * 1000.0 / 48000:F0} мс)");

        SetText(CrossFadeText, $"{CrossFade?.Value ?? 0:F2} с");
        SetText(ExtraConvertText, $"{ExtraConvert?.Value ?? 0:F2} с");
        SetText(SilentThresholdText, $"{SilentThreshold?.Value ?? 0:F0} дБ");

        SetText(SoftGateHangoverText, $"{SoftGateHangover?.Value ?? 0:F0} мс");
        SetText(SoftGateAttackText, $"{SoftGateAttack?.Value ?? 0:F0} мс");
        SetText(SoftGateReleaseText, $"{SoftGateRelease?.Value ?? 0:F0} мс");
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;

        UpdateTexts();
        Any_Changed(sender, e);
    }

    private void Any_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading || _settings == null) return;
        SaveToSettings();
    }

    private void SaveToSettings()
    {
        _settings.F0Method = (F0Method.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "rmvpe";
        _settings.EmbedderModel = (Embedder.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "contentvec";
        _settings.EmbedderModelCustom = EmbedderCustom.Text.Trim();

        _settings.IndexRate = IndexRate.Value;
        _settings.Protect = Protect.Value;
        _settings.VolumeEnvelope = VolumeEnvelope.Value;

        _settings.Autotune = Autotune.IsChecked == true;
        _settings.AutotuneStrength = AutotuneStrength.Value;
        _settings.ProposedPitch = ProposedPitch.IsChecked == true;
        _settings.ProposedPitchThreshold = ProposedPitchThreshold.Value;

        _settings.CleanAudio = CleanAudio.IsChecked == true;
        _settings.CleanStrength = CleanStrength.Value;
        _settings.PostProcess = PostProcess.IsChecked == true;

        _settings.Reverb = Reverb.IsChecked == true;
        _settings.ReverbRoomSize = ReverbRoom.Value;
        _settings.ReverbWetLevel = ReverbWet.Value;
        _settings.Compressor = Compressor.IsChecked == true;
        _settings.CompressorThresholdDb = CompThreshold.Value;
        _settings.CompressorRatio = CompRatio.Value;
        _settings.Limiter = Limiter.IsChecked == true;
        _settings.LimiterThresholdDb = LimiterThreshold.Value;

        _settings.ReadChunkSize = (int)ChunkSize.Value;
        _settings.CrossFadeOverlapSize = CrossFade.Value;
        _settings.ExtraConvertSize = ExtraConvert.Value;
        _settings.SilentThreshold = (int)SilentThreshold.Value;

        _settings.UsePhaseVocoder = UsePhaseVocoder.IsChecked == true;
        _settings.VadEnabled = VadEnabled.IsChecked == true;
        _settings.ExclusiveMode = ExclusiveMode.IsChecked == true;
        _settings.Device = (ComputeDevice)Math.Max(0, DeviceBox.SelectedIndex);

        _settings.Precision = (ComputePrecision)Math.Max(0, PrecisionBox.SelectedIndex);
        _settings.AllowTf32 = AllowTf32.IsChecked == true;
        _settings.TorchCompileEnabled = TorchCompile.IsChecked == true;
        _settings.TorchCompileMode = (TorchCompileModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString()
                                     ?? "reduce-overhead";
        _settings.ReduceGpuSync = ReduceGpuSync.IsChecked == true;
        _settings.UseRingBuffer = UseRingBuffer.IsChecked == true;

        _settings.VolumeGainMode = (VolumeGainMode)Math.Max(0, VolumeGainModeBox.SelectedIndex);
        _settings.SoftGateEnabled = SoftGate.IsChecked == true;
        _settings.SoftGateHangoverMs = (int)SoftGateHangover.Value;
        _settings.SoftGateAttackMs = (int)SoftGateAttack.Value;
        _settings.SoftGateReleaseMs = (int)SoftGateRelease.Value;

        _settings.ProxyMode = (ProxyMode)Math.Max(0, ProxyModeBox.SelectedIndex);
        _settings.ProxyHost = ProxyHost.Text.Trim();
        _settings.ProxyPort = int.TryParse(ProxyPort.Text.Trim(), out var port) ? port : 1080;
        _settings.ProxyUser = ProxyUser.Text.Trim();
        _settings.ProxyPassword = ProxyPass.Password;
        _settings.ProxyForPip = ProxyForPip.IsChecked == true;
        _settings.ProxyForHuggingFace = ProxyForHf.IsChecked == true;

        SettingsStore.Save(_settings);
        SetText(SaveStatus, $"Сохранено в {DateTime.Now:HH:mm:ss}");
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        SaveToSettings();

        await _bridge.PushSettingsAsync();
        SaveStatus.Text = "Настройки отправлены в движок";
        Log.Info("Settings", "Настройки применены к запущенному движку");

        MessageBox.Show("Сразу применяются: согласование громкости, soft-gate с его временами, синки GPU→CPU.\n\n"
            + "Требуют перезапуска (Стоп → Старт): размер чанка, кроссфейд, доп. преобразование, тип тензоров (fp16/bf16), TF32, torch.compile и кольцевой буфер.",
            "Настройки", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show("Сбросить все настройки модели к значениям по умолчанию?", "Подтверждение",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _settings.ResetModelDefaults();
        SettingsStore.Save(_settings);
        LoadFromSettings();

        SaveStatus.Text = "Сброшено";
    }

    private async void TestProxy_Click(object sender, RoutedEventArgs e)
    {
        SaveToSettings();
        ProxyStatus.Text = "Проверка...";

        var (ok, message) = await HttpFactory.TestConnectionAsync(_settings);

        ProxyStatus.Text = ok ? "Соединение есть ✓ (" + message + ")" : "Нет соединения ✗ (" + message + ")";
        ProxyStatus.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Ok" : "Err");
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
    }

    // ---- DirectML: догрузка и возврат обычного PyTorch ------------------------------

    /// <summary>Какая сборка torch сейчас лежит в рантайме — по метке установки.</summary>
    private static string TorchFlavorText() => RuntimeInstaller.InstalledTorchFlavor() switch
    {
        "cu121" => "NVIDIA CUDA 12.1",
        "dml" => "DirectML (AMD / Intel)",
        "cpu" => "CPU",
        _ => "неизвестна"
    };

    private async void DirectMlCheck_Click(object sender, RoutedEventArgs e)
    {
        DirectMlStatus.Text = "Состояние: проверяю...";

        try
        {
            var installer = new RuntimeInstaller(_settings);
            var status = await installer.DirectMlStatusAsync(CancellationToken.None);

            DirectMlStatus.Text = "Состояние: " + status + ". Сборка torch — " + TorchFlavorText() + ".";
        }
        catch (Exception ex)
        {
            DirectMlStatus.Text = "Состояние: проверить не удалось — " + ex.Message;
        }
    }

    private async void DirectMlInstall_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Будет установлена сборка PyTorch 2.4.1 и torch-directml (около 2 ГБ).\n\n"
            + "Если сейчас стоит CUDA-сборка, она будет заменена — вернуть её можно кнопкой "
            + "«Вернуть обычный PyTorch».\n\nПродолжить?",
            "Установка DirectML", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        var ok = await RunDirectMlTaskAsync((installer, progress, ct) => installer.InstallDirectMlAsync(progress, ct),
            "Установка DirectML");

        if (!ok) return;

        // Без переключения режима установка ничего не изменит: движок по-прежнему пойдёт на CPU.
        DeviceBox.SelectedIndex = (int)ComputeDevice.DirectMl;
    }

    private async void DirectMlRestore_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Будет удалён torch-directml и возвращена обычная сборка PyTorch "
            + "(CUDA — если есть подходящая NVIDIA, иначе CPU).\n\nПродолжить?",
            "Возврат обычного PyTorch", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        var ok = await RunDirectMlTaskAsync((installer, progress, ct) => installer.RestoreDefaultTorchAsync(progress, ct),
            "Возврат обычного PyTorch");

        if (!ok) return;

        DeviceBox.SelectedIndex = (int)ComputeDevice.Auto;
    }

    /// <summary>Общая обвязка для долгих операций с pip: прогресс, блокировка кнопок, отчёт.</summary>
    private async Task<bool> RunDirectMlTaskAsync(
        Func<RuntimeInstaller, IProgress<InstallProgress>, CancellationToken, Task> action,
        string title)
    {
        DirectMlCheckButton.IsEnabled = false;
        DirectMlInstallButton.IsEnabled = false;
        DirectMlRestoreButton.IsEnabled = false;
        DirectMlProgress.Visibility = Visibility.Visible;
        DirectMlProgress.Value = 0;

        var progress = new Progress<InstallProgress>(p =>
        {
            DirectMlProgress.Value = Math.Clamp(p.Percent, 0, 100);
            DirectMlStatus.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Stage : p.Stage + " — " + p.Detail;
        });

        try
        {
            var installer = new RuntimeInstaller(_settings);
            await action(installer, progress, CancellationToken.None);

            DirectMlStatus.Text = title + " завершена. Сборка torch — " + TorchFlavorText()
                                  + ". Нажмите Стоп → Старт, чтобы движок подхватил изменения.";
            Log.Info("Settings", title + ": готово");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Settings", title + " не удалась", ex);
            DirectMlStatus.Text = title + " не удалась: " + ex.Message;
            return false;
        }
        finally
        {
            DirectMlProgress.Visibility = Visibility.Collapsed;
            DirectMlCheckButton.IsEnabled = true;
            DirectMlInstallButton.IsEnabled = true;
            DirectMlRestoreButton.IsEnabled = true;
        }
    }
}
