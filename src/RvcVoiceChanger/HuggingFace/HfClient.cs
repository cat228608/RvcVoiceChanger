using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Net;

namespace RvcVoiceChanger.HuggingFace;

public sealed record HfFile(string Path, long Size)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    public bool IsZip => Extension == ".zip";
    public bool IsPth => Extension == ".pth";
    public bool IsIndex => Extension == ".index";

    public string SizeText => Size <= 0 ? "—" : $"{Size / 1024.0 / 1024.0:F1} МБ";
}

/// <summary>Один вариант модели внутри репозитория: либо zip, либо пара pth+index.</summary>
public sealed class HfModelCandidate
{
    public string RepoId { get; init; } = "";
    public string Title { get; init; } = "";
    public HfFile? Zip { get; init; }
    public HfFile? Pth { get; init; }
    public HfFile? Index { get; init; }

    public bool IsZip => Zip != null;

    public string KindText => IsZip ? "ZIP" : Index != null ? "pth + index" : "только pth";

    public string SizeText => IsZip
        ? Zip!.SizeText
        : $"{(Pth?.Size ?? 0) / 1024.0 / 1024.0:F1} МБ" + (Index != null ? $" + {Index.SizeText}" : "");

    public IEnumerable<string> DownloadUrls
    {
        get
        {
            if (IsZip) { yield return HfClient.ResolveUrl(RepoId, Zip!.Path); yield break; }
            if (Pth != null) yield return HfClient.ResolveUrl(RepoId, Pth.Path);
            if (Index != null) yield return HfClient.ResolveUrl(RepoId, Index.Path);
        }
    }
}

public sealed class HfRepo
{
    public string RepoId { get; init; } = "";
    public string Author => RepoId.Contains('/') ? RepoId.Split('/')[0] : RepoId;
    public string Name => RepoId.Contains('/') ? RepoId.Split('/')[1] : RepoId;
    public int Downloads { get; init; }
    public int Likes { get; init; }
    public DateTime? LastModified { get; init; }
    public List<HfModelCandidate> Candidates { get; init; } = new();

    public bool IsValid => Candidates.Count > 0;
    public string Url => HfClient.Base + "/" + RepoId;
    public string Summary => $"{Candidates.Count} моделей · {Downloads} скачиваний · {Likes} лайков";
}

/// <summary>
/// Парсер huggingface.co/models?other=rvc.
///
/// Страница со списком рендерится на JS, поэтому используем публичный API:
///   список  : /api/models?filter=rvc&amp;sort=downloads&amp;limit=10&amp;skip=N
///   файлы  : /api/models/{repo}/tree/main?recursive=true
///   скачать: /{repo}/resolve/main/{path}
///
/// Валидным автором считается тот, у кого есть либо .zip, либо пара .pth + .index.
/// Если есть и zip, и раздельные файлы — приоритет у zip.
/// </summary>
public sealed class HfClient
{
    private const int DefaultPageSize = 10;
    private const string DefaultBase = "https://huggingface.co";
    private const string DefaultFilter = "rvc";

    /// <summary>
    /// Адрес HuggingFace (или зеркала типа https://hf-mirror.com) из настроек.
    /// Статическое свойство, потому что ссылки строятся и из моделей-рекордов без доступа к настройкам.
    /// </summary>
    public static string Base { get; private set; } = DefaultBase;

    /// <summary>Кэш дерева файлов репозиториев — без него листание страниц быстро упирается в rate-limit HF.</summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, List<HfFile> Files)> TreeCache = new();
    private static readonly TimeSpan TreeCacheTtl = TimeSpan.FromMinutes(15);

    private readonly AppSettings _settings;

    public HfClient(AppSettings settings)
    {
        _settings = settings;
        Base = string.IsNullOrWhiteSpace(settings.HfEndpoint) ? DefaultBase : settings.HfEndpoint.Trim().TrimEnd('/');
    }

    /// <summary>Сколько авторов грузить на страницу (настраивается, по умолчанию 10).</summary>
    public int PageSize => _settings.HfPageSize > 0 ? _settings.HfPageSize : DefaultPageSize;

    private string Filter => string.IsNullOrWhiteSpace(_settings.HfSearchFilter) ? DefaultFilter : _settings.HfSearchFilter.Trim();

    public static string ResolveUrl(string repoId, string path) =>
        Base + "/" + repoId + "/resolve/main/" + Uri.EscapeDataString(path).Replace("%2F", "/") + "?download=true";

    /// <summary>GET с опциональным HF-токеном (приватные и gated-репозитории).</summary>
    private async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(_settings.HfToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.HfToken.Trim());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cts.Token);
    }

    /// <summary>Парсит одну страницу (10 авторов). page — с нуля.</summary>
    public async Task<List<HfRepo>> ParsePageAsync(
        int page,
        string? search,
        IProgress<string>? status,
        CancellationToken ct)
    {
        var skip = page * PageSize;
        status?.Report("Парсинг моделей...");

        // Общий кэшированный клиент вместо нового на каждую страницу.
        var client = HttpFactory.Shared(_settings, useProxy: _settings.ProxyForHuggingFace);

        var url = Base + "/api/models?filter=" + Uri.EscapeDataString(Filter) +
                  "&sort=downloads&direction=-1&limit=" + PageSize + "&skip=" + skip;
        if (!string.IsNullOrWhiteSpace(search))
            url += "&search=" + Uri.EscapeDataString(search.Trim());

        Log.Info("HF", $"Запрос списка: {url}");

        var listJson = await GetStringAsync(client, url, ct);
        using var listDoc = JsonDocument.Parse(listJson);

        var repoIds = new List<(string Id, int Downloads, int Likes, DateTime? Modified)>();

        foreach (var item in listDoc.RootElement.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var downloads = item.TryGetProperty("downloads", out var d) && d.TryGetInt32(out var dv) ? dv : 0;
            var likes = item.TryGetProperty("likes", out var l) && l.TryGetInt32(out var lv) ? lv : 0;
            DateTime? modified = item.TryGetProperty("lastModified", out var m) && m.TryGetDateTime(out var mv) ? mv : null;

            repoIds.Add((id!, downloads, likes, modified));
        }

        var result = new List<HfRepo>();
        var processed = 0;

        foreach (var (id, downloads, likes, modified) in repoIds)
        {
            ct.ThrowIfCancellationRequested();
            processed++;
            status?.Report($"Парсинг моделей... {processed}/{repoIds.Count}: {id}");

            try
            {
                var files = await GetFilesAsync(client, id, ct);
                var candidates = BuildCandidates(id, files);

                if (candidates.Count == 0)
                {
                    Log.Debug("HF", $"{id}: подходящих файлов нет — пропускаем");
                    continue;
                }

                result.Add(new HfRepo
                {
                    RepoId = id,
                    Downloads = downloads,
                    Likes = likes,
                    LastModified = modified,
                    Candidates = candidates
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("HF", $"{id}: не удалось прочитать файлы ({ex.Message})");
            }
        }

        status?.Report($"Готово: {result.Count} подходящих авторов из {repoIds.Count}");
        Log.Info("HF", $"Страница {page + 1}: валидных репозиториев {result.Count} из {repoIds.Count}");

        return result;
    }

    private async Task<List<HfFile>> GetFilesAsync(HttpClient client, string repoId, CancellationToken ct)
    {
        // Список файлов репозитория меняется редко — кэшируем, чтобы повторные
        // просмотры страниц и поиск не делали N+1 запросов заново.
        if (TreeCache.TryGetValue(repoId, out var cached) && DateTime.UtcNow - cached.At < TreeCacheTtl)
            return cached.Files;

        var url = Base + "/api/models/" + repoId + "/tree/main?recursive=true";
        var json = await GetStringAsync(client, url, ct);

        using var doc = JsonDocument.Parse(json);
        var files = new List<HfFile>();

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type != "file") continue;

            var path = item.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.IsNullOrWhiteSpace(path)) continue;

            long size = 0;
            if (item.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv)) size = sv;
            if (item.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object &&
                lfs.TryGetProperty("size", out var lfsSize) && lfsSize.TryGetInt64(out var lsv)) size = lsv;

            files.Add(new HfFile(path!, size));
        }

        TreeCache[repoId] = (DateTime.UtcNow, files);
        return files;
    }

    /// <summary>
    /// Логика валидации:
    /// 1) все zip — сразу кандидаты (приоритет);
    /// 2) pth группируем по папке и сопоставляем с index из той же папки;
    /// 3) если pth уже покрыт zip-ом с тем же именем — не дублируем;
    /// 4) служебные веса (D32k, G48k, hubert, rmvpe, pretrained и т.п.) отбрасываем.
    /// </summary>
    public static List<HfModelCandidate> BuildCandidates(string repoId, List<HfFile> files)
    {
        var candidates = new List<HfModelCandidate>();

        foreach (var zip in files.Where(f => f.IsZip && !IsServiceFile(f)))
        {
            candidates.Add(new HfModelCandidate
            {
                RepoId = repoId,
                Title = CleanTitle(zip.Name),
                Zip = zip
            });
        }

        var zipStems = candidates
            .Select(c => Normalize(System.IO.Path.GetFileNameWithoutExtension(c.Zip!.Name)))
            .ToHashSet();

        var indexFiles = files.Where(f => f.IsIndex && !IsServiceFile(f)).ToList();

        foreach (var pth in files.Where(f => f.IsPth && !IsServiceFile(f)))
        {
            var stem = Normalize(System.IO.Path.GetFileNameWithoutExtension(pth.Name));
            if (zipStems.Contains(stem)) continue; // уже есть zip с тем же именем

            var dir = GetDirectory(pth.Path);
            var index =
                indexFiles.FirstOrDefault(i => GetDirectory(i.Path) == dir && Normalize(System.IO.Path.GetFileNameWithoutExtension(i.Name)).Contains(stem))
                ?? indexFiles.FirstOrDefault(i => GetDirectory(i.Path) == dir)
                ?? (indexFiles.Count == 1 ? indexFiles[0] : null);

            // Требование пользователя: без zip должны быть именно 2 файла — pth и index.
            if (index == null) continue;

            candidates.Add(new HfModelCandidate
            {
                RepoId = repoId,
                Title = CleanTitle(pth.Name),
                Pth = pth,
                Index = index
            });
        }

        return candidates
            .OrderByDescending(c => c.IsZip)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetDirectory(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string CleanTitle(string fileName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return name.Replace('_', ' ').Trim();
    }

    private static readonly string[] BlockedWords =
    {
        "hubert", "rmvpe", "fcpe", "contentvec", "pretrained", "discriminator",
        "optimizer", "crepe", "vocoder", "nsf_hifigan", "embedder"
    };

    private static readonly Regex[] BlockedPatterns = BlockedWords
        .Select(w => new Regex(@"(?<![a-z0-9])" + Regex.Escape(w) + @"(?![a-z0-9])", RegexOptions.Compiled))
        .ToArray();

    private static readonly Regex ServiceWeightPattern =
        new(@"^(f0)?[dg]\d{2}k\.pth$", RegexOptions.Compiled);

    /// <summary>
    /// Отсеиваем претрены и служебные веса. Сравниваем ПО ГРАНИЦАМ СЛОВ,
    /// а не подстрокой по всему пути: иначе «crepe» отсекал реальную модель «Crepes».
    /// </summary>
    private static bool IsServiceFile(HfFile file)
    {
        var name = file.Name.ToLowerInvariant();

        if (BlockedPatterns.Any(rx => rx.IsMatch(name))) return true;

        // Служебные ПАПКИ (pretrained/, embedders/…) тоже блокируют, но только целыми сегментами пути.
        var directories = file.Path.ToLowerInvariant().Split('/')[..^1];
        if (directories.Any(dir => BlockedPatterns.Any(rx => rx.IsMatch(dir)))) return true;

        // Служебные веса вида D40k.pth / G48k.pth / f0D32k.pth
        return ServiceWeightPattern.IsMatch(name);
    }
}
