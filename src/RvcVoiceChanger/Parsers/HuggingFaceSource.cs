using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.HuggingFace;

namespace RvcVoiceChanger.Parsers;

/// <summary>
/// Источник «HuggingFace» — тонкая обёртка над старым HfClient,
/// чтобы UI работал с одними и теми же типами для всех сервисов.
/// </summary>
public sealed class HuggingFaceSource : IModelSource
{
    private readonly HfClient _client;

    public HuggingFaceSource(AppSettings settings) => _client = new HfClient(settings);

    public string Id => "huggingface";
    public string DisplayName => "HuggingFace";
    public string GroupsHeader => $"Авторы (по {_client.PageSize} на страницу)";
    public string SearchHint => "Поиск по названию репозитория (необязательно)";
    public string OpenButtonText => "Открыть репозиторий";

    public async Task<List<ModelGroup>> ParsePageAsync(
        int page,
        string? search,
        IProgress<string>? status,
        CancellationToken ct)
    {
        var repos = await _client.ParsePageAsync(page, search, status, ct);
        return repos.Select(ToGroup).ToList();
    }

    private static ModelGroup ToGroup(HfRepo repo) => new()
    {
        Title = repo.RepoId,
        Summary = repo.Summary,
        Url = repo.Url,
        Candidates = repo.Candidates.Select(c => ToCandidate(repo, c)).ToList()
    };

    private static ModelCandidate ToCandidate(HfRepo repo, HfModelCandidate candidate) => new()
    {
        Title = candidate.Title,
        KindText = candidate.KindText,
        SizeText = candidate.SizeText,
        Source = candidate.RepoId,
        PageUrl = repo.Url,
        FolderName = candidate.RepoId.Replace('/', '_'),
        Downloads = candidate.DownloadUrls
            .Select(url => new ModelDownload(url, DownloadFactory.FileNameFromUrl(url) ?? DownloadFactory.SafeFileName(candidate.Title) + ".bin"))
            .ToList()
    };
}
