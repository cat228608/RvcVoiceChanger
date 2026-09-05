using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using RvcVoiceChanger.Core;

namespace RvcVoiceChanger.Models;

public sealed class ModelEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string PthPath { get; set; } = "";
    public string? IndexPath { get; set; }
    public string? Source { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;

    // Свойства читаются биндингами WPF по многу раз — кэшируем обращения к диску
    // на несколько секунд, чтобы каждая отрисовка списка не дёргала файловую систему.
    private static readonly TimeSpan FsCacheTtl = TimeSpan.FromSeconds(3);
    private DateTime _fsCheckedAt = DateTime.MinValue;
    private bool _cachedExists;
    private bool _cachedHasIndex;
    private long _cachedSize;

    private void RefreshFsCache()
    {
        if (DateTime.UtcNow - _fsCheckedAt < FsCacheTtl) return;

        try
        {
            _cachedExists = File.Exists(PthPath);
            _cachedHasIndex = !string.IsNullOrWhiteSpace(IndexPath) && File.Exists(IndexPath);
            _cachedSize = _cachedExists ? new FileInfo(PthPath).Length : 0;
        }
        catch
        {
            _cachedExists = false;
            _cachedHasIndex = false;
            _cachedSize = 0;
        }

        _fsCheckedAt = DateTime.UtcNow;
    }

    public bool HasIndex
    {
        get { RefreshFsCache(); return _cachedHasIndex; }
    }

    public bool Exists
    {
        get { RefreshFsCache(); return _cachedExists; }
    }

    public string SizeText
    {
        get
        {
            RefreshFsCache();
            if (!_cachedExists) return "файл не найден";
            return $"{_cachedSize / 1024.0 / 1024.0:F1} МБ";
        }
    }

    public string StatusText => !Exists
        ? "ОШИБКА: pth не найден"
        : HasIndex ? "pth + index" : "только pth (без index)";
}

/// <summary>Список установленных моделей, хранится в models.json.</summary>
public sealed class ModelLibrary
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ObservableCollection<ModelEntry> Items { get; } = new();

    public void Load()
    {
        Items.Clear();

        try
        {
            if (!File.Exists(AppPaths.ModelsFile)) return;

            var list = JsonSerializer.Deserialize<List<ModelEntry>>(File.ReadAllText(AppPaths.ModelsFile), Options)
                       ?? new List<ModelEntry>();

            foreach (var item in list.OrderBy(m => m.Name)) Items.Add(item);
            Log.Info("Models", $"Загружено моделей: {Items.Count}");
        }
        catch (Exception ex)
        {
            Log.Error("Models", "Не удалось загрузить models.json", ex);
        }
    }

    public void Save()
    {
        try
        {
            // Атомарная запись: сначала во временный файл с fsync, потом подмена.
            // Крэш в момент записи больше не оставляет битый models.json.
            var json = JsonSerializer.Serialize(Items.ToList(), Options);
            var tmp = AppPaths.ModelsFile + ".tmp";

            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(AppPaths.ModelsFile))
                File.Replace(tmp, AppPaths.ModelsFile, null);
            else
                File.Move(tmp, AppPaths.ModelsFile);
        }
        catch (Exception ex)
        {
            Log.Error("Models", "Не удалось сохранить список моделей", ex);
        }
    }

    public ModelEntry? Find(string? id) => Items.FirstOrDefault(m => m.Id == id);

    /// <summary>Добавляет модель, копируя файлы в models/&lt;имя&gt;/.</summary>
    public ModelEntry Add(string name, string pthPath, string? indexPath, string? source = null, bool copyFiles = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileNameWithoutExtension(pthPath);

        name = SanitizeName(name);

        // Если модель с таким именем уже есть — не перезаписываем её файлы и не плодим дубли,
        // а подбираем свободное имя вида «Имя (2)».
        if (copyFiles && Items.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            var counter = 2;
            while (Items.Any(m => string.Equals(m.Name, $"{name} ({counter})", StringComparison.OrdinalIgnoreCase))
                   || Directory.Exists(Path.Combine(AppPaths.Models, $"{name} ({counter})")))
                counter++;

            name = $"{name} ({counter})";
            Log.Info("Models", "Имя занято, модель будет добавлена как: " + name);
        }

        var finalPth = pthPath;
        var finalIndex = indexPath;

        if (copyFiles)
        {
            var dir = Path.Combine(AppPaths.Models, name);
            Directory.CreateDirectory(dir);

            finalPth = Path.Combine(dir, Path.GetFileName(pthPath));
            if (!string.Equals(Path.GetFullPath(pthPath), Path.GetFullPath(finalPth), StringComparison.OrdinalIgnoreCase))
                File.Copy(pthPath, finalPth, true);

            if (!string.IsNullOrWhiteSpace(indexPath) && File.Exists(indexPath))
            {
                finalIndex = Path.Combine(dir, Path.GetFileName(indexPath));
                if (!string.Equals(Path.GetFullPath(indexPath), Path.GetFullPath(finalIndex), StringComparison.OrdinalIgnoreCase))
                    File.Copy(indexPath, finalIndex, true);
            }
        }

        var entry = new ModelEntry
        {
            Name = name,
            PthPath = finalPth,
            IndexPath = finalIndex,
            Source = source
        };

        Items.Add(entry);
        Save();

        Log.Info("Models", $"Модель добавлена: {entry.Name} ({entry.StatusText})");
        return entry;
    }

    public void Remove(ModelEntry entry, bool deleteFiles)
    {
        Items.Remove(entry);
        Save();

        if (!deleteFiles) return;

        try
        {
            var dir = Path.GetDirectoryName(entry.PthPath);
            if (dir != null && dir.StartsWith(AppPaths.Models, StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch (Exception ex)
        {
            Log.Warn("Models", "Не удалось удалить файлы модели: " + ex.Message);
        }
    }

    public static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
