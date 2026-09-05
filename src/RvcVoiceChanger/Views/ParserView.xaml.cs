using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Models;
using RvcVoiceChanger.Net;
using RvcVoiceChanger.Parsers;

namespace RvcVoiceChanger.Views;

public partial class ParserView : UserControl
{
    private readonly AppSettings _settings;
    private readonly ModelLibrary _library;

    /// <summary>Все доступные сервисы-источники моделей.</summary>
    private readonly List<IModelSource> _sources;

    private IModelSource _source;

    private int _page;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _initializing = true;

    public event Action? ModelAdded;

    public ParserView(AppSettings settings, ModelLibrary library)
    {
        InitializeComponent();

        _settings = settings;
        _library = library;

        _sources = new List<IModelSource>
        {
            new HuggingFaceSource(settings),
            new VoiceModelsSource(settings)
        };

        _source = _sources.FirstOrDefault(s =>
                      string.Equals(s.Id, settings.ParserSource, StringComparison.OrdinalIgnoreCase))
                  ?? _sources[0];

        SourceBox.ItemsSource = _sources;
        SourceBox.DisplayMemberPath = nameof(IModelSource.DisplayName);
        SourceBox.SelectedItem = _source;

        ApplySourceLabels();
        _initializing = false;

        FileList.SelectionChanged += (_, _) =>
            DownloadButton.IsEnabled = FileList.SelectedItem is ModelCandidate && !_busy;
    }

    // ---------- Выбор сервиса ----------

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || SourceBox.SelectedItem is not IModelSource selected) return;
        if (ReferenceEquals(selected, _source)) return;

        _source = selected;
        _page = 0;

        _cts?.Cancel();

        RepoList.ItemsSource = null;
        FileList.ItemsSource = null;
        PageText.Text = "Страница 1";
        PrevButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        DownloadStatus.Text = "";
        DownloadProgress.Value = 0;
        ParseStatus.Text = "Сервис: " + _source.DisplayName + " — нажмите «Парсить модели»";

        ApplySourceLabels();

        _settings.ParserSource = _source.Id;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log.Warn("Parser", "Не удалось сохранить выбор сервиса: " + ex.Message); }
    }

    private void ApplySourceLabels()
    {
        GroupsHeader.Text = _source.GroupsHeader;
        SearchBox.ToolTip = _source.SearchHint;
        OpenButton.Content = _source.OpenButtonText;
        FilesHeader.Text = "Файлы моделей";
    }

    // ---------- Парсинг ----------

    private async void Parse_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(0);

    private async void Next_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(_page + 1);

    private async void Prev_Click(object sender, RoutedEventArgs e) => await LoadPageAsync(Math.Max(0, _page - 1));

    private async void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await LoadPageAsync(0);
    }

    private async Task LoadPageAsync(int page)
    {
        if (_busy) return;

        var source = _source;

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
            var groups = await source.ParsePageAsync(page, SearchBox.Text, progress, _cts.Token);

            // Пока шёл запрос, пользователь мог переключить сервис — чужой результат не показываем.
            if (!ReferenceEquals(source, _source)) return;

            _page = page;
            PageText.Text = $"Страница {_page + 1}";
            RepoList.ItemsSource = groups;

            if (groups.Count > 0) RepoList.SelectedIndex = 0;
            else ParseStatus.Text = "На этой странице подходящих моделей нет — нажмите Вперёд → или уточните поиск";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Parser", $"Ошибка парсинга ({source.DisplayName})", ex);
            ParseStatus.Text = "Ошибка: " + ex.Message;

            MessageBox.Show($"Не удалось получить список с {source.DisplayName}:\n\n" + ex.Message +
                            "\n\nЕсли сайт заблокирован — укажите прокси во вкладке «Настройки».",
                "Парсер", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            ParseButton.IsEnabled = NextButton.IsEnabled = true;
            PrevButton.IsEnabled = _page > 0;
            DownloadButton.IsEnabled = FileList.SelectedItem is ModelCandidate;
        }
    }

    private void Repo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RepoList.SelectedItem is not ModelGroup group)
        {
            FileList.ItemsSource = null;
            return;
        }

        FilesHeader.Text = "Файлы моделей — " + group.Title;
        FileList.ItemsSource = group.Candidates;

        if (group.Candidates.Count > 0) FileList.SelectedIndex = 0;
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        var url = (FileList.SelectedItem as ModelCandidate)?.PageUrl
                  ?? (RepoList.SelectedItem as ModelGroup)?.Url;

        if (string.IsNullOrWhiteSpace(url)) return;
        OpenInBrowser(url);
    }

    // ---------- Скачивание и установка ----------

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not ModelCandidate candidate || _busy) return;

        _busy = true;
        DownloadButton.IsEnabled = false;
        DownloadProgress.Value = 0;

        var folder = Path.Combine(AppPaths.Downloads, ModelLibrary.SanitizeName(candidate.FolderName));

        try
        {
            DownloadStatus.Text = "Получаю ссылку на файл...";

            var downloads = (await candidate.GetDownloadsAsync(CancellationToken.None)).ToList();

            if (downloads.Count == 0)
            {
                DownloadStatus.Text = "Ссылка на файл не найдена";
                MessageBox.Show("У этой модели не нашлось ссылки на файл. Откройте страницу в браузере и скачайте вручную.",
                    "Парсер", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Файлообменники типа MEGA без браузера не отдадут файл — честно говорим об этом.
            var browserOnly = downloads.FirstOrDefault(d => DownloadFactory.IsBrowserOnly(d.Url));
            if (browserOnly != null)
            {
                DownloadStatus.Text = "Этот файлообменник требует браузера";

                var open = MessageBox.Show(
                    "Модель лежит на " + DownloadFactory.HostOf(browserOnly.Url) +
                    " — оттуда файл можно скачать только вручную.\n\nОткрыть ссылку в браузере?" +
                    "\n\nПотом добавьте скачанный архив во вкладке «Модели».",
                    "Нужен браузер", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (open == MessageBoxResult.Yes) OpenInBrowser(browserOnly.Url);
                return;
            }

            Directory.CreateDirectory(folder);

            var downloader = new FileDownloader(_settings);
            var saved = new List<string>();

            for (var i = 0; i < downloads.Count; i++)
            {
                var item = downloads[i];
                var fileName = MakeUniqueName(item.FileName, saved, i);
                var dest = Path.Combine(folder, fileName);
                var index = i + 1;

                var progress = new Progress<RvcVoiceChanger.Net.DownloadProgress>(p =>
                {
                    DownloadStatus.Text = $"Скачивание {index}/{downloads.Count}: {fileName} — {p.Text}";
                    DownloadProgress.Value = p.Percent;
                });

                await downloader.DownloadAsync(item.Url, dest, progress, CancellationToken.None);
                saved.Add(dest);
            }

            DownloadStatus.Text = "Проверка файлов...";

            var result = Install(candidate, saved, folder);

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
            Log.Error("Parser", "Ошибка скачивания", ex);
            DownloadStatus.Text = "Ошибка: " + ex.Message;

            MessageBox.Show("Не удалось скачать файл:\n\n" + ex.Message +
                            "\n\nЕсли папка с загрузками создана — открываю её.",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);

            if (Directory.Exists(folder)) OpenFolder(folder);
        }
        finally
        {
            _busy = false;
            DownloadButton.IsEnabled = FileList.SelectedItem is ModelCandidate;
        }
    }

    /// <summary>
    /// Разбирается с тем, что именно скачалось. На сторонних сайтах расширение в ссылке
    /// часто врёт (Google Drive), поэтому тип файла определяется по содержимому.
    /// </summary>
    private ImportResult Install(ModelCandidate candidate, List<string> saved, string folder)
    {
        var modelName = ModelLibrary.SanitizeName(ShortName(candidate.Title));
        var source = candidate.Source;

        var zip = saved.FirstOrDefault(LooksLikeZip);
        if (zip != null)
        {
            // ImportArchive ждёт именно .zip в имени файла.
            if (!zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                zip = RenameWithExtension(zip, ".zip");

            return ModelImporter.ImportArchive(zip, modelName, _library, source);
        }

        var html = saved.FirstOrDefault(LooksLikeHtml);
        if (html != null)
        {
            return new ImportResult(false,
                "Вместо файла сервер отдал HTML-страницу (часто так делает Google Drive на больших файлах). " +
                "Откройте страницу ��одели в браузере и скачайте архив вручную.", null, folder);
        }

        var pth = saved.FirstOrDefault(f => f.EndsWith(".pth", StringComparison.OrdinalIgnoreCase));
        var index = saved.FirstOrDefault(f => f.EndsWith(".index", StringComparison.OrdinalIgnoreCase));

        if (pth == null)
            return new ImportResult(false, "В загрузке нет ни архива, ни файла .pth. Открываю папку.", null, folder);

        try
        {
            var entry = _library.Add(modelName, pth, index, source);
            return new ImportResult(true,
                index != null
                    ? $"Модель \"{entry.Name}\" добавлена: .pth и .index на месте."
                    : $"Модель \"{entry.Name}\" добавлена без .index.",
                entry, null);
        }
        catch (Exception ex)
        {
            Log.Error("Parser", "Не удалось добавить модель", ex);
            return new ImportResult(false, "Не удалось добавить модель: " + ex.Message, null, folder);
        }
    }

    /// <summary>Имена моделей на voice-models.com очень длинные — режем для имени папки модели.</summary>
    private static string ShortName(string title)
    {
        var name = title.Trim();
        return name.Length <= 60 ? name : name[..60].Trim();
    }

    private static string MakeUniqueName(string fileName, List<string> saved, int index)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "file" + (index + 1) : fileName;

        if (!saved.Any(s => string.Equals(Path.GetFileName(s), name, StringComparison.OrdinalIgnoreCase)))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        return stem + "-" + (index + 1) + ext;
    }

    private static string RenameWithExtension(string path, string extension)
    {
        var target = path + extension;

        try
        {
            if (File.Exists(target)) File.Delete(target);
            File.Move(path, target);
            return target;
        }
        catch (Exception ex)
        {
            Log.Warn("Parser", "Не удалось переименовать файл: " + ex.Message);
            return path;
        }
    }

    /// <summary>zip определяем по подписи PK, а не по расширению в ссылке.</summary>
    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[2];
            return stream.Read(head, 0, 2) == 2 && head[0] == (byte)'P' && head[1] == (byte)'K';
        }
        catch (Exception ex)
        {
            Log.Debug("Parser", "Не удалось проверить файл: " + ex.Message);
            return false;
        }
    }

    private static bool LooksLikeHtml(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[256];
            var read = reader.Read(buffer, 0, buffer.Length);
            var head = new string(buffer, 0, Math.Max(0, read)).TrimStart().ToLowerInvariant();
            return head.StartsWith("<!doctype html") || head.StartsWith("<html") || head.StartsWith("<?xml") && head.Contains("html");
        }
        catch
        {
            return false;
        }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("UI", "Не удалось открыть ссылку: " + ex.Message);
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
