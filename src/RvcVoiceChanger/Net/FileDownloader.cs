using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RvcVoiceChanger.Core;

namespace RvcVoiceChanger.Net;

public sealed record DownloadProgress(long Received, long Total, double SpeedBytesPerSecond)
{
    public double Percent => Total > 0 ? Received * 100.0 / Total : 0;

    public string Text
    {
        get
        {
            var mb = Received / 1024.0 / 1024.0;
            var totalMb = Total / 1024.0 / 1024.0;
            var speed = SpeedBytesPerSecond / 1024.0 / 1024.0;
            return Total > 0
                ? $"{mb:F1} / {totalMb:F1} МБ ({Percent:F0}%), {speed:F1} МБ/с"
                : $"{mb:F1} МБ, {speed:F1} МБ/с";
        }
    }
}

/// <summary>
/// Загрузчик файлов с прогрессом, докачкой (Range) и повторами.
/// </summary>
public sealed class FileDownloader
{
    private readonly AppSettings _settings;

    public FileDownloader(AppSettings settings) => _settings = settings;

    /// <summary>Ошибки, которые повторными попытками не лечатся.</summary>
    private static bool IsPermanent(HttpStatusCode? code) =>
        code is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default,
        int maxAttempts = 4,
        string? expectedSha256 = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var partPath = destinationPath + ".part";
        Exception? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (attempt == 1)
                    Log.Info("Download", "Качаю " + url +
                        (HttpFactory.UseProxyForUrl(_settings, url) ? " (через прокси)" : " (напрямую)"));

                await DownloadOnceAsync(url, partPath, progress, ct);

                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    var actual = await ComputeSha256Async(partPath, ct);
                    if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        // Битый/подменённый файл — удаляем и пробуем заново с нуля.
                        File.Delete(partPath);
                        throw new IOException(
                            $"Контрольная сумма не совпала для {Path.GetFileName(destinationPath)}: ожидалась {expectedSha256}, получена {actual}");
                    }
                    Log.Info("Download", $"SHA-256 совпала: {Path.GetFileName(destinationPath)}");
                }

                if (File.Exists(destinationPath)) File.Delete(destinationPath);
                File.Move(partPath, destinationPath);

                Log.Info("Download", $"Готово: {Path.GetFileName(destinationPath)}");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex) when (IsPermanent(ex.StatusCode))
            {
                // 404/403/410 от повторов не пойдут: файла по адресу просто нет.
                Log.Warn("Download", $"Адрес недоступен навсегда ({(int?)ex.StatusCode}): {url}");
                throw new HttpRequestException(
                    $"{(int?)ex.StatusCode} — файла по адресу нет: {url}", ex, ex.StatusCode);
            }
            catch (Exception ex)
            {
                last = ex;
                Log.Warn("Download", $"Попытка {attempt}/{maxAttempts} не удалась: {ex.Message}");
                // Экспоненциальная пауза: 2, 4, 8, 16… секунд (максимум минута).
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt))), ct);
            }
        }

        var reason = last?.Message ?? "причина неизвестна";
        if (last?.InnerException != null) reason += " — " + last.InnerException.Message;

        throw new IOException("Не удалось скачать " + url + "\nПричина: " + reason, last);
    }

    /// <summary>
    /// Пробует несколько адресов по очереди (основной и зеркала).
    /// Нужно там, где основной сайт часто блокируется (например GitHub).
    /// </summary>
    public async Task DownloadWithMirrorsAsync(
        IEnumerable<string> urls,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default,
        int attemptsPerUrl = 2)
    {
        var list = urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (list.Count == 0) throw new ArgumentException("Не задан ни один адрес загрузки", nameof(urls));

        var problems = new List<string>();

        for (var i = 0; i < list.Count; i++)
        {
            var url = list[i];

            try
            {
                if (i > 0) Log.Info("Download", "Пробуем зеркало: " + url);
                await DownloadAsync(url, destinationPath, progress, ct, attemptsPerUrl);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                problems.Add(url + " — " + ex.Message.Replace("\n", " "));
                Log.Warn("Download", "Адрес не сработал: " + url + " (" + ex.Message + ")");

                // Недокачанный кусок от другого зеркала мешает следующей попытке.
                try { if (File.Exists(destinationPath + ".part")) File.Delete(destinationPath + ".part"); }
                catch { /* не критично */ }
            }
        }

        throw new IOException("Ни один источник не ответил:\n" + string.Join("\n", problems));
    }

    private async Task DownloadOnceAsync(
        string url,
        string partPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        var useProxy = HttpFactory.UseProxyForUrl(_settings, url);
        using var client = HttpFactory.Create(_settings, useProxy: useProxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (existing > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 416 при докачке обычно означает, что .part уже скачан целиком —
        // это успех, а не провал. Проверяем полный размер из Content-Range, если он есть.
        if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var fullLength = response.Content.Headers.ContentRange?.Length;
            if (fullLength is null || fullLength.Value == existing)
            {
                Log.Info("Download", "Файл уже был докачан ранее (HTTP 416) — используем его");
                progress?.Report(new DownloadProgress(existing, existing, 0));
                return;
            }

            // Размер не совпал — .part битый, начинаем с нуля.
            existing = 0;
            File.Delete(partPath);
            await DownloadOnceAsync(url, partPath, progress, ct);
            return;
        }

        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            // Сервер не умеет докачку — начинаем с нуля.
            existing = 0;
            File.Delete(partPath);
        }

        response.EnsureSuccessStatusCode();

        var total = (response.Content.Headers.ContentLength ?? 0) + existing;

        await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(
            partPath,
            existing > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1 << 20);

        var buffer = new byte[1 << 20];
        var received = existing;
        var sw = Stopwatch.StartNew();
        long lastReported = existing;
        var lastReportTime = TimeSpan.Zero;

        while (true)
        {
            var read = await httpStream.ReadAsync(buffer, ct);
            if (read == 0) break;

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;

            var elapsed = sw.Elapsed;
            if ((elapsed - lastReportTime).TotalMilliseconds >= 200)
            {
                var speed = (received - lastReported) / Math.Max(0.001, (elapsed - lastReportTime).TotalSeconds);
                progress?.Report(new DownloadProgress(received, total, speed));
                lastReported = received;
                lastReportTime = elapsed;
            }
        }

        progress?.Report(new DownloadProgress(received, total <= 0 ? received : total, 0));
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        var client = HttpFactory.Shared(_settings, HttpFactory.UseProxyForUrl(_settings, url));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        return await client.GetStringAsync(url, cts.Token);
    }
}
