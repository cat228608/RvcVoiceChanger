using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Net;
using RvcVoiceChanger.Runtime;

namespace RvcVoiceChanger.Views;

public partial class BootstrapWindow : Window
{
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    private readonly string _installLogFile = Path.Combine(AppPaths.Logs, "install.log");

    /// <summary>Сколько примерно байт дописано с последней проверки размера install.log.</summary>
    private long _installLogBytes;

    public bool InstallSucceeded { get; private set; }

    /// <summary>Что нашёл детектор видеокарт: нужно и для подсказки, и для вопроса про DirectML.</summary>
    private GpuRecommendation? _gpu;

    public BootstrapWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        ProxyMode.SelectedIndex = (int)_settings.ProxyMode;
        ProxyHost.Text = _settings.ProxyHost;
        ProxyPort.Text = _settings.ProxyPort.ToString();
        ProxyUser.Text = _settings.ProxyUser;
        ProxyPass.Password = _settings.ProxyPassword;
        PipIndex.Text = _settings.PipIndexUrl;
        ProxyForPip.IsChecked = _settings.ProxyForPip;
        ProxyForHf.IsChecked = _settings.ProxyForHuggingFace;

        LogPathText.Text = _installLogFile;

        DescribeGpu();

        // Вопрос задаём уже после появления окна, иначе диалог всплывёт раньше самого окна.
        Loaded += (_, _) => AskAboutDirectMlIfNeeded();

        // На этом этапе вкладки «Логи» ещё нет, поэтому тянем весь общий журнал сюда.
        Log.Written += OnLogWritten;
        Closed += (_, _) => Log.Written -= OnLogWritten;

        Append("Журнал установки: " + _installLogFile);
        Append("Общий журнал программы: " + AppPaths.Logs);
    }

    /// <summary>Всё, что пишется в общий лог, дублируем в журнал установки: вкладки «Логи» ещё нет.</summary>
    private void OnLogWritten(LogEntry entry)
    {
        if (entry.Level < LogLevel.Debug) return;

        Dispatcher.BeginInvoke(new Action(() =>
            AppendRaw($"[{entry.Time:HH:mm:ss}] {entry.Level.ToString().ToUpperInvariant()} {entry.Source}: {entry.Message}")));
    }

    /// <summary>Показывает найденные видеокарты и, если пригодной NVIDIA нет, предлагает DirectML.</summary>
    private void DescribeGpu()
    {
        try
        {
            // NvidiaUsable() — тот же самый критерий, по которому установщик выбирает cu121.
            // Если он говорит "да", мы вообще ничего не спрашиваем и идём привычным путём CUDA.
            _gpu = GpuInspector.Recommend(RuntimeInstaller.NvidiaUsable());
            GpuInfoText.Text = _gpu.Summary;

            if (_gpu.AskUser)
            {
                UseDirectMl.Visibility = Visibility.Visible;
                GpuHintText.Visibility = Visibility.Visible;
                UseDirectMl.IsChecked = _settings.Device == ComputeDevice.DirectMl;
            }
        }
        catch (Exception ex)
        {
            // Детектор видеокарт — вещь вспомогательная, из-за неё установку не рвём.
            GpuInfoText.Text = "Не удалось определить видеокарту — установка пойдёт обычным путём.";
            Append("Не удалось определить видеокарту: " + ex.Message);
        }
    }

    /// <summary>Один раз спрашивает про DirectML, если подходящей NVIDIA не нашлось.</summary>
    private void AskAboutDirectMlIfNeeded()
    {
        if (_gpu is not { AskUser: true }) return;
        if (_settings.DirectMlPrompted) return;

        var name = _gpu.Adapter?.Describe() ?? "видеокарта AMD / Intel";

        var answer = MessageBox.Show(this,
            "Видеокарта NVIDIA с поддержкой CUDA не найдена, зато найдена: " + name + ".\n\n"
            + "Такие карты умеет считать DirectML: вместо обычного PyTorch будет установлена сборка 2.4.1 "
            + "и torch-directml (около 2 ГБ). Это медленнее CUDA, но заметно быстрее процессора.\n\n"
            + "Включить DirectML? Если откажетесь, программа установится в режиме процессора, а включить "
            + "DirectML можно будет позже в настройках.",
            "Найдена видеокарта AMD / Intel", MessageBoxButton.YesNo, MessageBoxImage.Question);

        var useDml = answer == MessageBoxResult.Yes;

        UseDirectMl.IsChecked = useDml;
        _settings.Device = useDml ? ComputeDevice.DirectMl : ComputeDevice.Cpu;
        _settings.DirectMlPrompted = true;
        SettingsStore.Save(_settings);

        Append(useDml
            ? "Выбран DirectML: будет установлена сборка PyTorch 2.4.1 с torch-directml"
            : "DirectML отклонён: установка пойдёт в режиме процессора");
    }

    /// <summary>Переносит выбор по DirectML в настройки. Если есть рабочая NVIDIA — ничего не меняет.</summary>
    private void ApplyDeviceToSettings()
    {
        // Когда карта NVIDIA пригодна, чекбокс скрыт и режим из настроек (Auto/Cuda) остаётся как был.
        if (UseDirectMl.Visibility != Visibility.Visible) return;

        var wanted = UseDirectMl.IsChecked == true ? ComputeDevice.DirectMl : ComputeDevice.Cpu;
        if (_settings.Device == wanted) return;

        _settings.Device = wanted;
        Append("Режим вычислений: " + (wanted == ComputeDevice.DirectMl ? "DirectML (AMD / Intel)" : "процессор"));
    }

    private void ProxyMode_Changed(object sender, RoutedEventArgs e)
    {
        if (ProxyFields != null)
            ProxyFields.IsEnabled = ProxyMode.SelectedIndex > 0;
    }

    private void ApplyProxyToSettings()
    {
        _settings.ProxyMode = (ProxyMode)ProxyMode.SelectedIndex;
        _settings.ProxyHost = ProxyHost.Text.Trim();
        _settings.ProxyPort = int.TryParse(ProxyPort.Text.Trim(), out var port) ? port : 1080;
        _settings.ProxyUser = ProxyUser.Text.Trim();
        _settings.ProxyPassword = ProxyPass.Password;
        _settings.PipIndexUrl = PipIndex.Text.Trim();
        _settings.ProxyForPip = ProxyForPip.IsChecked == true;
        _settings.ProxyForHuggingFace = ProxyForHf.IsChecked == true;
        SettingsStore.Save(_settings);
    }

    private async void TestProxy_Click(object sender, RoutedEventArgs e)
    {
        ApplyProxyToSettings();
        ProxyStatus.Text = "Проверка...";

        var (ok, message) = await HttpFactory.TestConnectionAsync(_settings);

        ProxyStatus.Text = ok ? "Соединение есть ✓ (" + message + ")" : "Нет соединения ✗ (" + message + ")";
        ProxyStatus.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "Ok" : "Err");
    }

    private void LocalEnv_Click(object sender, RoutedEventArgs e)
    {
        ApplyProxyToSettings();

        var window = new LocalEnvWindow(_settings) { Owner = this };
        window.ShowDialog();

        if (window.Imported)
            Append("Пакеты перенесены с диска. Нажмите «Начать установку» — шаг torch будет пропущен.");
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        ApplyDeviceToSettings();
        ApplyProxyToSettings();

        StartButton.IsEnabled = false;
        CancelButton.Content = "Отмена";
        _cts = new CancellationTokenSource();

        var installer = new RuntimeInstaller(_settings);
        var progress = new Progress<InstallProgress>(p =>
        {
            StageText.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Stage : $"{p.Stage} — {p.Detail}";
            Progress.Value = Math.Clamp(p.Percent, 0, 100);
            Append($"[{DateTime.Now:HH:mm:ss}] {StageText.Text}");
        });

        try
        {
            await installer.EnsureInstalledAsync(progress, _cts.Token);

            Append("Проверка установки (self-test)...");
            var check = await installer.SelfTestAsync(_cts.Token);

            if (!check.Success)
            {
                Append("Проверка не пройдена: " + check.Message);
                StageText.Text = "Ошибка проверки";
                StartButton.IsEnabled = true;
                StartButton.Content = "Повторить";
                return;
            }

            Append("Всё готово: " + check.Message);
            StageText.Text = "Готово";
            Progress.Value = 100;
            InstallSucceeded = true;

            await Task.Delay(600);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            Append("Установка отменена пользователем");
            StageText.Text = "Отменено";
            StartButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Log.Error("Bootstrap", "Ошибка установки", ex);
            Append("ОШИБКА: " + ex.Message);
            Append("Совет: если сайты заблокированы — укажите прокси выше и повторите.");
            StageText.Text = "Ошибка";
            StartButton.IsEnabled = true;
            StartButton.Content = "Повторить";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is { IsCancellationRequested: false })
        {
            _cts.Cancel();
            return;
        }

        DialogResult = InstallSucceeded;
        Close();
    }

    /// <summary>Пишет строку и в окно, и в файл install.log, и в общий журнал.</summary>
    private void Append(string line)
    {
        Log.Info("Bootstrap", line);
    }

    /// <summary>Пишет только в окно и в install.log (без повторной отправки в общий журнал).</summary>
    private void AppendRaw(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => AppendRaw(line)));
            return;
        }

        InstallLog.AppendText(line + Environment.NewLine);
        InstallLog.ScrollToEnd();

        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            File.AppendAllText(_installLogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}",
                new UTF8Encoding(false));

            // Журнал установки тоже живёт в общем лимите: старые строки вытесняются новыми.
            _installLogBytes += line.Length + 24;
            if (_installLogBytes >= 32 * 1024)
            {
                _installLogBytes = 0;
                Log.TrimFile(_installLogFile, Log.InstallLogMaxBytes);
            }
        }
        catch
        {
            // Запись в файл — вещь вторичная, из-за неё установку не рвём.
        }
    }

    private void OpenInstallLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.Flush();
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.Logs + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendRaw("Не удалось открыть папку логов: " + ex.Message);
        }
    }

    private void CopyInstallLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(InstallLog.Text);
            AppendRaw("Журнал скопирован в буфер обмена");
        }
        catch (Exception ex)
        {
            AppendRaw("Не удалось скопировать журнал: " + ex.Message);
        }
    }
}
