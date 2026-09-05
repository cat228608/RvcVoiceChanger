using System.Windows;

namespace RvcVoiceChanger.Views;

public partial class RenameWindow : Window
{
    public string NewName => Input.Text.Trim();

    public RenameWindow(string current)
    {
        InitializeComponent();
        Input.Text = current;
        Input.SelectAll();
        Input.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NewName)) return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
