using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Brembana;

public sealed partial class ActivitiesPage : Page
{
    private const int CollapsedLimit = 5;
    private int _lastLayoutWidth;

    private MainWindow MainWin => (MainWindow)App.MainWindow;
    private readonly List<AttivitaDto> _allActivities = new();
    private bool _showAll;
    private string _filterQuery = "";
    private bool _navigating;

    public ActivitiesPage()
    {
        InitializeComponent();
        ActivityCardsPanel.SizeChanged += ActivityCardsPanel_SizeChanged;
        Loaded += ActivitiesPage_Loaded;
        Unloaded += ActivitiesPage_Unloaded;
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

    private List<AttivitaDto> GetFilteredActivities()
    {
        if (string.IsNullOrWhiteSpace(_filterQuery))
            return _allActivities.ToList();

        return _allActivities.Where(a =>
            a.ActivityId.Contains(_filterQuery, StringComparison.OrdinalIgnoreCase) ||
            (a.Plant?.Contains(_filterQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (a.Responsible?.Contains(_filterQuery, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
    }

    private void RenderActivityCards()
    {
        ActivityCardsPanel.Children.Clear();
        var filtered = GetFilteredActivities();
        var containerWidth = ActivityCardsPanel.ActualWidth;
        _lastLayoutWidth = (int)containerWidth;

        if (filtered.Count == 0)
        {
            ActivityCardsPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(_filterQuery) ? UiStrings.NoActivities : "Nessuna attività trovata.",
                FontSize = 18,
                Foreground = ThemeHelper.Brush("BrembanaTextMutedBrush"),
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        var visible = _showAll ? filtered : filtered.Take(CollapsedLimit).ToList();
        var hiddenCount = filtered.Count - visible.Count;

        for (int i = 0; i < visible.Count; i += CardLayoutHelper.CardsPerRow)
        {
            var rowCards = new List<FrameworkElement>();
            for (int c = 0; c < CardLayoutHelper.CardsPerRow && i + c < visible.Count; c++)
            {
                if (CreateActivityCard(visible[i + c]) is FrameworkElement card)
                    rowCards.Add(card);
            }

            ActivityCardsPanel.Children.Add(CardLayoutHelper.CreateRow(rowCards, containerWidth));
        }

        if (!_showAll && hiddenCount > 0)
            ActivityCardsPanel.Children.Add(CreateExpandCard(hiddenCount, containerWidth));
    }

    private UIElement CreateActivityCard(AttivitaDto attivita)
    {
        var isOutgoing = BridgeDataService.IsOutgoingOperation(attivita);
        var accentColor = isOutgoing
            ? ThemeHelper.GetColor("BrembanaAmberBrush")
            : ThemeHelper.GetColor("BrembanaGreenBrush");
        var badgeText = isOutgoing ? UiStrings.BadgeOutgoing : UiStrings.BadgeIncoming;
        var resourceCount = BridgeDataService.CountResources(attivita);

        var infoParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(attivita.Plant)) infoParts.Add(attivita.Plant);
        if (!string.IsNullOrWhiteSpace(attivita.Responsible)) infoParts.Add(attivita.Responsible);
        var infoText = string.Join("  ·  ", infoParts);

        var content = new Grid { Padding = new Thickness(18, 16, 18, 16), MinHeight = 116 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = attivita.ActivityId,
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 0);
        topRow.Children.Add(title);

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
        topRow.Children.Add(badge);
        Grid.SetRow(topRow, 0);
        content.Children.Add(topRow);

        if (!string.IsNullOrEmpty(infoText))
        {
            var info = new TextBlock
            {
                Text = infoText,
                FontSize = 17,
                Foreground = ThemeHelper.Brush("BrembanaTextMutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(info, 1);
            content.Children.Add(info);
        }

        var count = new TextBlock
        {
            Text = UiStrings.FormatAccessoryCount(resourceCount),
            FontSize = 16,
            Foreground = ThemeHelper.Brush("BrembanaTextSubtleBrush"),
            Margin = new Thickness(0, 6, 0, 0)
        };
        Grid.SetRow(count, 2);
        content.Children.Add(count);

        var button = new Button
        {
            Content = content,
            Tag = attivita,
            Style = (Style)Application.Current.Resources["BrembanaCardButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B)),
            BorderThickness = new Thickness(1),
            Background = ThemeHelper.Brush("BrembanaSurfaceBrush"),
            MinHeight = 116
        };
        button.Click += async (_, _) =>
        {
            if (button.Tag is AttivitaDto a)
                await NavigateToActivityAsync(a);
        };
        return CardHoverHelper.WrapCard(button, accentColor);
    }

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
            Text = UiStrings.ShowMoreActivities,
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

        if (ok)
            MainWin.NavigateToAccessories();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => MainWin.NavigateToHome();
}
