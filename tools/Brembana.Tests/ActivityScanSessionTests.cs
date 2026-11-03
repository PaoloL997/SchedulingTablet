using Brembana;
using Xunit;

namespace Brembana.Tests;

public class ActivityScanSessionTests
{
    private static readonly InventoryMatch C106 = new("Mandrino", "C106", "Valbrembo", "19.9", "mandrino");
    private static readonly InventoryMatch C107 = new("Mandrino", "C107", "Valbrembo", "20.1", "mandrino");

    [Fact]
    public void Substitute_then_the_original_keeps_four_slots_and_pairs_at_start()
    {
        // Il caso del test in officina: 4 mandrini previsti, si scansiona C106 e poi anche
        // l'originale C102. Prima diventavano 5; ora C102 è un previsto qualsiasi e C106
        // prende il posto di quello che resta scoperto.
        var session = Session();

        session.AddSubstitute(C106);
        session.ConfirmPlanned(Planned(session, "C102"));
        session.ConfirmPlanned(Planned(session, "C103"));
        session.ConfirmPlanned(Planned(session, "C331"));

        var mandrini = session.Groups.Single(g => g.TipoKey == "mandrino");
        Assert.Equal(new ChecklistProgress(4, 4), ActivityChecklistPolicy.ProgressOf(mandrini));
        Assert.Equal(4, session.Tracker.TrackedComponents.Count(c => c.TipoKey == "mandrino"));
        Assert.Equal(new ChecklistProgress(4, 5), session.Progress);

        session.ConfirmPlanned(Planned(session, "T-1"));
        Assert.True(session.Progress.IsComplete);

        var pairing = Assert.Single(session.Pairings());
        Assert.Equal("C106", pairing.Substitute.ResourceName);
        Assert.Equal("C330", pairing.Expected!.ResourceName);
    }

    [Fact]
    public void Pieces_can_be_scanned_in_any_order()
    {
        var session = Session();

        foreach (var uid in new[] { "C331", "T-1", "C103", "C330", "C102" })
            session.ConfirmPlanned(Planned(session, uid));

        Assert.True(session.Progress.IsComplete);
        Assert.Empty(session.Pairings());
    }

    [Fact]
    public void A_planned_piece_pushes_out_the_last_substitute_when_the_slots_are_full()
    {
        var session = Session();
        session.ConfirmPlanned(Planned(session, "C102"));
        session.ConfirmPlanned(Planned(session, "C103"));
        session.ConfirmPlanned(Planned(session, "C331"));
        session.AddSubstitute(C106);
        Assert.False(session.HasRoomForSubstitute("mandrino"));

        var evicted = session.ConfirmPlanned(Planned(session, "C330"));

        Assert.Equal("C106", evicted!.ResourceName);
        Assert.Empty(session.Substitutes);
        Assert.Empty(session.Pairings());
        var mandrini = session.Groups.Single(g => g.TipoKey == "mandrino");
        Assert.Equal(new ChecklistProgress(4, 4), ActivityChecklistPolicy.ProgressOf(mandrini));
    }

    [Fact]
    public void No_substitute_fits_once_every_slot_is_taken()
    {
        var session = Session();
        session.AddSubstitute(C106);
        session.ConfirmPlanned(Planned(session, "C102"));
        session.ConfirmPlanned(Planned(session, "C103"));
        Assert.True(session.HasRoomForSubstitute("mandrino"));
        session.AddSubstitute(C107);

        Assert.False(session.HasRoomForSubstitute("mandrino"));
        Assert.Equal(4, session.SlotsOf("mandrino"));
        Assert.Throws<InvalidOperationException>(() =>
            session.AddSubstitute(new InventoryMatch("Mandrino", "C108", null, "20", "mandrino")));
    }

    [Fact]
    public void Substitutes_pair_with_the_leftover_planned_pieces_in_scan_order()
    {
        var session = Session();
        session.AddSubstitute(C107);
        session.AddSubstitute(C106);
        session.ConfirmPlanned(Planned(session, "C103"));
        session.ConfirmPlanned(Planned(session, "C331"));

        var pairings = session.Pairings();

        Assert.Equal(new[] { ("C107", "C102"), ("C106", "C330") },
            pairings.Select(p => (p.Substitute.ResourceName, p.Expected!.ResourceName)));
    }

    [Fact]
    public void A_category_without_planned_pieces_takes_the_substitute_as_an_addition()
    {
        var session = new ActivityScanSession(Tracker(("testa", "T-1")));
        var maschera = new InventoryMatch("Maschera", "MA-2", "Valbrembo", "25,4", "maschera");

        Assert.True(session.HasRoomForSubstitute("maschera"));
        session.AddSubstitute(maschera);
        session.ConfirmPlanned(Planned(session, "T-1"));

        Assert.Equal(new ChecklistProgress(2, 2), session.Progress);
        var pairing = Assert.Single(session.Pairings());
        Assert.Null(pairing.Expected);

        var added = session.ApplyPairing(pairing);
        Assert.Equal("MA-2", added.ResourceName);
        Assert.Equal("Passo", added.SpecLabel);
        Assert.Equal(2, session.Tracker.TrackedComponents.Count);
        Assert.True(ActivityChecklistPolicy.Progress(session.Tracker.TrackedComponents).IsComplete);
    }

    [Fact]
    public void UndoLast_walks_back_every_scan()
    {
        var session = Session();
        session.ConfirmPlanned(Planned(session, "C102"));
        session.AddSubstitute(C106);
        session.ConfirmPlanned(Planned(session, "T-1"));

        Assert.Equal("T-1", session.UndoLast()!.ResourceName);
        Assert.False(Planned(session, "T-1").IsSelectedForRegister);

        Assert.Equal("C106", session.UndoLast()!.ResourceName);
        Assert.Empty(session.Substitutes);

        Assert.Equal("C102", session.UndoLast()!.ResourceName);
        Assert.False(Planned(session, "C102").IsSelectedForRegister);

        Assert.Null(session.UndoLast());
        Assert.False(session.CanUndo);
        Assert.Equal(new ChecklistProgress(0, 5), session.Progress);
    }

    [Fact]
    public void Undoing_the_planned_piece_brings_back_the_substitute_it_pushed_out()
    {
        var session = Session();
        session.ConfirmPlanned(Planned(session, "C102"));
        session.ConfirmPlanned(Planned(session, "C103"));
        session.ConfirmPlanned(Planned(session, "C331"));
        session.AddSubstitute(C106);
        session.ConfirmPlanned(Planned(session, "C330"));
        Assert.Empty(session.Substitutes);

        Assert.Equal("C330", session.UndoLast()!.ResourceName);

        Assert.Equal("C106", Assert.Single(session.Substitutes).ResourceName);
        Assert.Equal("C330", Assert.Single(session.Pairings()).Expected!.ResourceName);
    }

    [Fact]
    public void Any_scan_can_be_removed_and_undo_skips_it()
    {
        var session = Session();
        session.ConfirmPlanned(Planned(session, "C102"));
        var substitute = session.AddSubstitute(C106);
        session.ConfirmPlanned(Planned(session, "C103"));

        session.Remove(substitute);
        session.Remove(Planned(session, "C102"));

        Assert.Empty(session.Substitutes);
        Assert.False(Planned(session, "C102").IsSelectedForRegister);
        Assert.Equal("C103", session.UndoLast()!.ResourceName);
        Assert.Null(session.UndoLast());
    }

    [Fact]
    public void A_substitute_is_found_by_code_so_a_rescan_is_not_a_duplicate()
    {
        var session = Session();
        session.AddSubstitute(C106);

        Assert.Same(session.Substitutes[0], session.FindSubstitute(" c106 "));
        Assert.Null(session.FindSubstitute("C107"));
    }

    [Fact]
    public void Confirm_all_fills_the_free_slots_and_keeps_the_substitutes()
    {
        var session = Session();
        session.AddSubstitute(C106);

        session.ConfirmAllPlanned();

        Assert.True(session.Progress.IsComplete);
        Assert.Single(session.Substitutes);
        Assert.Equal("C331", Assert.Single(session.Pairings()).Expected!.ResourceName);

        session.ClearAll();
        Assert.Empty(session.Substitutes);
        Assert.Equal(new ChecklistProgress(0, 5), session.Progress);
    }

    [Fact]
    public void ApplyPairing_moves_the_substitute_into_the_planned_slot()
    {
        var session = Session();
        session.AddSubstitute(C106);
        foreach (var uid in new[] { "C102", "C103", "C331", "T-1" })
            session.ConfirmPlanned(Planned(session, uid));

        var applied = session.ApplyPairing(session.Pairings()[0]);

        Assert.Equal("C106", applied.ResourceName);
        Assert.Equal("19.9", applied.SpecValue);
        Assert.True(applied.IsSelectedForRegister);
        Assert.False(applied.IsSubstitute);
        Assert.Empty(session.Substitutes);
        Assert.DoesNotContain(session.Tracker.TrackedComponents, c => c.ResourceName == "C330");
        Assert.True(ActivityChecklistPolicy.Progress(session.Tracker.TrackedComponents).IsComplete);

        var assigned = session.Tracker.MatchedActivities[0].AssignedResources;
        Assert.Contains("C106", assigned);
        Assert.DoesNotContain("C330", assigned);
    }

    [Fact]
    public void Rebase_keeps_the_scans_and_turns_a_now_planned_substitute_into_a_confirmed_piece()
    {
        var session = Session();
        session.ConfirmPlanned(Planned(session, "C102"));
        session.AddSubstitute(C106);
        session.AddSubstitute(C107);

        // Sul server C330 è stato sostituito con C106 mentre la sessione era aperta.
        var fresh = Tracker(("testa", "T-1"), ("mandrino", "C102"), ("mandrino", "C103"),
            ("mandrino", "C106"), ("mandrino", "C331"));
        var dropped = session.Rebase(fresh);

        Assert.Empty(dropped);
        Assert.Same(fresh, session.Tracker);
        Assert.True(Planned(session, "C102").IsSelectedForRegister);
        Assert.True(Planned(session, "C106").IsSelectedForRegister);
        Assert.False(Planned(session, "C103").IsSelectedForRegister);
        Assert.Equal("C107", Assert.Single(session.Substitutes).ResourceName);
        Assert.Equal(new ChecklistProgress(3, 5), session.Progress);
    }

    [Fact]
    public void Rebase_drops_the_last_substitutes_that_no_longer_fit()
    {
        var session = Session();
        session.AddSubstitute(C106);
        session.AddSubstitute(C107);

        var dropped = session.Rebase(Tracker(("mandrino", "C102")));

        Assert.Equal("C107", Assert.Single(dropped).ResourceName);
        Assert.Equal("C106", Assert.Single(session.Substitutes).ResourceName);
        Assert.True(session.Progress.IsComplete);
    }

    private static ActivityScanSession Session() =>
        new(Tracker(("testa", "T-1"), ("mandrino", "C102"), ("mandrino", "C103"),
            ("mandrino", "C330"), ("mandrino", "C331")));

    private static TrackedComponent Planned(ActivityScanSession session, string uid) =>
        session.Tracker.TrackedComponents.Single(c => c.ResourceName == uid);

    private static JobOrderTracker Tracker(params (string Tipo, string Uid)[] components)
    {
        var tracker = new JobOrderTracker { JobOrderName = "25108-1-SNA-produzione" };
        tracker.MatchedActivities.Add(new TrackedActivity
        {
            OrmId = 16,
            ActivityName = "25108-1-SNA-produzione",
            AssignedResources = components.Select(c => c.Uid).ToList()
        });
        foreach (var (tipo, uid) in components)
        {
            tracker.TrackedComponents.Add(new TrackedComponent
            {
                ResourceName = uid,
                TipoKey = tipo,
                Categoria = char.ToUpperInvariant(tipo[0]) + tipo[1..],
                SpecLabel = tipo == "maschera" ? "Passo" : "Diametro"
            });
        }
        return tracker;
    }
}
