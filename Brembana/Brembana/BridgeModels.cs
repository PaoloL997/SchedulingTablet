using Newtonsoft.Json;

namespace Brembana;

public class BridgeResponse<T>
{
    [JsonProperty("ok")] public bool Ok { get; set; }
    [JsonProperty("data")] public T? Data { get; set; }
    [JsonProperty("error")] public string? Error { get; set; }
}

public class BridgeUserDto
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("username")] public string Username { get; set; } = "";
    [JsonProperty("stabilimento")] public string? Stabilimento { get; set; }
}

public class BridgeStatusDto
{
    [JsonProperty("status")] public string Status { get; set; } = "";
    [JsonProperty("detail")] public string? Detail { get; set; }
}

public class PublicStatusDto
{
    [JsonProperty("status")] public string Status { get; set; } = "";
    [JsonProperty("detail")] public string? Detail { get; set; }
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
    [JsonProperty("actual_start")] public string? ActualStart { get; set; }
    [JsonProperty("actual_finish")] public string? ActualFinish { get; set; }
    [JsonProperty("status_pills")] public List<StatusPillDto>? StatusPills { get; set; }

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

public class InventarioTestaDto
{
    [JsonProperty("uid")] public string Uid { get; set; } = "";
    [JsonProperty("model")] public string? Model { get; set; }
    [JsonProperty("plant")] public string? Plant { get; set; }
    [JsonProperty("notes")] public string? Notes { get; set; }
    [JsonProperty("in_manutenzione")] public bool InManutenzione { get; set; }
}

public class InventarioMandrinoDto
{
    [JsonProperty("uid")] public string Uid { get; set; } = "";
    [JsonProperty("diameter")] public string? Diameter { get; set; }
    [JsonProperty("plant")] public string? Plant { get; set; }
    [JsonProperty("notes")] public string? Notes { get; set; }
}

public class InventarioTastatoreDto
{
    [JsonProperty("uid")] public string Uid { get; set; } = "";
    [JsonProperty("diameter")] public string? Diameter { get; set; }
    [JsonProperty("plant")] public string? Plant { get; set; }
    [JsonProperty("notes")] public string? Notes { get; set; }
}

public class InventarioMascheraDto
{
    [JsonProperty("uid")] public string Uid { get; set; } = "";
    [JsonProperty("pitch")] public string? Pitch { get; set; }
    [JsonProperty("center_distance")] public string? CenterDistance { get; set; }
    [JsonProperty("plant")] public string? Plant { get; set; }
}

public class InventarioGeneratoreDto
{
    [JsonProperty("uid")] public string Uid { get; set; } = "";
    [JsonProperty("model")] public string? Model { get; set; }
    [JsonProperty("plant")] public string? Plant { get; set; }
    [JsonProperty("notes")] public string? Notes { get; set; }
    [JsonProperty("in_manutenzione")] public bool InManutenzione { get; set; }
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

public class BridgeApiException : Exception
{
    public int StatusCode { get; }

    public BridgeApiException(string message, int statusCode = 0) : base(message)
    {
        StatusCode = statusCode;
    }
}
