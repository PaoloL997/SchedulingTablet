using Brembana;
using Newtonsoft.Json;
using Xunit;

namespace Brembana.Tests;

public class InventoryLookupTests
{
    [Fact]
    public void FindInventoryMatches_returns_exact_uid_across_tables()
    {
        var service = new BridgeDataService(new BridgeApiClient());
        service.LoadDatasetForTests(inventario: new InventarioDto
        {
            Testa = { new InventarioTestaDto { Uid = "OV-T1", Plant = "Schio", Model = "TP60", InManutenzione = true } },
            Mandrino = { new InventarioMandrinoDto { Uid = "M-1", Plant = "Valbrembo", Diameter = "15,35" } },
        });

        var testa = service.FindInventoryMatches("OV-T1");
        Assert.Single(testa);
        Assert.Equal("Testa", testa[0].Table);
        Assert.Equal("Schio", testa[0].Plant);
        Assert.True(testa[0].InManutenzione);

        var mandrino = service.FindInventoryMatches("M-1");
        Assert.Single(mandrino);
        Assert.False(mandrino[0].InManutenzione);

        Assert.Empty(service.FindInventoryMatches("missing"));
        Assert.Empty(service.FindInventoryMatches(""));
    }

    [Fact]
    public void BuildCreateRequestBody_maps_measure_by_table()
    {
        var service = new BridgeDataService(new BridgeApiClient());

        var testa = service.BuildCreateRequestBody(new InventoryCreateForm
        {
            Table = "Testa",
            Uid = "OV-T9",
            Plant = "Valbrembo",
            MeasureValue = "TP60"
        });
        Assert.Equal("OV-T9", testa["uid"]);
        Assert.Equal("Valbrembo", testa["plant"]);
        Assert.Equal("TP60", testa["model"]);

        var mandrino = service.BuildCreateRequestBody(new InventoryCreateForm
        {
            Table = "Mandrino",
            Uid = "M-9",
            Plant = "Valbrembo",
            MeasureValue = "12,00"
        });
        Assert.Equal("12,00", mandrino["diameter"]);
        Assert.False(mandrino.ContainsKey("model"));
    }

    [Fact]
    public void BuildCreateForm_preselects_the_active_table_and_the_user_plant()
    {
        var form = BridgeDataService.BuildCreateForm(" mandrino ", "Valbrembo");

        // Il tipo arriva nella forma canonica della tabella, come lo vuole il ComboBox.
        Assert.Equal("Mandrino", form.Table);
        Assert.Equal("Valbrembo", form.Plant);

        // Il codice lo scrive l'operatore: niente più precompilazione dalla fotocamera.
        Assert.Equal("", form.Uid);
    }

    [Fact]
    public void BuildCreateForm_leaves_the_type_empty_for_an_unknown_table()
    {
        Assert.Equal("", BridgeDataService.BuildCreateForm("Pinza", "Valbrembo").Table);
        Assert.Equal("", BridgeDataService.BuildCreateForm(null, "Valbrembo").Table);
    }

    [Fact]
    public void FindExistingUid_detects_duplicates_across_tables()
    {
        var service = new BridgeDataService(new BridgeApiClient());
        service.LoadDatasetForTests(inventario: new InventarioDto
        {
            Mandrino = { new InventarioMandrinoDto { Uid = "C10", Plant = "Ricengo", Diameter = "10.8" } },
            Testa = { new InventarioTestaDto { Uid = "H01", Plant = "Valbrembo", Model = "TP60" } },
        });

        // Il server accetterebbe un Tastatore "C10" perché l'uid è chiave per tabella:
        // il controllo lato tablet deve vederlo comunque.
        var crossTable = service.FindExistingUid("C10", "Tastatore");
        Assert.NotNull(crossTable);
        Assert.Equal("Mandrino", crossTable!.Table);
        Assert.Equal("Ricengo", crossTable.Plant);

        Assert.NotNull(service.FindExistingUid("H01", "Testa"));
        Assert.Null(service.FindExistingUid("NUOVO-1", "Mandrino"));
    }

    [Fact]
    public void FindExistingUid_prefers_the_requested_table_and_ignores_blanks()
    {
        var service = new BridgeDataService(new BridgeApiClient());
        service.LoadDatasetForTests(inventario: new InventarioDto
        {
            Mandrino = { new InventarioMandrinoDto { Uid = "C10", Plant = "Ricengo" } },
            Tastatore = { new InventarioTastatoreDto { Uid = "C10", Plant = "Valbrembo" } },
        });

        // Con lo stesso uid in due tabelle si cita quella che l'operatore sta creando.
        Assert.Equal("Tastatore", service.FindExistingUid("C10", "Tastatore")!.Table);
        Assert.Equal("Mandrino", service.FindExistingUid("C10", "Mandrino")!.Table);
        Assert.NotNull(service.FindExistingUid("C10", null));

        // Spazi attorno al codice non devono far sfuggire il duplicato.
        Assert.NotNull(service.FindExistingUid("  C10  ", "Mandrino"));

        Assert.Null(service.FindExistingUid("", "Mandrino"));
        Assert.Null(service.FindExistingUid("   ", "Mandrino"));
        Assert.Null(service.FindExistingUid(null, "Mandrino"));
    }

    [Fact]
    public void Inventory_dtos_deserialize_the_notes_field()
    {
        const string payload = """
        {
          "Testa":      [{"uid":"T-1","model":"TP60","plant":"Schio","notes":"nota testa","in_manutenzione":true}],
          "Generatore": [{"uid":"G-1","model":"TX-40","plant":"Valbrembo","notes":"nota gen","in_manutenzione":false}],
          "Mandrino":   [{"uid":"M-1","diameter":"15,35","plant":"Valbrembo","notes":"nota mandrino"}],
          "Tastatore":  [{"uid":"A-1","diameter":"8,00","plant":"Ricengo","notes":"nota tastatore"}],
          "Maschera":   [{"uid":"S-1","pitch":"22,00","center_distance":"31,75","plant":"Valbrembo"}]
        }
        """;

        var inventario = JsonConvert.DeserializeObject<InventarioDto>(payload)!;

        Assert.Equal("nota testa", inventario.Testa[0].Notes);
        Assert.Equal("nota gen", inventario.Generatore[0].Notes);
        Assert.Equal("nota mandrino", inventario.Mandrino[0].Notes);
        Assert.Equal("nota tastatore", inventario.Tastatore[0].Notes);
        Assert.Equal("31,75", inventario.Maschera[0].CenterDistance);
    }

    [Fact]
    public void Inventory_dtos_tolerate_a_payload_without_notes()
    {
        // Compatibilità con build del bridge che non inviano ancora "notes".
        const string payload = """
        { "Testa": [{"uid":"T-1","model":"TP60","plant":"Schio","in_manutenzione":false}] }
        """;

        var inventario = JsonConvert.DeserializeObject<InventarioDto>(payload)!;

        Assert.Null(inventario.Testa[0].Notes);
        Assert.Equal("TP60", inventario.Testa[0].Model);
    }

    [Fact]
    public void MapComponentTypeToTable_is_case_insensitive()
    {
        Assert.Equal("Testa", BridgeDataService.MapComponentTypeToTable("testa"));
        Assert.Equal("Mandrino", BridgeDataService.MapComponentTypeToTable("MANDRINO"));
        Assert.Null(BridgeDataService.MapComponentTypeToTable("unknown"));
    }
}
