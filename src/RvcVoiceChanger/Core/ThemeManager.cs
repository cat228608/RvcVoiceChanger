using System;
using System.Windows;

namespace RvcVoiceChanger.Core;

/// <summary>
/// Переключение тёмной/светлой темы. Словарь темы всегда лежит первым в
/// Application.Resources.MergedDictionaries; все стили ссылаются на кисти через
/// DynamicResource, поэтому подмена словаря перекрашивает интерфейс на лету.
/// </summary>
public static class ThemeManager
{
    public const string Dark = "dark";
    public const string Light = "light";

    public static string Current { get; private set; } = Dark;

    public static void Apply(string? theme)
    {
        var normalized = string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;
        Current = normalized;

        var app = Application.Current;
        if (app == null) return;

        var source = new Uri(normalized == Light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        var dictionary = new ResourceDictionary { Source = source };

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0)
            merged[0] = dictionary;
        else
            merged.Add(dictionary);
    }
}
