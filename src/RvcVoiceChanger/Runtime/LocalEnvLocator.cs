using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using RvcVoiceChanger.Core;

namespace RvcVoiceChanger.Runtime;

/// <summary>Найденное на диске окружение Python, из которого можно забрать уже скачанные пакеты.</summary>
public sealed class LocalEnv
{
    /// <summary>Папка site-packages этого окружения.</summary>
    public string SitePackages { get; init; } = "";

    /// <summary>Версия torch, например 2.5.1+cu121. Пусто, если torch там нет.</summary>
    public string TorchVersion { get; init; } = "";

    /// <summary>Версия CUDA из torch/version.py, например 12.1. Пусто для CPU-сборки.</summary>
    public string CudaVersion { get; init; } = "";

    /// <summary>Метка ABI Python, под который собраны бинарники: cp310, cp311 и так далее.</summary>
    public string AbiTag { get; init; } = "";

    /// <summary>Пакеты из нашего списка, которые здесь нашлись: имя -> версия.</summary>
    public Dictionary<string, string> Packages { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasCuda => CudaVersion.Length > 0;

    public bool AbiMatches => string.Equals(AbiTag, LocalEnvLocator.RequiredAbiTag, StringComparison.OrdinalIgnoreCase);

    public string Summary
    {
        get
        {
            var torch = TorchVersion.Length > 0 ? "torch " + TorchVersion : "torch не найден";
            var abi = AbiTag.Length > 0 ? AbiTag : "неизвестный ABI";
            var verdict = AbiMatches ? "подходит" : "не подходит (нужен " + LocalEnvLocator.RequiredAbiTag + ")";
            return torch + " · " + abi + " · пакетов: " + Packages.Count + " · " + verdict;
        }
    }
}

/// <summary>
/// Ищет на диске чужие окружения Python и переносит из них уже скачанные пакеты
/// в наш встроенный рантайм. Нужно тем, у кого медленный интернет: torch с CUDA весит
/// больше двух гигабайт, а копирование с диска бесплатно.
/// </summary>
public static class LocalEnvLocator
{
    /// <summary>Бинарные пакеты собираются под конкретную версию Python, копировать можно только совпадающие.</summary>
    public const string RequiredAbiTag = "cp311";

    /// <summary>Что имеет смысл переносить. Ключ — имя пакета, значение — папки и файлы в site-packages.</summary>
    private static readonly (string Package, string[] Items)[] Portable =
    {
        ("torch", new[] { "torch", "torchgen", "functorch" }),
        ("torchaudio", new[] { "torchaudio", "torio" }),
        ("numpy", new[] { "numpy" }),
        ("scipy", new[] { "scipy" }),
        ("librosa", new[] { "librosa" }),
        ("soundfile", new[] { "soundfile.py", "_soundfile.py", "_soundfile_data" }),
        ("soxr", new[] { "soxr" }),
        ("transformers", new[] { "transformers" }),
        ("tokenizers", new[] { "tokenizers" }),
        ("safetensors", new[] { "safetensors" }),
        ("huggingface_hub", new[] { "huggingface_hub" }),
        ("regex", new[] { "regex" }),
        ("einops", new[] { "einops" }),
        ("numba", new[] { "numba" }),
        ("llvmlite", new[] { "llvmlite" }),
        ("joblib", new[] { "joblib" }),
        ("audioread", new[] { "audioread" }),
        ("pooch", new[] { "pooch" }),
        ("lazy_loader", new[] { "lazy_loader" }),
        ("msgpack", new[] { "msgpack" }),
        ("decorator", new[] { "decorator.py" }),
        ("threadpoolctl", new[] { "threadpoolctl.py" }),
        ("scikit_learn", new[] { "sklearn" }),
        ("cffi", new[] { "cffi" }),
        ("pycparser", new[] { "pycparser" }),
        ("filelock", new[] { "filelock" }),
        ("sympy", new[] { "sympy" }),
        ("mpmath", new[] { "mpmath" }),
        ("networkx", new[] { "networkx" }),
        ("jinja2", new[] { "jinja2" }),
        ("markupsafe", new[] { "markupsafe" }),
        ("typing_extensions", new[] { "typing_extensions.py" }),
        ("fsspec", new[] { "fsspec" }),
        ("packaging", new[] { "packaging" }),
        ("platformdirs", new[] { "platformdirs" }),
        ("pyyaml", new[] { "yaml", "_yaml" }),
        ("psutil", new[] { "psutil" }),
        ("requests", new[] { "requests" }),
        ("urllib3", new[] { "urllib3" }),
        ("certifi", new[] { "certifi" }),
        ("idna", new[] { "idna" }),
        ("charset_normalizer", new[] { "charset_normalizer" }),
        ("tqdm", new[] { "tqdm" }),
        ("colorama", new[] { "colorama" }),
        ("pysocks", new[] { "socks.py", "sockshandler.py" }),
        ("matplotlib", new[] { "matplotlib", "mpl_toolkits", "pylab.py" }),
        ("contourpy", new[] { "contourpy" }),
        ("cycler", new[] { "cycler" }),
        ("fonttools", new[] { "fontTools" }),
        ("kiwisolver", new[] { "kiwisolver" }),
        ("pyparsing", new[] { "pyparsing" }),
        ("python_dateutil", new[] { "dateutil" }),
        ("six", new[] { "six.py" }),
        ("pillow", new[] { "PIL" }),
    };

    /// <summary>Папки, в которые лезть бессмысленно или вредно.</summary>
    private static readonly string[] SkipNames =
    {
        "windows", "$recycle.bin", "system volume information", "node_modules",
        ".git", "temp", "tmp", "microsoft", "windowsapps", "driverstore"
    };

    /// <summary>Где обычно живут окружения Python.</summary>
    public static List<string> DefaultRoots()
    {
        var roots = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        void Add(string path)
        {
            if (path.Length > 0 && Directory.Exists(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                roots.Add(path);
        }

        Add(Path.Combine(localApp, "Programs", "Python"));
        Add(Path.Combine(localApp, "uv", "cache", "archive-v0"));
        Add(Path.Combine(profile, "anaconda3"));
        Add(Path.Combine(profile, "miniconda3"));
        Add(Path.Combine(profile, "AppData", "Roaming", "Python"));
        Add(Path.Combine(profile, "Desktop"));
        Add(Path.Combine(profile, "Downloads"));
        Add(Path.Combine(profile, "Documents"));

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;

            try
            {
                var root = drive.RootDirectory.FullName;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir).ToLowerInvariant();
                    // Без слишком широких подстрок вроде "ai" — она ловила пол-диска (Mail, Paint…).
                    if (name.StartsWith("python") || name.Contains("applio") || name.Contains("rvc")
                        || name.Contains("conda") || name.Contains("venv"))
                        Add(dir);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("LocalEnv", "Диск пропущен: " + ex.Message);
            }
        }

        return roots;
    }

    /// <summary>Обходит указанные папки и возвращает все окружения, где есть torch.</summary>
    public static List<LocalEnv> Scan(IEnumerable<string> roots, Action<string>? onStatus, CancellationToken ct, int maxDepth = 4)
    {
        var found = new List<LocalEnv>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            onStatus?.Invoke("Смотрю " + root);
            Walk(root, 0, maxDepth, found, seen, onStatus, ct);
        }

        return found.OrderByDescending(e => e.AbiMatches)
            .ThenByDescending(e => e.HasCuda)
            .ThenByDescending(e => e.Packages.Count)
            .ToList();
    }

    private static void Walk(string dir, int depth, int maxDepth, List<LocalEnv> found,
        HashSet<string> seen, Action<string>? onStatus, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > maxDepth) return;

        string[] children;
        try
        {
            children = Directory.GetDirectories(dir);
        }
        catch
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child).ToLowerInvariant();
            if (SkipNames.Contains(name)) continue;

            if (name is "site-packages" or "dist-packages")
            {
                if (seen.Add(child))
                {
                    var env = Inspect(child);
                    if (env != null)
                    {
                        onStatus?.Invoke("Найдено: " + child);
                        found.Add(env);
                    }
                }

                continue;
            }

            Walk(child, depth + 1, maxDepth, found, seen, onStatus, ct);
        }
    }

    /// <summary>Разбирает конкретную папку site-packages. Возвращает null, если torch там нет.</summary>
    public static LocalEnv? Inspect(string sitePackages)
    {
        try
        {
            var torchDir = Path.Combine(sitePackages, "torch");
            var versionFile = Path.Combine(torchDir, "version.py");
            if (!File.Exists(versionFile)) return null;

            var text = File.ReadAllText(versionFile);
            var version = Match(text, "__version__\\s*=\\s*['\"]([^'\"]+)['\"]");
            var cuda = Match(text, "cuda[^=]*=\\s*['\"]([^'\"]+)['\"]");

            var abi = DetectAbiTag(torchDir);
            var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var distInfos = SafeDirectories(sitePackages, "*.dist-info");

            foreach (var (package, items) in Portable)
            {
                if (!items.Any(item => File.Exists(Path.Combine(sitePackages, item))
                                       || Directory.Exists(Path.Combine(sitePackages, item))))
                    continue;

                packages[package] = DistInfoVersion(distInfos, package);
            }

            return new LocalEnv
            {
                SitePackages = sitePackages,
                TorchVersion = version,
                CudaVersion = cuda,
                AbiTag = abi,
                Packages = packages
            };
        }
        catch (Exception ex)
        {
            Log.Debug("LocalEnv", "Не удалось разобрать " + sitePackages + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>Под какую версию Python собран torch: смотрим на имя скомпилированного модуля.</summary>
    private static string DetectAbiTag(string torchDir)
    {
        foreach (var file in SafeFiles(torchDir, "*.pyd"))
        {
            var tag = Match(Path.GetFileName(file), "(cp3\\d+)");
            if (tag.Length > 0) return tag;
        }

        foreach (var file in SafeFiles(Path.Combine(torchDir, "lib"), "*.pyd"))
        {
            var tag = Match(Path.GetFileName(file), "(cp3\\d+)");
            if (tag.Length > 0) return tag;
        }

        return "";
    }

    private static string DistInfoVersion(string[] distInfos, string package)
    {
        var normalized = Normalize(package);

        foreach (var dir in distInfos)
        {
            var name = Path.GetFileNameWithoutExtension(dir);
            var dash = name.LastIndexOf('-');
            if (dash <= 0) continue;

            if (Normalize(name.Substring(0, dash)) == normalized)
                return name.Substring(dash + 1);
        }

        return "?";
    }

    private static string Normalize(string name) => name.ToLowerInvariant().Replace('-', '_').Replace('.', '_');

    private static string Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string[] SafeDirectories(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string[] SafeFiles(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Переносит пакеты из чужого окружения в наш рантайм: сами папки плюс метаданные
    /// *.dist-info, чтобы pip считал пакет установленным и не качал его снова.
    /// </summary>
    /// <summary>
    /// Пакеты, которые нельзя переносить из окружения с numpy 2.x:
    /// их бинарники скомпилированы под ABI numpy 2, а нам нужен numpy < 2.
    /// </summary>
    private static readonly string[] Numpy2Dependents =
    {
        "numpy", "scipy", "numba", "llvmlite", "scikit_learn", "librosa", "soxr", "matplotlib", "contourpy"
    };

    /// <summary>Проверяет, можно ли безопасно перенести пакет из этого окружения. null = можно.</summary>
    private static string? IncompatibilityReason(LocalEnv env, string package)
    {
        // numpy 2.x несовместим с нашими требованиями (numpy < 2), а скомпилированные
        // под него scipy/numba/sklearn сломаются после того, как pip переустановит numpy 1.26.
        if (env.Packages.TryGetValue("numpy", out var numpyVersion) && numpyVersion.StartsWith("2")
            && Numpy2Dependents.Contains(Normalize(package)))
            return "собран под numpy " + numpyVersion + " (нужен numpy < 2)";

        if (package is "torch" or "torchaudio")
        {
            // Установщик требует torch >= 2.4 — более старый переносить бессмысленно,
            // pip всё равно скачает нужный заново.
            var raw = env.TorchVersion.Split('+')[0];
            var parts = raw.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
                && (major < 2 || (major == 2 && minor < 4)))
                return "torch " + env.TorchVersion + " слишком стар (нужен 2.4+)";
        }

        return null;
    }

    public static (int Copied, long Bytes) Import(LocalEnv env, string targetSitePackages,
        Action<string> onStatus, CancellationToken ct)
    {
        Directory.CreateDirectory(targetSitePackages);

        var distInfos = SafeDirectories(env.SitePackages, "*.dist-info");
        var copied = 0;
        long bytes = 0;

        foreach (var (package, items) in Portable)
        {
            ct.ThrowIfCancellationRequested();
            if (!env.Packages.ContainsKey(package)) continue;

            var reason = IncompatibilityReason(env, package);
            if (reason != null)
            {
                onStatus("Пропускаю " + package + ": " + reason);
                Log.Warn("LocalEnv", "Пакет " + package + " не перенесён: " + reason);
                continue;
            }

            onStatus("Копирую " + package + " " + env.Packages[package]);

            foreach (var item in items)
            {
                var sourceDir = Path.Combine(env.SitePackages, item);
                var sourceFile = sourceDir;

                if (Directory.Exists(sourceDir))
                    bytes += CopyTree(sourceDir, Path.Combine(targetSitePackages, item), ct);
                else if (File.Exists(sourceFile))
                    bytes += CopyFile(sourceFile, Path.Combine(targetSitePackages, item));
            }

            // На Windows рядом с пакетом лежат папки с DLL: numpy.libs, scipy.libs,
            // scikit_learn.libs, pillow.libs… Без них импорт падает с «DLL load failed».
            foreach (var libsDir in SafeDirectories(env.SitePackages, "*.libs"))
            {
                var libsName = Path.GetFileName(libsDir);
                var owner = Normalize(libsName.Substring(0, libsName.Length - ".libs".Length));
                var belongs = owner == Normalize(package)
                    || items.Any(item => Normalize(Path.GetFileNameWithoutExtension(item)) == owner);
                if (!belongs) continue;

                bytes += CopyTree(libsDir, Path.Combine(targetSitePackages, libsName), ct);
            }

            foreach (var dir in distInfos)
            {
                var name = Path.GetFileNameWithoutExtension(dir);
                var dash = name.LastIndexOf('-');
                if (dash <= 0 || Normalize(name.Substring(0, dash)) != Normalize(package)) continue;

                bytes += CopyTree(dir, Path.Combine(targetSitePackages, Path.GetFileName(dir)), ct);

                // RECORD перечисляет и top-level файлы пакета вне его папки: например,
                // _cffi_backend.*.pyd у cffi лежит в корне site-packages. Без него
                // импорт падает с «No module named '_cffi_backend'».
                bytes += CopyTopLevelCompanions(dir, env.SitePackages, targetSitePackages);
            }

            copied++;
        }

        onStatus("Перенесено пакетов: " + copied + ", объём: " + FormatSize(bytes));
        Log.Info("LocalEnv", "Импорт из " + env.SitePackages + ": пакетов " + copied + ", " + FormatSize(bytes));

        return (copied, bytes);
    }

    /// <summary>Копирует top-level файлы пакета (*.pyd / *.py / *.dll в корне site-packages),
    /// перечисленные в его RECORD. Без них нативные пакеты ломаются после переноса.</summary>
    private static long CopyTopLevelCompanions(string distInfoDir, string sitePackages, string targetSitePackages)
    {
        long bytes = 0;
        try
        {
            var record = Path.Combine(distInfoDir, "RECORD");
            if (!File.Exists(record)) return 0;

            foreach (var line in File.ReadLines(record))
            {
                var path = line.Split(',')[0].Trim();
                if (path.Length == 0 || path.Contains('/') || path.Contains('\\')) continue;

                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".pyd" && ext != ".py" && ext != ".dll") continue;

                var source = Path.Combine(sitePackages, path);
                if (!File.Exists(source)) continue;

                bytes += CopyFile(source, Path.Combine(targetSitePackages, path));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("LocalEnv", "Не удалось перенести top-level файлы из RECORD: " + ex.Message);
        }

        return bytes;
    }

    private static long CopyTree(string source, string target, CancellationToken ct)
    {
        // Копируем во временную папку и подменяем цель только после успеха:
        // при отмене или ошибке старый пакет остаётся целым, а не полускопированным.
        var staging = target + ".new";
        long bytes = 0;

        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);

            Directory.CreateDirectory(staging);

            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dir).Equals("__pycache__", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.Combine(staging, Path.GetRelativePath(source, dir)));
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                if (file.Contains("__pycache__", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)) continue;

                var destination = Path.Combine(staging, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                bytes += CopyFile(file, destination);
            }

            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);

            Directory.Move(staging, target);
            return bytes;
        }
        catch
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // Недокопированная временная папка не мешает работе — удалим при следующем запуске.
            }

            throw;
        }
    }

    private static long CopyFile(string source, string target)
    {
        File.Copy(source, target, overwrite: true);
        return new FileInfo(source).Length;
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " ГБ";
        if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " МБ";
        return (bytes / 1024.0).ToString("F0") + " КБ";
    }
}
