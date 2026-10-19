using System.Windows;
using HddDefrag.Core;

namespace HddDefrag.UI;

/// <summary>
/// Sicherheitsabfrage vor jeder Defragmentierung. „Ja, defragmentieren“ wird erst nach Setzen des Häkchens aktiv,
/// „Abbrechen“ ist die Standardschaltfläche.
/// </summary>
public partial class ConfirmDefragDialog : Window
{
    public ConfirmDefragDialog(DriveDescriptor drive)
    {
        InitializeComponent();
        DriveText.Text = drive.DisplayName;
        ModelText.Text = string.IsNullOrWhiteSpace(drive.DiskModel) ? "unbekannt" : drive.DiskModel;
        BusText.Text = string.IsNullOrWhiteSpace(drive.BusType) ? "unbekannt" : drive.BusType;
        MediaText.Text = drive.MediaText;
        SizeText.Text = UiFormat.Bytes(drive.TotalBytes);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (UnderstoodBox.IsChecked == true) DialogResult = true;
    }
}
