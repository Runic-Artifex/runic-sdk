using System.ComponentModel;
using System.Windows;
using Runic.Navigation;

namespace HybridNotes.Wpf;

public partial class MainWindow : Window
{
    private readonly AppRegions _regions;
    private bool _mayClose;
    private bool _checkingClose;

    public MainWindow(AppRegions regions)
    {
        _regions = regions;
        InitializeComponent();
        Closing += GuardClose;
    }

    private async void GuardClose(object? sender, CancelEventArgs e)
    {
        if (_mayClose || _regions.Main.Current is not EditorViewModel) return;
        e.Cancel = true;
        if (_checkingClose) return;
        _checkingClose = true;
        try
        {
            // Closing uses the same departure guard and native confirmation as Back.
            if (await _regions.Main.BackAsync() is NavigationResult<object>.Committed)
            {
                _mayClose = true;
                Close();
            }
        }
        finally { _checkingClose = false; }
    }
}
