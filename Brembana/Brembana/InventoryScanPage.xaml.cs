using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Brembana;

public sealed partial class InventoryScanPage : Page
{
    private MainWindow MainWin => (MainWindow)App.MainWindow;
    private InventoryActionMode _mode = InventoryActionMode.Create;
    private bool _scanStarted;

    public InventoryScanPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _mode = e.Parameter is InventoryActionMode mode ? mode : InventoryActionMode.Create;
        if (_scanStarted)
            return;

        _scanStarted = true;
        await RunScanFlowAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _scanStarted = false;
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
            MainWin.NavigateToHome();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        MainWin.CancelCameraSession();
        MainWin.NavigateToHome();
    }
}
