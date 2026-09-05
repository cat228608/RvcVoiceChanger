using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Runtime;

namespace RvcVoiceChanger.Views;

/// <summary>
/// Позволяет забрать уже скачанные пакеты (в первую очередь torch с CUDA) из чужого
/// окружения Python вместо скачивания двух гигабайт из сети.
/// </summary>
public partial class LocalEnvWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<LocalEnv> _found = new();
    private bool _busy;

    public bool Imported { get; private set; }

    public LocalEnvWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        Append("Нужна версия Python " + RuntimeInstaller.RequiredPythonVersion + " (метка " + LocalEnvLocator.RequiredAbiTag + ")");
        Append("Куда копируем: " + RuntimeInstaller.SitePackages);
    }

    private void Append(string line)
    {
        ImportLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        ImportLog.ScrollToEnd();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        ScanButton.IsEnabled = false;
        PickButton.IsEnabled = false;
        ImportButton.IsEnabled = false;

        _found.Clear();
        EnvList.ItemsSource = null;
        SetStatus("Ищу окружения...");
        Append("Поиск начат");

        try
        {
            var status = new Progress<string>(text => SetStatus(text));

            var result = await Task.Run(() =>
            {
                var roots = LocalEnvLocator.DefaultRoots();
                return LocalEnvLocator.Scan(roots, text => ((IProgress<string>)status).Report(text), CancellationToken.None);
            });

            _found.AddRange(result);
            EnvList.ItemsSource = _found;

            if (_found.Count == 0)
            {
                SetStatus("Ничего не найдено");
                Append("Окружений с torch не нашлось. Укажите папку site-packages вручную.");
            }
            else
            {
                SetStatus("Найдено окружений: " + _found.Count);
                foreach (var env in _found)
                    Append(env.Summary + " — " + env.SitePackages);
            }
        }
        catch (Exception ex)
        {
            Append("Ошибка поиска: " + ex.Message);
            Log.Error("LocalEnv", "Поиск окружений не удался", ex);
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
            PickButton.IsEnabled = true;
        }
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку site-packages или папку с python.exe"
        };

        if (dialog.ShowDialog(this) != true) return;

        var picked = dialog.FolderName;
        var candidates = new List<string> { picked };

        // Если указали корень окружения, site-packages лежит внутри.
        candidates.Add(Path.Combine(picked, "Lib", "site-packages"));
        candidates.Add(Path.Combine(picked, "lib", "site-packages"));

        foreach (var candidate in candidates)
        {
            var env = LocalEnvLocator.Inspect(candidate);
            if (env == null) continue;

            if (!_found.Any(existing => string.Equals(existing.SitePackages, env.SitePackages, StringComparison.OrdinalIgnoreCase)))
                _found.Insert(0, env);

            EnvList.ItemsSource = null;
            EnvList.ItemsSource = _found;
            EnvList.SelectedIndex = 0;

            Append("Добавлено вручную: " + env.Summary);
            SetStatus("Окружение добавлено");
            return;
        }

        Append("В этой папке torch не найден: " + picked);
        SetStatus("torch здесь не найден");
    }

    private void Env_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ImportButton == null) return;
        ImportButton.IsEnabled = !_busy && EnvList.SelectedItem is LocalEnv;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || EnvList.SelectedItem is not LocalEnv env) return;

        if (!env.AbiMatches)
        {
            MessageBox.Show(
                "Это окружение собрано под другую версию Python (" + (env.AbiTag.Length > 0 ? env.AbiTag : "не определена") +
                ").\nНужна метка " + LocalEnvLocator.RequiredAbiTag + " (Python " + RuntimeInstaller.RequiredPythonVersion +
                ").\nСкопированные файлы не загрузятся.",
                "Несовместимое окружение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!env.HasCuda)
        {
            var answer = MessageBox.Show(
                "В этом окружении torch без CUDA. Переносить всё равно?",
                "Сборка без CUDA", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;
        }

        _busy = true;
        ImportButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        PickButton.IsEnabled = false;

        try
        {
            Append("Готовлю рантайм (Python " + RuntimeInstaller.RequiredPythonVersion + " и pip)...");
            SetStatus("Готовлю рантайм...");

            var installer = new RuntimeInstaller(_settings);
            var progress = new Progress<InstallProgress>(p =>
            {
                SetStatus(string.IsNullOrWhiteSpace(p.Detail) ? p.Stage : p.Stage + " — " + p.Detail);
            });

            await installer.PrepareForImportAsync(progress, CancellationToken.None);

            Append("Копирую пакеты из " + env.SitePackages);

            var status = new Progress<string>(text =>
            {
                SetStatus(text);
                Append(text);
            });

            var target = RuntimeInstaller.SitePackages;

            var (copied, bytes) = await Task.Run(() => LocalEnvLocator.Import(env, target,
                text => ((IProgress<string>)status).Report(text), CancellationToken.None));

            Append("Готово: пакетов " + copied + ", объём " + LocalEnvLocator.FormatSize(bytes));

            SetStatus("Проверяю torch...");
            var info = await installer.TorchInfoAsync(CancellationToken.None);

            if (info.Length == 0)
            {
                Append("torch после копирования не импортируется. На шаге установки он будет скачан обычным способом.");
                SetStatus("torch не загружается");
            }
            else
            {
                Append("torch работает: " + info);
                SetStatus("Готово");
                Imported = true;
            }
        }
        catch (Exception ex)
        {
            Append("Ошибка переноса: " + ex.Message);
            SetStatus("Ошибка");
            Log.Error("LocalEnv", "Перенос пакетов не удался", ex);
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
            PickButton.IsEnabled = true;
            ImportButton.IsEnabled = EnvList.SelectedItem is LocalEnv;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
