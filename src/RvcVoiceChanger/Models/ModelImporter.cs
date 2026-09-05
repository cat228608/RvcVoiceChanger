using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using RvcVoiceChanger.Core;

namespace RvcVoiceChanger.Models;

public sealed record ImportResult(bool Success, string Message, ModelEntry? Entry, string? FolderToOpen);

/// <summary>
/// Распаковка и проверка скачанных моделей.
/// Успешный сценарий: в архиве ровно 1 .pth и (желательно) 1 .index.
/// При ошибке возвращаем папку, чтобы UI открыл её в проводнике.
/// </summary>
public static class ModelImporter
{
    // Защита от zip-bomb: лимиты на число файлов, суммарный распакованный объём
    // и коэффициент сжатия одного файла.
    private const int MaxEntries = 2000;
    private const long MaxTotalUncompressedBytes = 4L * 1024 * 1024 * 1024; // 4 ГБ
    private const long MaxEntryUncompressedBytes = 3L * 1024 * 1024 * 1024; // 3 ГБ
    private const long MinPthBytes = 1024 * 1024; // .pth меньше 1 МБ — точно не голосовая модель

    /// <summary>Проверяет архив до распаковки. Возвращает текст ошибки или null.</summary>
    private static string? ValidateArchive(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        if (archive.Entries.Count > MaxEntries)
            return $"В архиве слишком много файлов ({archive.Entries.Count}) — похоже на zip-бомбу.";

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > MaxEntryUncompressedBytes)
                return $"Файл «{entry.FullName}» в архиве слишком большой ({entry.Length / 1024 / 1024} МБ).";

            total += entry.Length;
            if (total > MaxTotalUncompressedBytes)
                return "Суммарный размер архива после распаковки превышает 4 ГБ — похоже на zip-бомбу.";

            // Подозрительное сжатие: >200x на файле от 10 МБ.
            if (entry.CompressedLength > 0 && entry.Length > 10 * 1024 * 1024 &&
                entry.Length / entry.CompressedLength > 200)
                return $"Файл «{entry.FullName}» имеет аномальное сжатие — похоже на zip-бомбу.";
        }

        return null;
    }

    public static ImportResult ImportArchive(string archivePath, string modelName, ModelLibrary library, string? source = null)
    {
        var extractDir = Path.Combine(AppPaths.Temp, "extract", Path.GetFileNameWithoutExtension(archivePath) + "-" + Guid.NewGuid().ToString("N")[..6]);

        try
        {
            var ext = Path.GetExtension(archivePath).ToLowerInvariant();
            if (ext != ".zip")
                return new ImportResult(false, $"Формат {ext} не поддерживается. Нужен .zip либо пара .pth + .index.", null, Path.GetDirectoryName(archivePath));

            var problem = ValidateArchive(archivePath);
            if (problem != null)
            {
                Log.Warn("Import", "Архив отклонён: " + problem);
                return new ImportResult(false, problem, null, Path.GetDirectoryName(archivePath));
            }

            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
            Log.Info("Import", $"Архив распакован: {Path.GetFileName(archivePath)}");

            return ImportFolder(extractDir, modelName, library, source, archivePath);
        }
        catch (InvalidDataException ex)
        {
            Log.Error("Import", "Архив битый или не zip: " + ex.Message);
            return new ImportResult(false, "Архив повреждён или это не zip. Открываю папку с файлом.", null, Path.GetDirectoryName(archivePath));
        }
        catch (Exception ex)
        {
            Log.Error("Import", "Ошибка распаковки", ex);
            return new ImportResult(false, "Ошибка распаковки: " + ex.Message, null, Path.GetDirectoryName(archivePath));
        }
        finally
        {
            // Файлы модели уже скопированы в models/<имя>/ внутри library.Add —
            // временная папка распаковки больше не нужна и не должна копиться в Temp.
            try
            {
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            }
            catch (Exception cleanupEx)
            {
                Log.Debug("Import", "Не удалось очистить временную папку: " + cleanupEx.Message);
            }
        }
    }

    public static ImportResult ImportFolder(string folder, string modelName, ModelLibrary library, string? source, string? archivePath = null)
    {
        var pthFiles = Directory.GetFiles(folder, "*.pth", SearchOption.AllDirectories);
        var indexFiles = Directory.GetFiles(folder, "*.index", SearchOption.AllDirectories);

        if (pthFiles.Length == 0)
            return new ImportResult(false, "В архиве нет ни одного .pth — это не RVC-модель. Открываю папку.", null, folder);

        // Отсеиваем явно не-модели: крохотные .pth (оптимайзеры, обрывки чекпойнтов).
        var plausible = pthFiles.Where(f => new FileInfo(f).Length >= MinPthBytes).ToArray();
        if (plausible.Length == 0)
            return new ImportResult(false, "Все .pth в архиве меньше 1 МБ — это не голосовые модели RVC. Открываю папку.", null, folder);
        pthFiles = plausible;

        if (pthFiles.Length > 1)
        {
            // В архиве несколько моделей — берём самый большой pth и говорим об этом.
            Log.Warn("Import", $"В архиве {pthFiles.Length} файлов .pth — берём самый большой");
        }

        var pth = pthFiles.OrderByDescending(f => new FileInfo(f).Length).First();
        var index = MatchIndex(pth, indexFiles);

        try
        {
            var entry = library.Add(modelName, pth, index, source);

            var msg = index != null
                ? $"Модель \"{entry.Name}\" добавлена: найдены .pth и .index."
                : $"Модель \"{entry.Name}\" добавлена, но .index не найден — тембр будет чуть хуже.";

            return new ImportResult(true, msg, entry, null);
        }
        catch (Exception ex)
        {
            Log.Error("Import", "Не удалось добавить модель", ex);
            return new ImportResult(false, "Не удалось добавить модель: " + ex.Message, null, folder);
        }
    }

    /// <summary>Ищем index, максимально похожий на имя pth (в архивах имена часто расходятся).</summary>
    private static string? MatchIndex(string pth, string[] indexFiles)
    {
        if (indexFiles.Length == 0) return null;
        if (indexFiles.Length == 1) return indexFiles[0];

        var stem = Path.GetFileNameWithoutExtension(pth);
        var best = indexFiles
            .OrderByDescending(f => CommonPrefixLength(Path.GetFileNameWithoutExtension(f), stem))
            .ThenByDescending(f => new FileInfo(f).Length)
            .First();

        return best;
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
        return i;
    }
}
