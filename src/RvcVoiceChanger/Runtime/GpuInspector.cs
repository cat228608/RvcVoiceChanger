using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using RvcVoiceChanger.Core;

namespace RvcVoiceChanger.Runtime;

/// <summary>Производитель видеоадаптера — от него зависит, какой бэкенд вычислений предлагать.</summary>
public enum GpuVendor { Unknown, Nvidia, Amd, IntelArc, IntelIntegrated }

/// <summary>Один видеоадаптер, как его видит Windows.</summary>
public sealed record GpuAdapter(string Name, GpuVendor Vendor, string DriverVersion)
{
    public string Describe() =>
        string.IsNullOrWhiteSpace(DriverVersion) ? Name : Name + " (драйвер " + DriverVersion + ")";
}

/// <summary>Итог осмотра железа: что предложить пользователю и нужно ли спрашивать.</summary>
public sealed record GpuRecommendation(
    ComputeDevice Device,
    GpuAdapter? Adapter,
    string Summary,
    bool AskUser);

/// <summary>
/// Опрос видеоадаптеров через WMI (Win32_VideoController). Отдельный класс, потому что
/// NVIDIA определяется своим путём (nvidia-smi в RuntimeInstaller), а здесь нужен полный
/// список адаптеров: DirectML работает на AMD и Intel Arc, где nvidia-smi бесполезен.
///
/// Специально без пакета System.Management: он тянет лишнюю зависимость, а нам достаточно
/// одного запроса через PowerShell с запасным вариантом на wmic.
/// </summary>
public static class GpuInspector
{
    private static List<GpuAdapter>? _cache;

    /// <summary>Список адаптеров. Результат кэшируется: опрос WMI занимает до секунды.</summary>
    public static IReadOnlyList<GpuAdapter> Adapters() => _cache ??= Enumerate();

    /// <summary>Сбросить кэш (например, после установки драйвера).</summary>
    public static void Rescan() => _cache = null;

    /// <summary>
    /// Виртуальные и служебные «видеокарты», которые Windows показывает наравне с настоящими.
    /// Их нельзя предлагать как ускоритель.
    /// </summary>
    private static readonly string[] VirtualAdapterMarkers =
    {
        "basic display", "basic render", "remote display", "rdp",
        "virtual", "parsec", "citrix", "vmware", "virtualbox", "hyper-v",
        "idd", "meta", "oculus", "mirror"
    };

    private static bool IsVirtual(string name)
    {
        var text = name.ToLowerInvariant();
        return VirtualAdapterMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>Определяет производителя по названию адаптера.</summary>
    public static GpuVendor Classify(string name)
    {
        var text = (name ?? "").ToLowerInvariant();

        if (text.Contains("nvidia") || text.Contains("geforce") || text.Contains("quadro")
            || text.Contains("tesla") || text.Contains("titan"))
            return GpuVendor.Nvidia;

        if (text.Contains("radeon") || text.Contains("firepro") || text.Contains("vega")
            || text.Contains("amd") || text.Contains("ati "))
            return GpuVendor.Amd;

        if (text.Contains("intel"))
        {
            // Arc — дискретные карты Intel, на них DirectML имеет смысл.
            // Встроенная графика (UHD/Iris) обычно проигрывает современному процессору.
            var arc = text.Contains("arc") || text.Contains("battlemage") || text.Contains("alchemist");
            return arc ? GpuVendor.IntelArc : GpuVendor.IntelIntegrated;
        }

        return GpuVendor.Unknown;
    }

    private static List<GpuAdapter> Enumerate()
    {
        var rows = QueryPowerShell();
        if (rows.Count == 0) rows = QueryWmic();

        var result = new List<GpuAdapter>();

        foreach (var (name, driver) in rows)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            var clean = name.Trim();
            if (IsVirtual(clean)) continue;

            result.Add(new GpuAdapter(clean, Classify(clean), (driver ?? "").Trim()));
        }

        if (result.Count == 0) Log.Warn("GPU", "Не удалось получить список видеоадаптеров через WMI");
        else Log.Info("GPU", "Найдены адаптеры: " + string.Join(", ", result.Select(a => a.Name + " [" + a.Vendor + "]")));

        return result;
    }

    /// <summary>Основной способ: CIM-запрос через PowerShell.</summary>
    private static List<(string Name, string Driver)> QueryPowerShell()
    {
        const string script =
            "Get-CimInstance -ClassName Win32_VideoController -ErrorAction SilentlyContinue | " +
            "ForEach-Object { $_.Name + '|' + $_.DriverVersion }";

        var output = RunTool("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"", 12000);

        return ParsePipeRows(output);
    }

    /// <summary>Запасной способ для систем, где PowerShell урезан или заблокирован политикой.</summary>
    private static List<(string Name, string Driver)> QueryWmic()
    {
        var output = RunTool("wmic", "path win32_VideoController get Name,DriverVersion /format:csv", 12000);
        var result = new List<(string, string)>();

        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0) continue;

            // Формат csv: Node,DriverVersion,Name
            var parts = text.Split(',');
            if (parts.Length < 3) continue;
            if (parts[2].Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;

            result.Add((parts[2], parts[1]));
        }

        return result;
    }

    private static List<(string Name, string Driver)> ParsePipeRows(string output)
    {
        var result = new List<(string, string)>();

        foreach (var line in output.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0) continue;

            var bar = text.IndexOf('|');
            if (bar < 0) result.Add((text, ""));
            else result.Add((text[..bar], text[(bar + 1)..]));
        }

        return result;
    }

    private static string RunTool(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process == null) return "";

            // Читаем до ожидания выхода: иначе процесс может заснуть на полном буфере вывода.
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Log.Warn("GPU", exe + " не ответил за " + timeoutMs + " мс");
                return "";
            }

            return process.ExitCode == 0 ? stdout : "";
        }
        catch (Exception ex)
        {
            Log.Debug("GPU", "Опрос через " + exe + " не удался: " + ex.Message);
            return "";
        }
    }

    /// <summary>Есть ли адаптер, на котором имеет смысл предлагать DirectML.</summary>
    public static GpuAdapter? DirectMlCandidate()
    {
        var adapters = Adapters();

        // Arc важнее: если в системе и Arc, и встроенная графика — считаем по Arc.
        return adapters.FirstOrDefault(a => a.Vendor == GpuVendor.IntelArc)
               ?? adapters.FirstOrDefault(a => a.Vendor == GpuVendor.Amd);
    }

    /// <summary>
    /// Что предложить пользователю. NVIDIA всегда остаётся приоритетным путём: DirectML
    /// предлагается только тогда, когда пригодной карты NVIDIA нет.
    /// </summary>
    /// <param name="nvidiaUsable">Найдена ли NVIDIA с достаточно свежим драйвером.</param>
    public static GpuRecommendation Recommend(bool nvidiaUsable)
    {
        var adapters = Adapters();

        if (nvidiaUsable)
        {
            var nvidia = adapters.FirstOrDefault(a => a.Vendor == GpuVendor.Nvidia);
            return new GpuRecommendation(
                ComputeDevice.Cuda, nvidia,
                "Найдена NVIDIA " + (nvidia?.Name ?? "") + " — работаем через CUDA, это самый быстрый путь.",
                AskUser: false);
        }

        var candidate = DirectMlCandidate();
        if (candidate != null)
        {
            var vendor = candidate.Vendor == GpuVendor.IntelArc ? "Intel Arc" : "AMD";
            return new GpuRecommendation(
                ComputeDevice.DirectMl, candidate,
                "Найдена видеокарта " + vendor + ": " + candidate.Name + ". " +
                "CUDA на ней недоступна, но есть DirectML — расчёт пойдёт на видеокарте, а не на процессоре.",
                AskUser: true);
        }

        var integrated = adapters.FirstOrDefault(a => a.Vendor == GpuVendor.IntelIntegrated);
        if (integrated != null)
        {
            return new GpuRecommendation(
                ComputeDevice.Cpu, integrated,
                "Найдена только встроенная графика Intel (" + integrated.Name + "). " +
                "На ней DirectML обычно медленнее процессора, поэтому по умолчанию считаем на процессоре. " +
                "Включить DirectML вручную можно в настройках.",
                AskUser: false);
        }

        return new GpuRecommendation(
            ComputeDevice.Cpu, null,
            "Подходящая видеокарта не найдена — расчёт пойдёт на процессоре.",
            AskUser: false);
    }
}
