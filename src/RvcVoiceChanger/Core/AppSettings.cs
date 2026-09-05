using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RvcVoiceChanger.Core;

public enum ProxyMode { None, Http, Socks5 }

public enum ComputeDevice { Auto, Cuda, Cpu }

/// <summary>
/// Все настройки программы. Сериализуются в settings.json.
/// Класс реализует INotifyPropertyChanged — XAML биндится напрямую.
/// Любое изменение аудио-параметра уезжает в python-воркер на лету.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    // ---------- Сеть / прокси ----------
    private ProxyMode _proxyMode = ProxyMode.None;
    private string _proxyHost = "";
    private int _proxyPort = 1080;
    private string _proxyUser = "";
    private string _proxyPassword = "";
    private string _pipIndexUrl = "";
    private bool _proxyForHuggingFace = true;
    private bool _proxyForPip = true;
    private string _torchIndexUrl = "";
    private string _pythonZipUrl = "";
    private string _hfEndpoint = "";
    private string _hfToken = "";
    private string _weightsRepo = "";
    private string _vbCableUrl = "";
    private string _hfSearchFilter = "rvc";
    private int _hfPageSize = 10;
    private string _parserSource = "huggingface";
    private string _voiceModelsEndpoint = "";

    private int _settingsVersion;

    /// <summary>Версия схемы настроек — для одноразовых миграций старых settings.json.</summary>
    public int SettingsVersion { get => _settingsVersion; set => Set(ref _settingsVersion, value); }

    private string _theme = "dark";

    /// <summary>Тема оформления: "dark" или "light".</summary>
    public string Theme { get => _theme; set => Set(ref _theme, string.IsNullOrWhiteSpace(value) ? "dark" : value); }

    public ProxyMode ProxyMode { get => _proxyMode; set => Set(ref _proxyMode, value); }
    public string ProxyHost { get => _proxyHost; set => Set(ref _proxyHost, value); }
    public int ProxyPort { get => _proxyPort; set => Set(ref _proxyPort, value); }
    public string ProxyUser { get => _proxyUser; set => Set(ref _proxyUser, value); }
    /// <summary>Пароль прокси. В памяти — открытым текстом, в settings.json — только под DPAPI.</summary>
    [JsonIgnore]
    public string ProxyPassword { get => _proxyPassword; set => Set(ref _proxyPassword, value); }

    /// <summary>Сериализуемое представление пароля: DPAPI (CurrentUser) + Base64.</summary>
    [JsonPropertyName("ProxyPasswordProtected")]
    public string? ProxyPasswordProtected
    {
        get => ProtectSecret(_proxyPassword);
        set { var plain = UnprotectSecret(value); if (plain != null) _proxyPassword = plain; }
    }

    /// <summary>Миграция со старых settings.json, где пароль лежал открытым текстом.
    /// При следующем сохранении будет записана только защищённая версия.</summary>
    [JsonPropertyName("ProxyPassword")]
    public string? LegacyProxyPassword
    {
        set { if (!string.IsNullOrEmpty(value)) _proxyPassword = value; }
    }

    private static string? ProtectSecret(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            var raw = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(raw);
        }
        catch (Exception ex)
        {
            Log.Warn("Settings", "DPAPI недоступен, пароль прокси не будет сохранён: " + ex.Message);
            return null;
        }
    }

    private static string? UnprotectSecret(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch (Exception ex)
        {
            Log.Warn("Settings", "Не удалось расшифровать пароль прокси (другой пользователь/машина?): " + ex.Message);
            return null;
        }
    }

    /// <summary>Зеркало PyPI (например https://mirror.yandex.ru/mirrors/pypi/simple). Пусто = официальный.</summary>
    public string PipIndexUrl { get => _pipIndexUrl; set => Set(ref _pipIndexUrl, value); }
    public bool ProxyForHuggingFace { get => _proxyForHuggingFace; set => Set(ref _proxyForHuggingFace, value); }
    public bool ProxyForPip { get => _proxyForPip; set => Set(ref _proxyForPip, value); }

    /// <summary>Индекс для установки torch (пусто = https://download.pytorch.org/whl/cu121 или CPU-индекс).</summary>
    public string TorchIndexUrl { get => _torchIndexUrl; set => Set(ref _torchIndexUrl, value); }

    /// <summary>Прямая ссылка на embeddable-Python zip (пусто = python.org).</summary>
    public string PythonZipUrl { get => _pythonZipUrl; set => Set(ref _pythonZipUrl, value); }

    /// <summary>Зеркало HuggingFace, например https://hf-mirror.com (пусто = huggingface.co).</summary>
    public string HfEndpoint { get => _hfEndpoint; set => Set(ref _hfEndpoint, value); }

    /// <summary>Токен HuggingFace для приватных/гейтед репозиториев (можно пусто).</summary>
    [JsonIgnore]
    public string HfToken { get => _hfToken; set => Set(ref _hfToken, value); }

    [JsonPropertyName("HfTokenProtected")]
    public string? HfTokenProtected
    {
        get => ProtectSecret(_hfToken);
        set { var plain = UnprotectSecret(value); if (plain != null) _hfToken = plain; }
    }

    /// <summary>Репозиторий с весами rmvpe/contentvec/fcpe (пусто = IAHispano/Applio).</summary>
    public string WeightsRepo { get => _weightsRepo; set => Set(ref _weightsRepo, value); }

    /// <summary>URL инсталлятора VB-CABLE (пусто = официальный сайт VB-Audio).</summary>
    public string VbCableUrl { get => _vbCableUrl; set => Set(ref _vbCableUrl, value); }

    /// <summary>Фильтр поиска моделей на HuggingFace.</summary>
    public string HfSearchFilter { get => _hfSearchFilter; set => Set(ref _hfSearchFilter, value); }

    /// <summary>Размер страницы поиска моделей.</summary>
    public int HfPageSize { get => _hfPageSize; set => Set(ref _hfPageSize, value == 0 ? 10 : Math.Clamp(value, 1, 100)); }

    /// <summary>Выбранный сервис в парсере: "huggingface" или "voice-models".</summary>
    public string ParserSource
    {
        get => _parserSource;
        set => Set(ref _parserSource, string.IsNullOrWhiteSpace(value) ? "huggingface" : value.Trim());
    }

    /// <summary>Адрес voice-models.com (пусто = https://voice-models.com, можно указать зеркало).</summary>
    public string VoiceModelsEndpoint { get => _voiceModelsEndpoint; set => Set(ref _voiceModelsEndpoint, value); }

    // ---------- Аудиоустройства ----------
    private string? _inputDeviceId;
    private string? _outputDeviceId;
    private string? _monitorDeviceId;
    private bool _sendToVirtualMic = true;
    private bool _monitorEnabled;
    private bool _exclusiveMode;

    public string? InputDeviceId { get => _inputDeviceId; set => Set(ref _inputDeviceId, value); }

    /// <summary>Основное устройство вывода (обычно CABLE Input = виртуальный микрофон).</summary>
    public string? OutputDeviceId { get => _outputDeviceId; set => Set(ref _outputDeviceId, value); }

    /// <summary>Дополнительный вывод для самопрослушки (наушники).</summary>
    public string? MonitorDeviceId { get => _monitorDeviceId; set => Set(ref _monitorDeviceId, value); }

    public bool SendToVirtualMic { get => _sendToVirtualMic; set => Set(ref _sendToVirtualMic, value); }
    public bool MonitorEnabled { get => _monitorEnabled; set => Set(ref _monitorEnabled, value); }
    public bool ExclusiveMode { get => _exclusiveMode; set => Set(ref _exclusiveMode, value); }

    // ---------- Выбранная модель ----------
    private string? _activeModelId;
    public string? ActiveModelId { get => _activeModelId; set => Set(ref _activeModelId, value); }

    // ---------- Параметры конвертации ----------
    private int _pitch;
    private double _indexRate = 0.5;
    private double _protect = 0.5;
    private double _volumeEnvelope = 1.0;
    private bool _autotune;
    private double _autotuneStrength = 1.0;
    private bool _proposedPitch;
    private double _proposedPitchThreshold = 155.0;
    private bool _cleanAudio;
    private double _cleanStrength = 0.5;
    private bool _postProcess;
    private string _f0Method = "rmvpe";
    private string _embedderModel = "contentvec";
    private string _embedderModelCustom = "";
    private bool _usePhaseVocoder = true;
    private bool _vadEnabled = true;
    private ComputeDevice _device = ComputeDevice.Auto;

    /// <summary>Питч в полутонах (f0_up_key), -24..24.</summary>
    public int Pitch { get => _pitch; set => Set(ref _pitch, value); }
    public double IndexRate { get => _indexRate; set => Set(ref _indexRate, value); }

    /// <summary>Защита глухих согласных (protect), 0..0.5.</summary>
    public double Protect { get => _protect; set => Set(ref _protect, value); }
    public double VolumeEnvelope { get => _volumeEnvelope; set => Set(ref _volumeEnvelope, value); }
    public bool Autotune { get => _autotune; set => Set(ref _autotune, value); }
    public double AutotuneStrength { get => _autotuneStrength; set => Set(ref _autotuneStrength, value); }

    /// <summary>Предполагаемая высота тона (proposed pitch).</summary>
    public bool ProposedPitch { get => _proposedPitch; set => Set(ref _proposedPitch, value); }
    public double ProposedPitchThreshold { get => _proposedPitchThreshold; set => Set(ref _proposedPitchThreshold, value); }
    public bool CleanAudio { get => _cleanAudio; set => Set(ref _cleanAudio, value); }
    public double CleanStrength { get => _cleanStrength; set => Set(ref _cleanStrength, value); }
    public bool PostProcess { get => _postProcess; set => Set(ref _postProcess, value); }

    /// <summary>rmvpe | crepe | crepe-tiny | fcpe.</summary>
    public string F0Method { get => _f0Method; set => Set(ref _f0Method, value); }

    /// <summary>contentvec | chinese-hubert-base | japanese-hubert-base | korean-hubert-base | custom.</summary>
    public string EmbedderModel { get => _embedderModel; set => Set(ref _embedderModel, value); }
    public string EmbedderModelCustom { get => _embedderModelCustom; set => Set(ref _embedderModelCustom, value); }
    public bool UsePhaseVocoder { get => _usePhaseVocoder; set => Set(ref _usePhaseVocoder, value); }
    public bool VadEnabled { get => _vadEnabled; set => Set(ref _vadEnabled, value); }
    public ComputeDevice Device { get => _device; set => Set(ref _device, value); }

    // ---------- Буферизация / задержка ----------
    private int _readChunkSize = 48;
    private double _crossFadeOverlapSize = 0.05;
    private double _extraConvertSize = 0.50;
    private int _silentThreshold = -60;
    private double _inputGainDb;
    private double _outputGainDb;

    /// <summary>Размер чанка в единицах по 128 семплов @48k (как в Applio): 48 ~ 128 мс, 96 ~ 256 мс.</summary>
    public int ReadChunkSize { get => _readChunkSize; set => Set(ref _readChunkSize, value); }

    /// <summary>Размер перекрытия кроссфейда, сек.</summary>
    public double CrossFadeOverlapSize { get => _crossFadeOverlapSize; set => Set(ref _crossFadeOverlapSize, value); }

    /// <summary>Размер дополнительного преобразования, сек.</summary>
    public double ExtraConvertSize { get => _extraConvertSize; set => Set(ref _extraConvertSize, value); }

    /// <summary>Порог тишины, дБ (-90..0).</summary>
    public int SilentThreshold { get => _silentThreshold; set => Set(ref _silentThreshold, value); }

    /// <summary>Усиление ввода, дБ.</summary>
    public double InputGainDb { get => _inputGainDb; set => Set(ref _inputGainDb, value); }

    /// <summary>Усиление вывода, дБ.</summary>
    public double OutputGainDb { get => _outputGainDb; set => Set(ref _outputGainDb, value); }

    // ---------- Пост-обработка (pedalboard) ----------
    private bool _reverb;
    private double _reverbRoomSize = 0.5;
    private double _reverbWet = 0.33;
    private double _reverbDry = 0.4;
    private double _reverbDamping = 0.5;
    private bool _limiter;
    private double _limiterThresholdDb = -6;
    private bool _compressor;
    private double _compressorThresholdDb = -20;
    private double _compressorRatio = 4;

    public bool Reverb { get => _reverb; set => Set(ref _reverb, value); }
    public double ReverbRoomSize { get => _reverbRoomSize; set => Set(ref _reverbRoomSize, value); }
    public double ReverbWetLevel { get => _reverbWet; set => Set(ref _reverbWet, value); }
    public double ReverbDryLevel { get => _reverbDry; set => Set(ref _reverbDry, value); }
    public double ReverbDamping { get => _reverbDamping; set => Set(ref _reverbDamping, value); }
    public bool Limiter { get => _limiter; set => Set(ref _limiter, value); }
    public double LimiterThresholdDb { get => _limiterThresholdDb; set => Set(ref _limiterThresholdDb, value); }
    public bool Compressor { get => _compressor; set => Set(ref _compressor, value); }
    public double CompressorThresholdDb { get => _compressorThresholdDb; set => Set(ref _compressorThresholdDb, value); }
    public double CompressorRatio { get => _compressorRatio; set => Set(ref _compressorRatio, value); }

    // ---------- Прочее ----------
    private LogLevel _logLevel = LogLevel.Info;
    private bool _startMinimized;

    public LogLevel LogLevel { get => _logLevel; set => Set(ref _logLevel, value); }
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Срабатывает на любом изменении — используется для hot-reload воркера.</summary>
    public event Action<string>? Changed;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name != null) Changed?.Invoke(name);
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded != null)
                {
                    Migrate(loaded);
                    Log.Info("Settings", "Настройки загружены");
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings", "Не удалось прочитать settings.json, берём дефолты", ex);
        }

        var fresh = new AppSettings();
        Migrate(fresh);
        return fresh;
    }

    /// <summary>Одноразовые миграции настроек между версиями приложения.</summary>
    private static void Migrate(AppSettings settings)
    {
        if (settings.SettingsVersion < 2)
        {
            // Старые дефолты давали ~600-700 мс задержки (блок 256 мс + завышенный
            // кроссфейд). Если пользователь их не менял — переводим на быстрые.
            if (settings.ReadChunkSize >= 96) settings.ReadChunkSize = 48;
            if (settings.CrossFadeOverlapSize >= 0.10) settings.CrossFadeOverlapSize = 0.05;
            if (settings.ExtraConvertSize > 0.5) settings.ExtraConvertSize = 0.5;
            settings.SettingsVersion = 2;
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, Options);
            var tmp = AppPaths.SettingsFile + ".tmp";

            // Атомарная замена: крэш в момент записи не оставит битый settings.json.
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true); // fsync — данные гарантированно на диске до подмены
            }

            if (File.Exists(AppPaths.SettingsFile))
                File.Replace(tmp, AppPaths.SettingsFile, null);
            else
                File.Move(tmp, AppPaths.SettingsFile);
        }
        catch (Exception ex)
        {
            Log.Error("Settings", "Не удалось сохранить настройки", ex);
        }
    }
}
