using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.HuggingFace;
using RvcVoiceChanger.Models;
using RvcVoiceChanger.Net;

namespace RvcVoiceChanger.Views;

public partial class ParserView : UserControl
{
    private readonly AppSettings _settings;
    private readonly ModelLibrary _library;
    private readonly HfClient _client;

    private int _page;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public event Action? ModelAdded;

    public ParserView(AppSettings settings, ModelLibrary library)
    {
        InitializeComponent();

        _settings = settings;
        _library = library;
        _client = new HfClient(settings);

        FileList.SelectionChanged += (_, _) =>
            DownloadButton.IsEnabled = FileList.SelectedItem is HfModelCandidate && !_busy;
    }

    private async void Parse_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(_page);

    private async void Next_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(_page + 1);

    private async void Prev_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(Math.Max(0, _page - 1));

    private async Task LoadPageAsync(int page)
    {
        if (_busy) return;

        _busy = true;
        ParseButton.IsEnabled = NextButton.IsEnabled = PrevButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;

        RepoList.ItemsSource = null;
        FileList.ItemsSource = null;
        ParseStatus.Text = "Парсинг моделей...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(s => ParseStatus.Text = s);
            var repos = await _client.ParsePageAsync(page, SearchBox.Text, progress, _cts.Token);

            _page = page;
            PageText.Text = $"Страница {_page + 1}";
            RepoList.ItemsSource = repos;

            if (repos.Count > 0) RepoList.SelectedIndex = 0;
            else ParseStatus.Text = "На этой странице подходящих авторов нет — нажмите Next → >>";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("HF", "Ошибка парсинга", ex);
            ParseStatus.Text = "Ошибка: " + ex.Message;

            MessageBox.Show("Не удалось получить список с HuggingFace:\n\n" + ex.Message +
                            "\n\nЕсли сайт заблокирован — укажите прокси во вкладке «Настройки».",
                "Парсер", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            ParseButton.IsEnabled = NextButton.IsEnabled = true;
            PrevButton.IsEnabled = _page > 0;
        }
    }

    private void Repo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RepoList.SelectedItem is not HfRepo repo)
        {
            FileList.ItemsSource = null;
            return;
        }

        FilesHeader.Text = $"Файлы моделей — {repo.RepoId}";
        FileList.ItemsSource = repo.Candidates;

        if (repo.Candidates.Count > 0) FileList.SelectedIndex = 0;
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        if (RepoList.SelectedItem is not HfRepo repo) return;

        try
        {
            Process.Start(new ProcessStartInfo(repo.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("UI", "Не удалось открыть ссылку: " + ex.Message);
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not HfModelCandidate candidate || _busy) return;

        _busy = true;
        DownloadButton.IsEnabled = false;
        DownloadProgress.Value = 0;

        var folder = Path.Combine(AppPaths.Downloads, ModelLibrary.SanitizeName(candidate.RepoId.Replace('/', '_')));
        Directory.CreateDirectory(folder);

        try
        {
            var downloader = new FileDownloader(_settings);
            var saved = new List<string>();
            var urls = candidate.DownloadUrls.ToList();

            for (var i = 0; i < urls.Count; i++)
            {
                var url = urls[i];
                var fileName = Uri.UnescapeDataString(url.Split('?')[0].Split('/').Last());
                var dest = Path.Combine(folder, fileName);
                var index = i + 1;

                var progress = new Progress<DownloadProgress>(p =>
                {
                    DownloadStatus.Text = $"Скачивание {index}/{urls.Count}: {fileName} — {p.Text}";
                    DownloadProgress.Value = p.Percent;
                });

                await downloader.DownloadAsync(url, dest, progress, CancellationToken.None);
                saved.Add(dest);
            }

            DownloadStatus.Text = "Проверка файлов...";

            var modelName = ModelLibrary.SanitizeName(candidate.Title);
            var source = candidate.RepoId;

            ImportResult result;

            if (candidate.IsZip)
            {
                result = ModelImporter.ImportArchive(saved[0], modelName, _library, source);
            }
            else
            {
                var pth = saved.FirstOrDefault(f => f.EndsWith(".pth", StringComparison.OrdinalIgnoreCase));
                var idx = saved.FirstOrDefault(f => f.EndsWith(".index", StringComparison.OrdinalIgnoreCase));

                if (pth == null)
                {
                    result = new ImportResult(false, "Файл .pth не скачался. Открываю папку.", null, folder);
                }
                else
                {
                    var entry = _library.Add(modelName, pth, idx, source);
                    result = new ImportResult(true,
                        idx != null
                            ? $"Модель \"{entry.Name}\" добавлена: .pth и .index на месте."
                            : $"Модель \"{entry.Name}\" добавлена без .index.",
                        entry, null);
                }
            }

            DownloadStatus.Text = result.Message;
            DownloadProgress.Value = result.Success ? 100 : 0;

            if (result.Success)
            {
                ModelAdded?.Invoke();
                MessageBox.Show(result.Message + "\n\nМодель уже доступна во вкладке «Изменение голоса».",
                    "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(result.Message, "Ошибка установки модели", MessageBoxButton.OK, MessageBoxImage.Error);
                OpenFolder(result.FolderToOpen ?? folder);
            }
        }
        catch (Exception ex)
        {
            Log.Error("HF", "Ошибка скачивания", ex);
            DownloadStatus.Text = "Ошибка: " + ex.Message;

            MessageBox.Show("Не удалось скачать файл:\n\n" + ex.Message + "\n\nОткрываю папку с загрузками.",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            OpenFolder(folder);
        }
        finally
        {
            _busy = false;
            DownloadButton.IsEnabled = FileList.SelectedItem is HfModelCandidate;
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("UI", "Не удалось открыть папку: " + ex.Message);
        }
    }
}
