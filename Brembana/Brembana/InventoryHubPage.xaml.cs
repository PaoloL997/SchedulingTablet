using Microsoft.UI.Xaml.Controls.Primitives;

namespace Brembana;

/// <summary>
/// Hub inventario: elenco in sola consultazione dei componenti sincronizzati
/// più i tre accessi alle funzioni di sposta / aggiungi / manutenzione.
/// </summary>
public sealed partial class InventoryHubPage : Page
{
    private MainWindow MainWin => (MainWindow)App.MainWindow;

    private readonly Dictionary<string, List<InventoryListRow>> _rowCache =
        new(StringComparer.OrdinalIgnoreCase);

    private string _activeTable = "Generatore";
    private string _query = "";
    private List<InventoryListRow> _visibleRows = new();

    public InventoryHubPage()
    {
        InitializeComponent();

        HubTitleText.Text = UiStrings.InventoryHubTitle;
        BackButtonText.Text = UiStrings.BackToHome;
        SearchBox.PlaceholderText = UiStrings.InventorySearchPlaceholder;
        MoveButtonText.Text = UiStrings.HomeMoveComponent;
        AddButtonText.Text = UiStrings.HomeAddComponent;
        MaintenanceButtonText.Text = UiStrings.HomeMaintenanceComponent;
        ApplyTabLabels();

        Loaded += InventoryHubPage_Loaded;
        Unloaded += InventoryHubPage_Unloaded;
    }

    private void InventoryHubPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded può ripetersi: disiscrizione difensiva prima di iscriversi.
        MainWin.DatasetRefreshed -= OnDatasetRefreshed;
        MainWin.DatasetRefreshed += OnDatasetRefreshed;

        ApplyActionButtonAvailability();
        RebuildCache();
        SelectTab(_activeTable);
    }

    private void InventoryHubPage_Unloaded(object sender, RoutedEventArgs e)
        => MainWin.DatasetRefreshed -= OnDatasetRefreshed;

    private void OnDatasetRefreshed()
    {
        // Oggi ogni chiamante è già sul thread UI; l'accodamento è difensivo.
        DispatcherQueue.TryEnqueue(() =>
        {
            RebuildCache();
            ApplyRows();
        });
    }

    private void ApplyTabLabels()
    {
        foreach (var tab in TypeTabsHost.Children.OfType<ToggleButton>())
        {
            if (tab.Tag is string table)
                tab.Content = InventoryListing.LabelFor(table);
        }
    }

    /// <summary>
    /// Sposta e Aggiungi richiedono uno stabilimento sul profilo, la manutenzione no:
    /// stessa regola già applicata da MainWindow.ProcessInventoryScanAsync.
    /// </summary>
    private void ApplyActionButtonAvailability()
    {
        var hasPlant = !string.IsNullOrWhiteSpace(App.CurrentUserStabilimento);

        MoveButton.IsEnabled = hasPlant;
        AddButton.IsEnabled = hasPlant;
        MaintenanceButton.IsEnabled = true;

        var tip = hasPlant ? null : UiStrings.NoUserPlant;
        ToolTipService.SetToolTip(MoveButton, tip);
        ToolTipService.SetToolTip(AddButton, tip);
    }

    /// <summary>Proietta l'intero inventario una volta per dataset, non per battuta di ricerca.</summary>
    private void RebuildCache()
    {
        var inventario = App.BridgeData.Inventario;
        foreach (var tab in InventoryListing.Tabs)
            _rowCache[tab.Table] = InventoryListing.Project(inventario, tab.Table);
    }

    private void SelectTab(string table)
    {
        _activeTable = table;

        foreach (var tab in TypeTabsHost.Children.OfType<ToggleButton>())
            tab.IsChecked = tab.Tag is string t && string.Equals(t, table, StringComparison.OrdinalIgnoreCase);

        ApplyColumnHeaders(table);
        ApplyRows();
    }

    private void ApplyColumnHeaders(string table)
    {
        var columns = InventoryListing.ColumnsFor(table);
        HdrUid.Text = columns.Uid;
        HdrMeasure.Text = columns.Measure;
        HdrPlant.Text = columns.Plant;
        HdrExtra.Text = columns.Extra;
        HdrStatus.Text = columns.Status;
    }

    private void ApplyRows()
    {
        var rows = _rowCache.TryGetValue(_activeTable, out var cached)
            ? cached
            : new List<InventoryListRow>();

        _visibleRows = InventoryListing.Filter(rows, _query);
        InventoryList.ItemsSource = _visibleRows;
        CountText.Text = UiStrings.FormatInventoryCount(_visibleRows.Count);
        UpdateEmptyState(rows.Count);
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

    private void Move_Click(object sender, RoutedEventArgs e)
        => MainWin.NavigateToInventoryScan(InventoryActionMode.Move);

    private void Add_Click(object sender, RoutedEventArgs e)
        => MainWin.NavigateToInventoryScan(InventoryActionMode.Create);

    private void Maintenance_Click(object sender, RoutedEventArgs e)
        => MainWin.NavigateToInventoryScan(InventoryActionMode.Maintenance);
}
