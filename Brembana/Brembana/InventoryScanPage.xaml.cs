using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Brembana;

public sealed partial class InventoryScanPage : Page
{
    private MainWindow MainWin => (MainWindow)App.MainWindow;
    private InventoryActionMode _mode = InventoryActionMode.Create;
    private bool _scanStarted;
    private bool _navigatingAway;

    public InventoryScanPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _mode = e.Parameter is InventoryActionMode mode ? mode : InventoryActionMode.Create;
        SectionTitleText.Text = CameraSectionTitleHelper.ForInventoryMode(_mode);
        if (_scanStarted)
            return;

        _scanStarted = true;
        await RunScanFlowAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _scanStarted = false;
        _navigatingAway = false;
    }

    private async Task RunScanFlowAsync()
    {
        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        StatusText.Text = "Apertura fotocamera...";

        try
        {
            await MainWin.RunInventoryScanSessionAsync(_mode);
        }
        finally
        {
            BusyRing.IsActive = false;
            BusyRing.Visibility = Visibility.Collapsed;
            ReturnToHub();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        // Sblocca la sessione in attesa: il finally di RunScanFlowAsync chiamerà
        // ReturnToHub, che è idempotente se ci siamo già passati qui.
        MainWin.CancelCameraSession();
        ReturnToHub();
    }

    /// <summary>
    /// Torna all'hub inventario una sola volta per visita: Back_Click e il finally
    /// possono arrivare entrambi, e una doppia navigazione ricostruirebbe l'hub due volte.
    /// </summary>
    private void ReturnToHub()
    {
        if (_navigatingAway)
            return;

        _navigatingAway = true;
        MainWin.NavigateToInventory();
    }
}
