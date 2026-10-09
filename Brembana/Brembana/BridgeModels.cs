using Newtonsoft.Json;

namespace Brembana;

public class BridgeResponse<T>
{
    [JsonProperty("ok")] public bool Ok { get; set; }
    [JsonProperty("data")] public T? Data { get; set; }
    [JsonProperty("error")] public string? Error { get; set; }

    /// <summary>Codice macchina del rifiuto (es. <c>non_compatibile</c>, <c>occupata</c>).</summary>
    [JsonProperty("motivo")] public string? Motivo { get; set; }
}

public class BridgeUserDto
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("username")] public string Username { get; set; } = "";
    [JsonProperty("stabilimento")] public string? Stabilimento { get; set; }
}

public class StatusPillDto
{
    [JsonProperty("key")] public string Key { get; set; } = "";
    [JsonProperty("label")] public string Label { get; set; } = "";
}

public class AttivitaDto
{
    [JsonProperty("orm_id")] public int OrmId { get; set; }
    [JsonProperty("activity_id")] public string ActivityId { get; set; } = "";
    [JsonProperty("commessa")] public string Commessa { get; set; } = "";
    [JsonProperty("item")] public string Item { get; set; } = "";
    [JsonProperty("activity")] public string Activity { get; set; } = "";
    [JsonProperty("plant")] public string? Plant { get; set; }
    [JsonProperty("responsible")] public string? Responsible { get; set; }
    [JsonProperty("resources")] public string? Resources { get; set; }
    [JsonProperty("resources_by_tipo")] public Dictionary<string, List<string>>? ResourcesByTipo { get; set; }

    /// <summary>Inizio pianificato (data di preventivo): ordina le attività in lista.</summary>
    [JsonProperty("start")] public string? Start { get; set; }
    [JsonProperty("actual_start")] public string? ActualStart { get; set; }
    [JsonProperty("actual_finish")] public string? ActualFinish { get; set; }
    [JsonProperty("status_pills")] public List<StatusPillDto>? StatusPills { get; set; }

    // Requisiti di misura della pianificazione: guidano la scelta degli accessori.
    [JsonProperty("modello_testa")] public string? ModelloTesta { get; set; }
    [JsonProperty("bore_min")] public double? BoreMin { get; set; }
    [JsonProperty("bore_max")] public double? BoreMax { get; set; }
    [JsonProperty("bore_mid")] public double? BoreMid { get; set; }
    [JsonProperty("tube_pitch")] public double? TubePitch { get; set; }

    public override string ToString() => ActivityId;
}

public class DatasetDto
{
    [JsonProperty("attivita")] public List<AttivitaDto> Attivita { get; set; } = new();
    [JsonProperty("inventario")] public InventarioDto Inventario { get; set; } = new();
}

public class InventarioDto
{
    [JsonProperty("Testa")] public List<InventarioTestaDto> Testa { get; set; } = new();
    [JsonProperty("Mandrino")] public List<InventarioMandrinoDto> Mandrino { get; set; } = new();
    [JsonProperty("Tastatore")] public List<InventarioTastatoreDto> Tastatore { get; set; } = new();
    [JsonProperty("Maschera")] public List<InventarioMascheraDto> Maschera { get; set; } = new();
    [JsonProperty("Generatore")] public List<InventarioGeneratoreDto> Generatore { get; set; } = new();
}

/// <summary>
/// Disponibilità odierna di un componente, calcolata dal server con la stessa regola
/// della colonna Disponibilità della web app: occupato se oggi cade nella finestra di
/// un'attività (pianificata o in corso) di qualunque stabilimento.
/// </summary>
public class InventarioDisponibilitaDto
{
    [JsonProperty("stato")] public string? Stato { get; set; }
    [JsonProperty("libera")] public bool Libera { get; set; } = true;

    /// <summary>Da quali attività è occupato e quando si libera, o il prossimo impegno.</summary>
    [JsonProperty("tooltip")] public string? Tooltip { get; set; }
}

public class InventarioTestaDto
{
    [JsonProperty("uid")] public string Uid { get; set; } = "";
    [JsonProperty("model")] public string? Model { get; set; }
    [JsonProperty("plant")] public string? Plant { get; set; }
    [JsonProperty("notes")] public string? Notes { get; set; }
    [JsonProperty("in_manutenzione")] public bool InManutenzione { get; set; }
    [JsonProperty("disponibilita")] public InventarioDisponibilitaDto? Disponibilita { get; set; }
}

public class InventarioMandrinoDto
{
    [JsonProperty("uid")] public string Uid { get; set; } = "";
    [JsonProperty("diameter")] public string? Diameter { get; set; }
    [JsonProperty("plant")] public string? Plant { get; set; }
    [JsonProperty("notes")] public string? Notes { get; set; }
    [JsonProperty("disponibilita")] public InventarioDisponibilitaDto? Disponibilita { get; set; }
}

public class InventarioTastatoreDto
{
    [JsonProperty("uid")] public string Uid { get; set; } = "";
    [JsonProperty("diameter")] public string? Diameter { get; set; }
    [JsonProperty("plant")] public string? Plant { get; set; }
    [JsonProperty("notes")] public string? Notes { get; set; }
    [JsonProperty("disponibilita")] public InventarioDisponibilitaDto? Disponibilita { get; set; }
}

public class InventarioMascheraDto
{
    [JsonProperty("uid")] public string Uid { get; set; } = "";
    [JsonProperty("pitch")] public string? Pitch { get; set; }
    [JsonProperty("center_distance")] public string? CenterDistance { get; set; }
    [JsonProperty("plant")] public string? Plant { get; set; }
    [JsonProperty("disponibilita")] public InventarioDisponibilitaDto? Disponibilita { get; set; }
}

public class InventarioGeneratoreDto
{
    [JsonProperty("uid")] public string Uid { get; set; } = "";
    [JsonProperty("model")] public string? Model { get; set; }
    [JsonProperty("plant")] public string? Plant { get; set; }
    [JsonProperty("notes")] public string? Notes { get; set; }
    [JsonProperty("in_manutenzione")] public bool InManutenzione { get; set; }
    [JsonProperty("disponibilita")] public InventarioDisponibilitaDto? Disponibilita { get; set; }
}

public class AttivitaUpdateRequest
{
    [JsonProperty("actual_start", NullValueHandling = NullValueHandling.Ignore)]
    public string? ActualStart { get; set; }

    [JsonProperty("actual_finish", NullValueHandling = NullValueHandling.Ignore)]
    public string? ActualFinish { get; set; }
}

public class InventarioMutationResult
{
    [JsonProperty("uid")] public string? Uid { get; set; }
}

/// <summary>Body di <c>POST /api/bridge/attivita/{pk}/scan/</c>.</summary>
public class AttivitaScanRequest
{
    [JsonProperty("tipo")] public string Tipo { get; set; } = "";
    [JsonProperty("codice")] public string Codice { get; set; } = "";

    [JsonProperty("atteso", NullValueHandling = NullValueHandling.Ignore)]
    public string? Atteso { get; set; }

    [JsonProperty("conferma")] public bool Conferma { get; set; }
}

public class ScanAttivitaSottrattaDto
{
    [JsonProperty("attivita_id")] public int AttivitaId { get; set; }
    [JsonProperty("label")] public string? Label { get; set; }
    [JsonProperty("data_inizio")] public string? DataInizio { get; set; }
    [JsonProperty("data_fine")] public string? DataFine { get; set; }
}

public class ScanRiassegnazioneDto
{
    [JsonProperty("attivita_id")] public int AttivitaId { get; set; }
    [JsonProperty("activity_id")] public string? ActivityId { get; set; }
    [JsonProperty("label")] public string? Label { get; set; }
    [JsonProperty("tipo")] public string? Tipo { get; set; }
    [JsonProperty("da")] public string? Da { get; set; }
    [JsonProperty("a")] public string? A { get; set; }

    /// <summary><c>sostituita</c> o <c>scoperta</c>.</summary>
    [JsonProperty("esito")] public string? Esito { get; set; }
}

/// <summary>Esito della scansione di un componente contro la pianificazione.</summary>
public class AttivitaScanResult
{
    [JsonProperty("tipo")] public string Tipo { get; set; } = "";
    [JsonProperty("codice")] public string Codice { get; set; } = "";
    [JsonProperty("atteso")] public string? Atteso { get; set; }

    /// <summary><c>corrispondente</c> o <c>sostituzione</c>.</summary>
    [JsonProperty("esito")] public string Esito { get; set; } = "";

    /// <summary>1 pezzo libero, 3 pezzo assegnato a un'attività pianificata sovrapposta.</summary>
    [JsonProperty("caso")] public int? Caso { get; set; }

    [JsonProperty("richiede_conferma")] public bool RichiedeConferma { get; set; }
    [JsonProperty("applicata")] public bool Applicata { get; set; }
    [JsonProperty("messaggio")] public string? Messaggio { get; set; }
    [JsonProperty("avvisi")] public List<string>? Avvisi { get; set; }
    [JsonProperty("attivita_sottratte")] public List<ScanAttivitaSottrattaDto>? AttivitaSottratte { get; set; }
    [JsonProperty("riassegnazioni")] public List<ScanRiassegnazioneDto>? Riassegnazioni { get; set; }
    [JsonProperty("resources_by_tipo")] public Dictionary<string, List<string>>? ResourcesByTipo { get; set; }
}

public class BridgeApiException : Exception
{
    public int StatusCode { get; }

    /// <summary>Codice macchina del rifiuto, quando il server lo fornisce.</summary>
    public string? Motivo { get; }

    public BridgeApiException(string message, int statusCode = 0, string? motivo = null) : base(message)
    {
        StatusCode = statusCode;
        Motivo = motivo;
    }
}
