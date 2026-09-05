using System;
using System.IO;

namespace RvcVoiceChanger.Core;

/// <summary>
/// Все пути программы. По умолчанию данные живут в %LOCALAPPDATA%\RvcVoiceChanger,
/// чтобы не требовать прав админа. Если рядом с exe лежит portable.txt,
/// всё складывается в подпапку RvcData рядом с exe (портативный режим).
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> _root = new(ResolveRoot);

    public static string Root => _root.Value;

    public static string Runtime => Ensure(Path.Combine(Root, "runtime"));
    public static string Python => Ensure(Path.Combine(Runtime, "python"));
    public static string PythonExe => Path.Combine(Python, "python.exe");
    public static string Backend => Ensure(Path.Combine(Runtime, "backend"));
    public static string Downloads => Ensure(Path.Combine(Root, "downloads"));
    public static string Models => Ensure(Path.Combine(Root, "models"));
    public static string Logs => Ensure(Path.Combine(Root, "logs"));
    public static string Temp => Ensure(Path.Combine(Root, "temp"));

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ModelsFile => Path.Combine(Root, "models.json");
    public static string WorkerScript => Path.Combine(Backend, "vc_worker.py");
    public static string InstallStampFile => Path.Combine(Runtime, "install.json");

    /// <summary>Создаёт все рабочие папки при старте программы.</summary>
    public static void EnsureCreated()
    {
        Ensure(Root);
        Ensure(Runtime);
        Ensure(Python);
        Ensure(Backend);
        Ensure(Downloads);
        Ensure(Models);
        Ensure(Logs);
        Ensure(Temp);
    }

    private static string ResolveRoot()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.txt")))
            return Ensure(Path.Combine(exeDir, "RvcData"));

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Ensure(Path.Combine(local, "RvcVoiceChanger"));
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
