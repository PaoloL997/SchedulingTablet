namespace Brembana;

/// <summary>
/// Una riga dell'elenco inventario, pronta per il rendering.
/// Immutabile: le righe non vengono mai mutate sul posto, ma ricostruite in blocco.
/// Deve restare <c>public</c> perché è il tipo di <c>x:DataType</c> nel DataTemplate.
/// </summary>
public sealed class InventoryListRow
{
    public required string Table { get; init; }
    public required string Uid { get; init; }

    /// <summary>Modello (Testa/Generatore), diametro (Mandrino/Tastatore) o passo (Maschera).</summary>
    public string? MeasureValue { get; init; }

    public string? Plant { get; init; }

    /// <summary>Note, tranne per Maschera dove è l'interasse.</summary>
    public string? ExtraValue { get; init; }

    public bool InManutenzione { get; init; }

    /// <summary>
    /// Falso quando il server segnala la risorsa occupata oggi
    /// (<see cref="InventarioDisponibilitaDto"/>). Decide il colore del pallino Disponibilità.
    /// </summary>
    public bool IsAvailableToday { get; init; } = true;

    // Testi già pronti, calcolati una sola volta in Project().
    public required string UidText { get; init; }
    public required string MeasureText { get; init; }
    public required string PlantText { get; init; }
    public required string ExtraText { get; init; }

    /// <summary>"In manutenzione" solo dove la manutenzione è supportata e attiva, altrimenti "".</summary>
    public required string MaintenanceText { get; init; }

    /// <summary>
    /// Tooltip del pallino, che non ha testo: quello del server (da quali attività è
    /// occupata e quando si libera), altrimenti "Libera"/"Occupata".
    /// </summary>
    public required string AvailabilityText { get; init; }

    /// <summary>Concatenazione dei campi ricercabili, precalcolata per filtrare senza riallocare.</summary>
    public required string SearchBlob { get; init; }

    /// <summary>
    /// Il componente come lo vedono policy, dialog e API: dai valori grezzi, mai dai
    /// testi di display che al posto del vuoto mostrano "—".
    /// </summary>
    public InventoryMatch ToMatch() =>
        new(Table, Uid, Plant, MeasureValue, BridgeDataService.MapTableToTipoKey(Table), InManutenzione);
}

/// <summary>Un tipo di componente nella barra dei tab, con la sua etichetta al plurale.</summary>
public sealed record InventoryTab(string Table, string Label);

/// <summary>
/// Le sei intestazioni di colonna per un tipo. <paramref name="Status"/> è "" dove la
/// manutenzione non si applica; <paramref name="Availability"/> vale per tutti i tipi.
/// </summary>
public sealed record InventoryColumns(
    string Uid,
    string Measure,
    string Plant,
    string Extra,
    string Status,
    string Availability);

/// <summary>
/// Logica pura dell'elenco inventario: proiezione, colonne e ricerca.
/// Nessuna dipendenza da Microsoft.UI.Xaml, così resta interamente testabile.
/// </summary>
public static class InventoryListing
{
    private const string Missing = "—";

    /// <summary>
    /// I tab nell'ordine di visualizzazione. Le chiavi sono i nomi tabella già usati
    /// da <see cref="BridgeDataService.InventoryTables"/>, da <see cref="InventoryMatch.Table"/>
    /// e dagli URL del bridge: nessun vocabolario parallelo da tenere allineato.
    /// </summary>
    public static IReadOnlyList<InventoryTab> Tabs { get; } = new[]
    {
        new InventoryTab("Generatore", UiStrings.InventoryTabGeneratori),
        new InventoryTab("Mandrino", UiStrings.InventoryTabMandrini),
        new InventoryTab("Maschera", UiStrings.InventoryTabMaschere),
        new InventoryTab("Tastatore", UiStrings.InventoryTabTastatori),
        new InventoryTab("Testa", UiStrings.InventoryTabTeste),
    };

    public static string LabelFor(string table) =>
        Tabs.FirstOrDefault(t => string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase))?.Label ?? table;

    /// <summary>Intestazioni di colonna per tipo, identiche a quelle della web app.</summary>
    public static InventoryColumns ColumnsFor(string table) => table switch
    {
        "Testa" or "Generatore" => new InventoryColumns(
            UiStrings.InventoryColUid,
            UiStrings.InventoryColModel,
            UiStrings.InventoryFieldPlant,
            UiStrings.InventoryColNotes,
            UiStrings.InventoryColMaintenance,
            UiStrings.InventoryColAvailability),

        "Maschera" => new InventoryColumns(
            UiStrings.InventoryColUid,
            UiStrings.InventoryColPitch,
            UiStrings.InventoryFieldPlant,
            UiStrings.InventoryColCenterDistance,
            "",
            UiStrings.InventoryColAvailability),

        // Mandrino, Tastatore e qualunque tipo futuro non ancora specializzato.
        _ => new InventoryColumns(
            UiStrings.InventoryColUid,
            UiStrings.InventoryColDiameter,
            UiStrings.InventoryFieldPlant,
            UiStrings.InventoryColNotes,
            "",
            UiStrings.InventoryColAvailability),
    };

    /// <summary>
    /// Appiattisce le righe di un solo tipo, ordinate per uid in ordine naturale:
    /// le cifre si confrontano come numeri, quindi C2 precede C10 e C100.
    /// Si discosta di proposito dall'ordinamento testuale del server
    /// (<c>.order_by("uid")</c>), che su codici numerati mette C10 e C100 prima di C2.
    /// </summary>
    public static List<InventoryListRow> Project(InventarioDto? inventario, string table)
    {
        if (inventario == null)
            return new List<InventoryListRow>();

        var rows = table switch
        {
            "Testa" => inventario.Testa.Select(x =>
                Build(table, x.Uid, x.Model, x.Plant, x.Notes, x.InManutenzione, x.Disponibilita)),
            "Generatore" => inventario.Generatore.Select(x =>
                Build(table, x.Uid, x.Model, x.Plant, x.Notes, x.InManutenzione, x.Disponibilita)),
            "Mandrino" => inventario.Mandrino.Select(x =>
                Build(table, x.Uid, x.Diameter, x.Plant, x.Notes, false, x.Disponibilita)),
            "Tastatore" => inventario.Tastatore.Select(x =>
                Build(table, x.Uid, x.Diameter, x.Plant, x.Notes, false, x.Disponibilita)),
            "Maschera" => inventario.Maschera.Select(x =>
                Build(table, x.Uid, x.Pitch, x.Plant, x.CenterDistance, false, x.Disponibilita)),
            _ => Enumerable.Empty<InventoryListRow>(),
        };

        return rows
            .OrderBy(r => r.Uid, NaturalUidComparer.Instance)
            .ToList();
    }

    private static InventoryListRow Build(
        string table,
        string uid,
        string? measure,
        string? plant,
        string? extra,
        bool inManutenzione,
        InventarioDisponibilitaDto? disponibilita)
    {
        // La regola "solo Testa e Generatore" non viene duplicata: si riusa la policy esistente.
        var showMaintenance = InventoryActionPolicy.SupportsMaintenance(table) && inManutenzione;

        // La disponibilità non si ricalcola qui: il dataset porta solo le attività dello
        // stabilimento dell'utente, il server invece le vede tutte e usa la regola della
        // web app. Senza descrittore (server non aggiornato) il pezzo risulta libero.
        var available = disponibilita?.Libera ?? true;
        var availabilityText = string.IsNullOrWhiteSpace(disponibilita?.Tooltip)
            ? (available ? UiStrings.InventoryRowFree : UiStrings.InventoryRowBusy)
            : disponibilita.Tooltip.Trim();

        return new InventoryListRow
        {
            Table = table,
            Uid = uid ?? "",
            MeasureValue = measure,
            Plant = plant,
            ExtraValue = extra,
            InManutenzione = inManutenzione,
            IsAvailableToday = available,
            UidText = Display(uid),
            MeasureText = Display(measure),
            PlantText = Display(plant),
            ExtraText = Display(extra),
            MaintenanceText = showMaintenance ? UiStrings.InventoryRowInMaintenance : "",
            AvailabilityText = availabilityText,
            SearchBlob = BuildSearchBlob(uid, measure, plant, extra),
        };
    }

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Missing : value.Trim();

    /// <summary>
    /// I quattro campi ricercabili. <c>MaintenanceText</c> è escluso di proposito:
    /// sulla web app la cella manutenzione è un pulsante senza testo e non partecipa
    /// alla ricerca su <c>textContent</c>. Stessa ragione per <c>AvailabilityText</c>,
    /// che è solo il tooltip di un pallino colorato.
    /// </summary>
    private static string BuildSearchBlob(string? uid, string? measure, string? plant, string? extra) =>
        string.Join(
            " ",
            new[] { uid, measure, plant, extra }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));

    public static bool Matches(InventoryListRow row, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        return row.SearchBlob.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static List<InventoryListRow> Filter(IReadOnlyList<InventoryListRow> rows, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return rows.ToList();
        return rows.Where(r => Matches(r, query)).ToList();
    }

    /// <summary>
    /// La riga con questa chiave, se è ancora tra quelle date. Serve a ripristinare la
    /// selezione dopo una ricostruzione dell'elenco, che crea istanze nuove.
    /// </summary>
    public static InventoryListRow? FindRow(IReadOnlyList<InventoryListRow> rows, string? table, string? uid)
    {
        if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(uid))
            return null;

        return rows.FirstOrDefault(r =>
            string.Equals(r.Table, table, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Uid, uid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Intestazione di colonna in maiuscolo. L'unità tra parentesi resta com'è:
    /// "DIAMETRO (MM)" si leggerebbe come megametri.
    /// </summary>
    public static string HeaderText(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "";

        var text = label.Trim();
        var italian = System.Globalization.CultureInfo.GetCultureInfo("it-IT");
        var unitStart = text.LastIndexOf(" (", StringComparison.Ordinal);

        return unitStart > 0 && text.EndsWith(')')
            ? text[..unitStart].ToUpper(italian) + text[unitStart..]
            : text.ToUpper(italian);
    }
}

/// <summary>
/// Confronto naturale dei codici: i blocchi di cifre valgono come numeri, il resto
/// come testo senza distinzione di maiuscole. Così C2 &lt; C10 &lt; C100 invece
/// dell'ordine puramente testuale C10 &lt; C100 &lt; C2.
/// </summary>
public sealed class NaturalUidComparer : IComparer<string>
{
    public static readonly NaturalUidComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= "";
        y ??= "";

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                // Gli zeri iniziali non contano sul valore: "007" vale quanto "7".
                var numX = x.AsSpan(startX, i - startX).TrimStart('0');
                var numY = y.AsSpan(startY, j - startY).TrimStart('0');

                if (numX.Length != numY.Length)
                    return numX.Length - numY.Length;

                var digits = numX.SequenceCompareTo(numY);
                if (digits != 0)
                    return digits;

                // Valore identico: decide la lunghezza originale, per un ordine stabile.
                var padding = (i - startX) - (j - startY);
                if (padding != 0)
                    return padding;
            }
            else
            {
                var a = char.ToUpperInvariant(x[i]);
                var b = char.ToUpperInvariant(y[j]);
                if (a != b)
                    return a.CompareTo(b);
                i++;
                j++;
            }
        }

        return (x.Length - i) - (y.Length - j);
    }
}
