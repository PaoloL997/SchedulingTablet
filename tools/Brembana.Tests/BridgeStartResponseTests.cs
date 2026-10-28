using Brembana;
using Newtonsoft.Json;
using Xunit;

namespace Brembana.Tests;

/// <summary>
/// Le risposte del server all'avvio (<c>PUT /api/bridge/attivita/&lt;pk&gt;/update/</c>)
/// e alla scansione: avviso nel 200, rifiuti nel 409 con il testo sempre in <c>error</c>.
/// </summary>
public class BridgeStartResponseTests
{
    [Fact]
    public void Start_request_carries_only_the_date_of_the_direction()
    {
        var now = new DateTime(2026, 10, 28, 15, 30, 0);

        var start = JsonConvert.SerializeObject(BridgeDataService.BuildActivityDateUpdate(isOutgoing: true, now));
        Assert.Equal("""{"actual_start":"2026-10-28"}""", start);

        var finish = JsonConvert.SerializeObject(BridgeDataService.BuildActivityDateUpdate(isOutgoing: false, now));
        Assert.Equal("""{"actual_finish":"2026-10-28"}""", finish);
    }

    [Fact]
    public void Start_ok_with_warning_is_not_a_refusal()
    {
        const string json = """
        {"ok": true, "data": {"orm_id": 7},
         "warning_message": "MND-102 era destinato a C200-1-Saldatura: riassegnato.",
         "warnings": [{"tipo": "mandrino", "risorsa_id": "MND-102", "attivita_label": "C200-1-Saldatura",
                       "data_inizio": "2026-10-29", "data_fine": "2026-10-31"}]}
        """;
        var envelope = JsonConvert.DeserializeObject<BridgeResponse<object>>(json)!;

        Assert.True(envelope.Ok);
        Assert.False(envelope.HasConflicts);
        Assert.False(envelope.HasMissingResources);
        Assert.Contains("riassegnato", envelope.WarningMessage);
        var warning = Assert.Single(envelope.Warnings!);
        Assert.Equal("MND-102", warning.RisorsaId);
        Assert.Equal("C200-1-Saldatura", warning.AttivitaLabel);

        var result = new AttivitaUpdateResult { WarningMessage = envelope.WarningMessage, Warnings = envelope.Warnings! };
        Assert.True(result.HasWarning);
        var detail = Assert.Single(UiStrings.FormatActivityStartWarningDetails(result.Warnings));
        Assert.Equal("mandrino MND-102 era previsto per C200-1-Saldatura (2026-10-29 – 2026-10-31)", detail);
    }

    [Fact]
    public void Start_ok_without_warning_has_nothing_to_show()
    {
        var envelope = JsonConvert.DeserializeObject<BridgeResponse<object>>("""{"ok": true, "data": null}""")!;
        var result = new AttivitaUpdateResult { WarningMessage = envelope.WarningMessage, Warnings = envelope.Warnings ?? new List<BridgeWarningDto>() };
        Assert.False(result.HasWarning);
    }

    [Fact]
    public void Start_refused_for_conflicts_asks_for_another_piece()
    {
        const string json = """
        {"ok": false,
         "error": "Impossibile avviare: MND-102 è in uso in C300-2-Montaggio (dal 2026-10-27).\nTastatore TST-5 è in uso in C300-2-Montaggio.",
         "conflicts": [{"tipo": "mandrino", "risorsa_id": "MND-102", "attivita_label": "C300-2-Montaggio"},
                       {"tipo": "tastatore", "risorsa_id": "TST-5", "attivita_label": "C300-2-Montaggio"}]}
        """;
        var envelope = JsonConvert.DeserializeObject<BridgeResponse<object>>(json);
        var ex = BridgeApiClient.BuildFailure(envelope, 409, json);

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(BridgeRefusalKind.Conflicts, ex.Refusal);
        Assert.StartsWith("Impossibile avviare: MND-102", ex.Message);
        Assert.Contains("\n", ex.Message);

        var shown = UiStrings.FormatActivityStartRefused(ex);
        Assert.StartsWith(ex.Message, shown);
        Assert.EndsWith("\n" + UiStrings.ScanUseAnotherPiece, shown);
    }

    [Fact]
    public void Start_refused_for_missing_resources_shows_the_error_only()
    {
        const string json = """
        {"ok": false, "error": "Kit minimo incompleto: manca una testa.", "missing_resources": ["testa"]}
        """;
        var ex = BridgeApiClient.BuildFailure(JsonConvert.DeserializeObject<BridgeResponse<object>>(json), 409, json);

        Assert.Equal(BridgeRefusalKind.MissingResources, ex.Refusal);
        Assert.Equal("Kit minimo incompleto: manca una testa.", UiStrings.FormatActivityStartRefused(ex));
    }

    [Fact]
    public void Empty_conflicts_and_missing_resources_are_a_generic_refusal()
    {
        const string json = """{"ok": false, "error": "Attività già avviata.", "conflicts": [], "missing_resources": null}""";
        var ex = BridgeApiClient.BuildFailure(JsonConvert.DeserializeObject<BridgeResponse<object>>(json), 409, json);

        Assert.Equal(BridgeRefusalKind.Generic, ex.Refusal);
        Assert.Equal("Attività già avviata.", ex.Message);
    }

    [Fact]
    public void Refusal_text_comes_from_error_not_message()
    {
        const string json = """{"ok": false, "error": "Testo per l'operatore", "message": "testo tecnico"}""";
        var ex = BridgeApiClient.BuildFailure(JsonConvert.DeserializeObject<BridgeResponse<object>>(json), 409, json);
        Assert.Equal("Testo per l'operatore", ex.Message);
    }

    [Fact]
    public void Scan_refused_as_occupata_asks_for_another_piece_even_for_the_planned_one()
    {
        const string json = """
        {"ok": false, "error": "Mandrino «MND-102» è in uso in C300-2-Montaggio (in corso).", "motivo": "occupata"}
        """;
        var ex = BridgeApiClient.BuildFailure(JsonConvert.DeserializeObject<BridgeResponse<AttivitaScanResult>>(json), 409, json);

        Assert.Equal("occupata", ex.Motivo);
        Assert.True(ScanSubstitutionPolicy.IsOccupata(ex.Motivo));
        Assert.Equal(
            "Mandrino «MND-102» è in uso in C300-2-Montaggio (in corso). " + UiStrings.ScanUseAnotherPiece,
            UiStrings.FormatScanRefused(ex.Message, ex.Motivo));
    }

    [Fact]
    public void Other_scan_refusals_show_the_error_as_is()
    {
        Assert.Equal("fuori tolleranza", UiStrings.FormatScanRefused("fuori tolleranza", "non_compatibile"));
        Assert.Equal("senza motivo", UiStrings.FormatScanRefused("senza motivo", null));
        Assert.False(ScanSubstitutionPolicy.IsOccupata(null));
    }

    [Fact]
    public void Lost_session_without_error_text_uses_the_app_message()
    {
        var ex = BridgeApiClient.BuildFailure<object>(null, 302, "");
        Assert.Equal(UiStrings.BridgeSessionExpired, ex.Message);
        Assert.Equal(BridgeRefusalKind.Generic, ex.Refusal);
    }
}
