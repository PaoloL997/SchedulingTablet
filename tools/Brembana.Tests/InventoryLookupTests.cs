using Brembana;
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
    public void BuildCreateForm_prefills_uid_and_user_plant()
    {
        var service = new BridgeDataService(new BridgeApiClient());
        var form = service.BuildCreateForm(new ComponentScanResult
        {
            Success = true,
            ComponentCode = "NEW-1",
        }, "Valbrembo");

        Assert.Equal("NEW-1", form.Uid);
        Assert.Equal("Valbrembo", form.Plant);
    }

    [Fact]
    public void MapComponentTypeToTable_is_case_insensitive()
    {
        Assert.Equal("Testa", BridgeDataService.MapComponentTypeToTable("testa"));
        Assert.Equal("Mandrino", BridgeDataService.MapComponentTypeToTable("MANDRINO"));
        Assert.Null(BridgeDataService.MapComponentTypeToTable("unknown"));
    }
}
