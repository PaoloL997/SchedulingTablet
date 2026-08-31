namespace Brembana;

internal static class UiStrings
{
    public const string AppTitle = "Brembana — Gestione Magazzino";
    public const string BrandCompany = "Brembana&Rolle";
    public const string BrandApp = "Gestione Magazzino";

    public const string SearchActivityPlaceholder = "Cerca attività";
    public const string ShowMoreActivities = "Mostra le altre attività";
    public const string RegisterOutgoing = "Registra Uscita";
    public const string RegisterIncoming = "Registra Rientro";
    public const string ConfirmRegisterTitleOutgoing = "Conferma uscita";
    public const string ConfirmRegisterTitleIncoming = "Conferma rientro";
    public const string ConfirmRegisterButton = "Conferma";
    public const string CancelButton = "Annulla";
    public const string CloseButton = "Chiudi";
    public const string BadgeOutgoing = "Uscita";
    public const string BadgeIncoming = "Rientro";

    public const string BtnPhoto = "Foto";
    public const string BtnRefresh = "Aggiorna";
    public const string CameraPermissionDenied = "Accesso alla fotocamera negato. Abilitalo in Impostazioni > Privacy > Fotocamera.";
    public const string CameraOpenError = "Impossibile aprire la fotocamera.";
    public const string CameraAccept = "Accetta";
    public const string CameraRetake = "Riprendi";
    public const string CameraBack = "Indietro";
    public const string BackToHome = "Torna alla home";
    public const string CameraComponentNotRecognized = "Componente non riconosciuto. Riprova con un'inquadratura più chiara.";
    public const string CameraScanError = "Errore durante l'analisi dell'immagine.";

    public static string FormatCameraAccessoryNotFound(string code) =>
        $"\"{code}\" non trovato tra gli accessori di questa attività.";

    public const string NoComponents = "Nessun componente assegnato a questa attività.";
    public const string NoActivities = "Nessuna attività disponibile.";
    public const string NoUserPlantActivities = "Profilo utente senza stabilimento. Impossibile elencare le attività.";
    public const string ActivityWrongPlant = "Questa attività appartiene a un altro stabilimento.";
    public const string StatusAvailable = "Disponibile";
    public const string StatusConnecting = "Connessione...";
    public const string StatusRefreshing = "Aggiornamento...";
    public const string NoNetworkTitle = "Connessione assente";
    public const string NoNetworkMessage = "Connessione di rete non disponibile. L'app riprenderà automaticamente quando la connessione torna attiva.";
    public const string BridgeDisconnectedTitle = "Connessione al server persa";
    public const string BridgeDisconnectedMessage = "La sessione con il server non è più disponibile. Premi il pulsante per ricaricare il dataset.";
    public const string BridgeDatasetLoadingTitle = "Dataset in caricamento";
    public const string BridgeDatasetLoadingMessage = "Il server sta caricando il dataset. Premi il pulsante per riprovare quando è pronto.";
    public const string BridgeDatasetWritingTitle = "Scrittura in corso";
    public const string BridgeDatasetWritingMessage = "Il server sta scrivendo i dati. Attendi qualche istante e premi il pulsante per ricaricare.";
    public const string BridgeDatasetErrorTitle = "Errore sul server";
    public const string BridgeDatasetErrorMessage = "Il server ha segnalato un errore sul dataset.";
    public const string BridgeReloadButton = "Ricarica dataset";
    public const string BridgeReloadingMessage = "Ricaricamento dataset in corso...";
    public const string StatusStarting = "Avvio...";
    public const string ServerError = "Errore server";
    public const string NotConfigured = "Non configurato";
    public const string Saving = "Salvataggio sul server...";
    public const string Processing = "Elaborazione...";

    public const string CardStatusOutgoing = "In uscita";
    public const string CardStatusReturned = "In magazzino";
    public const string CardStatusPending = "In attesa";
    public const string CardStatusSelected = "Da registrare";

    public static string FormatSpec(string label, string value, string unit)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return string.IsNullOrWhiteSpace(unit) ? $"{label}: {value}" : $"{label}: {value} {unit}";
    }

    public static string FormatComponentTypeName(string categoria, string tipoKey)
    {
        if (!string.IsNullOrWhiteSpace(categoria)) return categoria.Trim();
        if (!string.IsNullOrWhiteSpace(tipoKey))
        {
            var key = tipoKey.Trim();
            return char.ToUpperInvariant(key[0]) + key[1..];
        }
        return "Componente";
    }

    public static string FormatComponentMeasure(string specLabel, string specValue, string specUnit, string specDetail = "")
    {
        if (string.IsNullOrWhiteSpace(specValue)) return specDetail.Trim();
        var label = string.IsNullOrWhiteSpace(specLabel) ? "" : specLabel.Trim();
        var value = string.IsNullOrWhiteSpace(specUnit) ? specValue.Trim() : $"{specValue.Trim()} {specUnit.Trim()}";
        var measure = string.IsNullOrWhiteSpace(label) ? value : $"{label} {value}";
        if (!string.IsNullOrWhiteSpace(specDetail))
            measure = string.IsNullOrEmpty(measure) ? specDetail.Trim() : $"{measure} · {specDetail.Trim()}";
        return measure;
    }

    public static string FormatSessionSubtitle(string? plant, string? responsible)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(plant)) parts.Add(plant.Trim());
        if (!string.IsNullOrWhiteSpace(responsible)) parts.Add(responsible.Trim());
        return string.Join("  ·  ", parts);
    }

    public static string FormatMoreActivities(int count) => $"+{count} altre";

    public static string FormatAccessoryCount(int count) =>
        count == 1 ? "1 accessorio" : $"{count} accessori";

    public static string FormatLoadedActivities(int count) =>
        count == 1 ? "Caricata 1 attività" : $"Caricate {count} attività";

    public static string FormatRegisterConfirmation(int count, bool isOutgoing)
    {
        var op = isOutgoing ? "in uscita" : "in rientro";
        var items = count == 1 ? "1 accessorio selezionato" : $"{count} accessori selezionati";
        return $"Vuoi registrare {items} {op}?";
    }

    public static string CardTooltipOutgoing => "Tocca per selezionare in uscita";
    public static string CardTooltipIncoming => "Tocca per selezionare in rientro";

    public const string HomeStartFinishActivity = "Inizio / Fine attività";
    public const string HomeMoveComponent = "Sposta componente";
    public const string HomeAddComponent = "Aggiungi componente";
    public const string HomeMaintenanceComponent = "Manutenzione componente";

    public static string FormatInventoryNotFoundForMove(string uid) =>
        $"\"{uid}\" non risulta in inventario.";

    public const string NoUserPlant = "Profilo utente senza stabilimento. Impossibile aggiornare l'inventario.";
    public const string InventoryCreateTitle = "Aggiungi nuovo componente";
    public const string InventoryCreateMessage = "Il componente non risulta in inventario. Conferma i dati per aggiungerlo.";
    public const string InventoryMoveTitle = "Sposta componente";
    public const string InventoryFoundTitle = "Componente presente";
    public const string InventoryPickMatchTitle = "Scegli componente";
    public const string InventoryPickMatchMessage = "Sono stati trovati più componenti corrispondenti. Seleziona quello corretto.";
    public const string InventoryFieldType = "Tipo";
    public const string InventoryFieldUid = "UID";
    public const string InventoryFieldMeasure = "Valore";
    public const string InventoryFieldPlant = "Stabilimento";
    public const string InventoryMeasureModel = "Modello";
    public const string InventoryMeasureDiameter = "Diametro";
    public const string InventoryMeasurePitch = "Passo";
    public const string InventoryCreated = "Componente aggiunto all'inventario.";
    public const string InventoryMoved = "Componente spostato nel tuo stabilimento.";
    public const string InventoryBridgeRequired = "Connessione al server necessaria per aggiornare l'inventario.";
    public const string InventoryMaintenanceEnableTitle = "Metti in manutenzione";
    public const string InventoryMaintenanceDisableTitle = "Togli da manutenzione";
    public const string InventoryMaintenanceEnabled = "Componente messo in manutenzione.";
    public const string InventoryMaintenanceDisabled = "Componente tolto dalla manutenzione.";
    public const string InventoryMaintenanceUnsupported = "La manutenzione è disponibile solo per teste e generatori.";

    public static string FormatInventoryMaintenanceEnableMessage(string table, string uid) =>
        $"Mettere {table} {uid} in manutenzione?";

    public static string FormatInventoryMaintenanceDisableMessage(string table, string uid) =>
        $"Togliere {table} {uid} dalla manutenzione?";

    public static string FormatInventoryMoveMessage(string tipo, string uid, string? fromPlant, string toPlant) =>
        $"Spostare {tipo} {uid} da {fromPlant ?? "—"} a {toPlant}?";

    public static string FormatInventoryFoundMessage(string uid, string? plant, string table) =>
        $"\"{uid}\" ({table}) è già presente in inventario nello stabilimento {plant ?? "—"}.";
}
