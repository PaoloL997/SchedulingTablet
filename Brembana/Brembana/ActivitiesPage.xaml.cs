using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace Brembana;

/// <summary>
/// Gestione attività su due livelli: prima le commesse (solo il numero, in ordine
/// crescente), poi le attività della commessa scelta, per data di preventivo crescente.
/// </summary>
public sealed partial class ActivitiesPage : Page
{
    private const int CollapsedLimit = 5;
    private int _lastLayoutWidth;

    private MainWindow MainWin => (MainWindow)App.MainWindow;
    private readonly List<AttivitaDto> _allActivities = new();
    private bool _showAll;
    private string _filterQuery = "";
    private bool _navigating;

    /// <summary>Commessa aperta: null mostra l'elenco delle commesse, altrimenti le sue attività.</summary>
    private string? _selectedCommessa;

    public ActivitiesPage()
    {
        InitializeComponent();
        ActivityCardsPanel.SizeChanged += ActivityCardsPanel_SizeChanged;
        Loaded += ActivitiesPage_Loaded;
        Unloaded += ActivitiesPage_Unloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _selectedCommessa = e.Parameter as string;
        UpdateHeader();
    }

    private void ActivityCardsPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = (int)e.NewSize.Width;
        if (width != _lastLayoutWidth && width > 0)
            RenderActivityCards();
    }

    private async void ActivitiesPage_Loaded(object sender, RoutedEventArgs e)
    {
        MainWin.DatasetRefreshed += OnDatasetRefreshed;
        await LoadAndRenderAsync();
    }

    private void ActivitiesPage_Unloaded(object sender, RoutedEventArgs e)
        => MainWin.DatasetRefreshed -= OnDatasetRefreshed;

    private void OnDatasetRefreshed()
    {
        _showAll = false;
        ReloadActivities();
        RenderActivityCards();
    }

    private async Task LoadAndRenderAsync()
    {
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        await MainWin.LoadActivitiesAsync();
        ReloadActivities();
        RenderActivityCards();
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
    }

    private void ReloadActivities()
    {
        _allActivities.Clear();
        if (App.BridgeConnected)
            _allActivities.AddRange(App.BridgeData.GetOpenActivities());

        // Commessa rimasta senza attività aperte (es. appena avviata/chiusa l'ultima): si torna all'elenco.
        if (_selectedCommessa != null && !_allActivities.Any(a => IsInCommessa(a, _selectedCommessa)))
            ShowCommesse();
    }

    private static bool IsInCommessa(AttivitaDto attivita, string commessa) =>
        string.Equals(attivita.Commessa?.Trim() ?? "", commessa, StringComparison.OrdinalIgnoreCase);

    private void OpenCommessa(string commessa)
    {
        _selectedCommessa = commessa;
        ResetFilter();
        UpdateHeader();
        RenderActivityCards();
    }

    private void ShowCommesse()
    {
        _selectedCommessa = null;
        ResetFilter();
        UpdateHeader();
    }

    private void ResetFilter()
    {
        _filterQuery = "";
        _showAll = false;
        ActivitySearchBox.Text = "";
        ActivitySearchBox.ItemsSource = null;
    }

    private void UpdateHeader()
    {
        var inCommessa = _selectedCommessa != null;
        BackButtonText.Text = inCommessa ? UiStrings.BackToCommesse : UiStrings.BackToHome;
        ToolTipService.SetToolTip(BackButton, BackButtonText.Text);
        CommessaTitleText.Text = inCommessa ? UiStrings.FormatCommessaTitle(_selectedCommessa!) : "";
        CommessaTitleText.Visibility = inCommessa ? Visibility.Visible : Visibility.Collapsed;
        ActivitySearchBox.PlaceholderText = inCommessa
            ? UiStrings.SearchActivityPlaceholder
            : UiStrings.SearchCommessaPlaceholder;
    }

    private void ActivitySearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        _filterQuery = sender.Text?.Trim() ?? "";
        _showAll = false;
        UpdateSuggestions(sender);
        RenderActivityCards();
    }

    private void UpdateSuggestions(AutoSuggestBox sender)
    {
        sender.ItemsSource = GetFilteredActivities()
            .Take(10)
            .ToList();
    }

    private async void ActivitySearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is AttivitaDto attivita)
        {
            sender.Text = attivita.ActivityId;
            await NavigateToActivityAsync(attivita);
        }
    }

    private async void ActivitySearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        AttivitaDto? match = args.ChosenSuggestion as AttivitaDto;
        match ??= GetFilteredActivities().FirstOrDefault(a =>
            a.ActivityId.Equals(sender.Text?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match != null)
            await NavigateToActivityAsync(match);
    }

    /// <summary>Attività che rispondono alla ricerca, limitate alla commessa aperta se ce n'è una.</summary>
    private List<AttivitaDto> GetFilteredActivities()
    {
        var scope = _selectedCommessa == null
            ? _allActivities
            : _allActivities.Where(a => IsInCommessa(a, _selectedCommessa));

        if (!string.IsNullOrWhiteSpace(_filterQuery))
        {
            scope = scope.Where(a =>
                a.ActivityId.Contains(_filterQuery, StringComparison.OrdinalIgnoreCase) ||
                (a.Plant?.Contains(_filterQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (a.Responsible?.Contains(_filterQuery, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return ActivityListing.SortActivities(scope).ToList();
    }

    private void RenderActivityCards()
    {
        ActivityCardsPanel.Children.Clear();
        var filtered = GetFilteredActivities();
        var containerWidth = ActivityCardsPanel.ActualWidth;
        _lastLayoutWidth = (int)containerWidth;

        if (filtered.Count == 0)
        {
            var emptyText = _selectedCommessa == null ? UiStrings.NoCommesseFound : UiStrings.NoActivitiesFound;
            if (string.IsNullOrWhiteSpace(_filterQuery))
            {
                emptyText = string.IsNullOrWhiteSpace(App.CurrentUserStabilimento)
                    ? UiStrings.NoUserPlantActivities
                    : UiStrings.NoActivities;
            }

            ActivityCardsPanel.Children.Add(new TextBlock
            {
                Text = emptyText,
                FontSize = 18,
                Foreground = ThemeHelper.Brush("BrembanaTextMutedBrush"),
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        var cards = _selectedCommessa == null
            ? ActivityListing.GroupByCommessa(filtered).Select(CreateCommessaCard).ToList()
            : filtered.Select(CreateActivityCard).ToList();

        var visible = _showAll ? cards : cards.Take(CollapsedLimit).ToList();
        var hiddenCount = cards.Count - visible.Count;

        for (int i = 0; i < visible.Count; i += CardLayoutHelper.CardsPerRow)
        {
            var rowCards = visible.Skip(i).Take(CardLayoutHelper.CardsPerRow).ToList();
            ActivityCardsPanel.Children.Add(CardLayoutHelper.CreateRow(rowCards, containerWidth));
        }

        if (!_showAll && hiddenCount > 0)
            ActivityCardsPanel.Children.Add(CreateExpandCard(hiddenCount, containerWidth));
    }

    /// <summary>Primo livello: solo il numero della commessa.</summary>
    private FrameworkElement CreateCommessaCard(CommessaGroup group)
    {
        var accentColor = ThemeHelper.GetColor("BrembanaAccentBrush");
        var content = new Grid { Padding = new Thickness(18, 16, 18, 16), MinHeight = 96 };
        content.Children.Add(new TextBlock
        {
            Text = group.Commessa,
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var button = CreateCardButton(content, accentColor, 96);
        button.Click += (_, _) => OpenCommessa(group.Commessa);
        return CardHoverHelper.WrapCard(button, accentColor);
    }

    /// <summary>Secondo livello: nome {disegno}-{item}-{attività} e, sulla stessa riga, da avviare/da chiudere.</summary>
    private FrameworkElement CreateActivityCard(AttivitaDto attivita)
    {
        var isOutgoing = BridgeDataService.IsOutgoingOperation(attivita);
        var accentColor = isOutgoing
            ? ThemeHelper.GetColor("BrembanaAmberBrush")
            : ThemeHelper.GetColor("BrembanaGreenBrush");
        var badgeText = isOutgoing ? UiStrings.BadgeOutgoing : UiStrings.BadgeIncoming;

        var content = new Grid { Padding = new Thickness(18, 16, 18, 16), MinHeight = 96, ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = ActivityListing.ActivityLabel(attivita),
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 0);
        content.Children.Add(title);

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(30, accentColor.R, accentColor.G, accentColor.B)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 32
        };
        badge.Child = new TextBlock
        {
            Text = badgeText,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(accentColor),
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = 20
        };
        Grid.SetColumn(badge, 1);
        content.Children.Add(badge);

        var button = CreateCardButton(content, accentColor, 96);
        button.Click += async (_, _) => await NavigateToActivityAsync(attivita);
        return CardHoverHelper.WrapCard(button, accentColor);
    }

    private static Button CreateCardButton(UIElement content, Color accentColor, double minHeight) => new()
    {
        Content = content,
        Style = (Style)Application.Current.Resources["BrembanaCardButtonStyle"],
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(0),
        CornerRadius = new CornerRadius(16),
        BorderBrush = new SolidColorBrush(Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B)),
        BorderThickness = new Thickness(1),
        Background = ThemeHelper.Brush("BrembanaSurfaceBrush"),
        MinHeight = minHeight
    };

    private UIElement CreateExpandCard(int hiddenCount, double containerWidth)
    {
        var content = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 24, 20, 24)
        };
        content.Children.Add(new TextBlock
        {
            Text = _selectedCommessa == null ? UiStrings.ShowMoreCommesse : UiStrings.ShowMoreActivities,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush("BrembanaAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = UiStrings.FormatMoreActivities(hiddenCount),
            FontSize = 16,
            Foreground = ThemeHelper.Brush("BrembanaTextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var button = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["BrembanaCardButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            BorderBrush = ThemeHelper.Brush("BrembanaBorderBrush"),
            BorderThickness = new Thickness(1),
            Background = ThemeHelper.Brush("BrembanaSurfaceBrush"),
            MinHeight = 96,
            Width = CardLayoutHelper.GetRowWidth(containerWidth)
        };
        button.Click += (_, _) =>
        {
            _showAll = true;
            RenderActivityCards();
        };
        return CardHoverHelper.WrapCard(button, ThemeHelper.GetColor("BrembanaAccentBrush"));
    }

    private async Task NavigateToActivityAsync(AttivitaDto attivita)
    {
        if (_navigating) return;
        _navigating = true;
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;

        var ok = await MainWin.StartSessionAsync(attivita);
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        _navigating = false;

        if (!ok)
            return;

        // Uscita: dritti alla scansione, Indietro dalla fotocamera riporta qui. Rientro: i
        // pezzi sono già confermati, si va al riepilogo per chiudere.
        if (ActivityChecklistPolicy.RequiresScan(MainWin.IsOutgoing))
            await MainWin.RunCameraScanSessionAsync();
        else
            MainWin.NavigateToAccessories();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCommessa == null)
        {
            MainWin.NavigateToHome();
            return;
        }

        ShowCommesse();
        RenderActivityCards();
    }
}
