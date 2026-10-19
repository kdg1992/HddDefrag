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
            MessageBox.Show(
                "HddDefrag benötigt Administratorrechte, um die Belegungs-Bitmap zu lesen und Dateien zu verschieben.\n\n" +
                "Bitte das Programm per Rechtsklick → „Als Administrator ausführen“ starten.",
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

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
