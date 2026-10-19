using System.Security.Principal;
using System.Windows;
using HddDefrag.Core;
using HddDefrag.Engine;
using HddDefrag.Native;
using HddDefrag.UI;
using HddDefrag.UI.Demo;

namespace HddDefrag;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --demo: simulierte Laufwerke ohne Adminrechte, zum Ausprobieren der Oberfläche.
        bool demo = e.Args.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));

        if (!demo && !IsAdministrator())
        {
            // Selbst mit Adminrechten neu starten (UAC-Abfrage). Das Manifest steht auf asInvoker,
            // damit der Demo-Modus ohne Rechteanhebung läuft.
            if (TryRelaunchElevated(e.Args))
            {
                Shutdown(0);
                return;
            }

            MessageBox.Show(
                "HddDefrag benötigt Administratorrechte, um die Belegungs-Bitmap zu lesen und Dateien zu verschieben.\n\n" +
                "Die Rechteanhebung wurde abgelehnt. Bitte das Programm per Rechtsklick → „Als Administrator ausführen“ starten.",
                "Administratorrechte erforderlich", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        IDriveEnumerator enumerator = demo ? new DemoDriveEnumerator() : new DriveEnumerator();
        Func<DriveDescriptor, IDefragEngine> engineFactory = demo
            ? drive => new DemoEngine(drive)
            : drive => new DefragEngine(drive, VolumeAccess.Open(drive.Letter));

        var window = new MainWindow(enumerator, engineFactory);
        MainWindow = window;
        window.Show();
    }

    /// <summary>Startet dieselbe EXE mit denselben Argumenten über den Verb „runas“ (UAC). false, wenn der Nutzer ablehnt.</summary>
    private static bool TryRelaunchElevated(string[] args)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        try
        {
            System.Diagnostics.Process.Start(startInfo);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // ERROR_CANCELLED: Nutzer hat die UAC-Abfrage abgelehnt.
            return false;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
