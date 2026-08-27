using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace Brembana;

/// <summary>
/// Dialog PIN e menu amministratore per uscita controllata dalla modalità kiosk.
/// </summary>
internal static class AdminMenuHelper
{
    public static async Task<bool> PromptPinAsync(XamlRoot xamlRoot, string? expectedPin)
    {
        if (string.IsNullOrWhiteSpace(expectedPin))
        {
            await ShowMessageAsync(
                xamlRoot,
                "PIN non configurato",
                "Imposta KIOSK_ADMIN_PIN nel file .env per abilitare il menu amministratore.");
            return false;
        }

        var input = new PasswordBox
        {
            PlaceholderText = "PIN amministratore",
            MinWidth = 360,
            FontSize = 22,
            MinHeight = 52
        };

        var dialog = new ContentDialog
        {
            Title = "Accesso amministratore",
            Content = input,
            PrimaryButtonText = "Sblocca",
            CloseButtonText = UiStrings.CancelButton,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return false;

        var ok = string.Equals(input.Password.Trim(), expectedPin.Trim(), StringComparison.Ordinal);
        if (!ok)
        {
            AppLogger.Warning("PIN amministratore errato.");
            await ShowMessageAsync(xamlRoot, "PIN errato", "Il PIN inserito non è corretto.");
        }

        return ok;
    }

    public static async Task ShowAdminMenuAsync(
        XamlRoot xamlRoot,
        KioskModeService kiosk,
        Action closeApplication,
        Action restartApplication)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };

        var closeBtn = CreateMenuButton("Chiudi applicazione", "#FFE02424");
        var restartBtn = CreateMenuButton("Riavvia applicazione", "#FF4D9EF7");
        var exitKioskBtn = CreateMenuButton(
            kiosk.IsKioskActive ? "Esci modalità kiosk" : "Riattiva modalità kiosk",
            "#FFF29F0D");

        panel.Children.Add(closeBtn);
        panel.Children.Add(restartBtn);
        panel.Children.Add(exitKioskBtn);

        var dialog = new ContentDialog
        {
            Title = "Menu amministratore",
            Content = panel,
            CloseButtonText = UiStrings.CloseButton,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot
        };

        string? selected = null;
        closeBtn.Click += (_, _) =>
        {
            selected = "close";
            dialog.Hide();
        };
        restartBtn.Click += (_, _) =>
        {
            selected = "restart";
            dialog.Hide();
        };
        exitKioskBtn.Click += (_, _) =>
        {
            selected = kiosk.IsKioskActive ? "exit-kiosk" : "enter-kiosk";
            dialog.Hide();
        };

        await dialog.ShowAsync();

        switch (selected)
        {
            case "close":
                AppLogger.Info("Chiusura applicazione richiesta da amministratore.");
                closeApplication();
                break;
            case "restart":
                AppLogger.Info("Riavvio applicazione richiesto da amministratore.");
                restartApplication();
                break;
            case "exit-kiosk":
                kiosk.ExitKioskMode();
                await ShowMessageAsync(xamlRoot, "Kiosk disattivato",
                    "La finestra è tornata in modalità normale. La chiusura resta protetta finché non usi Chiudi applicazione.");
                break;
            case "enter-kiosk":
                kiosk.EnterKioskMode();
                break;
        }
    }

    public static void CloseApplication(KioskModeService kiosk)
    {
        kiosk.PermitClose();
        Application.Current.Exit();
    }

    public static void RestartApplication(KioskModeService kiosk)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Impossibile avviare nuova istanza per riavvio.", ex);
        }

        CloseApplication(kiosk);
    }

    /// <summary>
    /// Riavvio dopo crash non gestito (best-effort lato applicazione).
    /// </summary>
    public static void TryRestartAfterCrash()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
            AppLogger.Info("Riavvio automatico dopo crash avviato.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Riavvio automatico dopo crash fallito.", ex);
        }
    }

    private static Button CreateMenuButton(string text, string hexColor)
    {
        return new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 56,
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(ParseColor(hexColor)),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            BorderThickness = new Thickness(0)
        };
    }

    private static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.WrapWholeWords,
                FontSize = 16,
                MaxWidth = 420
            },
            CloseButtonText = UiStrings.CloseButton,
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
            hex = "FF" + hex;
        var value = Convert.ToUInt32(hex, 16);
        return Windows.UI.Color.FromArgb(
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
    }
}
