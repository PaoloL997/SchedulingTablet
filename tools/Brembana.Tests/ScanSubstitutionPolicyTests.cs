using Brembana;
using Newtonsoft.Json;
using Xunit;

namespace Brembana.Tests;

public class ScanSubstitutionPolicyTests
{
    [Fact]
    public void Decide_keeps_not_found_when_the_code_is_not_in_inventory()
    {
        var decision = ScanSubstitutionPolicy.Decide(null);
        Assert.Equal(ScanSubstitutionKind.NotInInventory, decision.Kind);
        Assert.Null(decision.Match);
    }

    [Fact]
    public void Decide_requires_the_exact_piece_for_teste_and_generatori()
    {
        var testa = new InventoryMatch("Testa", "OV-T2", "Valbrembo", "TP60", "testa");
        var generatore = new InventoryMatch("Generatore", "OV-G2", "Valbrembo", "TX-40", "generatore");

        Assert.Equal(ScanSubstitutionKind.ExactTypeRequired, ScanSubstitutionPolicy.Decide(testa).Kind);
        Assert.Equal(ScanSubstitutionKind.ExactTypeRequired, ScanSubstitutionPolicy.Decide(generatore).Kind);
    }

    [Fact]
    public void Decide_lets_the_server_judge_accessories()
    {
        foreach (var (table, tipo) in new[] { ("Mandrino", "mandrino"), ("Tastatore", "tastatore"), ("Maschera", "MASCHERA") })
        {
            var match = new InventoryMatch(table, "X-1", "Valbrembo", "15,75", tipo);
            var decision = ScanSubstitutionPolicy.Decide(match);
            Assert.Equal(ScanSubstitutionKind.Candidate, decision.Kind);
            Assert.Same(match, decision.Match);
        }
    }

    [Fact]
    public void AssignedUidsOfTipo_lists_only_that_category()
    {
        var tracker = Tracker(("mandrino", "M-1"), ("mandrino", "M-2"), ("testa", "T-1"));

        Assert.Equal(new[] { "M-1", "M-2" }, ScanSubstitutionPolicy.AssignedUidsOfTipo(tracker, "mandrino"));
        Assert.Equal(new[] { "T-1" }, ScanSubstitutionPolicy.AssignedUidsOfTipo(tracker, "Testa"));
        Assert.Empty(ScanSubstitutionPolicy.AssignedUidsOfTipo(tracker, "maschera"));
    }

    [Fact]
    public void ApplySubstitution_replaces_the_expected_piece_and_selects_it()
    {
        var tracker = Tracker(("mandrino", "M-102"), ("testa", "T-1"));
        var scanned = new InventoryMatch("Mandrino", "M-117", "Valbrembo", "23,5", "mandrino");

        var comp = ScanSubstitutionPolicy.ApplySubstitution(tracker, "mandrino", "M-102", scanned);

        Assert.Equal("M-117", comp.ResourceName);
        Assert.Equal("23,5", comp.SpecValue);
        Assert.True(comp.IsSelectedForRegister);
        Assert.Equal(2, tracker.TrackedComponents.Count);
        Assert.DoesNotContain(tracker.TrackedComponents, c => c.ResourceName == "M-102");

        var assigned = tracker.MatchedActivities[0].AssignedResources;
        Assert.Contains("M-117", assigned);
        Assert.DoesNotContain("M-102", assigned);
        Assert.Contains("T-1", assigned);
    }

    [Fact]
    public void ApplySubstitution_adds_the_piece_when_nothing_was_expected()
    {
        var tracker = Tracker(("testa", "T-1"));
        var scanned = new InventoryMatch("Maschera", "MA-2", "Valbrembo", "25,4", "maschera");

        var comp = ScanSubstitutionPolicy.ApplySubstitution(tracker, "maschera", null, scanned);

        Assert.Equal(2, tracker.TrackedComponents.Count);
        Assert.Equal("Maschera", comp.Categoria);
        Assert.Equal("Passo", comp.SpecLabel);
        Assert.True(comp.IsSelectedForRegister);
        Assert.Contains("MA-2", tracker.MatchedActivities[0].AssignedResources);
    }

    [Fact]
    public void UncoveredActivities_reports_only_scoperta_rows()
    {
        var result = new AttivitaScanResult
        {
            Riassegnazioni = new List<ScanRiassegnazioneDto>
            {
                new() { ActivityId = "C200-1-Saldatura", Esito = "sostituita", A = "M-120" },
                new() { ActivityId = "C300-1-Montaggio", Esito = "scoperta" },
                new() { Label = "solo etichetta", Esito = "scoperta" },
            }
        };

        Assert.Equal(new[] { "C300-1-Montaggio", "solo etichetta" }, ScanSubstitutionPolicy.UncoveredActivities(result));
        Assert.Empty(ScanSubstitutionPolicy.UncoveredActivities(new AttivitaScanResult()));
    }

    [Fact]
    public void Scan_result_and_refusal_envelope_deserialize()
    {
        const string ok = """
        {"ok": true, "data": {"tipo":"mandrino","codice":"M117","atteso":"M102","esito":"sostituzione",
          "caso":3,"richiede_conferma":true,"applicata":false,
          "messaggio":"Stai usando M117 al posto di M102, va bene?","avvisi":["altro stabilimento"],
          "attivita_sottratte":[{"attivita_id":7,"label":"C200-1-Saldatura","data_inizio":"2026-06-02","data_fine":"2026-06-04"}],
          "riassegnazioni":[{"attivita_id":7,"activity_id":"C200-1-Saldatura","tipo":"mandrino","da":"M117","a":null,"esito":"scoperta"}]}}
        """;
        var envelope = JsonConvert.DeserializeObject<BridgeResponse<AttivitaScanResult>>(ok)!;
        Assert.True(envelope.Ok);
        var data = envelope.Data!;
        Assert.Equal(3, data.Caso);
        Assert.True(data.RichiedeConferma);
        Assert.Equal("M102", data.Atteso);
        Assert.Single(data.AttivitaSottratte!);
        Assert.Equal("scoperta", data.Riassegnazioni![0].Esito);
        Assert.Null(data.Riassegnazioni[0].A);

        const string refused = """{"ok": false, "error": "Mandrino «M130» (Ø 40 mm) fuori tolleranza: richiesto Ø 20–25 mm.", "motivo": "non_compatibile"}""";
        var error = JsonConvert.DeserializeObject<BridgeResponse<AttivitaScanResult>>(refused)!;
        Assert.False(error.Ok);
        Assert.Equal("non_compatibile", error.Motivo);
        Assert.Contains("fuori tolleranza", error.Error);
    }

    [Fact]
    public void Scan_request_omits_atteso_when_absent()
    {
        var json = JsonConvert.SerializeObject(new AttivitaScanRequest { Tipo = "mandrino", Codice = "M117", Conferma = true });
        Assert.DoesNotContain("atteso", json);
        Assert.Contains("\"conferma\":true", json);
    }

    private static JobOrderTracker Tracker(params (string Tipo, string Uid)[] components)
    {
        var tracker = new JobOrderTracker { JobOrderName = "C100-1-Saldatura" };
        tracker.MatchedActivities.Add(new TrackedActivity
        {
            OrmId = 1,
            ActivityName = "C100-1-Saldatura",
            AssignedResources = components.Select(c => c.Uid).ToList()
        });
        foreach (var (tipo, uid) in components)
        {
            tracker.TrackedComponents.Add(new TrackedComponent
            {
                ResourceName = uid,
                TipoKey = tipo,
                Categoria = char.ToUpperInvariant(tipo[0]) + tipo[1..]
            });
        }
        return tracker;
    }
}
