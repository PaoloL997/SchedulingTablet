using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Brembana;

public sealed partial class HomePage : Page
{
    private MainWindow MainWin => (MainWindow)App.MainWindow;
    private AdminAccessHelper? _adminAccess;

    public HomePage()
    {
        InitializeComponent();
        Loaded += HomePage_Loaded;
        HomeActionCardsHost.SizeChanged += HomeActionCardsHost_SizeChanged;
        // Pressione prolungata sul logo per sbloccare il menu admin.
        _adminAccess = new AdminAccessHelper(AdminLogoHotspot, () => MainWin.TryOpenAdminMenuAsync());
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e) => BuildActionCards();

    private void HomeActionCardsHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0)
            BuildActionCards();
    }

    private void BuildActionCards()
    {
        var containerWidth = HomeActionCardsHost.ActualWidth;
        if (containerWidth <= 0)
            return;

        HomeActionCardsHost.Children.Clear();

        var startFinishCard = CreateActionCard(
            UiStrings.HomeStartFinishActivity,
            ThemeHelper.GetColor("BrembanaGreenBrush"),
            StartFinish_Click);
        var moveCard = CreateActionCard(
            UiStrings.HomeMoveComponent,
            ThemeHelper.GetColor("BrembanaAmberBrush"),
            MoveComponent_Click);
        var addCard = CreateActionCard(
            UiStrings.HomeAddComponent,
            ThemeHelper.GetColor("BrembanaAccentBrush"),
            AddComponent_Click);
        var maintenanceCard = CreateActionCard(
            UiStrings.HomeMaintenanceComponent,
            ThemeHelper.GetColor("BrembanaTextMutedBrush"),
            Maintenance_Click);

        HomeActionCardsHost.Children.Add(CardLayoutHelper.CreateRow(
            new FrameworkElement[] { startFinishCard, moveCard },
            containerWidth));
        HomeActionCardsHost.Children.Add(CardLayoutHelper.CreateRow(
            new FrameworkElement[] { addCard, maintenanceCard },
            containerWidth));
    }

    private static FrameworkElement CreateActionCard(string text, Color accentColor, RoutedEventHandler clickHandler)
    {
        var content = new TextBlock
        {
            Text = text,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.WrapWholeWords,
            Margin = new Thickness(24, 18, 24, 18)
        };

        var button = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["BrembanaCardButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0),
            MinHeight = 136,
            CornerRadius = new CornerRadius(16),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B)),
            BorderThickness = new Thickness(1),
            Background = ThemeHelper.Brush("BrembanaSurfaceBrush")
        };
        button.Click += clickHandler;

        return CardHoverHelper.WrapCard(button, accentColor);
    }

    private void StartFinish_Click(object sender, RoutedEventArgs e)
        => MainWin.NavigateToActivities();

    private void MoveComponent_Click(object sender, RoutedEventArgs e)
        => MainWin.NavigateToInventoryScan(InventoryActionMode.Move);

    private void AddComponent_Click(object sender, RoutedEventArgs e)
        => MainWin.NavigateToInventoryScan(InventoryActionMode.Create);

    private void Maintenance_Click(object sender, RoutedEventArgs e)
        => MainWin.NavigateToInventoryScan(InventoryActionMode.Maintenance);
}
