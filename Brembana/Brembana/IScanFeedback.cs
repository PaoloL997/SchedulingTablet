namespace Brembana;

/// <summary>Come è finita la risoluzione di un codice contro la checklist dell'attività.</summary>
internal enum ScanResolution
{
    /// <summary>Pezzo previsto: confermato presente.</summary>
    Confirmed,

    /// <summary>Pezzo compatibile: ha preso il posto di quello previsto ed è confermato.</summary>
    Substituted,

    /// <summary>Caso chiuso con un messaggio (tipo esatto richiesto, offline, rifiuto del server, annullato).</summary>
    Handled,

    /// <summary>Non previsto e non in inventario: messaggio già mostrato.</summary>
    NotFound
}

/// <summary>
/// Dove far arrivare i messaggi della risoluzione codice: l'overlay fotocamera ha il suo
/// banner rosso, la pagina accessori una riga sotto la casella. La logica è la stessa.
/// </summary>
internal interface IScanFeedback
{
    /// <summary>Messaggio persistente, rosso, finché non arriva un nuovo input.</summary>
    void Problem(string message);

    /// <summary>Nasconde il messaggio di <see cref="Problem"/>.</summary>
    void Clear();

    /// <summary>Esito positivo, transitorio.</summary>
    void Success(string message);

    /// <summary>Avviso importante ma transitorio (es. attività rimaste scoperte).</summary>
    void Warning(string message);

    /// <summary>Sta per aprirsi un dialog: la fotocamera toglie l'overlay di elaborazione.</summary>
    void PrepareForDialog();
}
