using System.Windows;
using HddDefrag.Core;

namespace HddDefrag.UI;

/// <summary>Ergebnisdialog im Stil des alten Windows-Defrag. DialogResult = true bedeutet „Defragmentieren“ gewählt.</summary>
public partial class AnalysisDoneDialog : Window
{
    public const double FragmentationThresholdPercent = 5.0;

    public AnalysisDoneDialog(DriveDescriptor drive, AnalysisResult result)
    {
        InitializeComponent();
        double percent = result.FragmentationPercent;
        DriveText.Text = $"Laufwerk {drive.DisplayName}";
        SummaryText.Text =
            $"Das Laufwerk ist zu {UiFormat.Percent(percent)} fragmentiert. " +
            $"{UiFormat.Number(result.FilesFragmented)} von {UiFormat.Number(result.FilesScanned)} Dateien sind in mehrere Teile zerlegt.";
        AdviceText.Text = percent < FragmentationThresholdPercent
            ? "Sie müssen dieses Laufwerk jetzt nicht defragmentieren."
            : "Sie sollten dieses Laufwerk jetzt defragmentieren.";
    }

    private void DefragButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
