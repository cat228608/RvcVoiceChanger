using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Net;

namespace RvcVoiceChanger.Parsers;

/// <summary>Строка таблицы voice-models.com.</summary>
public sealed record VoiceModelRow(string Id, string Title, string? SizeText, string? DownloadUrl);

/// <summary>
/// Парсер https://voice-models.com.
///
/// Сайт на Bootstrap + jQuery. Важное про его устройство:
///
///   главная / поиск : GET /  и  GET /?search=putin  — таблица уже в HTML;
///   страницы       : POST /fetch_data.php  { page, search }  → JSON { table, pagination }.
///                     Кнопки пагинации — это onclick="fetchData(2, 'putin')",
///                     а не ссылки ?page=2, поэтому вторую страницу GET-ом
///                     честно не получить — нужен тот же POST, что делает сайт.
///   модель        : GET /model/&lt;id&gt; — там блок «Download Link:».
///
/// В строке таблицы точный адрес файла лежит в кнопке копирования
/// (data-clipboard-text) — это самый надёжный источник, потому что видимая
/// ссылка в соседней колонке обрезана многоточием. Файлы хранятся не на
/// сайте, а на HuggingFace / Google Drive / weights.gg и т.п.
/// </summary>
public sealed class VoiceModelsSource : IModelSource
{
    public const string DefaultBase = "https://voice-models.com";

    private readonly AppSettings _settings;
    private readonly string _base;

    public VoiceModelsSource(AppSettings settings)
    {
        _settings = settings;
        _base = string.IsNullOrWhiteSpace(settings.VoiceModelsEndpoint)
            ? DefaultBase
            : settings.VoiceModelsEndpoint.Trim().TrimEnd('/');
    }

    public string Id => "voice-models";
    public string DisplayName => "voice-models.com";
    public string GroupsHeader => "Модели на странице";
    public string SearchHint => "Поиск по голосу, например: putin";
    public string OpenButtonText => "Открыть страницу модели";

    // ---------- Загрузка списка ----------

    public async Task<List<ModelGroup>> ParsePageAsync(
        int page,
        string? search,
        IProgress<string>? status,
        CancellationToken ct)
    {
        var query = (search ?? "").Trim();

        status?.Report(page == 0 && query.Length == 0
            ? "Загружаю список моделей..."
            : $"Загружаю страницу {page + 1}...");

        var html = await LoadListingAsync(page, query, ct);
        var rows = ParseListing(html);

        Log.Info("Parser", $"voice-models.com: страница {page + 1}, найдено строк: {rows.Count}");

        if (rows.Count == 0)
        {
            status?.Report(query.Length > 0
                ? $"По запросу «{query}» ничего не найдено"
                : "Страница пустая — вернитесь назад");
            return new List<ModelGroup>();
        }

        var withLink = rows.Count(r => r.DownloadUrl != null);
        status?.Report($"Моделей на странице: {rows.Count} (со ссылкой на файл: {withLink})");

        return rows.Select(ToGroup).ToList();
    }

    /// <summary>
    /// Первая страница есть в обычном HTML, остальные сайт грузит через fetch_data.php.
    /// Для надёжности POST пробуем всегда, а при любой его ошибке откатываемся
    /// на GET (тогда работает хотя бы первая страница и поиск).
    /// </summary>
    private async Task<string> LoadListingAsync(int page, string search, CancellationToken ct)
    {
        try
        {
            return await PostFetchDataAsync(page + 1, search, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Parser", "voice-models.com: fetch_data.php не ответил (" + ex.Message + "), пробую обычную страницу");

            if (page > 0)
                throw new InvalidOperationException(
                    "Сайт грузит вторую и дальние страницы через fetch_data.php, и этот запрос не прошёл: "
                    + ex.Message, ex);

            return await GetStringAsync(BuildListUrl(search), ct);
        }
    }

    private string BuildListUrl(string search) =>
        _base + "/" + (search.Length > 0 ? "?search=" + Uri.EscapeDataString(search) : "");

    /// <summary>Тот же запрос, что делает сайт при клике по номеру страницы. page — с единицы.</summary>
    private async Task<string> PostFetchDataAsync(int page, string search, CancellationToken ct)
    {
        var url = _base + "/fetch_data.php";
        var client = HttpFactory.Shared(_settings, useProxy: HttpFactory.UseProxyForUrl(_settings, url));

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["page"] = page.ToString(),
                ["search"] = search
            })
        };

        request.Headers.Accept.ParseAdd("application/json, text/javascript, */*;q=0.01");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9,ru;q=0.8");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri(BuildListUrl(search));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(1));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cts.Token);
        return ExtractTableHtml(body);
    }

    /// <summary>Из JSON-ответа { table, pagination } берёт HTML таблицы.</summary>
    private static string ExtractTableHtml(string body)
    {
        var trimmed = body.TrimStart();

        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            return body; // Сайт ответил сразу HTML — разбираем как есть.

        using var doc = JsonDocument.Parse(body);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return body;

        if (doc.RootElement.TryGetProperty("table", out var table) && table.ValueKind == JsonValueKind.String)
            return table.GetString() ?? "";

        // На случай переименованного поля — берём первую строку, в которой есть ссылка на модель.
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;

            var value = property.Value.GetString() ?? "";
            if (ModelHrefPattern.IsMatch(value)) return value;
        }

        return "";
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        var client = HttpFactory.Shared(_settings, useProxy: HttpFactory.UseProxyForUrl(_settings, url));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9,ru;q=0.8");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(1));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cts.Token);
    }

    // ---------- Преобразование в общие типы ----------

    private ModelGroup ToGroup(VoiceModelRow row)
    {
        var pageUrl = _base + "/model/" + row.Id;
        var hasLink = row.DownloadUrl != null;

        IReadOnlyList<ModelDownload> downloads = new List<ModelDownload>();
        Func<CancellationToken, Task<IReadOnlyList<ModelDownload>>>? resolver = null;

        if (hasLink)
            downloads = new List<ModelDownload> { DownloadFactory.Create(row.DownloadUrl!, row.Title) };
        else
            resolver = ct => ResolveFromPageAsync(pageUrl, row.Title, ct);

        var candidate = new ModelCandidate
        {
            Title = row.Title,
            KindText = hasLink ? DownloadFactory.KindText(row.DownloadUrl!) : "ссылка на странице модели",
            SizeText = row.SizeText ?? "—",
            Source = "voice-models.com/model/" + row.Id,
            PageUrl = pageUrl,
            FolderName = "vm-" + row.Id + "-" + DownloadFactory.SafeFileName(row.Title),
            Downloads = downloads,
            DownloadResolver = resolver
        };

        var parts = new List<string>();
        if (row.SizeText != null) parts.Add(row.SizeText);
        parts.Add(hasLink ? DownloadFactory.KindText(row.DownloadUrl!) : "ссылка берётся со страницы");

        return new ModelGroup
        {
            Title = row.Title,
            Summary = string.Join(" · ", parts),
            Url = pageUrl,
            Candidates = new List<ModelCandidate> { candidate }
        };
    }

    /// <summary>Лениво берёт ссылку со страницы модели (блок «Download Link:»).</summary>
    private async Task<IReadOnlyList<ModelDownload>> ResolveFromPageAsync(string pageUrl, string title, CancellationToken ct)
    {
        Log.Info("Parser", "voice-models.com: беру ссылку со страницы " + pageUrl);

        var html = await GetStringAsync(pageUrl, ct);
        var url = FindDownloadUrlOnModelPage(html);

        if (url == null)
            throw new InvalidOperationException(
                "На странице модели не нашлось ссылки на файл. Откройте страницу в браузере и скачайте вручную.");

        return new List<ModelDownload> { DownloadFactory.Create(url, title) };
    }

    // ---------- Разбор HTML ----------

    private static readonly RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;

    private static readonly Regex RowPattern = new(@"<tr\b[^>]*>(?<row>.*?)</tr>", Opts);

    /// <summary>Ссылка на модель: &lt;a href="/model/1nUUtnLNlpi" class="fs-5"&gt;Название&lt;/a&gt;.</summary>
    private static readonly Regex ModelLinkPattern =
        new(@"<a\b[^>]*href\s*=\s*[""'](?:https?://[^""']*?)?/model/(?<id>[^""'?#/]+)[""'][^>]*>(?<text>.*?)</a>", Opts);

    private static readonly Regex ModelHrefPattern =
        new(@"href\s*=\s*[""'](?:https?://[^""']*?)?/model/", Opts);

    /// <summary>Кнопка «скопировать URL» — там лежит полный, необрезанный адрес файла.</summary>
    private static readonly Regex ClipboardPattern =
        new(@"data-clipboard-text\s*=\s*[""'](?<url>https?://[^""']+)[""']", Opts);

    private static readonly Regex HrefPattern =
        new(@"href\s*=\s*[""'](?<url>https?://[^""']+)[""']", Opts);

    /// <summary>Адрес внутри url= / url[]= (кнопка Run на easyaivoice.com).</summary>
    private static readonly Regex EncodedUrlPattern =
        new(@"url(?:%5B%5D|\[\])?=(?<url>https?(?::|%3A)(?:/|%2F){2}[^""'&<>\s]+)", Opts);

    /// <summary>Размер в бейдже: &lt;span class="badge bg-secondary ..."&gt;73.44 MB&lt;/span&gt;.</summary>
    private static readonly Regex BadgeSizePattern =
        new(@"<span\b[^>]*class\s*=\s*[""'][^""']*badge[^""']*[""'][^>]*>\s*(?<size>\d+(?:[.,]\d+)?\s*(?:KB|MB|GB|TB))\s*</span>", Opts);

    private static readonly Regex TagPattern = new(@"<[^>]*>", Opts);
    private static readonly Regex SpacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Блок «&lt;b&gt;Download Link:&lt;/b&gt; &lt;a href="..."&gt;» на странице модели.</summary>
    private static readonly Regex DownloadLinkPattern =
        new(@"Download\s*Link\s*:?\s*(?:</b>|</strong>)?\s*<a\b[^>]*href\s*=\s*[""'](?<url>https?://[^""']+)[""']", Opts);

    private static readonly Regex RelatedUrlPattern =
        new(@"Related\s*URL\s*:?\s*<a\b[^>]*href\s*=\s*[""'](?<url>https?://[^""']+)[""']", Opts);

    /// <summary>Строка «Model : https://...» в описании.</summary>
    private static readonly Regex ModelLinePattern =
        new(@"Model\s*:\s*(?<url>https?://[^\s""'<>]+)", Opts);

    /// <summary>С этого места на странице модели идут СОСЕДНИЕ голоса — их ссылки брать нельзя.</summary>
    private static readonly Regex SoundalikePattern = new(@"soundalike|vm-sound|vm-enrichment", Opts);

    /// <summary>Файловые расширения, по которым опознаём ссылку на модель.</summary>
    private static readonly string[] FileExtensions = { ".zip", ".pth", ".7z", ".rar", ".index" };

    /// <summary>Хосты, где реально лежат файлы моделей.</summary>
    private static readonly string[] FileHosts =
    {
        "huggingface.co", "hf.co", "hf-mirror.com", "models.weights.gg", "weights.gg",
        "drive.google.com", "docs.google.com", "drive.usercontent.google.com",
        "pixeldrain.com", "cdn.discordapp.com", "media.discordapp.net",
        "mega.nz", "mega.co.nz", "mediafire.com", "dropbox.com",
        "raw.githubusercontent.com", "github.com", "workupload.com", "krakenfiles.com", "gofile.io"
    };

    /// <summary>Хосты-посредники: сам адрес не файл, но внутри него есть закодированный адрес файла.</summary>
    private static bool IsProxyLink(string url) =>
        url.Contains("easyaivoice.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("voice-models.com/url.php", StringComparison.OrdinalIgnoreCase);

    /// <summary>Разбор таблицы со страницы списка/поиска или из ответа fetch_data.php.</summary>
    public static List<VoiceModelRow> ParseListing(string html)
    {
        var rows = new List<VoiceModelRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in SplitRows(html))
        {
            var link = ModelLinkPattern.Match(chunk);
            if (!link.Success) continue;

            var id = link.Groups["id"].Value;
            if (id.Length == 0 || !seen.Add(id)) continue;

            var title = CleanText(link.Groups["text"].Value);
            if (title.Length == 0) title = "Модель " + id;

            var size = BadgeSizePattern.Match(chunk) is { Success: true } badge
                ? SpacePattern.Replace(badge.Groups["size"].Value.Trim(), " ")
                : null;

            rows.Add(new VoiceModelRow(id, title, size, FindDownloadUrlInRow(chunk)));
        }

        return rows;
    }

    /// <summary>Строки таблицы; если разметка изменится — режем по ссылкам /model/.</summary>
    private static IEnumerable<string> SplitRows(string html)
    {
        var rows = RowPattern.Matches(html)
            .Select(m => m.Groups["row"].Value)
            .Where(r => ModelHrefPattern.IsMatch(r))
            .ToList();

        if (rows.Count > 0) return rows;

        var anchors = ModelHrefPattern.Matches(html).Select(m => m.Index).ToList();
        var chunks = new List<string>();

        for (var i = 0; i < anchors.Count; i++)
        {
            var start = Math.Max(0, anchors[i] - 200);
            var end = i + 1 < anchors.Count ? anchors[i + 1] : html.Length;
            chunks.Add(html[start..Math.Max(start, end)]);
        }

        return chunks;
    }

    /// <summary>Ссылка на файл из одной строки таблицы.</summary>
    private static string? FindDownloadUrlInRow(string chunk)
    {
        // 1. Кнопка копирования — точный адрес без обрезки.
        var clipboard = ClipboardPattern.Match(chunk);
        if (clipboard.Success)
        {
            var url = CleanUrl(clipboard.Groups["url"].Value);
            if (url != null && !IsProxyLink(url)) return url;
        }

        return PickBest(CollectUrls(chunk));
    }

    /// <summary>Ссылка на файл со страницы модели /model/&lt;id&gt;.</summary>
    public static string? FindDownloadUrlOnModelPage(string html)
    {
        // Ниже блока «похожие голоса» лежат ссылки ДРУГИХ моделей — отрезаем.
        var cut = SoundalikePattern.Match(html);
        var head = cut.Success ? html[..cut.Index] : html;

        foreach (var pattern in new[] { DownloadLinkPattern, RelatedUrlPattern, ModelLinePattern })
        {
            var match = pattern.Match(head);
            if (!match.Success) continue;

            var url = CleanUrl(match.Groups["url"].Value);
            if (url != null && !IsProxyLink(url)) return url;
        }

        return PickBest(CollectUrls(head));
    }

    /// <summary>Все правдоподобные адреса файлов в куске HTML (в т.ч. из-под easyaivoice).</summary>
    private static List<string> CollectUrls(string chunk)
    {
        var found = new List<string>();

        foreach (Match match in HrefPattern.Matches(chunk))
        {
            var url = CleanUrl(match.Groups["url"].Value);
            if (url != null && !IsProxyLink(url)) found.Add(url);
        }

        // Кнопка Run ведёт на easyaivoice.com/run?url=<закодированный адрес файла>.
        foreach (Match match in EncodedUrlPattern.Matches(chunk))
        {
            var decoded = CleanUrl(Uri.UnescapeDataString(WebUtility.HtmlDecode(match.Groups["url"].Value)));
            if (decoded != null && !IsProxyLink(decoded)) found.Add(decoded);
        }

        return found.Where(IsFileUrl).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? PickBest(List<string> urls) =>
        urls.Count == 0 ? null : urls.OrderByDescending(Score).First();

    /// <summary>Какой адрес предпочтительнее для автоматического скачивания.</summary>
    private static int Score(string url)
    {
        var lower = url.ToLowerInvariant();
        var score = 0;

        if (lower.Contains("huggingface.co") || lower.Contains("hf-mirror")) score += 60;
        if (lower.Contains("/resolve/")) score += 20;
        if (lower.Contains("weights.gg")) score += 40;
        if (lower.Contains(".zip")) score += 30;
        if (lower.Contains("pixeldrain")) score += 10;
        if (lower.Contains("drive.google") || lower.Contains("docs.google")) score += 15;
        if (DownloadFactory.IsBrowserOnly(lower)) score -= 40;

        return score;
    }

    private static bool IsFileUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        if (host.EndsWith("voice-models.com", StringComparison.Ordinal)) return false;

        if (FileHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal))) return true;

        return FileExtensions.Any(e => path.EndsWith(e, StringComparison.Ordinal));
    }

    /// <summary>Обрезки вида «https://huggingface.co/...» бесполезны — отбрасываем их.</summary>
    private static string? CleanUrl(string raw)
    {
        var url = WebUtility.HtmlDecode(raw).Trim().Trim('"', '\'', '<', '>', ')', ',', ';');

        if (url.Length == 0) return null;
        if (url.Contains("...", StringComparison.Ordinal) || url.Contains('\u2026')) return null;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;

        return url;
    }

    /// <summary>Текст без тегов (название часто разбито &lt;span&gt;ом подсветки поиска).</summary>
    private static string CleanText(string html) =>
        SpacePattern.Replace(WebUtility.HtmlDecode(TagPattern.Replace(html, " ")), " ").Trim();
}
