using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Brembana;

/// <summary>
/// Riepilogo della sessione: i componenti dell'attività per categoria. In uscita ci si
/// arriva quando la scansione è completa; in rientro subito, con la lista già tutta
/// confermata. Qui si controlla la lista, si corregge a mano una conferma e si registra.
/// L'attività si avvia solo con tutti i pezzi confermati in uscita e si chiude solo con
/// tutti in rientro.
/// </summary>
public sealed partial class AccessoriesPage : Page, IScanFeedback
{
    private MainWindow MainWin => (MainWindow)App.MainWindow;
    private bool _busy;

    public AccessoriesPage()
    {
        InitializeComponent();
        ScanButtonText.Text = UiStrings.ScanButton;
        CodeErrorText.Foreground = ThemeHelper.Brush("BrembanaRedBrush");

        ComponentsPanel.SizeChanged += (_, _) => RefreshComponentList();
        Loaded += AccessoriesPage_Loaded;
        Unloaded += AccessoriesPage_Unloaded;
    }

    private void AccessoriesPage_Loaded(object sender, RoutedEventArgs e)
    {
        MainWin.DatasetRefreshed += OnDatasetRefreshed;
        RefreshPage();
    }

    private void AccessoriesPage_Unloaded(object sender, RoutedEventArgs e)
        => MainWin.DatasetRefreshed -= OnDatasetRefreshed;

    private void OnDatasetRefreshed() => RefreshPage();

    private void RefreshPage()
    {
        if (!MainWin.HasActiveSession)
        {
            MainWin.NavigateToHome();
            return;
        }

        UpdateSessionHeader();
        UpdateProgress();
        RefreshComponentList();
    }

    private void UpdateSessionHeader()
    {
        var attivita = MainWin.CurrentSessionActivity;
        SessionTitle.Text = attivita?.ActivityId ?? MainWin.SessionActivityName ?? "";
        SessionSubtitle.Text = UiStrings.FormatSessionSubtitle(attivita?.Plant, attivita?.Responsible);
        SessionSubtitle.Visibility = string.IsNullOrWhiteSpace(SessionSubtitle.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var isOutgoing = MainWin.IsOutgoing;
        SessionModeText.Text = isOutgoing ? UiStrings.SessionBadgeOutgoing : UiStrings.SessionBadgeIncoming;
        var brush = isOutgoing ? "BrembanaAmberBrush" : "BrembanaGreenBrush";
        SessionModeBadge.Background = ThemeHelper.WithAlpha(brush, 40);
        SessionModeText.Foreground = ThemeHelper.Brush(brush);
    }

    /// <summary>Contatore, scorciatoia e tasto finale seguono la checklist.</summary>
    private void UpdateProgress()
    {
        var isOutgoing = MainWin.IsOutgoing;
        var progress = MainWin.Progress;

        ProgressText.Text = UiStrings.FormatChecklistProgress(progress);
        BtnRegisterText.Text = UiStrings.FormatRegisterButton(isOutgoing, progress);
        BtnRegister.IsEnabled = progress.IsComplete;

        var brush = !progress.IsComplete
            ? "BrembanaSurface2Brush"
            : isOutgoing ? "BrembanaAmberBrush" : "BrembanaGreenBrush";
        BtnRegister.Background = ThemeHelper.Brush(brush);
        BtnRegisterText.Foreground = progress.IsComplete
            ? new SolidColorBrush(Microsoft.UI.Colors.White)
            : ThemeHelper.Brush("BrembanaTextMutedBrush");

        // Un solo tasto: conferma tutto finché manca qualcosa, poi azzera.
        ToggleAllButton.Content = progress.IsComplete ? UiStrings.ToggleAllClear : UiStrings.ToggleAllConfirm;
        ToggleAllButton.IsEnabled = progress.Total > 0;
    }

    private void RefreshComponentList() => MainWin.FillComponentsPanel(ComponentsPanel, OnChecklistChanged);

    private void OnChecklistChanged()
    {
        RefreshComponentList();
        UpdateProgress();
    }

    // ── IScanFeedback: i messaggi finiscono sopra la barra azioni, non nel banner fotocamera ──

    public void Problem(string message)
    {
        CodeErrorText.Text = message;
        CodeErrorText.Visibility = Visibility.Visible;
    }

    public void Clear() => CodeErrorText.Visibility = Visibility.Collapsed;

    public void Success(string message) => MainWin.ShowToast(message, InfoBarSeverity.Success);

    public void Warning(string message) => MainWin.ShowToast(message, InfoBarSeverity.Warning);

    public void PrepareForDialog() { }

    // ── Azioni ───────────────────────────────────────────────────────────────

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        Clear();
        await RunBusyAsync(() => MainWin.RunCameraScanSessionAsync(OnChecklistChanged));
    }

    private void ToggleAll_Click(object sender, RoutedEventArgs e)
    {
        if (MainWin.Progress.IsComplete)
            MainWin.ClearAllComponents();
        else
            MainWin.ConfirmAllComponents();

        OnChecklistChanged();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        // Si torna nella commessa da cui si è entrati, come dopo la registrazione.
        var commessa = MainWin.CurrentSessionActivity?.Commessa;
        MainWin.ClearWarehouseSession();
        MainWin.NavigateToActivities(commessa);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!MainWin.Progress.IsComplete)
            return;

        if (!await ConfirmRegisterAsync())
            return;

        await RunBusyAsync(async () =>
        {
            await MainWin.CompleteSessionAsync();
            if (MainWin.HasActiveSession)
                RefreshPage();
        });
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_busy)
            return;

        _busy = true;
        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        try
        {
            await action();
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            BusyRing.Visibility = Visibility.Collapsed;
            if (MainWin.HasActiveSession)
                OnChecklistChanged();
        }
    }

    private async Task<bool> ConfirmRegisterAsync()
    {
        var isOutgoing = MainWin.IsOutgoing;
        var total = MainWin.Progress.Total;

        var dialog = new ContentDialog
        {
            Title = isOutgoing ? UiStrings.ConfirmRegisterTitleOutgoing : UiStrings.ConfirmRegisterTitleIncoming,
            Content = InventoryScanDialogs.CreateRegisterConfirmationContent(
                UiStrings.FormatRegisterConfirmation(isOutgoing, total),
                MainWin.SubstitutionSummary,
                fontSize: 18),
            Style = (Style)Application.Current.Resources["BrembanaContentDialogStyle"],
            PrimaryButtonText = UiStrings.ConfirmRegisterButton,
            CloseButtonText = UiStrings.CancelButton,
            // Con DefaultButton=Primary WinUI colora il tasto con l'accento di sistema di
            // Windows invece che con lo stile dell'app: stesso motivo per cui l'omologo
            // dialog sopra la fotocamera non lo usa.
            DefaultButton = ContentDialogButton.None,
            XamlRoot = XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
