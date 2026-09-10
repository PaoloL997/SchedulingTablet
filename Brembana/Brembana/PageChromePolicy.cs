namespace Brembana;

/// <summary>
/// Quali pagine mostrano le barre globali di MainWindow (brand, utente, stato, Aggiorna).
/// L'inventario ha un'intestazione sua con ricerca e avatar, e usa tutta l'altezza per l'elenco.
/// </summary>
internal static class PageChromePolicy
{
    public static bool ShowsGlobalChrome(Type? page) => page != typeof(InventoryHubPage);
}
