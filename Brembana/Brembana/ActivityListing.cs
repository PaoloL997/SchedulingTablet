using System.Globalization;

namespace Brembana;

/// <summary>Una commessa della lista attività con le sue attività già in ordine.</summary>
public sealed record CommessaGroup(string Commessa, IReadOnlyList<AttivitaDto> Activities);

/// <summary>
/// Lista attività su due livelli: prima le commesse (solo il numero, in ordine crescente),
/// poi le attività della commessa scelta, in ordine di data di preventivo (inizio
/// pianificato) crescente.
/// </summary>
public static class ActivityListing
{
    /// <summary>Nome dell'attività senza commessa: <c>{disegno}-{item}-{attività}</c>.</summary>
    /// <remarks>Il campo item del server contiene già <c>{disegno}-{item}</c>.</remarks>
    public static string ActivityLabel(AttivitaDto attivita)
    {
        var parts = new[] { attivita.Item, attivita.Activity }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList();
        return parts.Count > 0 ? string.Join("-", parts) : attivita.ActivityId;
    }

    public static IReadOnlyList<CommessaGroup> GroupByCommessa(IEnumerable<AttivitaDto> activities) =>
        activities
            .GroupBy(a => a.Commessa?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, NaturalComparer.Instance)
            .Select(g => new CommessaGroup(g.Key, SortActivities(g)))
            .ToList();

    /// <summary>Data di preventivo crescente; senza data in fondo; a parità, per nome.</summary>
    public static IReadOnlyList<AttivitaDto> SortActivities(IEnumerable<AttivitaDto> activities) =>
        activities
            .OrderBy(a => PlannedStart(a) ?? DateTime.MaxValue)
            .ThenBy(ActivityLabel, NaturalComparer.Instance)
            .ToList();

    public static DateTime? PlannedStart(AttivitaDto attivita) =>
        DateTime.TryParse(attivita.Start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>Confronta le sequenze di cifre come numeri: "C9" viene prima di "C10".</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

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
                    var numX = x[startX..i].TrimStart('0');
                    var numY = y[startY..j].TrimStart('0');
                    var byNumber = numX.Length != numY.Length
                        ? numX.Length.CompareTo(numY.Length)
                        : string.CompareOrdinal(numX, numY);
                    if (byNumber != 0)
                        return byNumber;
                }
                else
                {
                    var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (byChar != 0)
                        return byChar;
                    i++;
                    j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
