using System.Globalization;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Brembana;

/// <summary>
/// Hub inventario: elenco dei componenti sincronizzati. Si seleziona una riga per
/// spostarla nel proprio stabilimento o cambiarne la manutenzione; "+ Aggiungi"
/// apre il form di creazione. Nessuna fotocamera: il componente è la riga scelta.
/// </summary>
public sealed partial class InventoryHubPage : Page
{
    private MainWindow MainWin => (MainWindow)App.MainWindow;

    private readonly Dictionary<string, List<InventoryListRow>> _rowCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, (ToggleButton Tab, Border Badge, TextBlock Count)> _tabs =
        new(StringComparer.OrdinalIgnoreCase);

    private string _activeTable = "Generatore";
    private string _query = "";
    private List<InventoryListRow> _visibleRows = new();

    // La selezione vive come chiave: a ogni ricostruzione dell'elenco le righe sono istanze nuove.
    private string? _selectedTable;
    private string? _selectedUid;
    private bool _restoringSelection;
    private bool _actionInFlight;

    public InventoryHubPage()
    {
        InitializeComponent();

        HubTitleText.Text = UiStrings.InventoryHubTitle;
        HubSubtitleText.Text = UiStrings.InventoryHubSubtitle;
        ToolTipService.SetToolTip(BackButton, UiStrings.BackToHome);
        AutomationProperties.SetName(BackButton, UiStrings.BackToHome);
        SearchBox.PlaceholderText = UiStrings.InventorySearchPlaceholder;
        AddButtonText.Text = UiStrings.InventoryAddAction;
        SelectedSuffixRun.Text = UiStrings.InventorySelectedSuffix;
        SelectionCancelButton.Content = UiStrings.CancelButton;
        SelectionMoveButton.Content = UiStrings.InventoryMoveAction;
        BuildTabs();

        Loaded += InventoryHubPage_Loaded;
        Unloaded += InventoryHubPage_Unloaded;
    }

    private InventoryListRow? SelectedRow => InventoryList.SelectedItem as InventoryListRow;

    private void InventoryHubPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded può ripetersi: disiscrizione difensiva prima di iscriversi.
        MainWin.DatasetRefreshed -= OnDatasetRefreshed;
        MainWin.DatasetRefreshed += OnDatasetRefreshed;
        MainWin.LoggedInUserChanged -= OnLoggedInUserChanged;
        MainWin.LoggedInUserChanged += OnLoggedInUserChanged;

        ApplyUserChrome();
        RebuildCache();
        SelectTab(_activeTable);
    }

    private void InventoryHubPage_Unloaded(object sender, RoutedEventArgs e)
    {
        MainWin.DatasetRefreshed -= OnDatasetRefreshed;
        MainWin.LoggedInUserChanged -= OnLoggedInUserChanged;
    }

    private void OnDatasetRefreshed()
    {
        // Oggi ogni chiamante è già sul thread UI; l'accodamento è difensivo.
        DispatcherQueue.TryEnqueue(() =>
        {
            RebuildCache();
            ApplyRows();
            ApplyUserChrome();
        });
    }

    private void OnLoggedInUserChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyUserChrome();
            UpdateSelectionBar();
        });
    }

    /// <summary>I tab nascono da <see cref="InventoryListing.Tabs"/>: ordine ed etichette in un posto solo.</summary>
    private void BuildTabs()
    {
        foreach (var tab in InventoryListing.Tabs)
        {
            // Nessun Foreground esplicito: etichetta e conteggio seguono i colori di stato del tab.
            var count = new TextBlock
            {
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            };
            var badge = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 1, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = count
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(new TextBlock { Text = tab.Label, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(badge);

            var button = new ToggleButton
            {
                Tag = tab.Table,
                Content = content,
                Style = (Style)Resources["InvTypeTabStyle"]
            };
            button.Click += TypeTab_Click;

            TypeTabsHost.Children.Add(button);
            _tabs[tab.Table] = (button, badge, count);
        }
    }

    /// <summary>Avatar e "+ Aggiungi" dipendono dall'utente, che può cambiare a pagina aperta.</summary>
    private void ApplyUserChrome()
    {
        var initial = UiStrings.FormatUserInitial(MainWin.LoggedInUserName);
        AvatarText.Text = initial;
        AvatarBadge.Visibility = initial.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(AvatarBadge, MainWin.LoggedInUserName);

        var hasPlant = !string.IsNullOrWhiteSpace(App.CurrentUserStabilimento);
        AddButton.IsEnabled = hasPlant && !_actionInFlight;
        ToolTipService.SetToolTip(AddButton, hasPlant ? null : UiStrings.NoUserPlant);
    }

    /// <summary>Proietta l'intero inventario una volta per dataset, non per battuta di ricerca.</summary>
    private void RebuildCache()
    {
        // La disponibilità arriva già calcolata dal server, dentro l'inventario.
        var inventario = App.BridgeData.Inventario;

        foreach (var tab in InventoryListing.Tabs)
            _rowCache[tab.Table] = InventoryListing.Project(inventario, tab.Table);

        foreach (var (table, parts) in _tabs)
        {
            var total = _rowCache.TryGetValue(table, out var rows) ? rows.Count : 0;
            parts.Count.Text = total.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void SelectTab(string table)
    {
        // L'altra tabella non contiene la riga selezionata: la barra non deve restare su
        // un pezzo che non si vede più.
        if (!string.Equals(_activeTable, table, StringComparison.OrdinalIgnoreCase))
            ClearSelectionKey();

        _activeTable = table;

        foreach (var (key, parts) in _tabs)
        {
            var active = string.Equals(key, table, StringComparison.OrdinalIgnoreCase);
            parts.Tab.IsChecked = active;
            parts.Badge.Background = active
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))
                : ThemeHelper.Brush("BrembanaSurface2Brush");
        }

        ApplyColumnHeaders(table);
        ApplyRows();
    }

    private void ApplyColumnHeaders(string table)
    {
        var columns = InventoryListing.ColumnsFor(table);
        HdrUid.Text = InventoryListing.HeaderText(columns.Uid);
        HdrMeasure.Text = InventoryListing.HeaderText(columns.Measure);
        HdrPlant.Text = InventoryListing.HeaderText(columns.Plant);
        HdrExtra.Text = InventoryListing.HeaderText(columns.Extra);
        HdrAvailability.Text = InventoryListing.HeaderText(columns.Availability);
        HdrStatus.Text = InventoryListing.HeaderText(columns.Status);
    }

    private void ApplyRows()
    {
        var rows = _rowCache.TryGetValue(_activeTable, out var cached)
            ? cached
            : new List<InventoryListRow>();

        _visibleRows = InventoryListing.Filter(rows, _query);

        // Cambiare ItemsSource azzera la selezione e alza SelectionChanged: si riseleziona
        // per chiave la stessa riga, se è ancora visibile, senza passare dall'handler.
        _restoringSelection = true;
        try
        {
            InventoryList.ItemsSource = _visibleRows;
            var keep = InventoryListing.FindRow(_visibleRows, _selectedTable, _selectedUid);
            InventoryList.SelectedItem = keep;
            if (keep == null)
                ClearSelectionKey();
        }
        finally
        {
            _restoringSelection = false;
        }

        CountText.Text = UiStrings.FormatInventoryVisibleCount(_visibleRows.Count, rows.Count);
        UpdateEmptyState(rows.Count);
        UpdateSelectionBar();
    }

    private void UpdateEmptyState(int rowsBeforeFilter)
    {
        if (_visibleRows.Count > 0)
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            InventoryList.Visibility = Visibility.Visible;
            return;
        }

        // L'elenco resta consultabile anche offline mostrando l'ultimo snapshot;
        // solo se lo snapshot è davvero vuoto vale la pena citare la connessione.
        if (rowsBeforeFilter > 0)
            EmptyStateText.Text = UiStrings.InventoryNoSearchResults;
        else if (!App.BridgeConnected)
            EmptyStateText.Text = UiStrings.InventoryListUnavailable;
        else
            EmptyStateText.Text = UiStrings.FormatInventoryEmpty(InventoryListing.LabelFor(_activeTable));

        EmptyStateText.Visibility = Visibility.Visible;
        InventoryList.Visibility = Visibility.Collapsed;
    }

    private void ClearSelectionKey()
    {
        _selectedTable = null;
        _selectedUid = null;
    }

    private void InventoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection)
            return;

        var row = SelectedRow;
        _selectedTable = row?.Table;
        _selectedUid = row?.Uid;
        UpdateSelectionBar();

        // La barra che compare accorcia l'elenco: la riga toccata in fondo resta in vista.
        if (row != null)
            DispatcherQueue.TryEnqueue(() => InventoryList.ScrollIntoView(row));
    }

    private void UpdateSelectionBar()
    {
        var row = SelectedRow;
        var actions = InventorySelectionActions.For(row, App.CurrentUserStabilimento);
        if (row == null || actions == null)
        {
            SelectionBar.Visibility = Visibility.Collapsed;
            return;
        }

        SelectedUidRun.Text = row.UidText;
        SelectionHintText.Text = actions.MoveHint ?? "";
        SelectionHintText.Visibility = actions.MoveHint == null ? Visibility.Collapsed : Visibility.Visible;

        SelectionCancelButton.IsEnabled = !_actionInFlight;
        SelectionMoveButton.IsEnabled = actions.CanMove && !_actionInFlight;
        SelectionMaintenanceButton.Content = actions.MaintenanceLabel;
        SelectionMaintenanceButton.Visibility = actions.ShowMaintenance ? Visibility.Visible : Visibility.Collapsed;
        SelectionMaintenanceButton.IsEnabled = !_actionInFlight;

        SelectionBar.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Una sola azione alla volta: due ContentDialog aperti insieme fanno eccezione in
    /// WinUI, e sul tablet un doppio tap veloce ci arriva facilmente.
    /// </summary>
    private async Task RunActionAsync(Func<Task> action)
    {
        if (_actionInFlight)
            return;

        _actionInFlight = true;
        UpdateSelectionBar();
        ApplyUserChrome();
        try
        {
            await action();
        }
        finally
        {
            _actionInFlight = false;

            // Il DatasetRefreshed dell'azione è accodato e può arrivare dopo: si riallinea
            // subito, così riga e barra mostrano già lo stato nuovo (es. la manutenzione).
            RebuildCache();
            ApplyRows();
            ApplyUserChrome();
        }
    }

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
            await RunActionAsync(() => MainWin.RunInventoryMoveAsync(row.ToMatch()));
    }

    private async void Maintenance_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
            await RunActionAsync(() => MainWin.RunInventoryMaintenanceAsync(row.ToMatch()));
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        InventoryCreateForm? created = null;
        await RunActionAsync(async () => created = await MainWin.RunInventoryCreateAsync(_activeTable));
        if (created == null)
            return;

        // Il pezzo appena creato si vede subito: la sua tab, niente filtro, selezionato.
        _query = "";
        SearchBox.Text = "";
        SelectTab(created.Table);
        _selectedTable = created.Table;
        _selectedUid = created.Uid;
        ApplyRows();
    }

    private void SelectionCancel_Click(object sender, RoutedEventArgs e)
        => InventoryList.SelectedItem = null;

    private void TypeTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string table })
            SelectTab(table);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;

        _query = sender.Text?.Trim() ?? "";
        ApplyRows();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => MainWin.NavigateToHome();
}
