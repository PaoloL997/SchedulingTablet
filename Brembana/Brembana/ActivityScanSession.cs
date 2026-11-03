namespace Brembana;

/// <summary>
/// Un sostituto e il pezzo previsto di cui prende il posto all'avvio; <see cref="Expected"/>
/// è <c>null</c> quando l'attività non ha pezzi di quella categoria e il sostituto si aggiunge.
/// </summary>
internal sealed record SubstitutionPairing(TrackedComponent Substitute, TrackedComponent? Expected);

/// <summary>
/// Stato di una sessione di scansione: i pezzi previsti (la pianificazione, che durante la
/// scansione non cambia) e i pezzi compatibili scansionati al loro posto.
///
/// I pezzi si scansionano in qualsiasi ordine. Ogni categoria ha tanti posti quanti pezzi
/// previsti: un posto si riempie con un previsto confermato o con un sostituto, che non è
/// legato a nessun previsto preciso. Solo all'avvio ogni sostituto prende il posto di un
/// previsto rimasto senza conferma (<see cref="Pairings"/>): la compatibilità dipende dai
/// requisiti dell'attività, non dal pezzo sostituito, quindi l'abbinamento è libero.
///
/// Un previsto ha sempre la precedenza: se arriva quando i posti sono già pieni per via di
/// un sostituto, l'ultimo sostituto esce. Qualsiasi scansione si può annullare, l'ultima
/// (<see cref="UndoLast"/>) o una qualunque (<see cref="Remove"/>).
///
/// Qui non c'è UI né server: la finestra principale la usa, i test la verificano.
/// </summary>
internal sealed class ActivityScanSession
{
    private readonly List<TrackedComponent> _substitutes = new();
    private readonly List<ScanStep> _history = new();

    /// <summary>Una scansione: il pezzo confermato o aggiunto e il sostituto che ha fatto uscire.</summary>
    private sealed record ScanStep(TrackedComponent Piece, TrackedComponent? Evicted);

    public ActivityScanSession(JobOrderTracker tracker) => Tracker = tracker;

    /// <summary>La pianificazione dell'attività: i pezzi previsti e le loro conferme.</summary>
    public JobOrderTracker Tracker { get; private set; }

    /// <summary>Pezzi compatibili scansionati al posto dei previsti, in ordine di scansione.</summary>
    public IReadOnlyList<TrackedComponent> Substitutes => _substitutes;

    public IReadOnlyList<ComponentGroup> Groups =>
        ActivityChecklistPolicy.GroupByCategory(Tracker.TrackedComponents, _substitutes);

    public ChecklistProgress Progress => ActivityChecklistPolicy.Progress(Groups);

    /// <summary>C'è una scansione da annullare con <see cref="UndoLast"/>.</summary>
    public bool CanUndo => _history.Any(IsLive);

    /// <summary>
    /// Conferma un pezzo previsto. Se la categoria era già piena per via di un sostituto,
    /// l'ultimo sostituto esce: la pianificazione ha la precedenza. Restituisce il
    /// sostituto tolto, se c'è; un previsto già confermato non cambia nulla.
    /// </summary>
    public TrackedComponent? ConfirmPlanned(TrackedComponent planned)
    {
        if (planned.IsSelectedForRegister)
            return null;

        planned.IsSelectedForRegister = true;

        TrackedComponent? evicted = null;
        var group = GroupOf(planned.TipoKey);
        if (group != null && ActivityChecklistPolicy.FreeSlots(group) < 0)
        {
            evicted = group.Substitutes[^1];
            _substitutes.Remove(evicted);
        }

        _history.Add(new ScanStep(planned, evicted));
        return evicted;
    }

    /// <summary>
    /// Un sostituto di questa categoria trova posto? Sì finché ci sono previsti non coperti;
    /// una categoria senza previsti accetta il pezzo, che verrà aggiunto all'attività.
    /// </summary>
    public bool HasRoomForSubstitute(string tipoKey)
    {
        var group = GroupOf(tipoKey);
        if (group == null || group.Components.Count == 0)
            return true;
        return ActivityChecklistPolicy.FreeSlots(group) > 0;
    }

    /// <summary>Posti della categoria (i pezzi previsti), per i messaggi.</summary>
    public int SlotsOf(string tipoKey) => GroupOf(tipoKey)?.Components.Count ?? 0;

    /// <summary>
    /// Aggiunge un pezzo compatibile già verificato dal server. Non sostituisce ancora
    /// nessun previsto: l'abbinamento si decide all'avvio.
    /// </summary>
    public TrackedComponent AddSubstitute(InventoryMatch item)
    {
        if (!HasRoomForSubstitute(item.TipoKey))
            throw new InvalidOperationException($"Nessun posto libero per {item.Uid}.");

        var measure = item.MeasureValue ?? "";
        var substitute = new TrackedComponent
        {
            ResourceName = item.Uid,
            TipoKey = item.TipoKey,
            Categoria = item.Table,
            Modello = measure,
            SpecLabel = ScanSubstitutionPolicy.SpecLabelFor(item.TipoKey),
            SpecValue = measure,
            SpecUnit = "mm",
            IsSelectedForRegister = true,
            IsSubstitute = true
        };
        _substitutes.Add(substitute);
        _history.Add(new ScanStep(substitute, null));
        return substitute;
    }

    /// <summary>Il sostituto con questo codice, se è già nella lista.</summary>
    public TrackedComponent? FindSubstitute(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var normalized = code.Trim();
        return _substitutes.FirstOrDefault(s =>
            string.Equals(s.ResourceName.Trim(), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Toglie un pezzo dalla lista dei presenti: un previsto torna da confermare, un
    /// sostituto esce. Serve per una scansione sbagliata, in qualunque momento.
    /// </summary>
    public void Remove(TrackedComponent piece)
    {
        if (!_substitutes.Remove(piece))
            piece.IsSelectedForRegister = false;
        _history.RemoveAll(s => ReferenceEquals(s.Piece, piece));
    }

    /// <summary>
    /// Annulla l'ultima scansione ancora valida: un previsto torna da confermare (e rientra
    /// il sostituto che aveva fatto uscire), un sostituto esce. Restituisce il pezzo
    /// annullato, <c>null</c> se non c'è nulla da annullare.
    /// </summary>
    public TrackedComponent? UndoLast()
    {
        while (_history.Count > 0)
        {
            var step = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            if (!IsLive(step))
                continue;

            if (step.Piece.IsSubstitute)
            {
                _substitutes.Remove(step.Piece);
            }
            else
            {
                step.Piece.IsSelectedForRegister = false;
                if (step.Evicted != null
                    && !_substitutes.Contains(step.Evicted)
                    && HasRoomForSubstitute(step.Evicted.TipoKey))
                {
                    _substitutes.Add(step.Evicted);
                }
            }
            return step.Piece;
        }
        return null;
    }

    /// <summary>
    /// "Tutti presenti" dal riepilogo: riempie i posti liberi con i previsti, in ordine di
    /// elenco, e tiene i sostituti già scansionati.
    /// </summary>
    public void ConfirmAllPlanned()
    {
        foreach (var group in Groups)
        {
            var free = ActivityChecklistPolicy.FreeSlots(group);
            foreach (var comp in ActivityChecklistPolicy.MissingPlanned(group).Take(Math.Max(0, free)))
                comp.IsSelectedForRegister = true;
        }
        _history.Clear();
    }

    /// <summary>"Azzera" dal riepilogo: nessun pezzo confermato, nessun sostituto.</summary>
    public void ClearAll()
    {
        ActivityChecklistPolicy.ClearAll(Tracker.TrackedComponents);
        _substitutes.Clear();
        _history.Clear();
    }

    /// <summary>
    /// Abbinamenti per l'avvio: in ogni categoria i sostituti, in ordine di scansione,
    /// prendono il posto dei previsti rimasti senza conferma, in ordine di elenco. In una
    /// categoria senza previsti il sostituto si aggiunge (<c>Expected</c> nullo).
    /// </summary>
    public IReadOnlyList<SubstitutionPairing> Pairings()
    {
        var pairings = new List<SubstitutionPairing>();
        foreach (var group in Groups)
        {
            if (group.Components.Count == 0)
            {
                pairings.AddRange(group.Substitutes.Select(s => new SubstitutionPairing(s, null)));
                continue;
            }

            var missing = ActivityChecklistPolicy.MissingPlanned(group);
            var count = Math.Min(missing.Count, group.Substitutes.Count);
            for (var i = 0; i < count; i++)
                pairings.Add(new SubstitutionPairing(group.Substitutes[i], missing[i]));
        }
        return pairings;
    }

    /// <summary>
    /// Il server ha scritto la sostituzione: sul tracker il previsto prende uid e misura
    /// del sostituto (o il sostituto si aggiunge) e il sostituto esce dalla lista.
    /// </summary>
    public TrackedComponent ApplyPairing(SubstitutionPairing pairing)
    {
        var substitute = pairing.Substitute;
        var match = new InventoryMatch(
            substitute.Categoria, substitute.ResourceName, null, substitute.SpecValue, substitute.TipoKey);
        var applied = ScanSubstitutionPolicy.ApplySubstitution(
            Tracker, substitute.TipoKey, pairing.Expected?.ResourceName, match);

        _substitutes.Remove(substitute);
        _history.RemoveAll(s => ReferenceEquals(s.Piece, substitute));
        return applied;
    }

    /// <summary>
    /// La pianificazione è cambiata sul server: si riparte dal nuovo elenco tenendo le
    /// conferme dei pezzi ancora previsti. Un sostituto che ora è previsto diventa un
    /// previsto confermato; i sostituti che non trovano più posto escono, gli ultimi
    /// scansionati per primi. Restituisce i sostituti tolti.
    /// </summary>
    public IReadOnlyList<TrackedComponent> Rebase(JobOrderTracker fresh)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var comp in Tracker.TrackedComponents.Where(c => c.IsSelectedForRegister))
            present.Add(comp.ResourceName.Trim());
        foreach (var sub in _substitutes)
            present.Add(sub.ResourceName.Trim());

        var plannedNow = new HashSet<string>(
            fresh.TrackedComponents.Select(c => c.ResourceName.Trim()), StringComparer.OrdinalIgnoreCase);
        foreach (var comp in fresh.TrackedComponents)
            comp.IsSelectedForRegister = present.Contains(comp.ResourceName.Trim());

        var kept = _substitutes.Where(s => !plannedNow.Contains(s.ResourceName.Trim())).ToList();
        Tracker = fresh;
        _substitutes.Clear();
        _history.Clear();

        var dropped = new List<TrackedComponent>();
        foreach (var sub in kept)
        {
            if (HasRoomForSubstitute(sub.TipoKey))
                _substitutes.Add(sub);
            else
                dropped.Add(sub);
        }
        return dropped;
    }

    private ComponentGroup? GroupOf(string? tipoKey)
    {
        var key = (tipoKey ?? "").Trim();
        return Groups.FirstOrDefault(g => string.Equals(g.TipoKey, key, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsLive(ScanStep step) =>
        step.Piece.IsSubstitute ? _substitutes.Contains(step.Piece) : step.Piece.IsSelectedForRegister;
}
