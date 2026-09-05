using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RvcVoiceChanger.Core;
using RvcVoiceChanger.Net;

namespace RvcVoiceChanger.Audio;

/// <summary>
/// Виртуальный микрофон реализован через VB-CABLE (бесплатный аудиодрайвер VB-Audio).
/// Логика: программа пишет обработанный голос в "CABLE Input", а Discord / OBS / игра
/// выбирают "CABLE Output" как микрофон.
///
/// Почему не свой драйвер: собственный WDM/APO-драйвер требует EV-подписи и
/// аттестации Microsoft, иначе Windows его не загрузит.
/// </summary>
public static class VbCable
{
    /// <summary>URL по умолчанию; переопределяется настройкой VbCableUrl в settings.json.</summary>
    private const string DefaultDownloadUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip";

    private static string DownloadUrlFor(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.VbCableUrl) ? DefaultDownloadUrl : settings.VbCableUrl.Trim();

    public static bool IsInstalled()
    {
        var outputs = AudioDevices.Outputs();
        var inputs = AudioDevices.Inputs();

        var hasInput = outputs.Any(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        var hasOutput = inputs.Any(d => d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));

        return hasInput && hasOutput;
    }

    public static AudioDeviceInfo? FindCableInput() =>
        AudioDevices.Outputs().FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
        ?? AudioDevices.Outputs().FirstOrDefault(d => d.LooksLikeVirtualCable);

    public static AudioDeviceInfo? FindCableOutput() =>
        AudioDevices.Inputs().FirstOrDefault(d => d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase))
        ?? AudioDevices.Inputs().FirstOrDefault(d => d.LooksLikeVirtualCable);

    /// <summary>
    /// Скачивает и запускает установщик драйвера (требует подтверждения UAC).
    /// После установки Windows может попросить перезагрузку.
    /// </summary>
    public static async Task<bool> InstallAsync(AppSettings settings, IProgress<string>? status = null, CancellationToken ct = default)
    {
        try
        {
            status?.Report("Скачивание VB-CABLE...");

            var zip = Path.Combine(AppPaths.Downloads, "VBCABLE_Driver_Pack.zip");
            var downloader = new FileDownloader(settings);
            await downloader.DownloadAsync(DownloadUrlFor(settings), zip,
                new Progress<DownloadProgress>(p => status?.Report($"Скачивание VB-CABLE: {p.Text}")), ct);

            // У VB-Audio нет опубликованных контрольных сумм — фиксируем SHA-256 скачанного
            // файла в логе, чтобы его можно было сверить вручную. Сам установщик подписан
            // (Authenticode), подпись проверит Windows при UAC-запуске.
            var sha = await FileDownloader.ComputeSha256Async(zip, ct);
            Log.Info("VbCable", "SHA-256 скачанного архива: " + sha);

            var dir = Path.Combine(AppPaths.Temp, "vbcable");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            ZipFile.ExtractToDirectory(zip, dir);

            var setup = Directory.GetFiles(dir, "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories).FirstOrDefault()
                        ?? Directory.GetFiles(dir, "VBCABLE_Setup*.exe", SearchOption.AllDirectories).FirstOrDefault();

            if (setup == null)
            {
                Log.Error("VbCable", "В архиве не нашли VBCABLE_Setup_x64.exe");
                OpenFolder(dir);
                return false;
            }

            status?.Report("Запуск установщика драйвера (подтвердите UAC)...");
            Log.Info("VbCable", "Запускаем установщик VB-CABLE с правами администратора");

            var psi = new ProcessStartInfo(setup)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(setup)!,
                Arguments = "-i -h"
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync(ct);

            // Драйверу нужно время, чтобы устройства появились в системе (до 30 секунд).
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(1000, ct);
                if (IsInstalled())
                {
                    Log.Info("VbCable", "VB-CABLE установлен и виден в системе");
                    status?.Report("VB-CABLE установлен");
                    return true;
                }
            }

            status?.Report("Установщик завершён, но устройства пока не видны — возможно, нужна перезагрузка");
            Log.Warn("VbCable", "Устройства VB-CABLE не появились — требуется перезагрузка Windows");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("VbCable", "Ошибка установки VB-CABLE", ex);
            status?.Report("Ошибка установки: " + ex.Message);
            return false;
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("VbCable", "Не удалось открыть папку: " + ex.Message);
        }
    }
}
