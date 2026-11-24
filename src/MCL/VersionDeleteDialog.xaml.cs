using System.Windows;
using MCL.Models;

namespace MCL;

public partial class VersionDeleteDialog : Window
{
    public VersionDeleteDialog(VersionDeletionInfo info)
    {
        InitializeComponent();
        DataContext = info;
        ContentRendered += (_, _) => CancelDeleteButton.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
