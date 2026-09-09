using Microsoft.UI.Xaml.Media;

namespace Brembana;

/// <summary>
/// Colore del pallino della colonna Disponibilità: verde libera, rosso occupata.
/// Sta qui e non su <see cref="InventoryListRow"/> perché il modello dell'elenco resta
/// senza dipendenze da XAML; il DataTemplate lo richiama come function binding di x:Bind.
/// </summary>
public static class InventoryAvailabilityBrushes
{
    public static Brush For(bool available) =>
        ThemeHelper.Brush(available ? "BrembanaGreenBrush" : "BrembanaRedBrush");
}
