using System;
using System.Windows;

namespace RvcVoiceChanger.Core;

/// <summary>
/// Подбор размера окна под реальный экран.
/// Фиксированные Width/Height в XAML на ноутбуках и при масштабе 125–150 %
/// вылезают за границы рабочего стола, и нижняя полоса с кнопками оказывается под панелью задач.
/// </summary>
public static class WindowSizing
{
    private const double Gap = 24;

    /// <summary>
    /// Ставит окно в желаемый размер, но не больше рабочей области экрана,
    /// и центрирует его. Если экран меньше нужного — окно раскрывается на весь экран.
    /// </summary>
    public static void FitToWorkArea(Window window, double desiredWidth, double desiredHeight,
        double minWidth = 0, double minHeight = 0)
    {
        var area = SystemParameters.WorkArea;
        if (area.Width <= 0 || area.Height <= 0) return;

        var availableWidth = Math.Max(320, area.Width - Gap);
        var availableHeight = Math.Max(240, area.Height - Gap);

        var width = Math.Min(desiredWidth, availableWidth);
        var height = Math.Min(desiredHeight, availableHeight);

        // Минимальные размеры не должны превышать экран, иначе окно не вместиться вообще.
        window.MinWidth = Math.Min(minWidth > 0 ? minWidth : width, availableWidth);
        window.MinHeight = Math.Min(minHeight > 0 ? minHeight : height, availableHeight);

        var fillsScreen = desiredWidth > availableWidth || desiredHeight > availableHeight;
        if (fillsScreen)
        {
            // На маленьких экранах проще сразу развернуть окно.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Width = width;
            window.Height = height;
            window.WindowState = WindowState.Maximized;
            return;
        }

        window.MaxWidth = area.Width;
        window.MaxHeight = area.Height;
        window.Width = width;
        window.Height = height;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = area.Left + (area.Width - width) / 2;
        window.Top = area.Top + (area.Height - height) / 2;
    }
}
