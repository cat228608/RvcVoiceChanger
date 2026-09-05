using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RvcVoiceChanger.Core;

namespace RvcVoiceChanger.Net;

/// <summary>
/// Создаёт HttpClient с учётом прокси из настроек.
/// .NET 8 умеет SOCKS5 нативно: WebProxy("socks5://host:port").
/// Клиенты кэшируются по конфигурации прокси — не создаём новый на каждый запрос.
/// </summary>
public static class HttpFactory
{
    public const string UserAgent = "RvcVoiceChanger/1.0 (+windows)";

    private static readonly ConcurrentDictionary<string, HttpClient> SharedClients = new();

    private static readonly Regex CredentialsInUrl = new(
        @"(?<scheme>[a-z][a-z0-9+.-]*://)[^/@\s]+@",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Маскирует логин:пароль в любых URL внутри строки (аргументы процессов,
    /// сообщения исключений, логи). Обязательно прогонять через это всё,
    /// что может содержать ProxyUrlForChildProcess.
    /// </summary>
    public static string MaskSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return CredentialsInUrl.Replace(text, "${scheme}***@");
    }

    /// <summary>
    /// Разбирает введённый пользователем адрес прокси.
    /// Понимает "host", "host:port", "scheme://host:port", IPv6 с/без скобок.
    /// Порт из строки имеет приоритет над отдельным полем (раньше он молча терялся).
    /// Никаких «починок опечаток» IP — адрес пользователя не переписываем.
    /// </summary>
    public static (string Host, int Port) ParseHostPort(string? host, int fallbackPort)
    {
        var value = (host ?? "").Trim();
        if (value.Length == 0) return ("", fallbackPort);

        var schemeAt = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeAt >= 0) value = value[(schemeAt + 3)..];

        // Отбрасываем user:pass@, если пользователь вставил полный URL.
        var at = value.LastIndexOf('@');
        if (at >= 0) value = value[(at + 1)..];

        value = value.Trim('/', ' ');

        // IPv6 в скобках: [::1] или [::1]:1080
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close > 0)
            {
                var v6 = value[1..close];
                var rest = value[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var v6Port) && v6Port is > 0 and < 65536)
                    return (v6, v6Port);
                return (v6, fallbackPort);
            }
        }

        // Голый IPv6 без скобок (несколько двоеточий) — порт не выделяем.
        if (value.Count(static c => c == ':') > 1)
            return (value, fallbackPort);

        // host:port
        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out var port) && port is > 0 and < 65536)
            return (value[..colon], port);

        return (value, fallbackPort);
    }

    private static int Count(this string s, Func<char, bool> predicate)
    {
        var n = 0;
        foreach (var c in s) if (predicate(c)) n++;
        return n;
    }

    /// <summary>Хост для URI: IPv6 берём в скобки.</summary>
    private static string ForUri(string host) =>
        host.Contains(':') && !host.StartsWith('[') ? "[" + host + "]" : host;

    public static IWebProxy? BuildProxy(AppSettings s)
    {
        if (s.ProxyMode == ProxyMode.None || string.IsNullOrWhiteSpace(s.ProxyHost))
            return null;

        var scheme = s.ProxyMode == ProxyMode.Socks5 ? "socks5" : "http";
        var (host, port) = ParseHostPort(s.ProxyHost, s.ProxyPort);
        var proxy = new WebProxy(scheme + "://" + ForUri(host) + ":" + port);

        if (!string.IsNullOrWhiteSpace(s.ProxyUser))
            proxy.Credentials = new NetworkCredential(s.ProxyUser, s.ProxyPassword);

        // Локальные адреса через прокси гонять нельзя: внутри живёт связь с воркером.
        proxy.BypassProxyOnLocal = true;

        return proxy;
    }

    /// <summary>
    /// Нужен ли прокси для конкретного адреса. HuggingFace (включая зеркало из настроек)
    /// смотрит на свою галочку, всё остальное (python.org, pypi.org,
    /// files.pythonhosted.org, download.pytorch.org, VB-Audio) — на галочку pip.
    /// </summary>
    public static bool UseProxyForUrl(AppSettings s, string url)
    {
        if (s.ProxyMode == ProxyMode.None || string.IsNullOrWhiteSpace(s.ProxyHost)) return false;

        var host = "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) host = uri.Host.ToLowerInvariant();

        var isHf = host.Contains("huggingface") || host == "hf.co" ||
                   host.EndsWith(".hf.co", StringComparison.Ordinal) ||
                   host.Contains("hf-mirror");

        if (!isHf && !string.IsNullOrWhiteSpace(s.HfEndpoint) &&
            Uri.TryCreate(s.HfEndpoint, UriKind.Absolute, out var mirror))
        {
            isHf = string.Equals(host, mirror.Host, StringComparison.OrdinalIgnoreCase);
        }

        return isHf ? s.ProxyForHuggingFace : s.ProxyForPip;
    }

    private static string ProxySignature(AppSettings s) =>
        string.Join("|", s.ProxyMode, s.ProxyHost, s.ProxyPort, s.ProxyUser, s.ProxyPassword);

    /// <summary>
    /// Общий клиент для коротких API-запросов (поиск моделей, проверка связи).
    /// Кэшируется по конфигурации прокси. Не для больших загрузок.
    /// </summary>
    public static HttpClient Shared(AppSettings s, bool useProxy)
    {
        var key = (useProxy ? "proxy|" + ProxySignature(s) : "direct") ;
        return SharedClients.GetOrAdd(key, _ => Create(s, useProxy, TimeSpan.FromSeconds(100)));
    }

    public static HttpClient Create(AppSettings s, bool useProxy = true, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };

        var proxy = useProxy ? BuildProxy(s) : null;
        if (proxy != null)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            // Для загрузок больших файлов таймаут на весь стрим не годится —
            // FileDownloader передаёт свой CancellationToken. Для API-запросов
            // вызывающие передают разумный timeout явно.
            Timeout = timeout ?? Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>
    /// Проверка связи — используется кнопкой "Проверить прокси".
    /// Учитывает галочки «прокси для HF/pip» так же, как реальные загрузки.
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestConnectionAsync(
        AppSettings s, string url = "https://huggingface.co/api/models?limit=1")
    {
        try
        {
            var useProxy = UseProxyForUrl(s, url);
            var client = Shared(s, useProxy);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var resp = await client.GetAsync(url, cts.Token);
            sw.Stop();

            var route = useProxy ? "через прокси" : "напрямую (по настройкам галочек)";
            var message = $"HTTP {(int)resp.StatusCode} {resp.StatusCode}, {sw.ElapsedMilliseconds} мс, {route}";
            return (resp.IsSuccessStatusCode, message);
        }
        catch (Exception ex)
        {
            Log.Warn("Net", "Проверка соединения не удалась: " + MaskSecrets(ex.Message));
            return (false, MaskSecrets(ex.Message));
        }
    }

    /// <summary>
    /// Переменные среды для дочерних python/pip-процессов — pip, huggingface_hub и wget
    /// пойдут через тот же прокси. useProxy решается вызывающим по назначению процесса
    /// (гранулярность галочек сохраняется).
    /// </summary>
    public static void ApplyProxyEnv(AppSettings s, System.Collections.Specialized.StringDictionary env, bool useProxy)
    {
        var url = useProxy ? ProxyUrlForChildProcess(s) : null;
        if (url == null)
        {
            // Явно отключаем возможный системный прокси, чтобы поведение было предсказуемым.
            env["NO_PROXY"] = "*";
            return;
        }

        env["HTTP_PROXY"] = url;
        env["HTTPS_PROXY"] = url;
        env["ALL_PROXY"] = url;
        env["NO_PROXY"] = "localhost,127.0.0.1,::1";
    }

    public static string? ProxyUrlForChildProcess(AppSettings s)
    {
        if (s.ProxyMode == ProxyMode.None || string.IsNullOrWhiteSpace(s.ProxyHost)) return null;

        var scheme = s.ProxyMode == ProxyMode.Socks5 ? "socks5h" : "http";
        var auth = string.IsNullOrWhiteSpace(s.ProxyUser)
            ? ""
            : $"{Uri.EscapeDataString(s.ProxyUser)}:{Uri.EscapeDataString(s.ProxyPassword)}@";

        var (host, port) = ParseHostPort(s.ProxyHost, s.ProxyPort);
        return scheme + "://" + auth + ForUri(host) + ":" + port;
    }
}
