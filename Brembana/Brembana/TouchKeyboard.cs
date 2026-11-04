using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Brembana;

/// <summary>
/// Invio della tastiera virtuale come conferma. Senza, l'operatore doveva chiudere la
/// tastiera con la X e poi toccare il pulsante di conferma.
/// </summary>
internal static class TouchKeyboard
{
    /// <summary>
    /// Invio nel campo conferma il dialog come il pulsante primario. Serve a mano perché i
    /// dialog dell'app non usano DefaultButton=Primary: WinUI colorerebbe il pulsante con
    /// l'accento di sistema invece che con lo stile dell'app.
    /// </summary>
    public static void SubmitDialogOnEnter(TextBox input, ContentDialog dialog, Action onSubmit)
    {
        input.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(input.Text))
                return;

            e.Handled = true;
            onSubmit();
            dialog.Hide();
        };
    }

    /// <summary>
    /// Chiude la tastiera virtuale dopo un Invio che non lascia il campo: altrimenti resta
    /// sopra i risultati appena filtrati.
    /// </summary>
    public static void Hide(Window window)
    {
        try
        {
            InputPaneInterop.GetForWindow(WindowNative.GetWindowHandle(window)).TryHide();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Chiusura della tastiera virtuale non riuscita: {ex.Message}");
        }
    }
}
