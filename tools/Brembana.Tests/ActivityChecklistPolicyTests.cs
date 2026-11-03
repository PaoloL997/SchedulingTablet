using Brembana;
using Xunit;

namespace Brembana.Tests;

public class ActivityChecklistPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 8, 10, 30, 0);

    [Fact]
    public void Progress_counts_confirmed_pieces_and_is_never_complete_when_empty()
    {
        Assert.Equal(new ChecklistProgress(0, 0), ActivityChecklistPolicy.Progress(Array.Empty<TrackedComponent>()));
        Assert.False(new ChecklistProgress(0, 0).IsComplete);

        var comps = new[] { Comp("M-1", confirmed: true), Comp("T-1"), Comp("MA-1", confirmed: true) };
        var progress = ActivityChecklistPolicy.Progress(comps);
        Assert.Equal(new ChecklistProgress(2, 3), progress);
        Assert.False(progress.IsComplete);

        comps[1].IsSelectedForRegister = true;
        Assert.True(ActivityChecklistPolicy.Progress(comps).IsComplete);
    }

    [Fact]
    public void ConfirmAll_and_ClearAll_toggle_every_piece()
    {
        var comps = new[] { Comp("M-1"), Comp("T-1", confirmed: true) };

        ActivityChecklistPolicy.ConfirmAll(comps);
        Assert.All(comps, c => Assert.True(c.IsSelectedForRegister));

        ActivityChecklistPolicy.ClearAll(comps);
        Assert.All(comps, c => Assert.False(c.IsSelectedForRegister));
    }

    [Fact]
    public void ApplyStart_sends_every_piece_out_and_clears_the_checklist()
    {
        var comps = new[] { Comp("M-1", confirmed: true), Comp("T-1", confirmed: true) };
        comps[1].IncomingScanTime = Now.AddDays(-3);

        ActivityChecklistPolicy.ApplyStart(comps, Now);

        foreach (var comp in comps)
        {
            Assert.True(comp.IsOut);
            Assert.Equal(Now, comp.OutgoingScanTime);
            Assert.Null(comp.IncomingScanTime);
            Assert.False(comp.IsSelectedForRegister);
        }
    }

    [Fact]
    public void ApplyClose_brings_every_piece_back_and_keeps_the_outgoing_time()
    {
        var left = Now.AddHours(-5);
        var comps = new[]
        {
            Comp("M-1", confirmed: true, isOut: true, outAt: left),
            Comp("T-1", confirmed: true, isOut: true)
        };

        ActivityChecklistPolicy.ApplyClose(comps, Now);

        Assert.All(comps, c => Assert.False(c.IsOut));
        Assert.All(comps, c => Assert.Equal(Now, c.IncomingScanTime));
        Assert.All(comps, c => Assert.False(c.IsSelectedForRegister));
        Assert.Equal(left, comps[0].OutgoingScanTime);
        Assert.Equal(Now, comps[1].OutgoingScanTime);
    }

    [Fact]
    public void ApplyStart_and_ApplyClose_refuse_an_incomplete_checklist()
    {
        var comps = new[] { Comp("M-1", confirmed: true), Comp("T-1") };

        Assert.Throws<InvalidOperationException>(() => ActivityChecklistPolicy.ApplyStart(comps, Now));
        Assert.Throws<InvalidOperationException>(() => ActivityChecklistPolicy.ApplyClose(comps, Now));
        Assert.Throws<InvalidOperationException>(() => ActivityChecklistPolicy.ApplyStart(Array.Empty<TrackedComponent>(), Now));
        Assert.False(comps[0].IsOut);
    }

    [Fact]
    public void FindPlanned_prefers_the_exact_uid_over_containment()
    {
        var tracker = Tracker(("mandrino", "M-10"), ("mandrino", "M-1"));

        Assert.Equal("M-1", ActivityChecklistPolicy.FindPlanned(tracker, "m-1")!.ResourceName);
        Assert.Equal("M-10", ActivityChecklistPolicy.FindPlanned(tracker, " M-10 ")!.ResourceName);
    }

    [Fact]
    public void FindPlanned_matches_modello_and_partial_reads_but_not_short_fragments()
    {
        var tracker = Tracker(("testa", "OV-T2"), ("maschera", "MA-25"));
        tracker.TrackedComponents[0].Modello = "TP60";

        Assert.Equal("OV-T2", ActivityChecklistPolicy.FindPlanned(tracker, "TP60")!.ResourceName);
        Assert.Equal("MA-25", ActivityChecklistPolicy.FindPlanned(tracker, "MA-2")!.ResourceName);
        Assert.Equal("MA-25", ActivityChecklistPolicy.FindPlanned(tracker, "XMA-25X")!.ResourceName);
        Assert.Null(ActivityChecklistPolicy.FindPlanned(tracker, "MA"));
        Assert.Null(ActivityChecklistPolicy.FindPlanned(tracker, ""));
        Assert.Null(ActivityChecklistPolicy.FindPlanned(tracker, "ZZZ"));
    }

    [Fact]
    public void CandidateCodes_keeps_order_dedupes_and_drops_the_placeholder()
    {
        var scan = new ComponentScanResult
        {
            ComponentCode = "NO_COMPONENT_FOUND",
            AllCodes = new List<string> { " m-117 ", "M-117", "", "T-1" }
        };

        Assert.Equal(new[] { "M-117", "T-1" }, ActivityChecklistPolicy.CandidateCodes("M-117", scan));
        Assert.Equal(new[] { "m-117", "T-1" }, ActivityChecklistPolicy.CandidateCodes(null, scan));
        Assert.Empty(ActivityChecklistPolicy.CandidateCodes("  ", null));
    }

    [Fact]
    public void Substitution_is_allowed_only_on_the_way_out()
    {
        Assert.True(ActivityChecklistPolicy.AllowsSubstitution(isOutgoing: true));
        Assert.False(ActivityChecklistPolicy.AllowsSubstitution(isOutgoing: false));
    }

    [Fact]
    public void Register_texts_follow_direction_and_progress()
    {
        Assert.Equal("Avvia attività (3/5)", UiStrings.FormatRegisterButton(true, new ChecklistProgress(3, 5)));
        Assert.Equal("Chiudi attività (5/5)", UiStrings.FormatRegisterButton(false, new ChecklistProgress(5, 5)));
        Assert.Equal("Presenti 0 di 2", UiStrings.FormatChecklistProgress(new ChecklistProgress(0, 2)));

        Assert.Equal("Tutti i 5 accessori sono usciti. Avviare l'attività?", UiStrings.FormatRegisterConfirmation(true, 5));
        Assert.Equal("Tutti i 2 accessori sono rientrati. Chiudere l'attività?", UiStrings.FormatRegisterConfirmation(false, 2));
        Assert.Equal("L'unico accessorio è uscito. Avviare l'attività?", UiStrings.FormatRegisterConfirmation(true, 1));
        Assert.Equal("L'unico accessorio è rientrato. Chiudere l'attività?", UiStrings.FormatRegisterConfirmation(false, 1));
    }

    [Fact]
    public void GroupByCategory_follows_the_shopping_list_order_and_sorts_uids()
    {
        var tracker = Tracker(("maschera", "MA-2"), ("mandrino", "M-9"), ("testa", "T-1"),
            ("mandrino", "M-10"), ("generatore", "G-1"), ("tastatore", "TS-1"), ("altro", "X-1"));

        var groups = ActivityChecklistPolicy.GroupByCategory(tracker.TrackedComponents);

        Assert.Equal(new[] { "testa", "generatore", "tastatore", "mandrino", "maschera", "altro" },
            groups.Select(g => g.TipoKey));
        Assert.Equal(new[] { "Testa", "Generatore", "Tastatore", "Mandrino", "Maschera", "Altro" },
            groups.Select(g => g.Label));
        Assert.Equal(new[] { "M-10", "M-9" }, groups[3].Components.Select(c => c.ResourceName));
        Assert.Empty(ActivityChecklistPolicy.GroupByCategory(Array.Empty<TrackedComponent>()));
    }

    [Fact]
    public void FormatRequirement_shows_measures_only_for_the_categories_that_have_them()
    {
        Assert.Equal("Ø min 20 · puntuale 22,5 · max 25,5 mm",
            ActivityChecklistPolicy.FormatRequirement("mandrino", 20, 22.5, 25.5, 30, "TP60"));
        Assert.Equal("Ø min 20 · max 25,5 mm",
            ActivityChecklistPolicy.FormatRequirement("Tastatore", 20, null, 25.5, null, null));
        Assert.Equal("Ø min 20 mm",
            ActivityChecklistPolicy.FormatRequirement("mandrino", 20, null, null, null, null));
        Assert.Equal("Passo 25,4 mm", ActivityChecklistPolicy.FormatRequirement("maschera", 20, 22, 25, 25.4, null));
        Assert.Equal("Modello TP60", ActivityChecklistPolicy.FormatRequirement("testa", 20, 22, 25, 25.4, " TP60 "));
        Assert.Equal("", ActivityChecklistPolicy.FormatRequirement("generatore", 20, 22, 25, 25.4, "TP60"));
        Assert.Equal("", ActivityChecklistPolicy.FormatRequirement("maschera", null, null, null, null, null));
        Assert.Equal("", ActivityChecklistPolicy.FormatRequirement("mandrino", null, null, null, 25.4, "TP60"));
        Assert.Equal("", ActivityChecklistPolicy.FormatRequirement(null, 20, 22, 25, 25.4, "TP60"));
    }

    [Fact]
    public void MissingPlanned_lists_every_unconfirmed_piece_whatever_the_order()
    {
        var tracker = Tracker(("mandrino", "M-1"), ("mandrino", "M-2"), ("mandrino", "M-3"));
        var group = ActivityChecklistPolicy.GroupByCategory(tracker.TrackedComponents)[0];

        Assert.Equal(new[] { "M-1", "M-2", "M-3" }, ActivityChecklistPolicy.MissingPlanned(group).Select(c => c.ResourceName));

        group.Components[1].IsSelectedForRegister = true;
        Assert.Equal(new[] { "M-1", "M-3" }, ActivityChecklistPolicy.MissingPlanned(group).Select(c => c.ResourceName));
        Assert.Equal(2, ActivityChecklistPolicy.FreeSlots(group));

        group.Components[0].IsSelectedForRegister = true;
        group.Components[2].IsSelectedForRegister = true;
        Assert.Empty(ActivityChecklistPolicy.MissingPlanned(group));
        Assert.True(ActivityChecklistPolicy.ProgressOf(group).IsComplete);
    }

    [Fact]
    public void A_substitute_fills_a_slot_without_adding_one()
    {
        var tracker = Tracker(("mandrino", "C102"), ("mandrino", "C103"), ("mandrino", "C330"), ("mandrino", "C331"));
        tracker.TrackedComponents[0].IsSelectedForRegister = true;
        var substitute = new TrackedComponent
        {
            ResourceName = "C106", TipoKey = "mandrino", Categoria = "Mandrino",
            IsSelectedForRegister = true, IsSubstitute = true
        };

        var groups = ActivityChecklistPolicy.GroupByCategory(tracker.TrackedComponents, new[] { substitute });

        var group = Assert.Single(groups);
        Assert.Equal(4, group.Components.Count);
        Assert.Same(substitute, Assert.Single(group.Substitutes));
        Assert.Equal(new ChecklistProgress(2, 4), ActivityChecklistPolicy.ProgressOf(group));
        Assert.Equal(2, ActivityChecklistPolicy.FreeSlots(group));
        Assert.Equal(new ChecklistProgress(2, 4), ActivityChecklistPolicy.Progress(groups));
    }

    [Fact]
    public void A_category_with_only_substitutes_counts_one_slot_each()
    {
        var tracker = Tracker(("testa", "T-1"));
        var added = new TrackedComponent
        {
            ResourceName = "MA-2", TipoKey = "maschera", Categoria = "Maschera",
            IsSelectedForRegister = true, IsSubstitute = true
        };

        var groups = ActivityChecklistPolicy.GroupByCategory(tracker.TrackedComponents, new[] { added });

        Assert.Equal(new[] { "testa", "maschera" }, groups.Select(g => g.TipoKey));
        Assert.Equal("Maschera", groups[1].Label);
        Assert.Equal(new ChecklistProgress(1, 1), ActivityChecklistPolicy.ProgressOf(groups[1]));
        Assert.Equal(new ChecklistProgress(1, 2), ActivityChecklistPolicy.Progress(groups));
    }

    [Fact]
    public void FindPlanned_refuses_a_partial_read_that_fits_several_pieces()
    {
        var tracker = Tracker(("mandrino", "C102"), ("mandrino", "C103"), ("mandrino", "C330"));

        Assert.Null(ActivityChecklistPolicy.FindPlanned(tracker, "C10", out var ambiguous));
        Assert.Equal(new[] { "C102", "C103" }, ambiguous.Select(c => c.ResourceName));

        Assert.Equal("C330", ActivityChecklistPolicy.FindPlanned(tracker, "C33", out var none)!.ResourceName);
        Assert.Empty(none);
        Assert.Equal("C102", ActivityChecklistPolicy.FindPlanned(tracker, "C102", out _)!.ResourceName);
    }

    [Fact]
    public void NextIncompleteTipo_stays_put_while_there_is_work_then_moves_on()
    {
        var tracker = Tracker(("testa", "T-1"), ("mandrino", "M-1"), ("maschera", "MA-1"));
        var groups = ActivityChecklistPolicy.GroupByCategory(tracker.TrackedComponents);

        // Nessuna categoria scelta: si parte dalla prima incompleta in ordine di lista.
        Assert.Equal("testa", ActivityChecklistPolicy.NextIncompleteTipo(groups, null));

        // Finché la corrente ha pezzi da fare, si resta lì.
        Assert.Equal("mandrino", ActivityChecklistPolicy.NextIncompleteTipo(groups, "mandrino"));

        // Categoria finita: si passa alla successiva.
        groups[1].Components[0].IsSelectedForRegister = true;
        Assert.Equal("maschera", ActivityChecklistPolicy.NextIncompleteTipo(groups, "mandrino"));

        // Dall'ultima si riparte dalla prima incompleta.
        groups[2].Components[0].IsSelectedForRegister = true;
        Assert.Equal("testa", ActivityChecklistPolicy.NextIncompleteTipo(groups, "maschera"));

        // Tutto confermato: resta la corrente.
        foreach (var comp in tracker.TrackedComponents)
            comp.IsSelectedForRegister = true;
        Assert.Equal("mandrino", ActivityChecklistPolicy.NextIncompleteTipo(groups, "mandrino"));
        Assert.Null(ActivityChecklistPolicy.NextIncompleteTipo(Array.Empty<ComponentGroup>(), "mandrino"));
    }

    [Fact]
    public void Header_texts_show_the_counters()
    {
        Assert.Equal("1/2", UiStrings.FormatChipCount(new ChecklistProgress(1, 2)));
        Assert.Equal("ne manca 1", UiStrings.FormatTargetMissing(1));
        Assert.Equal("ne mancano 2", UiStrings.FormatTargetMissing(2));
        Assert.Equal("C330 · C331", UiStrings.FormatTargetCandidates(new[] { "C330", "C331" }));
        Assert.Equal("A · B · C · D …", UiStrings.FormatTargetCandidates(new[] { "A", "B", "C", "D", "E" }));
    }

    private static TrackedComponent Comp(string uid, bool confirmed = false, bool isOut = false, DateTime? outAt = null) =>
        new()
        {
            ResourceName = uid,
            TipoKey = "mandrino",
            Categoria = "Mandrino",
            IsSelectedForRegister = confirmed,
            IsOut = isOut,
            OutgoingScanTime = outAt
        };

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
