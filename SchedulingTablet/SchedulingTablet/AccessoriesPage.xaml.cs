using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Brembana;

public sealed partial class AccessoriesPage : Page
{
    private MainWindow MainWin => (MainWindow)App.MainWindow;

    public AccessoriesPage()
    {
        InitializeComponent();
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

        SessionTitle.Text = MainWin.SessionActivityName ?? "";
        UpdateSessionHeader();
        UpdateRegisterButton();
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
        SessionModeText.Text = isOutgoing ? UiStrings.BadgeOutgoing : UiStrings.BadgeIncoming;
        if (isOutgoing)
        {
            SessionModeBadge.Background = ThemeHelper.WithAlpha("BrembanaAmberBrush", 40);
            SessionModeText.Foreground = ThemeHelper.Brush("BrembanaAmberBrush");
        }
        else
        {
            SessionModeBadge.Background = ThemeHelper.WithAlpha("BrembanaGreenBrush", 40);
            SessionModeText.Foreground = ThemeHelper.Brush("BrembanaGreenBrush");
        }
    }

    private void UpdateRegisterButton()
    {
        var isOutgoing = MainWin.IsOutgoing;
        BtnRegisterText.Text = isOutgoing ? UiStrings.RegisterOutgoing : UiStrings.RegisterIncoming;
        BtnRegisterText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);

        if (isOutgoing)
        {
            BtnRegister.Background = ThemeHelper.Brush("BrembanaAmberBrush");
            BtnRegister.BorderBrush = ThemeHelper.Brush("BrembanaAmberBrush");
        }
        else
        {
            BtnRegister.Background = ThemeHelper.Brush("BrembanaGreenBrush");
            BtnRegister.BorderBrush = ThemeHelper.Brush("BrembanaGreenBrush");
        }
        BtnRegister.BorderThickness = new Thickness(0);
    }

    private void RefreshComponentList()
    {
        MainWin.FillComponentsPanel(ComponentsPanel, RefreshComponentList);
    }

    private async void Photo_Click(object sender, RoutedEventArgs e)
    {
        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        try
        {
            await MainWin.RunCameraScanSessionAsync(RefreshComponentList);
        }
        finally
        {
            BusyRing.IsActive = false;
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => MainWin.NavigateToActivities();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (MainWin.SelectedRegisterCount == 0)
        {
            await MainWin.SaveSessionAsync();
            return;
        }

        if (!await ConfirmRegisterAsync())
            return;

        BusyRing.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        try
        {
            await MainWin.SaveSessionAsync();
            RefreshComponentList();
        }
        finally
        {
            BusyRing.IsActive = false;
            BusyRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<bool> ConfirmRegisterAsync()
    {
        var isOutgoing = MainWin.IsOutgoing;
        var count = MainWin.SelectedRegisterCount;

        var dialog = new ContentDialog
        {
            Title = isOutgoing ? UiStrings.ConfirmRegisterTitleOutgoing : UiStrings.ConfirmRegisterTitleIncoming,
            Content = new TextBlock
            {
                Text = UiStrings.FormatRegisterConfirmation(count, isOutgoing),
                FontSize = 18,
                Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
                TextWrapping = TextWrapping.WrapWholeWords
            },
            Style = (Style)Application.Current.Resources["BrembanaContentDialogStyle"],
            PrimaryButtonText = UiStrings.ConfirmRegisterButton,
            CloseButtonText = UiStrings.CancelButton,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
