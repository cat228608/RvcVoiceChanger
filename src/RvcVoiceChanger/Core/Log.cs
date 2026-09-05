using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;

namespace RvcVoiceChanger.Core;

public enum LogLevel { Trace, Debug, Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string LevelText => Level.ToString().ToUpperInvariant();

    public override string ToString() =>
        $"{Time:yyyy-MM-dd HH:mm:ss.fff} [{LevelText,-5}] [{Source}] {Message}";
}

/// <summary>
/// Общий логгер: коллекция для окна "Логи" + асинхронная запись в файл logs/app.log.
///
/// Размер логов жёстко ограничен: вся папка логов не превышает 1 МБ. Файлы работают
/// как кольцевой буфер — когда файл дорастает до своего лимита, самая старая половина
/// строк выбрасывается, а свежие записи продолжают писаться. Поэтому даже через год
/// работы логи занимают столько же, сколько в первый день.
/// </summary>
public static class Log
{
    private const int MaxInMemory = 5000;

    /// <summary>Общий бюджет всей папки логов.</summary>
    public const long TotalBudgetBytes = 1024 * 1024;

    /// <summary>Лимит основного журнала (app.log).</summary>
    public const long AppLogMaxBytes = 700 * 1024;

    /// <summary>Лимит журнала установки (install.log).</summary>
    public const long InstallLogMaxBytes = 300 * 1024;

    private static readonly object _fileLock = new();
    private static readonly BlockingCollection<LogEntry> _pending = new(new ConcurrentQueue<LogEntry>());
    private static string _logFile = string.Empty;
    private static Thread? _writer;
    private static volatile bool _shutdown;

    /// <summary>Сколько байт записано с последней проверки размера.</summary>
    private static long _bytesSinceCheck;

    public static ObservableCollection<LogEntry> Entries { get; } = new();

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static event Action<LogEntry>? Written;

    public static string CurrentLogFile => _logFile;

    public static void Start()
    {
        if (_writer != null) return;

        // Один файл вместо файла-на-дату: так размер логов предсказуем и ограничен.
        _logFile = Path.Combine(AppPaths.Logs, "app.log");
        CleanupLogs();

        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "log-writer" };
        _writer.Start();

        Info("Log", $"Файл логов: {_logFile} (лимит {AppLogMaxBytes / 1024} КБ)");
    }

    public static void Trace(string source, string message) => Write(LogLevel.Trace, source, message);
    public static void Debug(string source, string message) => Write(LogLevel.Debug, source, message);
    public static void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public static void Warn(string source, string message) => Write(LogLevel.Warn, source, message);
    public static void Error(string source, string message) => Write(LogLevel.Error, source, message);

    public static void Error(string source, string message, Exception ex) =>
        Write(LogLevel.Error, source, $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    public static void Write(LogLevel level, string source, string message)
    {
        if (level < MinLevel && level != LogLevel.Error) return;

        var entry = new LogEntry(DateTime.Now, level, source, message);

        // Логгер не имеет права уронить приложение ни при каких условиях.
        // Пока поток-писатель не запущен (или уже остановлен) — пишем в файл сами,
        // иначе строки, накопленные до Log.Start(), никогда не попадут на диск.
        if (_writer != null && !_shutdown)
        {
            try { _pending.TryAdd(entry); } catch { }
        }
        else
        {
            try { WriteFileFallback(entry); } catch { }
        }

        var app = Application.Current;
        if (app?.Dispatcher == null) { PushToCollection(entry); return; }

        if (app.Dispatcher.CheckAccess()) PushToCollection(entry);
        else app.Dispatcher.BeginInvoke(() => PushToCollection(entry));
    }

    private static void PushToCollection(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxInMemory) Entries.RemoveAt(0);
        try { Written?.Invoke(entry); } catch { }
    }

    /// <summary>
    /// Прямая запись в файл, когда поток-писатель не работает: до Log.Start()
    /// (например, во время окна установки) или после Shutdown().
    /// </summary>
    private static void WriteFileFallback(LogEntry entry)
    {
        if (string.IsNullOrEmpty(_logFile))
        {
            _logFile = Path.Combine(AppPaths.Logs, "app.log");
            try { Directory.CreateDirectory(AppPaths.Logs); } catch { }
        }

        lock (_fileLock)
            AppendLocked(entry + Environment.NewLine);
    }

    private static void WriterLoop()
    {
        foreach (var entry in _pending.GetConsumingEnumerable())
        {
            try
            {
                lock (_fileLock)
                    AppendLocked(entry + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>Дописывает текст и время от времени подрезает файл. Вызывать под _fileLock.</summary>
    private static void AppendLocked(string text)
    {
        File.AppendAllText(_logFile, text, Encoding.UTF8);

        // Проверять размер на каждой строке дорого, поэтому раз в 32 КБ записанного.
        _bytesSinceCheck += text.Length;
        if (_bytesSinceCheck < 32 * 1024) return;

        _bytesSinceCheck = 0;
        TrimFile(_logFile, AppLogMaxBytes);
    }

    /// <summary>
    /// Подрезает файл до лимита, выбрасывая самые старые строки и оставляя свежие.
    /// Обрезка идёт по границе строки, поэтому файл всегда остаётся читаемым.
    /// </summary>
    public static void TrimFile(string path, long maxBytes)
    {
        try
        {
            if (!File.Exists(path)) return;

            var info = new FileInfo(path);
            if (info.Length <= maxBytes) return;

            // Оставляем половину лимита, чтобы подрезка случалась редко, а не на каждой строке.
            var keep = Math.Max(16 * 1024, maxBytes / 2);

            byte[] tail;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(-keep, SeekOrigin.End);
                tail = new byte[keep];
                var read = stream.Read(tail, 0, tail.Length);
                if (read < tail.Length) Array.Resize(ref tail, read);
            }

            // Отрезаем начало до первого перевода строки, чтобы не получить обрывок строки.
            var offset = Array.IndexOf(tail, (byte)'\n');
            offset = offset < 0 ? 0 : offset + 1;

            var header = Encoding.UTF8.GetBytes(
                $"--- старые записи удалены {DateTime.Now:yyyy-MM-dd HH:mm:ss}, лимит {maxBytes / 1024} КБ ---{Environment.NewLine}");

            var tempPath = path + ".trim";
            using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(header, 0, header.Length);
                output.Write(tail, offset, tail.Length - offset);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Не смогли подрезать — не беда, но и падать из-за логов нельзя.
        }
    }

    /// <summary>
    /// Убирает старые файлы логов (в том числе прежние app-ГГГГ-ММ-ДД.log) и держит
    /// папку логов в пределах общего бюджета.
    /// </summary>
    private static void CleanupLogs()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);

            // Файлы прежней схемы (по одному на дату) больше не нужны.
            foreach (var file in Directory.GetFiles(AppPaths.Logs, "app-*.log"))
            {
                try { File.Delete(file); } catch { }
            }

            foreach (var file in Directory.GetFiles(AppPaths.Logs, "*.trim"))
            {
                try { File.Delete(file); } catch { }
            }

            TrimFile(_logFile, AppLogMaxBytes);
            TrimFile(Path.Combine(AppPaths.Logs, "install.log"), InstallLogMaxBytes);

            EnforceTotalBudget();
        }
        catch { }
    }

    /// <summary>
    /// Страховка: если в папке логов оказалось что-то лишнее и суммарный размер
    /// превысил бюджет, удаляем самые старые посторонние файлы.
    /// </summary>
    public static void EnforceTotalBudget()
    {
        try
        {
            var files = new DirectoryInfo(AppPaths.Logs).GetFiles();
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (total <= TotalBudgetBytes) return;

            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            foreach (var f in files)
            {
                if (total <= TotalBudgetBytes) break;

                var isCurrent = string.Equals(f.FullName, _logFile, StringComparison.OrdinalIgnoreCase);
                var isInstall = string.Equals(f.Name, "install.log", StringComparison.OrdinalIgnoreCase);

                if (isCurrent || isInstall) continue;

                var size = f.Length;
                try { f.Delete(); total -= size; } catch { }
            }

            // Если и после этого не влезаем, режем сами журналы жёстче.
            if (total > TotalBudgetBytes)
            {
                TrimFile(Path.Combine(AppPaths.Logs, "install.log"), InstallLogMaxBytes / 2);
                TrimFile(_logFile, AppLogMaxBytes / 2);
            }
        }
        catch { }
    }

    /// <summary>
    /// Дожидается, пока накопленные строки лягут в файл. ОЧЕРЕДЬ НЕ ЗАКРЫВАЕТ:
    /// раньше здесь вызывался CompleteAdding(), и следующая же запись в лог роняла программу.
    /// </summary>
    public static void Flush()
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (_pending.Count > 0 && DateTime.UtcNow < deadline)
                Thread.Sleep(15);
        }
        catch { }
    }

    /// <summary>Вызывать ТОЛЬКО при выходе из программы.</summary>
    public static void Shutdown()
    {
        try
        {
            Flush();
            _shutdown = true;
            _pending.CompleteAdding();
            _writer?.Join(TimeSpan.FromSeconds(2));

            lock (_fileLock)
                TrimFile(_logFile, AppLogMaxBytes);

            EnforceTotalBudget();
        }
        catch { }
    }
}
