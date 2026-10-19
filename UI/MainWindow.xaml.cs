using System.Windows;
using HddDefrag.Core;

namespace HddDefrag.UI;

// PLATZHALTER – wird durch das echte Hauptfenster ersetzt.
public partial class MainWindow : Window
{
    public MainWindow(IDriveEnumerator enumerator, Func<DriveDescriptor, IDefragEngine> engineFactory)
    {
        InitializeComponent();
    }
}
