using System.Windows;

namespace MCL;

internal enum SettingsExitChoice { Cancel, Discard, Save }

public partial class SaveSettingsDialog : Window
{
    internal SettingsExitChoice Choice { get; private set; }

    public SaveSettingsDialog()
    {
        InitializeComponent();
        ContentRendered += (_, _) => CancelButton.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        Choice = SettingsExitChoice.Discard;
        DialogResult = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Choice = SettingsExitChoice.Save;
        DialogResult = true;
    }
}
