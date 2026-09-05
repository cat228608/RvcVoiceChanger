using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Models;

namespace RvcVoiceChanger.Views;

public partial class ModelsView : UserControl
{
    private readonly AppSettings _settings;
    private readonly ModelLibrary _library;

    public event Action? ModelsChanged;

    public ModelsView(AppSettings settings, ModelLibrary library)
    {
        InitializeComponent();
        _settings = settings;
        _library = library;
        ModelList.ItemsSource = _library.Items;
    }

    public void Refresh()
    {
        ModelList.Items.Refresh();
    }

    private void BrowsePth_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите файл модели (.pth)",
            Filter = "Модель RVC (*.pth)|*.pth|Все файлы (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        PthBox.Text = dialog.FileName;

        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);

        // Попробуем автоматически найти index рядом.
        if (string.IsNullOrWhiteSpace(IndexBox.Text))
        {
            var dir = Path.GetDirectoryName(dialog.FileName);
            if (dir != null)
            {
                var found = Directory.GetFiles(dir, "*.index");
                if (found.Length == 1) IndexBox.Text = found[0];
            }
        }
    }

    private void BrowseIndex_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите индекс (.index)",
            Filter = "Индекс Faiss (*.index)|*.index|Все файлы (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true) IndexBox.Text = dialog.FileName;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var pth = PthBox.Text.Trim();

        if (!File.Exists(pth))
        {
            MessageBox.Show("Укажите существующий файл .pth", "Не хватает данных",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var index = IndexBox.Text.Trim();
        if (index.Length > 0 && !File.Exists(index)) index = "";

        try
        {
            var entry = _library.Add(NameBox.Text.Trim(), pth, string.IsNullOrEmpty(index) ? null : index, "ручное добавление");

            AddStatus.Text = $"Добавлено: {entry.Name} ({entry.StatusText})";
            NameBox.Text = PthBox.Text = IndexBox.Text = "";

            Refresh();
            ModelsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Models", "Ошибка добавления модели", ex);
            MessageBox.Show("Не удалось добавить модель:\n" + ex.Message, "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите архив с моделью",
            Filter = "Архив (*.zip)|*.zip"
        };

        if (dialog.ShowDialog() != true) return;

        var name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? Path.GetFileNameWithoutExtension(dialog.FileName)
            : NameBox.Text.Trim();

        var result = ModelImporter.ImportArchive(dialog.FileName, name, _library, "локальный архив");
        AddStatus.Text = result.Message;

        if (result.Success)
        {
            NameBox.Text = "";
            Refresh();
            ModelsChanged?.Invoke();
            MessageBox.Show(result.Message, "Модель добавлена", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(result.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            if (result.FolderToOpen != null) OpenFolder(result.FolderToOpen);
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelEntry entry) return;

        var dialog = new RenameWindow(entry.Name) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        entry.Name = ModelLibrary.SanitizeName(dialog.NewName);
        _library.Save();

        Refresh();
        ModelsChanged?.Invoke();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelEntry entry) return;

        _library.Remove(entry, deleteFiles: false);
        ModelsChanged?.Invoke();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelEntry entry) return;

        var answer = MessageBox.Show($"Удалить модель «{entry.Name}» вместе с файлами?", "Подтверждение",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        _library.Remove(entry, deleteFiles: true);
        ModelsChanged?.Invoke();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = ModelList.SelectedItem is ModelEntry entry
            ? Path.GetDirectoryName(entry.PthPath) ?? AppPaths.Models
            : AppPaths.Models;

        OpenFolder(path);
    }

    private void ModelList_DoubleClick(object sender, RoutedEventArgs e) => Rename_Click(sender, e);

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _library.Load();
        Refresh();
        ModelsChanged?.Invoke();
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
