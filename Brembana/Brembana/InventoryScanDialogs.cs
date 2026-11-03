using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Brembana;

internal static class InventoryScanDialogs
{
    private const double PanelMinWidth = 580;
    private const double TextAdaptivePanelMinWidth = 420;
    private const double CreatePanelMinWidth = 820;
    private const double BodyFontSize = 24;
    private const double LabelFontSize = 22;
    private const double TitleFontSize = 36;
    private const double InputFontSize = 22;
    private const double InputMinHeight = 58;
    private const double PanelSpacing = 14;

    /// <summary>
    /// Un ContentDialog non obbedisce a Width/MinWidth/MaxWidth impostate come proprieta:
    /// le sue misure vengono dalle risorse del tema (di serie ~548 di larghezza), ed e li
    /// che un messaggio lungo veniva tagliato. La prassi WinUI e sovrascrivere solo quelle
    /// risorse, mai insieme a Width/MinWidth/MaxWidth sul controllo: farlo insieme e quello
    /// che una volta ha spostato il dialog in alto a sinistra, confondendo il calcolo con
    /// cui il popup si centra. Qui si tocca solo la risorsa: il dialog resta libero di
    /// stringersi sul testo e di centrarsi come sempre.
    /// </summary>
    internal static void WidenForWindow(ContentDialog dialog, XamlRoot? xamlRoot, double preferredMaxWidth)
    {
        var available = xamlRoot?.Size ?? new Windows.Foundation.Size(1280, 800);
        var maxWidth = Math.Max(360, Math.Min(preferredMaxWidth, available.Width - 80));
        var maxHeight = Math.Max(320, available.Height - 160);

        dialog.Resources["ContentDialogMaxWidth"] = maxWidth;
        dialog.Resources["ContentDialogMinWidth"] = Math.Min(TextAdaptivePanelMinWidth + 80, maxWidth);
        dialog.Resources["ContentDialogMaxHeight"] = maxHeight;
    }

    private static Style DialogStyle
        => (Style)Application.Current.Resources["BrembanaInventoryScanContentDialogStyle"];

    private static Style PrimaryButtonStyle
        => (Style)Application.Current.Resources["BrembanaInventoryScanPrimaryDialogButtonStyle"];

    public static async Task<InventoryCreateForm?> ShowCreateAsync(
        XamlRoot xamlRoot,
        InventoryCreateForm initialForm)
    {
        var typeBox = new ComboBox
        {
            ItemsSource = BridgeDataService.InventoryTables,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = InputFontSize,
            MinHeight = InputMinHeight,
            PlaceholderText = UiStrings.InventoryFieldType,
            RequestedTheme = ElementTheme.Dark,
            Foreground = ThemeHelper.Brush("BrembanaTextBrush")
        };
        if (!string.IsNullOrWhiteSpace(initialForm.Table) &&
            BridgeDataService.InventoryTables.Contains(initialForm.Table))
            typeBox.SelectedItem = initialForm.Table;

        var uidBox = new TextBox
        {
            Text = initialForm.Uid,
            PlaceholderText = UiStrings.InventoryFieldUid,
            FontSize = InputFontSize,
            MinHeight = InputMinHeight,
            Padding = new Thickness(12, 10, 12, 10),
            RequestedTheme = ElementTheme.Dark,
            Foreground = ThemeHelper.Brush("BrembanaTextBrush")
        };

        // Avviso duplicato sotto il campo UID: l'operatore lo vede mentre digita,
        // senza dover compilare tutto il form per scoprirlo dal server.
        var duplicateWarning = new TextBlock
        {
            FontSize = LabelFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush("BrembanaAmberBrush"),
            TextWrapping = TextWrapping.WrapWholeWords,
            Visibility = Visibility.Collapsed
        };

        var measureFieldLabel = new TextBlock
        {
            Text = UiStrings.InventoryFieldMeasure,
            FontSize = LabelFontSize,
            Foreground = ThemeHelper.Brush("BrembanaTextMutedBrush")
        };

        var measureBox = new TextBox
        {
            Text = initialForm.MeasureValue ?? "",
            PlaceholderText = UiStrings.InventoryFieldMeasure,
            FontSize = InputFontSize,
            MinHeight = InputMinHeight,
            Padding = new Thickness(12, 10, 12, 10),
            RequestedTheme = ElementTheme.Dark,
            Foreground = ThemeHelper.Brush("BrembanaTextBrush")
        };
        var modelBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = InputFontSize,
            MinHeight = InputMinHeight,
            PlaceholderText = "Seleziona modello",
            RequestedTheme = ElementTheme.Dark,
            Foreground = ThemeHelper.Brush("BrembanaTextBrush")
        };
        if (!string.IsNullOrWhiteSpace(initialForm.MeasureValue))
            modelBox.SelectedItem = initialForm.MeasureValue;

        var plantBox = new TextBox
        {
            Text = initialForm.Plant ?? "",
            PlaceholderText = UiStrings.InventoryFieldPlant,
            FontSize = InputFontSize,
            MinHeight = InputMinHeight,
            Padding = new Thickness(12, 10, 12, 10),
            RequestedTheme = ElementTheme.Dark,
            Foreground = ThemeHelper.Brush("BrembanaTextBrush")
        };

        var centerDistanceLabel = new TextBlock
        {
            Text = "Distanza interasse (e.g., 15,35)",
            FontSize = LabelFontSize,
            Foreground = ThemeHelper.Brush("BrembanaTextMutedBrush"),
            Visibility = Visibility.Collapsed
        };
        var centerDistanceBox = new TextBox
        {
            Text = initialForm.CenterDistanceValue ?? "",
            PlaceholderText = "Interasse",
            FontSize = InputFontSize,
            MinHeight = InputMinHeight,
            Padding = new Thickness(12, 10, 12, 10),
            RequestedTheme = ElementTheme.Dark,
            Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
            Visibility = Visibility.Collapsed
        };

        var measurePanel = new StackPanel { Spacing = 10 };
        measurePanel.Children.Add(measureFieldLabel);
        measurePanel.Children.Add(measureBox);
        measurePanel.Children.Add(modelBox);
        measurePanel.Children.Add(centerDistanceLabel);
        measurePanel.Children.Add(centerDistanceBox);

        void UpdateMeasureUi(string? tableValue)
        {
            var table = tableValue ?? "";
            var isModelTable = table is "Testa" or "Generatore";
            var isDiameterTable = table is "Mandrino" or "Tastatore";
            var isMask = table == "Maschera";

            modelBox.Visibility = isModelTable ? Visibility.Visible : Visibility.Collapsed;
            measureBox.Visibility = isModelTable ? Visibility.Collapsed : Visibility.Visible;

            measureFieldLabel.Text = table switch
            {
                "Mandrino" or "Tastatore" => "Diametro (e.g., 15,35)",
                "Maschera" => "Passo (e.g., 15,35)",
                "Testa" or "Generatore" => "Modello",
                _ => UiStrings.InventoryFieldMeasure
            };

            if (isModelTable)
            {
                var options = App.BridgeData.GetExistingModelOptions(table);
                modelBox.ItemsSource = options;
                if (!string.IsNullOrWhiteSpace(initialForm.MeasureValue) &&
                    options.Contains(initialForm.MeasureValue, StringComparer.OrdinalIgnoreCase))
                {
                    modelBox.SelectedItem = options.First(x =>
                        x.Equals(initialForm.MeasureValue, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    modelBox.SelectedIndex = -1;
                }
            }

            centerDistanceLabel.Visibility = isMask ? Visibility.Visible : Visibility.Collapsed;
            centerDistanceBox.Visibility = isMask ? Visibility.Visible : Visibility.Collapsed;
            if (!isMask)
                centerDistanceBox.Text = "";
        }

        typeBox.SelectionChanged += (_, _) =>
        {
            if (typeBox.SelectedItem is string table)
                UpdateMeasureUi(table);
            else
                UpdateMeasureUi(null);
        };

        var uidField = LabeledField(UiStrings.InventoryFieldUid, uidBox);
        uidField.Children.Add(duplicateWarning);

        var formPanel = new StackPanel { Spacing = 16 };
        formPanel.Children.Add(LabeledField(UiStrings.InventoryFieldType, typeBox));
        formPanel.Children.Add(uidField);
        formPanel.Children.Add(measurePanel);
        formPanel.Children.Add(LabeledField(UiStrings.InventoryFieldPlant, plantBox));
        UpdateMeasureUi(typeBox.SelectedItem as string);

        var content = CreateTitledContent(CreatePanelMinWidth,
            UiStrings.InventoryCreateTitle,
            "",
            formPanel);

        var dialog = CreateConfirmDialog(xamlRoot, content);

        // Blocca la conferma finché l'uid non è libero: il server rifiuterebbe comunque
        // il duplicato nella stessa tabella, ma non quello tra tabelle diverse.
        void ValidateUid()
        {
            var uid = uidBox.Text.Trim();
            var selectedTable = typeBox.SelectedItem as string;
            var existing = App.BridgeData.FindExistingUid(uid, selectedTable);

            if (existing != null)
            {
                duplicateWarning.Text = UiStrings.FormatInventoryFoundMessage(
                    existing.Uid, existing.Plant, existing.Table);
                duplicateWarning.Visibility = Visibility.Visible;
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }

            duplicateWarning.Visibility = Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled =
                !string.IsNullOrWhiteSpace(uid) && !string.IsNullOrWhiteSpace(selectedTable);
        }

        uidBox.TextChanged += (_, _) => ValidateUid();
        typeBox.SelectionChanged += (_, _) => ValidateUid();
        ValidateUid();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        var table = typeBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(table))
            return null;

        var uid = uidBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(uid))
            return null;

        var measureValue = table is "Testa" or "Generatore"
            ? (modelBox.SelectedItem as string ?? "").Trim()
            : measureBox.Text.Trim();

        return new InventoryCreateForm
        {
            Table = table,
            Uid = uid,
            MeasureValue = measureValue,
            Plant = plantBox.Text.Trim(),
            CenterDistanceValue = centerDistanceBox.Text.Trim()
        };
    }

    public static async Task<bool> ShowMoveAsync(XamlRoot xamlRoot, InventoryMatch match, string destinationPlant)
    {
        var content = CreateTitledContent(TextAdaptivePanelMinWidth,
            UiStrings.InventoryMoveTitle,
            UiStrings.FormatInventoryMoveMessage(match.Table, match.Uid, match.Plant, destinationPlant));

        var dialog = CreateConfirmDialog(xamlRoot, content, fixedWidth: null);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<bool> ShowMaintenanceAsync(XamlRoot xamlRoot, InventoryMatch match, bool enable)
    {
        var content = CreateTitledContent(TextAdaptivePanelMinWidth,
            enable ? UiStrings.InventoryMaintenanceEnableTitle : UiStrings.InventoryMaintenanceDisableTitle,
            enable
                ? UiStrings.FormatInventoryMaintenanceEnableMessage(match.Table, match.Uid)
                : UiStrings.FormatInventoryMaintenanceDisableMessage(match.Table, match.Uid));

        var dialog = CreateConfirmDialog(xamlRoot, content, fixedWidth: null);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// «Stai usando X al posto di Y, va bene?» — il testo arriva dal server, che conosce
    /// anche le attività a cui il pezzo verrebbe sottratto. Gli avvisi (es. pezzo di un
    /// altro stabilimento) vanno sotto, in ambra.
    /// </summary>
    public static async Task<bool> ShowScanSubstitutionAsync(
        XamlRoot xamlRoot,
        string message,
        IReadOnlyList<string>? warnings)
    {
        var extra = new List<UIElement>();
        foreach (var warning in warnings ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(warning))
                continue;
            extra.Add(new TextBlock
            {
                Text = warning,
                FontSize = LabelFontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush("BrembanaAmberBrush"),
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }

        var content = CreateTitledContent(PanelMinWidth,
            UiStrings.ScanSubstitutionTitle,
            message,
            extra.ToArray());

        var dialog = CreateConfirmDialog(xamlRoot, content);
        dialog.PrimaryButtonText = UiStrings.ScanSubstitutionConfirmButton;

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Togliere un pezzo scansionato (lettura sbagliata, pezzo sbagliato)? Si chiede perché
    /// sul tablet un tocco può essere involontario e un sostituto va poi riscansionato.
    /// </summary>
    public static async Task<bool> ConfirmRemoveScanAsync(XamlRoot xamlRoot, string uid, bool isSubstitute)
    {
        var content = CreateTitledContent(TextAdaptivePanelMinWidth,
            UiStrings.RemoveScanTitle,
            UiStrings.FormatRemoveScanMessage(uid, isSubstitute));

        var dialog = CreateConfirmDialog(xamlRoot, content, fixedWidth: null);
        dialog.PrimaryButtonText = UiStrings.RemoveScanButton;

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Corpo della conferma d'avvio/chiusura: la domanda e, sotto in ambra, le sostituzioni
    /// che verranno scritte ("C106 al posto di C330").
    /// </summary>
    public static StackPanel CreateRegisterConfirmationContent(
        string message,
        IReadOnlyList<string> substitutions,
        double fontSize)
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = fontSize,
            Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
            TextWrapping = TextWrapping.WrapWholeWords
        });

        if (substitutions.Count == 0)
            return content;

        content.Children.Add(new TextBlock
        {
            Text = UiStrings.SubstitutionsHeader,
            FontSize = fontSize - 2,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush("BrembanaAmberBrush")
        });
        foreach (var line in substitutions)
        {
            content.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = fontSize - 2,
                Foreground = ThemeHelper.Brush("BrembanaAmberBrush"),
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }
        return content;
    }

    /// <summary>
    /// Avviso o rifiuto del server con il solo tasto Chiudi: il messaggio può avere più
    /// righe (<c>\n</c>), le righe di dettaglio vanno in ambra sotto.
    /// </summary>
    public static async Task ShowNoticeAsync(
        XamlRoot xamlRoot,
        string title,
        string message,
        IReadOnlyList<string>? details = null)
    {
        var extra = new List<UIElement>();
        foreach (var detail in details ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(detail))
                continue;
            extra.Add(new TextBlock
            {
                Text = detail,
                FontSize = LabelFontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush("BrembanaAmberBrush"),
                TextWrapping = TextWrapping.WrapWholeWords
            });
        }

        var content = CreateTitledContent(PanelMinWidth, title, message, extra.ToArray());

        var dialog = CreateConfirmDialog(xamlRoot, content);
        dialog.PrimaryButtonText = "";
        dialog.CloseButtonText = UiStrings.CloseButton;

        await dialog.ShowAsync();
    }

    public static async Task ShowFoundAsync(XamlRoot xamlRoot, InventoryMatch match)
    {
        var content = CreateTitledContent(TextAdaptivePanelMinWidth,
            UiStrings.InventoryFoundTitle,
            UiStrings.FormatInventoryFoundMessage(match.Uid, match.Plant, match.Table));

        var dialog = new ContentDialog
        {
            Content = content,
            RequestedTheme = ElementTheme.Dark,
            Style = DialogStyle,
            CloseButtonText = UiStrings.CloseButton,
            DefaultButton = ContentDialogButton.None,
            XamlRoot = xamlRoot
        };

        await dialog.ShowAsync();
    }

    private static StackPanel CreateTitledContent(double minWidth, string title, string message, params UIElement[] extraChildren)
    {
        var content = new StackPanel { Spacing = PanelSpacing, MinWidth = minWidth };
        content.Children.Add(CreateTitleBlock(title));
        if (!string.IsNullOrWhiteSpace(message))
            content.Children.Add(CreateBodyBlock(message));

        foreach (var child in extraChildren)
            content.Children.Add(child);

        return content;
    }

    private static TextBlock CreateTitleBlock(string title) => new()
    {
        Text = title,
        FontSize = TitleFontSize,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
        TextWrapping = TextWrapping.WrapWholeWords
    };

    private static TextBlock CreateBodyBlock(string message) => new()
    {
        Text = message,
        TextWrapping = TextWrapping.WrapWholeWords,
        Foreground = ThemeHelper.Brush("BrembanaTextBrush"),
        FontSize = BodyFontSize,
        LineHeight = 34
    };

    /// <summary>
    /// Il dialog si adatta alla finestra: mai più largo dello schermo meno un margine (lo
    /// <summary>
    /// Con fixedWidth nullo (i dialog brevi di conferma, spostamento e manutenzione) il
    /// comportamento e quello originale, mai stato un problema. Con un valore, il dialog si
    /// allarga sul testo fino a quel limite tramite WidenForWindow, senza toccare Width,
    /// MinWidth o MaxWidth sul controllo, e il contenuto scorre se supera l'altezza della
    /// finestra.
    /// </summary>
    private static ContentDialog CreateConfirmDialog(XamlRoot xamlRoot, StackPanel content, double? fixedWidth = 1100)
    {
        if (!fixedWidth.HasValue)
        {
            return new ContentDialog
            {
                Content = content,
                MinWidth = TextAdaptivePanelMinWidth + 80,
                RequestedTheme = ElementTheme.Dark,
                Style = DialogStyle,
                PrimaryButtonText = UiStrings.ConfirmRegisterButton,
                PrimaryButtonStyle = PrimaryButtonStyle,
                CloseButtonText = UiStrings.CancelButton,
                DefaultButton = ContentDialogButton.None,
                XamlRoot = xamlRoot
            };
        }

        var dialog = new ContentDialog
        {
            RequestedTheme = ElementTheme.Dark,
            Style = DialogStyle,
            PrimaryButtonText = UiStrings.ConfirmRegisterButton,
            PrimaryButtonStyle = PrimaryButtonStyle,
            CloseButtonText = UiStrings.CancelButton,
            DefaultButton = ContentDialogButton.None,
            XamlRoot = xamlRoot
        };

        WidenForWindow(dialog, xamlRoot, fixedWidth.Value);

        var available = xamlRoot?.Size ?? new Windows.Foundation.Size(1280, 800);
        var maxWidth = Math.Max(360, Math.Min(fixedWidth.Value, available.Width - 80));
        var maxHeight = Math.Max(320, available.Height - 160);

        content.MaxWidth = Math.Max(280, maxWidth - 96);
        content.MinWidth = Math.Min(content.MinWidth, content.MaxWidth);

        dialog.Content = new ScrollViewer
        {
            Content = content,
            MaxHeight = Math.Max(200, maxHeight - 230),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled
        };

        return dialog;
    }

    private static StackPanel LabeledField(string label, FrameworkElement input)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = LabelFontSize,
            Foreground = ThemeHelper.Brush("BrembanaTextMutedBrush")
        });
        panel.Children.Add(input);
        return panel;
    }
}
