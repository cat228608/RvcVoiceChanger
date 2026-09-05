using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RvcVoiceChanger.Parsers;

/// <summary>Один файл, который надо скачать (адрес + имя на диске).</summary>
public sealed record ModelDownload(string Url, string FileName);

/// <summary>
/// Вариант модели в правом списке парсера.
/// Ссылки могут быть известны сразу (HuggingFace, таблица voice-models)
/// либо доставаться лениво со страницы модели — тогда заполнен DownloadResolver.
/// </summary>
public sealed class ModelCandidate
{
    public string Title { get; init; } = "";

    /// <summary>Тип раздачи: ZIP, pth + index, Google Drive и т.п.</summary>
    public string KindText { get; init; } = "";

    public string SizeText { get; init; } = "—";

    /// <summary>Откуда модель — пишется в библиотеку моделей как источник.</summary>
    public string Source { get; init; } = "";

    /// <summary>Страница модели/репозитория для кнопки «Открыть».</summary>
    public string? PageUrl { get; init; }

    /// <summary>Имя папки в Downloads.</summary>
    public string FolderName { get; init; } = "model";

    public IReadOnlyList<ModelDownload> Downloads { get; init; } = Array.Empty<ModelDownload>();

    /// <summary>Ленивая догрузка ссылок (используется, когда в списке ссылки нет).</summary>
    public Func<CancellationToken, Task<IReadOnlyList<ModelDownload>>>? DownloadResolver { get; init; }

    public async Task<IReadOnlyList<ModelDownload>> GetDownloadsAsync(CancellationToken ct)
    {
        if (Downloads.Count > 0) return Downloads;
        if (DownloadResolver != null) return await DownloadResolver(ct);
        return Array.Empty<ModelDownload>();
    }
}

/// <summary>Элемент левого списка: автор на HuggingFace или модель на voice-models.com.</summary>
public sealed class ModelGroup
{
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? Url { get; init; }
    public List<ModelCandidate> Candidates { get; init; } = new();
}

/// <summary>Общий интерфейс сервиса-источника моделей (HuggingFace, voice-models.com, ...).</summary>
public interface IModelSource
{
    /// <summary>Идентификатор для settings.json.</summary>
    string Id { get; }

    /// <summary>Название в выпадающем списке.</summary>
    string DisplayName { get; }

    /// <summary>Заголовок левого списка.</summary>
    string GroupsHeader { get; }

    /// <summary>Подсказка к полю поиска.</summary>
    string SearchHint { get; }

    /// <summary>Текст кнопки «открыть в браузере».</summary>
    string OpenButtonText { get; }

    /// <summary>Парсит одну страницу. page — с нуля.</summary>
    Task<List<ModelGroup>> ParsePageAsync(
        int page,
        string? search,
        IProgress<string>? status,
        CancellationToken ct);
}

/// <summary>
/// Приведение найденных ссылок к виду, пригодному для FileDownloader:
/// нормализация Google Drive, дописывание download=true для HuggingFace,
/// безопасные имена файлов.
/// </summary>
public static class DownloadFactory
{
    private static readonly Regex DriveFileId =
        new(@"/file/d/(?<id>[A-Za-z0-9_-]{10,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DriveIdParam =
        new(@"[?&]id=(?<id>[A-Za-z0-9_-]{10,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] BrowserOnlyHosts =
    {
        "mega.nz", "mega.co.nz", "mediafire.com", "workupload.com",
        "krakenfiles.com", "terabox.com", "1024terabox.com", "gofile.io"
    };

    /// <summary>Хостинги, откуда прямой скачкой файл не забрать — только через браузер.</summary>
    public static bool IsBrowserOnly(string url)
    {
        var lower = url.ToLowerInvariant();
        return IsFolderLink(url) || BrowserOnlyHosts.Any(h => lower.Contains(h));
    }

    /// <summary>
    /// Ссылка на папку, а не на файл: huggingface.co/…/tree/main или Google Drive /drive/folders/…
    /// На voice-models.com такие встречаются регулярно; качать их бессмысленно — придёт HTML.
    /// </summary>
    public static bool IsFolderLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        if (IsHuggingFace(host) && (path.Contains("/tree/") || path.EndsWith("/tree"))) return true;
        if (host.Contains("drive.google") && path.Contains("/folders/")) return true;

        return false;
    }

    private static bool IsHuggingFace(string host) =>
        host.Contains("huggingface") || host.Contains("hf-mirror") || host == "hf.co";

    public static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : "";

    /// <summary>Короткая подпись типа раздачи для колонки «Тип».</summary>
    public static string KindText(string url)
    {
        var host = HostOf(url);
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath.ToLowerInvariant() : url.ToLowerInvariant();

        if (IsFolderLink(url))
            return IsHuggingFace(host) ? "HF папка (браузер)" : "папка (браузер)";

        var ext = path.EndsWith(".zip") ? "ZIP"
            : path.EndsWith(".pth") ? "PTH"
            : path.EndsWith(".7z") ? "7z"
            : path.EndsWith(".rar") ? "RAR"
            : "";

        if (host.Contains("huggingface") || host.Contains("hf-mirror") || host == "hf.co")
            return ext.Length > 0 ? "HF " + ext : "HuggingFace";

        if (host.Contains("drive.google") || host.Contains("googleusercontent"))
            return "Google Drive";

        if (IsBrowserOnly(url)) return host + " (браузер)";

        if (ext.Length > 0) return ext;
        return host.Length > 0 ? host : "ссылка";
    }

    public static ModelDownload Create(string url, string? titleHint = null)
    {
        var normalized = url.Trim();
        var fallbackName = SafeFileName(titleHint) + ".zip";

        var host = HostOf(normalized);

        // Google Drive: /file/d/<id>/view не скачивается напрямую — переводим на
        // прямой эндпоинт с подтверждением (иначе прилетает HTML-страница).
        if (host.Contains("drive.google") || host.Contains("drive.usercontent.google") || host.Contains("docs.google"))
        {
            var id = DriveFileId.Match(normalized) is { Success: true } m1 ? m1.Groups["id"].Value
                : DriveIdParam.Match(normalized) is { Success: true } m2 ? m2.Groups["id"].Value
                : null;

            if (id != null)
                normalized = "https://drive.usercontent.google.com/download?id=" + id + "&export=download&confirm=t";

            return new ModelDownload(normalized, fallbackName);
        }

        // Страница файла (/blob/) — это HTML; сам файл лежит по /resolve/.
        if (IsHuggingFace(host) && normalized.Contains("/blob/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized.Replace("/blob/", "/resolve/", StringComparison.OrdinalIgnoreCase);

        // HuggingFace отдаёт файл только с download=true (иначе HTML-страница файла).
        if (IsHuggingFace(host) &&
            normalized.Contains("/resolve/", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("download=true", StringComparison.OrdinalIgnoreCase))
        {
            normalized += normalized.Contains('?') ? "&download=true" : "?download=true";
        }

        return new ModelDownload(normalized, FileNameFromUrl(normalized) ?? fallbackName);
    }

    /// <summary>Имя файла из адреса (без query). null, если в адресе имени нет.</summary>
    public static string? FileNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var last = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(last)) return null;

        last = Uri.UnescapeDataString(last);
        if (!last.Contains('.')) return null;

        var clean = Sanitize(last);
        return clean.Length == 0 ? null : clean;
    }

    public static string SafeFileName(string? title)
    {
        var clean = Sanitize(title ?? "").Trim();
        if (clean.Length == 0) clean = "model";
        return clean.Length > 60 ? clean[..60].Trim() : clean;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim();
    }
}
