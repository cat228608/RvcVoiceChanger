using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using RvcVoiceChanger.Core;

namespace RvcVoiceChanger.Views;

public sealed class LogRow
{
    public LogEntry Entry { get; init; } = null!;
    public string TimeText => Entry.Time.ToString("HH:mm:ss");
    public string LevelText => Entry.Level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "СВЕД",
        LogLevel.Warn => "ПРЕДУП",
        _ => "ОШИБКА"
    };
    public string Source => Entry.Source;
    public string Message => Entry.Message;
}

public partial class LogsView : UserControl
{
    private readonly ObservableCollection<LogRow> _rows = new();

    public LogsView()
    {
        InitializeComponent();
        LogList.ItemsSource = _rows;

        Rebuild();
        Log.Written += OnWritten;
        Unloaded += (_, _) => Log.Written -= OnWritten;
    }

    private LogLevel MinLevel => LevelFilter.SelectedIndex switch
    {
        0 => LogLevel.Trace,
        1 => LogLevel.Debug,
        2 => LogLevel.Info,
        3 => LogLevel.Warn,
        _ => LogLevel.Error
    };

    private void OnWritten(LogEntry entry)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!Matches(entry)) return;

            _rows.Add(new LogRow { Entry = entry });
            while (_rows.Count > 5000) _rows.RemoveAt(0);

            if (AutoScroll.IsChecked == true && _rows.Count > 0)
                LogList.ScrollIntoView(_rows[^1]);
        }));
    }

    private bool Matches(LogEntry entry)
    {
        if (entry.Level < MinLevel) return false;

        var query = SearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return true;

        return entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
               || entry.Source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void Rebuild()
    {
        _rows.Clear();
        foreach (var entry in Log.Entries.Where(Matches))
            _rows.Add(new LogRow { Entry = entry });
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Rebuild();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Logs}\"") { UseShellExecute = true });
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var row in _rows)
            sb.AppendLine($"{row.TimeText} [{row.LevelText}] {row.Source}: {row.Message}");

        try
        {
            Clipboard.SetText(sb.ToString());
            Log.Info("UI", "Логи скопированы в буфер обмена");
        }
        catch (Exception ex)
        {
            Log.Warn("UI", "Не удалось скопировать: " + ex.Message);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _rows.Clear();
}
