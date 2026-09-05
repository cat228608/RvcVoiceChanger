using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Net;

namespace RvcVoiceChanger.Runtime;

public sealed record InstallProgress(string Stage, string Detail, double Percent);

public sealed record InstallStamp(string PythonVersion, string BackendVersion, string TorchFlavor, DateTime InstalledAt);

/// <summary>
/// Первый запуск: ставит portable Python, pip-зависимости, бэкенд RVC и веса предобученных
/// моделей (rmvpe, contentvec). Всё скачивание идёт через FileDownloader, т.е. через прокси,
/// если он задан в настройках.
/// </summary>
public sealed class RuntimeInstaller
{
    // Версия 3.11 выбрана не случайно: так можно переносить готовые пакеты
    // из самого распространённого сегодня окружения — бинарники привязаны к версии Python.
    private const string PythonVersion = "3.11.9";
    private const string PythonZipUrl = "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip";

    /// <summary>Файл, по которому видно, что распакована именно нужная версия Python.</summary>
    private const string PythonDllName = "python311.dll";

    /// <summary>Версия torch. Поднята до 2.5.1: именно она чаще всего уже есть у людей на диске.</summary>
    private const string TorchVersion = "2.5.1";
    private const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";

    // Запасной способ бутстрапа pip: zipapp, работает без site-packages.
    private const string PipPyzUrl = "https://bootstrap.pypa.io/pip/pip.pyz";

    // Свежие pip (25.x/26.x) падают при работе через прокси:
    // TypeError: PoolKey.__new__() got an unexpected keyword argument 'key_proxy_ssl_context'.
    // Поэтому при включённом прокси фиксируем заведомо рабочую версию.
    private const string PipPinnedVersion = "24.2";
    private const string PipPinnedWheelUrl = "https://files.pythonhosted.org/packages/d4/55/90db48d85f7689ec6f81c0db0622d704306c5284850383c090e6c7195a5c/pip-24.2-py3-none-any.whl";
    private const string PypiPipApiBase = "https://pypi.org/pypi/pip/";

    private bool _pipRepaired;

    // PySocks нужен pip-у, чтобы уметь ходить через SOCKS5. Качаем его сами — наш загрузчик SOCKS5 умеет.
    private const string PySocksWheelUrl = "https://files.pythonhosted.org/packages/8d/59/b4572118e098ac8e46e399a1dd0f2d85403ce8bbaad9ec79373ed6badaf9/PySocks-1.7.1-py3-none-any.whl";

    // Бэкенд = исходники RVC (Applio 3.6.4). Ссылку можно переопределить в настройках.
    private const string BackendVersion = "3.6.6";


    /// <summary>
    /// Веса моделей. В exe их не вошьёшь — это сотни мегабайт, но любой файл можно
    /// положить руками в папку prereq — тогда скачивание пропускается.
    /// Optional = без него программа работает (нужен только для отдельных алгоритмов).
    /// </summary>
    private const string DefaultWeightsRepo = "IAHispano/Applio";
    private const string DefaultHfEndpoint = "https://huggingface.co";

    /// <summary>Зеркало HF из настроек (например https://hf-mirror.com) или официальный адрес.</summary>
    private string HfEndpoint =>
        string.IsNullOrWhiteSpace(_settings.HfEndpoint) ? DefaultHfEndpoint : _settings.HfEndpoint.Trim().TrimEnd('/');

    private string WeightsRepo =>
        string.IsNullOrWhiteSpace(_settings.WeightsRepo) ? DefaultWeightsRepo : _settings.WeightsRepo.Trim().Trim('/');

    /// <summary>
    /// Веса моделей. В exe их не вошьёшь — это сотни мегабайт, но любой файл можно
    /// положить руками в папку prereq — тогда скачивание пропускается.
    /// MinBytes — минимальный правдоподобный размер: защита от HTML-заглушек и оборванных загрузок.
    /// </summary>
    private (string Url, string RelativePath, bool Optional, long MinBytes)[] PrerequisiteList()
    {
        var baseUrl = HfEndpoint + "/" + WeightsRepo + "/resolve/main/Resources/";
        return new[]
        {
            (baseUrl + "predictors/rmvpe.pt", "rvc/models/predictors/rmvpe.pt", false, 50L * 1024 * 1024),
            (baseUrl + "embedders/contentvec/pytorch_model.bin", "rvc/models/embedders/contentvec/pytorch_model.bin", false, 50L * 1024 * 1024),
            (baseUrl + "embedders/contentvec/config.json", "rvc/models/embedders/contentvec/config.json", false, 100L),
            // fcpe нужен только если выбрать алгоритм fcpe вместо rmvpe.
            (baseUrl + "predictors/fcpe.pt", "rvc/models/predictors/fcpe.pt", true, 10L * 1024 * 1024),
        };
    }


    private readonly AppSettings _settings;
    private readonly FileDownloader _downloader;

    public RuntimeInstaller(AppSettings settings)
    {
        _settings = settings;
        _downloader = new FileDownloader(settings);
    }

    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(AppPaths.InstallStampFile)) return false;
            if (!File.Exists(AppPaths.PythonExe)) return false;
            if (!File.Exists(AppPaths.WorkerScript)) return false;

            var stamp = JsonSerializer.Deserialize<InstallStamp>(File.ReadAllText(AppPaths.InstallStampFile));
            if (stamp == null || stamp.BackendVersion != BackendVersion) return false;

            // Смена версии Python делает все старые пакеты негодными — ставим рантайм заново.
            if (stamp.PythonVersion != PythonVersion) return false;

            // Содержимое бэкенда меняется вместе с обновлениями программы, а номер версии Applio — нет.
            // Сравниваем хеш вшитого архива с тем, что развёрнуто на диске.
            return BackendContentIsCurrent();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>cu121-сборка torch требует драйвер NVIDIA не ниже 525.60.</summary>
    private const int MinCudaDriverMajor = 525;

    private static string RunTool(string exe, string args, int timeoutMs = 5000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return "";
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(timeoutMs);
        return p.HasExited && p.ExitCode == 0 ? output : "";
    }

    /// <summary>Есть ли NVIDIA GPU — от этого зависит, ставим ли torch+cu121 или CPU-сборку.</summary>
    public static bool HasNvidiaGpu()
    {
        try
        {
            return RunTool("nvidia-smi", "-L").Contains("GPU", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Мажорная версия драйвера NVIDIA или 0, если узнать не удалось.</summary>
    public static int NvidiaDriverMajor()
    {
        try
        {
            var output = RunTool("nvidia-smi", "--query-gpu=driver_version --format=csv,noheader").Trim();
            var first = output.Split('\n').FirstOrDefault()?.Trim() ?? "";
            var dot = first.IndexOf('.');
            var majorText = dot > 0 ? first[..dot] : first;
            return int.TryParse(majorText, out var major) ? major : 0;
        }
        catch
        {
            return 0;
        }
    }

    public async Task InstallAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var gpu = HasNvidiaGpu();

        if (gpu)
        {
            var driver = NvidiaDriverMajor();
            if (driver > 0 && driver < MinCudaDriverMajor)
            {
                // Ставить cu121 на старый драйвер бессмысленно — CUDA просто не заведётся.
                // Откат на CPU делаем ГРОМКО, а не молча.
                var warning = $"Драйвер NVIDIA {driver} старее требуемого ({MinCudaDriverMajor}+ для CUDA 12.1). " +
                              "Ставлю CPU-сборку torch. Обновите драйвер и переустановите окружение, чтобы включить GPU.";
                Log.Warn("Installer", warning);
                progress.Report(new InstallProgress("GPU", warning, 1));
                gpu = false;
            }
        }

        var torchFlavor = gpu ? "cu121" : "cpu";
        Log.Info("Installer", gpu
            ? "Найдена NVIDIA GPU — ставим torch с CUDA 12.1"
            : "NVIDIA GPU не найдена или непригодна — ставим CPU-сборку torch (будет медленнее)");
        if (!gpu)
            progress.Report(new InstallProgress("GPU", "GPU не используется: будет установлена CPU-версия torch", 1));

        await CheckProxyReachableAsync(progress, ct);
        await EnsurePythonAsync(progress, ct);
        await EnsurePipAsync(progress, ct);
        await InstallPipPackagesAsync(torchFlavor, progress, ct);
        await EnsureBackendAsync(progress, ct);
        await EnsurePrerequisitesAsync(progress, ct);
        await VerifyAsync(progress, ct);

        var stamp = new InstallStamp(PythonVersion, BackendVersion, torchFlavor, DateTime.Now);
        File.WriteAllText(AppPaths.InstallStampFile, JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }));

        progress.Report(new InstallProgress("Готово", "Все зависимости установлены и проверены", 100));
        Log.Info("Installer", "Установка завершена");
    }

    private async Task EnsurePythonAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var correctVersion = File.Exists(Path.Combine(AppPaths.Python, PythonDllName));

        if (File.Exists(AppPaths.PythonExe) && correctVersion)
        {
            progress.Report(new InstallProgress("Python", "Уже установлен", 10));
            return;
        }

        if (File.Exists(AppPaths.PythonExe) && !correctVersion)
        {
            // Остался рантайм от другой версии Python: его пакеты всё равно неработоспособны.
            progress.Report(new InstallProgress("Python", "Найдена другая версия Python — пересоздаю рантайм...", 2));
            Log.Info("Installer", "Удаляю старый рантайм Python: нужен " + PythonVersion);

            // Пакеты в site-packages могли качаться часами (torch — гигабайты).
            // Перед пересозданием рантайма откладываем их в сторону и возвращаем после распаковки:
            // для той же версии Python они полностью работоспособны, а для другой их перепроверит pip.
            var savedPackages = Path.Combine(AppPaths.Temp, "site-packages-saved");
            var oldPackages = Path.Combine(AppPaths.Python, "Lib", "site-packages");

            try
            {
                if (Directory.Exists(savedPackages)) Directory.Delete(savedPackages, true);
                if (Directory.Exists(oldPackages))
                {
                    Directory.CreateDirectory(AppPaths.Temp);
                    Directory.Move(oldPackages, savedPackages);
                    Log.Info("Installer", "site-packages отложены в сторону перед переустановкой Python");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Installer", "Не удалось сохранить site-packages, пакеты будут скачаны заново: " + ex.Message);
            }

            try
            {
                Directory.Delete(AppPaths.Python, recursive: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Не удалось удалить старую папку Python (" + AppPaths.Python + ").\n" +
                    "Закройте все процессы python.exe и повторите. Причина: " + ex.Message, ex);
            }

            Directory.CreateDirectory(AppPaths.Python);

            try
            {
                if (Directory.Exists(savedPackages))
                {
                    Directory.CreateDirectory(Path.Combine(AppPaths.Python, "Lib"));
                    Directory.Move(savedPackages, Path.Combine(AppPaths.Python, "Lib", "site-packages"));
                    Log.Info("Installer", "site-packages возвращены на место");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Installer", "Не удалось вернуть site-packages: " + ex.Message);
            }
        }

        progress.Report(new InstallProgress("Python", $"Скачивание Python {PythonVersion}...", 2));

        // Ссылку можно переопределить в настройках (зеркало python.org для заблокированных сетей).
        var pythonUrl = string.IsNullOrWhiteSpace(_settings.PythonZipUrl) ? PythonZipUrl : _settings.PythonZipUrl.Trim();

        var zip = Path.Combine(AppPaths.Downloads, "python-embed.zip");
        await _downloader.DownloadAsync(pythonUrl, zip,
            new Progress<DownloadProgress>(p => progress.Report(new InstallProgress("Python", $"Скачивание Python: {p.Text}", 2 + p.Percent * 0.05))), ct);

        progress.Report(new InstallProgress("Python", "Распаковка...", 8));
        ZipFile.ExtractToDirectory(zip, AppPaths.Python, overwriteFiles: true);

        // В embeddable-сборке нужно включить site-packages, иначе pip не будет виден.
        var pthFile = Directory.GetFiles(AppPaths.Python, "python*._pth").FirstOrDefault();
        if (pthFile != null)
        {
            var lines = File.ReadAllLines(pthFile).ToList();
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Trim() == "#import site") lines[i] = "import site";
            if (!lines.Any(l => l.Trim() == "import site")) lines.Add("import site");
            if (!lines.Any(l => l.Trim() == "Lib\\site-packages")) lines.Add("Lib\\site-packages");
            File.WriteAllLines(pthFile, lines);
            Log.Debug("Installer", $"Отредактирован {Path.GetFileName(pthFile)}");
        }

        progress.Report(new InstallProgress("Python", "Python установлен", 10));
    }

    private async Task EnsurePipAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var pipExists = Directory.Exists(Path.Combine(AppPaths.Python, "Lib", "site-packages", "pip"));
        if (pipExists)
        {
            progress.Report(new InstallProgress("pip", "Уже установлен", 14));
            return;
        }

        // Способ 1: get-pip.py (в нём pip уже вшит, сеть не нужна).
        progress.Report(new InstallProgress("pip", "Скачивание get-pip.py...", 11));

        var getPip = Path.Combine(AppPaths.Downloads, "get-pip.py");
        if (File.Exists(getPip)) File.Delete(getPip);
        await _downloader.DownloadAsync(GetPipUrl, getPip, null, ct);
        ValidatePythonScript(getPip, "get-pip.py");

        progress.Report(new InstallProgress("pip", "Установка pip...", 12));

        // Сам pip вшит в get-pip.py, а setuptools и wheel он тянет из сети. На этом шаге
        // поддержки SOCKS5 ещё нет, поэтому сеть здесь отключаем совсем — именно из-за этого
        // в журнале сыпались предупреждения о повторах. Что нужно — доставим позже, через прокси.
        // ВАЖНО: на этом этапе PySocks ещё не установлен, поэтому при SOCKS5-прокси
        // любой сетевой запрос pip упадёт. pip вшит в get-pip.py — ставим его совсем без сети
        // (--no-index), а setuptools/wheel доставим позже, когда прокси заработает полностью.
        var pipArgs = new List<string>
        {
            getPip, "--no-warn-script-location", "--no-cache-dir", "--no-setuptools", "--no-wheel", "--no-index"
        };

        var (code, tail) = await RunPythonLoggedAsync(pipArgs.ToArray(), progress, "pip", 12, 14, ct);

        if (code != 0)
        {
            Log.Warn("Installer", "get-pip.py вернул код " + code + ", пробуем запасной способ (pip.pyz)");
            progress.Report(new InstallProgress("pip", "get-pip.py не сработал, пробуем pip.pyz...", 13));

            await BootstrapPipWithPyzAsync(progress, ct);
        }

        // Проверяем, что pip реально завёлся.
        var (checkCode, checkOut) = await RunPythonCaptureAsync(new[] { "-m", "pip", "--version" }, ct);
        if (checkCode != 0)
        {
            throw new InvalidOperationException(
                "Не удалось установить pip.\n\n" +
                "Вывод get-pip.py:\n" + tail + "\n\n" +
                "Вывод проверки:\n" + checkOut.Trim() + "\n\n" +
                "Полный журнал: " + Path.Combine(AppPaths.Logs, "install.log"));
        }

        Log.Info("Installer", "pip готов: " + checkOut.Trim());
        progress.Report(new InstallProgress("pip", checkOut.Trim(), 14));
    }

    /// <summary>Запасной бутстрап pip через zipapp pip.pyz.</summary>
    private async Task BootstrapPipWithPyzAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var pyz = Path.Combine(AppPaths.Downloads, "pip.pyz");
        if (File.Exists(pyz)) File.Delete(pyz);

        await _downloader.DownloadAsync(PipPyzUrl, pyz, null, ct);

        var size = new FileInfo(pyz).Length;
        if (size < 100_000)
            throw new InvalidOperationException(
                "Файл pip.pyz скачался битым (" + size + " байт). Похоже, прокси или провайдер подменяет ответ. Проверьте настройки прокси.");

        // pip.pyz тоже не умеет SOCKS5 без PySocks, поэтому wheel самого pip скачиваем
        // нашим загрузчиком (он умеет SOCKS5 нативно) и ставим офлайн.
        var (wheelUrl, wheelSha) = await ResolvePypiWheelAsync("pip", PipPinnedVersion, PipPinnedWheelUrl, ct);
        var pipWheel = Path.Combine(AppPaths.Downloads, "pip-" + PipPinnedVersion + "-py3-none-any.whl");
        if (!File.Exists(pipWheel) || new FileInfo(pipWheel).Length < 500_000)
            await _downloader.DownloadAsync(wheelUrl, pipWheel, null, ct, expectedSha256: wheelSha);

        var args = new List<string> { pyz, "install", "--no-warn-script-location", "--no-cache-dir", "--no-index", pipWheel };

        var (code, tail) = await RunPythonLoggedAsync(args.ToArray(), progress, "pip", 13, 14, ct);
        if (code != 0)
            throw new InvalidOperationException(
                "Запасной способ установки pip тоже не сработал (код " + code + ").\n\n" + tail);
    }

    /// <summary>
    /// Аргументы сети для pip: прокси, зеркало, терпеливые таймауты.
    /// Вызывать во ВСЕХ запусках pip, которые лезут в сеть, иначе pip пойдёт напрямую
    /// и будет бесконечно писать "WARNING: Retrying (Retry(total=4...))".
    /// </summary>
    private IEnumerable<string> PipNetworkArgs(bool includeIndex = true)
    {
        var result = new List<string>();

        var proxy = HttpFactory.ProxyUrlForChildProcess(_settings);
        if (proxy != null && _settings.ProxyForPip)
        {
            var isSocks = proxy.StartsWith("socks", StringComparison.OrdinalIgnoreCase);
            if (isSocks && !PySocksReady())
            {
                // Без PySocks pip упадёт на первом же запросе. Говорим об этом явно,
                // а не молча пропускаем прокси (иначе трафик неожиданно пойдёт напрямую).
                Log.Warn("Installer", "SOCKS5 выбран, но PySocks ещё не установлен — этот запуск pip идёт без прокси");
            }
            else
            {
                result.Add("--proxy");
                result.Add(proxy);
            }
        }

        if (includeIndex && !string.IsNullOrWhiteSpace(_settings.PipIndexUrl))
        {
            result.Add("--index-url");
            result.Add(_settings.PipIndexUrl.Trim());
        }

        // Медленный канал и прокси — главная причина обрывов на больших файлах.
        result.Add("--retries");
        result.Add("10");
        result.Add("--timeout");
        result.Add("60");

        return result;
    }

    /// <summary>
    /// Когда вывод pip перенаправлен, обычная полоска загрузки отключается и процентов не видно.
    /// Режим raw печатает строки "Progress X of Y", которые мы разбираем сами.
    /// </summary>
    private static IEnumerable<string> PipProgressArgs() => new[] { "--progress-bar", "raw" };

    /// <summary>Разбирает строку pip вида "Progress 12345 of 67890".</summary>
    private static bool TryParseDownloadProgress(string line, out long received, out long total)
    {
        received = 0;
        total = 0;

        var text = line.Trim();
        if (!text.StartsWith("Progress ", StringComparison.OrdinalIgnoreCase)) return false;

        var rest = text.Substring("Progress ".Length);
        var parts = rest.Split(new[] { " of " }, StringSplitOptions.None);

        if (!long.TryParse(parts[0].Trim(), out received)) return false;
        if (parts.Length > 1) long.TryParse(parts[1].Trim(), out total);

        return true;
    }

    /// <summary>Из строки "Downloading &lt;url&gt; (2.4 GB)" достаёт только имя файла.</summary>
    private static string? TryParseDownloadStart(string line)
    {
        var text = line.Trim();
        if (!text.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase)) return null;

        var rest = text.Substring("Downloading ".Length).Trim();

        var space = rest.IndexOf(' ');
        if (space > 0) rest = rest.Substring(0, space);

        var query = rest.IndexOf('?');
        if (query > 0) rest = rest.Substring(0, query);

        var slash = rest.LastIndexOf('/');
        if (slash >= 0 && slash < rest.Length - 1) rest = rest.Substring(slash + 1);

        return rest.Length == 0 ? null : rest;
    }

    /// <summary>Из строки "Downloading &lt;url&gt; (2.4 GB)" достаёт полный размер в байтах.</summary>
    private static long TryParseSizeHint(string line)
    {
        var open = line.LastIndexOf('(');
        var close = line.LastIndexOf(')');
        if (open < 0 || close <= open) return 0;

        var text = line.Substring(open + 1, close - open - 1).Trim();
        var parts = text.Split(' ');
        if (parts.Length != 2) return 0;

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return 0;

        var unit = parts[1].Trim().ToLowerInvariant();
        var multiplier = 0.0;

        if (unit == "b" || unit == "byte" || unit == "bytes") multiplier = 1.0;
        else if (unit == "kb") multiplier = 1024.0;
        else if (unit == "mb") multiplier = 1024.0 * 1024.0;
        else if (unit == "gb") multiplier = 1024.0 * 1024.0 * 1024.0;

        if (multiplier <= 0) return 0;

        return (long)(value * multiplier);
    }

    private static string FormatBytes(long value)
    {
        if (value >= 1024L * 1024L * 1024L)
            return (value / (1024.0 * 1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " ГБ";

        if (value >= 1024L * 1024L)
            return (value / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " МБ";

        if (value >= 1024L)
            return (value / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " КБ";

        return value + " Б";
    }

    /// <summary>Через SOCKS5 pip работает только с PySocks, а его надо положить раньше всего остального.</summary>
    private async Task EnsurePySocksAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        if (_settings.ProxyMode != ProxyMode.Socks5 || !_settings.ProxyForPip) return;

        progress.Report(new InstallProgress("pip", "Установка PySocks (поддержка SOCKS5 в pip)...", 15));

        var wheel = Path.Combine(AppPaths.Downloads, "PySocks-1.7.1-py3-none-any.whl");
        if (!File.Exists(wheel))
        {
            var (wheelUrl, wheelSha) = await ResolvePypiWheelAsync("PySocks", "1.7.1", PySocksWheelUrl, ct);
            await _downloader.DownloadAsync(wheelUrl, wheel, null, ct, expectedSha256: wheelSha);
        }

        var (code, tail) = await RunPythonLoggedAsync(
            new[] { "-m", "pip", "install", "--no-warn-script-location", "--no-cache-dir", "--no-index", wheel },
            progress, "pip", 15, 16, ct);

        if (code != 0)
            Log.Warn("Installer", "PySocks не установился, SOCKS5 для pip может не работать:\n" + tail);

        if (!PySocksReady())
            throw new InvalidOperationException(
                "Выбран SOCKS5, но библиотека PySocks не установилась.\n" +
                "Без неё pip не умеет работать через SOCKS5 и будет бесконечно повторять попытки.\n" +
                "Переключите прокси на HTTP или проверьте журнал установки.");

        Log.Info("Installer", "PySocks на месте, SOCKS5 для pip доступен");
    }

    /// <summary>Есть ли PySocks в нашем рантайме (без него python не умеет socks5).</summary>
    private static bool PySocksReady()
    {
        var site = Path.Combine(AppPaths.Python, "Lib", "site-packages");
        return File.Exists(Path.Combine(site, "socks.py"))
               || File.Exists(Path.Combine(site, "socks", "__init__.py"));
    }

    /// <summary>Проверяет, жив ли прокси, до начала длинных загрузок.</summary>
    private async Task CheckProxyReachableAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        if (_settings.ProxyMode == ProxyMode.None) return;

        var (host, port) = HttpFactory.ParseHostPort(_settings.ProxyHost, _settings.ProxyPort);
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Выбран режим прокси, но адрес не заполнен.");

        var target = host + ":" + port;
        progress.Report(new InstallProgress("Прокси", "Проверка прокси " + target + "...", 1));

        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync(host, port, ct).AsTask();
            var finished = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(6), ct));

            if (finished != connect) throw new TimeoutException("нет ответа за 6 секунд");
            await connect;

            Log.Info("Installer", "Прокси отвечает: " + target);
            progress.Report(new InstallProgress("Прокси", "Прокси отвечает: " + target, 2));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Прокси " + target + " не отвечает (" + ex.Message + ").\n" +
                "Проверьте, что клиент прокси запущен, а адрес и порт указаны без опечаток.");
        }

        // Открытый порт ещё не значит, что через него есть интернет: делаем боевой запрос
        // именно к PyPI — оттуда пойдёт основной объём загрузки.
        await ProbeProxyTargetsAsync(progress, ct);
    }

    /// <summary>Пробует ключевые адреса тем же способом, каким их будет грузить установщик.</summary>
    private async Task ProbeProxyTargetsAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var targets = new[]
        {
            "https://pypi.org/simple/pip/",
            "https://www.python.org/ftp/python/",
            "https://huggingface.co/api/models?limit=1"
        };

        foreach (var url in targets)
        {
            var useProxy = HttpFactory.UseProxyForUrl(_settings, url);
            var how = useProxy ? "через прокси" : "напрямую";

            progress.Report(new InstallProgress("Прокси", "Проверка " + url + " (" + how + ")...", 2));

            try
            {
                using var client = HttpFactory.Create(_settings, useProxy, TimeSpan.FromSeconds(25));
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url);
                using var response = await client.SendAsync(request, ct);

                Log.Info("Installer", "Проверка " + url + " (" + how + "): HTTP " + (int)response.StatusCode);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("Installer", "Нет доступа к " + url + " (" + how + "): " + ex.Message);
                progress.Report(new InstallProgress("Прокси",
                    "Нет доступа к " + url + " (" + how + ") — шаги с этого адреса могут не пройти", 2));
            }
        }
    }

    /// <summary>При работе через прокси pip 25+ неработоспособен — проверяем версию заранее.</summary>
    private async Task EnsurePipUsableAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        if (_settings.ProxyMode == ProxyMode.None || !_settings.ProxyForPip) return;

        var (code, output) = await RunPythonCaptureAsync(new[] { "-m", "pip", "--version" }, ct);
        var text = output.Trim();
        Log.Info("Installer", "Текущий pip: " + text);

        var major = 0;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"pip\s+(\d+)\.");
        if (match.Success) int.TryParse(match.Groups[1].Value, out major);

        if (code != 0 || major == 0 || major >= 25)
        {
            Log.Warn("Installer", "Эта версия pip не умеет работать через прокси — ставим pip " + PipPinnedVersion);
            await RepairPipAsync(progress, ct);
        }
    }

    /// <summary>Ставит pip фиксированной версии из wheel, который скачиваем сами (наш загрузчик умеет SOCKS5).</summary>
    private async Task RepairPipAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        if (_pipRepaired) return;
        _pipRepaired = true;

        progress.Report(new InstallProgress("pip", "Установка совместимого с прокси pip " + PipPinnedVersion + "...", 15));

        var wheel = Path.Combine(AppPaths.Downloads, "pip-" + PipPinnedVersion + "-py3-none-any.whl");

        if (!File.Exists(wheel) || new FileInfo(wheel).Length < 500_000)
        {
            if (File.Exists(wheel)) File.Delete(wheel);

            var (url, sha) = await ResolvePypiWheelAsync("pip", PipPinnedVersion, PipPinnedWheelUrl, ct);
            await _downloader.DownloadAsync(url, wheel, null, ct, expectedSha256: sha);
        }

        var size = new FileInfo(wheel).Length;
        if (size < 500_000)
            throw new InvalidOperationException(
                "Файл pip " + PipPinnedVersion + " скачался битым (" + size + " байт). Проверьте прокси.");

        var (code, tail) = await RunPythonLoggedAsync(
            new[] { "-m", "pip", "install", "--no-warn-script-location", "--no-cache-dir", "--no-index", "--force-reinstall", wheel },
            progress, "pip", 15, 16, ct);

        if (code != 0)
            throw new InvalidOperationException(
                "Не удалось установить совместимый pip " + PipPinnedVersion + " (код " + code + ").\n\n" + tail);

        var (checkCode, checkOut) = await RunPythonCaptureAsync(new[] { "-m", "pip", "--version" }, ct);
        Log.Info("Installer", "pip после замены: " + checkOut.Trim() + " (код " + checkCode + ")");
    }

    /// <summary>
    /// Спрашивает у PyPI ссылку И контрольную сумму wheel-файла пакета.
    /// Если PyPI недоступен — возвращает вшитую запасную ссылку без суммы.
    /// </summary>
    private async Task<(string Url, string? Sha256)> ResolvePypiWheelAsync(
        string package, string version, string fallbackUrl, CancellationToken ct)
    {
        try
        {
            var pypiUrl = "https://pypi.org/pypi/" + package + "/" + version + "/json";

            var client = HttpFactory.Shared(_settings, HttpFactory.UseProxyForUrl(_settings, pypiUrl));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var json = await client.GetStringAsync(pypiUrl, cts.Token);

            using var doc = JsonDocument.Parse(json);
            foreach (var file in doc.RootElement.GetProperty("urls").EnumerateArray())
            {
                var name = file.GetProperty("filename").GetString() ?? "";
                if (!name.EndsWith(".whl", StringComparison.OrdinalIgnoreCase)) continue;

                var url = file.GetProperty("url").GetString();
                string? sha = null;
                if (file.TryGetProperty("digests", out var digests) &&
                    digests.TryGetProperty("sha256", out var shaProp))
                {
                    sha = shaProp.GetString();
                }

                if (!string.IsNullOrWhiteSpace(url))
                {
                    Log.Info("Installer", "Ссылка на " + package + " получена с PyPI (sha256 " +
                        (sha != null ? "будет проверен" : "недоступен") + "): " + url);
                    return (url!, sha);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Installer", "PyPI не ответил про " + package + ", использую запасную ссылку: " + ex.Message);
        }

        return (fallbackUrl, null);
    }

    /// <summary>Запуск pip с автопочинкой: если pip сломан на прокси, меняем версию и повторяем шаг.</summary>
    private async Task RunPipWithRepairAsync(List<string> args, IProgress<InstallProgress> progress, string stage, double from, double to, CancellationToken ct)
    {
        var (code, tail) = await RunPythonLoggedAsync(args.ToArray(), progress, stage, from, to, ct);
        if (code == 0) return;

        // Очень старый pip может не знать режим raw — тогда пробуем без показа процентов.
        if (args.Contains("raw") && tail.Contains("progress-bar", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("Installer", "pip не понимает --progress-bar raw, повторяем без него");

            var plain = new List<string>(args);
            var at = plain.IndexOf("--progress-bar");
            if (at >= 0) plain.RemoveRange(at, Math.Min(2, plain.Count - at));

            (code, tail) = await RunPythonLoggedAsync(plain.ToArray(), progress, stage, from, to, ct);
            if (code == 0) return;
        }

        var brokenPip = tail.Contains("key_proxy_ssl_context", StringComparison.OrdinalIgnoreCase)
                        || tail.Contains("PoolKey", StringComparison.OrdinalIgnoreCase);

        if (brokenPip && !_pipRepaired)
        {
            Log.Warn("Installer", "pip упал на работе через прокси — ставим рабочую версию и повторяем шаг");
            progress.Report(new InstallProgress(stage, "pip несовместим с прокси, меняем версию и пробуем снова...", from));

            await RepairPipAsync(progress, ct);

            (code, tail) = await RunPythonLoggedAsync(args.ToArray(), progress, stage, from, to, ct);
            if (code == 0) return;
        }

        throw new InvalidOperationException(
            "Команда python " + HttpFactory.MaskSecrets(string.Join(' ', args)) + " вернула код " + code + ".\n\n" +
            "Последние строки вывода:\n" + HttpFactory.MaskSecrets(tail) + "\n\n" +
            "Полный журнал: " + Path.Combine(AppPaths.Logs, "install.log"));
    }

    /// <summary>Проверяет, что скачан именно python-скрипт, а не HTML-заглушка провайдера.</summary>
    private static void ValidatePythonScript(string path, string name)
    {
        var info = new FileInfo(path);
        var head = "";

        using (var reader = new StreamReader(path, Encoding.UTF8))
        {
            var buffer = new char[512];
            var read = reader.Read(buffer, 0, buffer.Length);
            head = new string(buffer, 0, Math.Max(0, read));
        }

        var looksHtml = head.TrimStart().StartsWith("<", StringComparison.Ordinal)
                        || head.Contains("<html", StringComparison.OrdinalIgnoreCase);

        if (info.Length < 20_000 || looksHtml)
        {
            Log.Error("Installer", name + " скачался неверно (" + info.Length + " байт). Начало файла:\n" + head);

            throw new InvalidOperationException(
                name + " скачался неверно (" + info.Length + " байт, похоже на страницу-заглушку).\n" +
                "Обычно это значит, что провайдер или прокси подменяет ответ. Проверьте прокси и нажмите «Проверить соединение».");
        }
    }

    private async Task InstallPipPackagesAsync(string torchFlavor, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var reqFile = Path.Combine(AppPaths.Runtime, "requirements.txt");
        File.WriteAllText(reqFile, EmbeddedResources.Read("requirements-extra.txt"), new UTF8Encoding(false));

        await EnsurePySocksAsync(progress, ct);
        await EnsurePipUsableAsync(progress, ct);

        var proxyArg = HttpFactory.ProxyUrlForChildProcess(_settings);
        Log.Info("Installer", proxyArg != null && _settings.ProxyForPip
            ? "pip работает через прокси " + HttpFactory.MaskSecrets(proxyArg)
            : "pip работает напрямую, без прокси");

        // Самый тяжёлый шаг: сборка с CUDA — больше двух гигабайт. Если подходящий torch
        // уже есть (например, перенесён из другого окружения) — качать его нет смысла.
        var installedTorch = await TorchInfoAsync(ct);

        if (TorchIsSuitable(installedTorch, torchFlavor))
        {
            progress.Report(new InstallProgress("torch", "Уже установлен: " + installedTorch, 55));
            Log.Info("Installer", "Шаг torch пропущен, найдена подходящая сборка: " + installedTorch);
        }
        else
        {
            if (installedTorch.Length > 0)
                Log.Info("Installer", "Установленный torch не подходит (" + installedTorch + "), ставлю заново");

            // torch ставим отдельно: у него свой индекс. Индекс можно переопределить в настройках
            // (TorchIndexUrl), а пользовательское зеркало PyPI добавляется как запасной источник.
            var torchArgs = new List<string> { "-m", "pip", "install", "--no-warn-script-location", "--no-cache-dir" };
            torchArgs.AddRange(PipProgressArgs());
            torchArgs.Add("torch==" + TorchVersion);
            torchArgs.Add("torchaudio==" + TorchVersion);

            var torchIndex = !string.IsNullOrWhiteSpace(_settings.TorchIndexUrl)
                ? _settings.TorchIndexUrl.Trim()
                : torchFlavor == "cu121" ? "https://download.pytorch.org/whl/cu121" : null;

            if (torchIndex != null)
            {
                torchArgs.Add("--index-url");
                torchArgs.Add(torchIndex);
            }

            if (!string.IsNullOrWhiteSpace(_settings.PipIndexUrl))
            {
                // Зеркало PyPI из настроек теперь участвует и в шаге torch.
                torchArgs.Add("--extra-index-url");
                torchArgs.Add(_settings.PipIndexUrl.Trim());
            }

            torchArgs.AddRange(PipNetworkArgs(includeIndex: false));

            progress.Report(new InstallProgress("torch", "Установка PyTorch (это самый долгий шаг)...", 16));
            await RunPipWithRepairAsync(torchArgs, progress, "torch", 16, 55, ct);
        }

        var reqArgs = new List<string> { "-m", "pip", "install", "--no-warn-script-location", "--no-cache-dir", "-r", reqFile };
        reqArgs.AddRange(PipProgressArgs());
        reqArgs.AddRange(PipNetworkArgs());

        progress.Report(new InstallProgress("pip", "Установка остальных зависимостей...", 56));
        await RunPipWithRepairAsync(reqArgs, progress, "pip", 56, 72, ct);
    }

    /// <summary>Папка site-packages нашего встроенного Python.</summary>
    public static string SitePackages => Path.Combine(AppPaths.Python, "Lib", "site-packages");

    /// <summary>Какая версия Python нужна переносимым пакетам.</summary>
    public static string RequiredPythonVersion => PythonVersion;

    /// <summary>Ставит только Python и pip — минимум, чтобы принять пакеты из другого окружения.</summary>
    public async Task PrepareForImportAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        await EnsurePythonAsync(progress, ct);
        await EnsurePipAsync(progress, ct);
    }

    /// <summary>Что за torch стоит в нашем рантайме. Пустая строка — не стоит или не импортируется.</summary>
    public async Task<string> TorchInfoAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(AppPaths.PythonExe)) return "";

            const string code = "import torch, torchaudio; print(torch.__version__, torchaudio.__version__, torch.cuda.is_available())";
            var (exit, output) = await RunPythonCaptureAsync(new[] { "-c", code }, ct);

            if (exit != 0) return "";

            var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
            return line;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("Installer", "Не удалось спросить версию torch: " + ex.Message);
            return "";
        }
    }

    /// <summary>Годится ли уже установленный torch: версия не ниже 2.4 и есть CUDA, если она нужна.</summary>
    private static bool TorchIsSuitable(string info, string torchFlavor)
    {
        if (info.Length == 0) return false;

        var parts = info.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;

        var numbers = parts[0].Split('+')[0].Split('.');
        if (numbers.Length < 2) return false;
        if (!int.TryParse(numbers[0], out var major) || !int.TryParse(numbers[1], out var minor)) return false;
        if (major < 2 || (major == 2 && minor < 4)) return false;

        // torchaudio должен быть той же версии, иначе он не загрузится вместе с torch.
        if (parts[1].Split('+')[0] != parts[0].Split('+')[0]) return false;

        if (torchFlavor == "cu121" && !parts[2].StartsWith("True", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private async Task EnsureBackendAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var marker = Path.Combine(AppPaths.Backend, "rvc", "realtime", "core.py");
        var embeddedHash = EmbeddedBackendHash();

        // Распаковываем заново не только при первой установке, но и когда вшитые исходники обновились:
        // иначе исправления в бэкенде никогда не доедут до уже установленной копии.
        if (!File.Exists(marker) || !BackendContentIsCurrent())
        {
            progress.Report(new InstallProgress("Бэкенд", "Распаковка исходников RVC...", 74));

            // Исходники RVC лежат внутри exe: никаких загрузок с GitHub и никаких блокировок.
            var zip = Path.Combine(AppPaths.Downloads, $"backend-{BackendVersion}.zip");
            EmbeddedResources.ExtractTo("backend.zip", zip);

            if (!IsUsableZip(zip))
                throw new InvalidOperationException("Встроенный архив исходников RVC повреждён. Переустановите программу.");

            Log.Info("Installer", "Исходники RVC извлечены из самой программы");

            progress.Report(new InstallProgress("Бэкенд", "Распаковка бэкенда...", 78));
            var tempDir = Path.Combine(AppPaths.Temp, "backend-extract");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            ZipFile.ExtractToDirectory(zip, tempDir);

            // Встроенный архив распаковывается сразу с папкой rvc в корне.
            var inner = Directory.Exists(Path.Combine(tempDir, "rvc"))
                ? tempDir
                : Directory.GetDirectories(tempDir).FirstOrDefault() ?? tempDir;

            // Старые исходники удаляем целиком (иначе удалённые в новой версии файлы
            // остаются навсегда), но скачанные веса в rvc/models бережём — это сотни мегабайт.
            var oldRvc = Path.Combine(AppPaths.Backend, "rvc");
            var savedModels = Path.Combine(AppPaths.Temp, "rvc-models-saved");

            if (Directory.Exists(oldRvc))
            {
                try
                {
                    var models = Path.Combine(oldRvc, "models");
                    if (Directory.Exists(savedModels)) Directory.Delete(savedModels, true);
                    if (Directory.Exists(models))
                    {
                        Directory.CreateDirectory(AppPaths.Temp);
                        Directory.Move(models, savedModels);
                    }

                    Directory.Delete(oldRvc, true);
                    Log.Info("Installer", "Старые исходники бэкенда удалены перед распаковкой новых");
                }
                catch (Exception ex)
                {
                    Log.Warn("Installer", "Не удалось очистить старый бэкенд, копирую поверх: " + ex.Message);
                }
            }

            CopyDirectory(inner, AppPaths.Backend);

            try
            {
                if (Directory.Exists(savedModels))
                {
                    var models = Path.Combine(AppPaths.Backend, "rvc", "models");
                    if (Directory.Exists(models))
                    {
                        // Сливаем: файлы из архива важнее, но скачанные веса добавляем обратно.
                        foreach (var file in Directory.GetFiles(savedModels, "*", SearchOption.AllDirectories))
                        {
                            var relative = Path.GetRelativePath(savedModels, file);
                            var target = Path.Combine(models, relative);
                            if (File.Exists(target)) continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            File.Move(file, target);
                        }
                        Directory.Delete(savedModels, true);
                    }
                    else
                    {
                        Directory.Move(savedModels, models);
                    }

                    Log.Info("Installer", "Скачанные веса моделей возвращены на место");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Installer", "Не удалось вернуть веса моделей (будут скачаны заново): " + ex.Message);
            }

            Directory.Delete(tempDir, true);

            File.WriteAllText(BackendStampFile, embeddedHash);
            Log.Info("Installer", "Исходники бэкенда развёрнуты, отпечаток: " + embeddedHash[..12]);
        }

        // Наш воркер лежит внутри exe и всегда перезаписывается — так обновления программы
        // автоматически обновляют скрипт бэкенда.
        File.WriteAllText(AppPaths.WorkerScript, EmbeddedResources.Read("vc_worker.py"), new UTF8Encoding(false));

        RegisterBackendInPth();
        progress.Report(new InstallProgress("Бэкенд", "Бэкенд готов", 80));
    }

    private static string BackendStampFile => Path.Combine(AppPaths.Backend, ".backend-stamp");

    /// <summary>SHA-256 вшитого в exe архива исходников бэкенда.</summary>
    private static string EmbeddedBackendHash()
    {
        using var stream = EmbeddedResources.Open("backend.zip");
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>Совпадает ли развёрнутый бэкенд с тем, что лежит внутри программы.</summary>
    private static bool BackendContentIsCurrent()
    {
        try
        {
            if (!File.Exists(BackendStampFile)) return false;
            return string.Equals(File.ReadAllText(BackendStampFile).Trim(), EmbeddedBackendHash(),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Embeddable-сборка Python работает в изолированном режиме и не читает PYTHONPATH,
    /// поэтому папку бэкенда нужно добавить прямо в файл python*._pth.
    /// </summary>
    private static void RegisterBackendInPth()
    {
        try
        {
            var pthFile = Directory.GetFiles(AppPaths.Python, "python*._pth").FirstOrDefault();
            if (pthFile == null) return;

            var lines = File.ReadAllLines(pthFile).ToList();
            var absolute = AppPaths.Backend.TrimEnd(Path.DirectorySeparatorChar);

            // Относительный путь (обычно "..\backend") — portable-папку можно переносить целиком.
            var backend = Path.GetRelativePath(AppPaths.Python, AppPaths.Backend);

            // Убираем старые записи с абсолютным путём от предыдущих версий программы.
            lines.RemoveAll(l => string.Equals(l.Trim(), absolute, StringComparison.OrdinalIgnoreCase));

            if (lines.Any(l => string.Equals(l.Trim(), backend, StringComparison.OrdinalIgnoreCase)))
            {
                File.WriteAllLines(pthFile, lines);
                return;
            }

            var insertAt = lines.FindIndex(l => l.Trim() == "import site");
            if (insertAt < 0) lines.Add(backend);
            else lines.Insert(insertAt, backend);

            File.WriteAllLines(pthFile, lines);
            Log.Info("Installer", "Путь к бэкенду добавлен в " + Path.GetFileName(pthFile));
        }
        catch (Exception ex)
        {
            Log.Warn("Installer", "Не удалось прописать путь бэкенда в ._pth: " + ex.Message);
        }
    }

    /// <summary>Проверяет, что файл действительно zip-архив, а не страница-заглушка.</summary>
    private static bool IsUsableZip(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (new FileInfo(path).Length < 100_000) return false;

            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsurePrerequisitesAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var index = 0;
        var missing = new List<string>();
        var prerequisites = PrerequisiteList();

        foreach (var (url, relative, optional, minBytes) in prerequisites)
        {
            ct.ThrowIfCancellationRequested();
            index++;

            var target = Path.Combine(AppPaths.Backend, relative.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(target))
            {
                // Файл меньше минимального правдоподобного размера — оборванная загрузка
                // или HTML-заглушка. Считаем его отсутствующим и качаем заново.
                if (new FileInfo(target).Length >= minBytes) continue;
                Log.Warn("Installer", Path.GetFileName(relative) + " на диске подозрительно мал — скачиваю заново");
                File.Delete(target);
            }

            var name = Path.GetFileName(relative);
            var basePercent = 80 + (index - 1) * (12.0 / prerequisites.Length);

            // Сначала ищем файл, положенный руками — тогда сеть вообще не нужна.
            var local = FindLocalPrerequisite(name, minBytes);
            if (local != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(local, target, true);
                Log.Info("Installer", "Взят локальный файл вместо скачивания: " + local);
                progress.Report(new InstallProgress("Модели", name + ": взят из папки prereq", basePercent));
                continue;
            }

            try
            {
                await _downloader.DownloadAsync(url, target,
                    new Progress<DownloadProgress>(pr => progress.Report(
                        new InstallProgress("Модели", $"{name}: {pr.Text}",
                            basePercent + pr.Percent * (12.0 / prerequisites.Length) / 100))), ct);

                var downloadedSize = new FileInfo(target).Length;
                if (downloadedSize < minBytes)
                    throw new IOException(
                        $"{name} скачался подозрительно маленьким ({downloadedSize} байт, ожидалось не менее {minBytes}). " +
                        "Похоже на страницу-заглушку от провайдера или прокси.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (optional)
                {
                    // Необязательный файл — установку из-за него не рвём, но говорим о последствиях явно.
                    Log.Warn("Installer", "Необязательный файл пропущен (" + name + "): " + ex.Message +
                        (name.StartsWith("fcpe") ? " — алгоритм f0=fcpe будет недоступен, останется rmvpe" : ""));
                    progress.Report(new InstallProgress("Модели",
                        name + ": пропущен (необязательный)" +
                        (name.StartsWith("fcpe") ? " — режим fcpe будет недоступен" : ""), basePercent));

                    try { if (File.Exists(target)) File.Delete(target); } catch { }
                    continue;
                }

                Log.Error("Installer", "Не удалось получить " + name, ex);
                missing.Add(name + " — " + ex.Message.Replace(Environment.NewLine, " "));
            }
        }

        if (missing.Count > 0)
            throw new InvalidOperationException(BuildPrerequisiteHelp(missing));

        progress.Report(new InstallProgress("Модели", "Веса предобученных моделей на месте", 92));
    }

    /// <summary>Где искать веса, положенные пользователем вручную.</summary>
    public static string[] PrerequisiteSearchFolders()
    {
        var exeDir = AppContext.BaseDirectory;

        return new[]
        {
            Path.Combine(AppPaths.Root, "prereq"),
            Path.Combine(AppPaths.Downloads, "prereq"),
            Path.Combine(exeDir, "prereq")
        };
    }

    /// <summary>Ищет файл весов в папках prereq (включая вложенные).</summary>
    private static string? FindLocalPrerequisite(string fileName, long minBytes)
    {
        foreach (var folder in PrerequisiteSearchFolders())
        {
            try
            {
                if (!Directory.Exists(folder)) continue;

                var hit = Directory.GetFiles(folder, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault(f => new FileInfo(f).Length >= minBytes);

                if (hit != null) return hit;
            }
            catch
            {
                // папка может быть недоступна — пробуем следующую
            }
        }

        return null;
    }

    /// <summary>Подробная подсказка с адресами и путями для ручной установки весов.</summary>
    private string BuildPrerequisiteHelp(List<string> missing)
    {
        var folder = Path.Combine(AppPaths.Root, "prereq");

        var text = new StringBuilder();
        text.AppendLine("Не удалось получить веса моделей:");
        foreach (var m in missing) text.AppendLine("  • " + m);

        text.AppendLine();
        text.AppendLine("Можно положить их руками — скачивание тогда будет пропущено.");
        text.AppendLine("Папка для файлов (имена менять нельзя):");
        text.AppendLine("  " + folder);
        text.AppendLine();
        text.AppendLine("Страница с файлами: " + HfEndpoint + "/" + WeightsRepo + "/tree/main/Resources");

        foreach (var (url, relative, optional, _) in PrerequisiteList())
        {
            if (optional) continue;
            text.AppendLine("  " + Path.GetFileName(relative) + "  ←  " + url);
        }

        return text.ToString();
    }

    /// <summary>Имена модулей Python не всегда совпадают с именами пакетов на PyPI.</summary>
    private static readonly Dictionary<string, string?> ModulePackageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["faiss"] = "faiss-cpu==1.8.0.post1",
        ["sklearn"] = "scikit-learn",
        ["webrtcvad"] = "webrtcvad-wheels==2.0.14",
        ["yaml"] = "PyYAML",
        ["bs4"] = "beautifulsoup4",
        ["PIL"] = "pillow",
        ["cv2"] = "opencv-python-headless",
        ["edge_tts"] = "edge-tts",
        ["local_attention"] = "local-attention",
        ["soundfile"] = "soundfile==0.12.1",
        ["torch"] = null,       // ставится отдельно, со своим индексом
        ["torchaudio"] = null,
        ["wget"] = null,        // бэкенд больше не использует wget (заменён на requests)
        ["_cffi_backend"] = "cffi",              // нативная часть cffi: если сломана — переустанавливаем пакет
        ["_soundfile"] = "soundfile==0.12.1",    // аналогично для soundfile
        ["_soundfile_data"] = "soundfile==0.12.1"
    };

    private static string? MapModuleToPackage(string module)
    {
        if (ModulePackageMap.TryGetValue(module, out var mapped)) return mapped;
        return module;
    }

    /// <summary>Вытаскивает список missing_modules из JSON-отчёта самопроверки.</summary>
    private static List<string> ParseMissingModules(string output)
    {
        var result = new List<string>();

        try
        {
            var start = output.IndexOf('{');
            var end = output.LastIndexOf('}');
            if (start < 0 || end <= start) return result;

            using var doc = JsonDocument.Parse(output[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("missing_modules", out var list)) return result;
            if (list.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in list.EnumerateArray())
            {
                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name)) result.Add(name!);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Installer", "Не удалось разобрать отчёт самопроверки: " + ex.Message);
        }

        return result;
    }

    /// <summary>Финальная проверка: import torch/faiss + доступность воркера.</summary>
    public async Task VerifyAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        progress.Report(new InstallProgress("Проверка", "Проверяем установленное окружение...", 94));

        var (code, output) = await RunPythonCaptureAsync(new[] { AppPaths.WorkerScript, "--selftest" }, ct);
        Log.Info("Installer", "Самопроверка бэкенда:\n" + output.Trim());

        // Самопроверка сразу отдаёт весь список недостающих пакетов — доставляем их одним вызовом pip
        // и перепроверяем. Так пользователю не нужно запускать установку заново ради каждой библиотеки.
        if (code != 0)
        {
            var missing = ParseMissingModules(output);
            if (missing.Count > 0)
            {
                var packages = missing.Select(MapModuleToPackage).Where(p => p != null).Select(p => p!).Distinct().ToList();
                Log.Warn("Installer", "Не хватает модулей: " + string.Join(", ", missing));

                if (packages.Count > 0)
                {
                    progress.Report(new InstallProgress("Проверка",
                        "Доставляем недостающие библиотеки: " + string.Join(", ", packages), 95));

                    // --force-reinstall: пакет может числиться установленным («already satisfied»),
                    // но быть битым — например, перенесённый с диска cffi без _cffi_backend.pyd.
                    var args = new List<string> { "-m", "pip", "install", "--no-warn-script-location", "--no-cache-dir", "--force-reinstall" };
                    args.AddRange(PipProgressArgs());
                    args.AddRange(PipNetworkArgs());
                    args.AddRange(packages);

                    await RunPipWithRepairAsync(args, progress, "pip", 95, 97, ct);

                    (code, output) = await RunPythonCaptureAsync(new[] { AppPaths.WorkerScript, "--selftest" }, ct);
                    Log.Info("Installer", "Повторная самопроверка:\n" + output.Trim());
                }
            }
        }

        if (code != 0)
            throw new InvalidOperationException("Самопроверка бэкенда не прошла:\n" + output);

        progress.Report(new InstallProgress("Проверка", "Окружение работоспособно", 98));
    }

    /// <summary>Устанавливает окружение, если оно ещё не готово, и проверяет результат.</summary>
    public async Task EnsureInstalledAsync(IProgress<InstallProgress> progress, CancellationToken ct)
    {
        if (IsInstalled())
        {
            progress.Report(new InstallProgress("Проверка", "Окружение уже установлено", 90));
            Log.Info("Installer", "Окружение уже установлено — повторная загрузка не требуется");
            return;
        }

        await InstallAsync(progress, ct);
    }

    /// <summary>Самопроверка бэкенда без исключений: удобно для UI.</summary>
    public async Task<(bool Success, string Message)> SelfTestAsync(CancellationToken ct)
    {
        try
        {
            var (code, output) = await RunPythonCaptureAsync(
                new[] { AppPaths.WorkerScript, "--selftest" }, ct);

            var text = output.Trim();
            Log.Info("Installer", "Самопроверка бэкенда (код " + code + "):\n" + text);

            if (code != 0)
                return (false, SummarizeSelfTest(text, false));

            return (true, SummarizeSelfTest(text, true));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Installer", "Самопроверка завершилась ошибкой", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>Достаёт из JSON-ответа vc_worker.py короткую сводку для пользователя.</summary>
    private static string SummarizeSelfTest(string output, bool success)
    {
        try
        {
            var start = output.LastIndexOf('{');
            if (start >= 0)
            {
                using var doc = JsonDocument.Parse(output.Substring(start));
                var root = doc.RootElement;

                var parts = new List<string>();

                if (root.TryGetProperty("python", out var py))
                    parts.Add("Python " + py.GetString());

                if (root.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var check in checks.EnumerateArray())
                    {
                        var name = check.TryGetProperty("name", out var n) ? n.GetString() : "?";
                        var okFlag = check.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                        var detail = check.TryGetProperty("detail", out var d) ? d.GetString() : null;

                        if (!okFlag)
                            parts.Add("ОШИБКА " + name + ": " + detail);
                        else if (name == "torch")
                            parts.Add(detail ?? "torch ok");
                    }
                }

                if (parts.Count > 0)
                    return string.Join("; ", parts);
            }
        }
        catch
        {
            // Не смогли разобрать JSON — отдаём сырой текст.
        }

        if (string.IsNullOrWhiteSpace(output))
            return success ? "проверка прошла" : "бэкенд не ответил";

        return output.Length > 400 ? output.Substring(output.Length - 400) : output;
    }

    public ProcessStartInfo CreatePythonStartInfo(IEnumerable<string> args)
    {
        // На ранних этапах (python/pip) папки бэкенда ещё нет — иначе процесс падает сразу.
        var backendReady = File.Exists(AppPaths.WorkerScript) || Directory.Exists(Path.Combine(AppPaths.Backend, "rvc"));
        var workDir = backendReady ? AppPaths.Backend : AppPaths.Python;
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo(AppPaths.PythonExe)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        if (backendReady)
        {
            // Embeddable-сборка Python игнорирует PYTHONPATH, если рядом лежит ._pth,
            // поэтому путь к бэкенду дополнительно прописан в ._pth и передан воркеру.
            psi.Environment["PYTHONPATH"] = AppPaths.Backend;
            psi.Environment["RVC_BACKEND_DIR"] = AppPaths.Backend;
        }
        else
        {
            psi.Environment.Remove("PYTHONPATH");
            psi.Environment.Remove("RVC_BACKEND_DIR");
        }
        // pip скачивает пакеты в %TEMP%; направляем его в свою папку, чтобы считать прогресс по размеру файла.
        psi.Environment["TEMP"] = AppPaths.Temp;
        psi.Environment["TMP"] = AppPaths.Temp;

        psi.Environment["HF_HOME"] = Path.Combine(AppPaths.Runtime, "hf-cache");
        psi.Environment["TORCH_HOME"] = Path.Combine(AppPaths.Runtime, "torch-cache");

        var proxyUrl = HttpFactory.ProxyUrlForChildProcess(_settings);
        if (proxyUrl != null && (_settings.ProxyForPip || _settings.ProxyForHuggingFace))
        {
            var isSocks = proxyUrl.StartsWith("socks", StringComparison.OrdinalIgnoreCase);

            // Без PySocks python не умеет socks5 и падает на первом же запросе.
            if (!isSocks || PySocksReady())
            {
                psi.Environment["HTTP_PROXY"] = proxyUrl;
                psi.Environment["HTTPS_PROXY"] = proxyUrl;
                psi.Environment["ALL_PROXY"] = proxyUrl;

                // Переменные в нижнем регистре читают requests и urllib3, в верхнем — часть других либ.
                psi.Environment["http_proxy"] = proxyUrl;
                psi.Environment["https_proxy"] = proxyUrl;
                psi.Environment["all_proxy"] = proxyUrl;
            }
            else
            {
                Log.Warn("Installer", "SOCKS5 выбран, но PySocks ещё не установлен — прокси для python пока не задаю");
            }
        }

        // Связь с воркером идёт на 127.0.0.1 и никогда не должна уходить в прокси.
        psi.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        psi.Environment["no_proxy"] = "localhost,127.0.0.1,::1";

        // Явный выбор устройства для бэкенда: rvc/configs/config.py читает RVC_DEVICE.
        psi.Environment["RVC_DEVICE"] = _settings.Device switch
        {
            ComputeDevice.Cpu => "cpu",
            ComputeDevice.Cuda => "cuda",
            _ => "auto"
        };

        if (_settings.Device == ComputeDevice.Cpu)
            psi.Environment["CUDA_VISIBLE_DEVICES"] = "";

        // Зеркало HuggingFace для докачки эмбеддеров внутри бэкенда (rvc/lib/utils.py).
        if (!string.IsNullOrWhiteSpace(_settings.HfEndpoint))
            psi.Environment["RVC_HF_ENDPOINT"] = _settings.HfEndpoint.Trim().TrimEnd('/');

        return psi;
    }

    private async Task RunPythonAsync(string[] args, IProgress<InstallProgress> progress, string stage, double from, double to, CancellationToken ct)
    {
        var (code, tail) = await RunPythonLoggedAsync(args, progress, stage, from, to, ct);

        if (code != 0)
            throw new InvalidOperationException(
                "Команда python " + HttpFactory.MaskSecrets(string.Join(' ', args)) + " вернула код " + code + ".\n\n" +
                "Последние строки вывода:\n" + HttpFactory.MaskSecrets(tail) + "\n\n" +
                "Полный журнал: " + Path.Combine(AppPaths.Logs, "install.log"));
    }

    /// <summary>
    /// Запускает python и пишет АБСОЛЮТНО весь вывод (stdout и stderr) в журнал и в окно установки.
    /// Возвращает код выхода и последние строки вывода без броска исключения.
    /// </summary>
    private async Task<(int Code, string Tail)> RunPythonLoggedAsync(string[] args, IProgress<InstallProgress> progress, string stage, double from, double to, CancellationToken ct)
    {
        var psi = CreatePythonStartInfo(args);
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var lineCount = 0;
        var tail = new Queue<string>();

        // Состояние текущего скачивания — чтобы показывать проценты, а не адрес файла.
        var currentFile = "";
        var lastPercent = -1.0;
        var lastProgressLog = DateTime.MinValue;
        var progressLock = new object();

        // pip не всегда соглашается отдавать прогресс текстом, поэтому есть второй,
        // независимый способ: смотрим, как растёт файл в нашей папке temp.
        var rawProgressSeen = false;
        var expectedTotal = 0L;
        System.Threading.Timer? sizeWatcher = null;

        void ReportBySize()
        {
            try
            {
                long total;
                string name;

                lock (progressLock)
                {
                    if (rawProgressSeen) return;
                    total = expectedTotal;
                    name = currentFile;
                }

                if (total <= 0 || !Directory.Exists(AppPaths.Temp)) return;

                // pip кладёт загрузки в подпапки первого уровня нашего temp —
                // глубже двух уровней сканировать не нужно (и temp у нас свой, не общесистемный %TEMP%).
                var root = new DirectoryInfo(AppPaths.Temp);
                var growing = root.EnumerateFiles()
                    .Concat(root.EnumerateDirectories().SelectMany(d => d.EnumerateFiles()))
                    .Where(f => f.Length > 0 && (DateTime.Now - f.LastWriteTime).TotalSeconds < 60)
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault();

                if (growing == null) return;

                var percent = Math.Min(100.0, growing.Length * 100.0 / total);
                var text = (name.Length > 0 ? name + ": " : "")
                           + percent.ToString("0.0", CultureInfo.InvariantCulture) + "% — "
                           + FormatBytes(growing.Length) + " из " + FormatBytes(total);

                var bar = from + Math.Min(to - from, (to - from) * percent / 100.0);
                progress.Report(new InstallProgress(stage, text, bar));

                lock (progressLock)
                {
                    if ((DateTime.UtcNow - lastProgressLog).TotalSeconds >= 15)
                    {
                        lastProgressLog = DateTime.UtcNow;
                        Log.Info(stage, text);
                    }
                }
            }
            catch
            {
                // счётчик прогресса не должен мешать установке
            }
        }

        void Capture(string line, bool isError)
        {
            // pip печатает адрес прокси (с паролем) в своих сообщениях об ошибках.
            line = HttpFactory.MaskSecrets(line);
            lineCount++;

            // Строки прогресса в хвост ошибки не кладём: они вытесняют полезный вывод.
            var isProgress = TryParseDownloadProgress(line, out var received, out var total);

            if (!isProgress)
            {
                lock (tail)
                {
                    tail.Enqueue(line);
                    while (tail.Count > 25) tail.Dequeue();
                }
            }

            var pct = from + Math.Min(to - from, (to - from) * (lineCount / 400.0));

            if (isProgress)
            {
                string text;
                bool logIt;

                lock (progressLock)
                {
                    rawProgressSeen = true;

                    var percent = total > 0 ? Math.Min(100.0, received * 100.0 / total) : -1.0;
                    var finished = total > 0 && received >= total;

                    // Обновляем надпись не чаще чем каждые 0.1%, иначе UI задыхается.
                    if (percent >= 0 && lastPercent >= 0 && !finished && percent - lastPercent < 0.1) return;
                    lastPercent = percent;

                    text = percent >= 0
                        ? percent.ToString("0.0", CultureInfo.InvariantCulture) + "% — " + FormatBytes(received) + " из " + FormatBytes(total)
                        : "Скачано " + FormatBytes(received);

                    if (currentFile.Length > 0) text = currentFile + ": " + text;

                    // В журнал пишем редко, иначе файл раздуется до гигабайтов.
                    logIt = finished || (DateTime.UtcNow - lastProgressLog).TotalSeconds >= 15;
                    if (logIt) lastProgressLog = DateTime.UtcNow;
                }

                if (logIt) Log.Info(stage, text);
                progress.Report(new InstallProgress(stage, text, pct));
                return;
            }

            // Началось новое скачивание — запоминаем имя файла и сбрасываем проценты.
            var startedFile = TryParseDownloadStart(line);

            if (isError) Log.Warn(stage, line);
            else Log.Info(stage, line);

            if (startedFile != null)
            {
                var hint = TryParseSizeHint(line);

                lock (progressLock)
                {
                    currentFile = startedFile;
                    lastPercent = -1.0;
                    lastProgressLog = DateTime.MinValue;
                    if (hint > 0) expectedTotal = hint;
                }

                var head = hint > 0
                    ? "Скачивание " + startedFile + " (" + FormatBytes(hint) + ")..."
                    : "Скачивание " + startedFile + "...";

                progress.Report(new InstallProgress(stage, head, pct));

                // Страховка на случай, если pip не пришлёт ни одной строки прогресса.
                sizeWatcher ??= new System.Threading.Timer(_ => ReportBySize(), null,
                    TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

                return;
            }

            progress.Report(new InstallProgress(stage, line.Length > 160 ? line[..160] : line, pct));
        }

        // Саму команду тоже фиксируем — потом проще разбираться.
        // Обязательно через MaskSecrets: в аргументах может быть --proxy с логином и паролем.
        Log.Info("Installer", "Запуск: " + AppPaths.PythonExe + " " + HttpFactory.MaskSecrets(string.Join(' ', args)));

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            Capture(e.Data, false);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            Capture(e.Data, true);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            sizeWatcher?.Dispose();
            sizeWatcher = null;

            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* процесс уже умер */ }
            throw;
        }

        // Даём асинхронным читателям добрать остаток вывода.
        process.WaitForExit();

        sizeWatcher?.Dispose();
        sizeWatcher = null;

        Log.Info("Installer", "Код возврата: " + process.ExitCode);

        string tailText;
        lock (tail) tailText = string.Join(Environment.NewLine, tail);

        if (string.IsNullOrWhiteSpace(tailText))
            tailText = "(python ничего не вывел)";

        return (process.ExitCode, tailText);
    }

    private async Task<(int Code, string Output)> RunPythonCaptureAsync(string[] args, CancellationToken ct)
    {
        var psi = CreatePythonStartInfo(args);
        using var process = Process.Start(psi)!;

        // Оба потока читаем ПАРАЛЛЕЛЬНО: последовательное чтение дедлочит,
        // когда процесс заполняет буфер stderr раньше, чем закроет stdout (pip так умеет).
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout + stderr);
    }

    private static void CopyDirectory(string source, string target)
    {
        // string.Replace для путей опасен (подстрока может встретиться в середине пути) —
        // строим относительные пути явно.
        Directory.CreateDirectory(target);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), true);
    }
}

internal static class EmbeddedResources
{
    /// <summary>
    /// Ищет ресурс по ТОЧНОМУ имени файла: имя ресурса заканчивается на ".имяфайла"
    /// или равно ему целиком. Простой EndsWith ловил бы и "x-vc_worker.py".
    /// </summary>
    private static string ResolveName(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .FirstOrDefault(n =>
                string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Встроенный ресурс {fileName} не найден");
    }

    /// <summary>Открывает встроенный ресурс на чтение (нужно для подсчёта контрольной суммы).</summary>
    public static Stream Open(string fileName)
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(ResolveName(fileName))!;
    }

    public static string Read(string fileName)
    {
        using var stream = Open(fileName);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Извлекает встроенный двоичный ресурс в файл на диске.</summary>
    public static void ExtractTo(string fileName, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var stream = Open(fileName);
        using var file = File.Create(destinationPath);
        stream.CopyTo(file);
    }
}
