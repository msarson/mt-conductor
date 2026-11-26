namespace MtConductor.Cards;

/// <summary>The orders the header drop-down offers. The page and settings file use <see cref="CardOrder.Key"/>.</summary>
public enum CardSortOrder
{
    NeedsYouFirst,
    Name,
    RecentlyWorkedOn,
    OpenInMtFirst,
    MostTasks,
}

/// <summary>Sorts cards for display. Pure; every order falls back to the name so ties are stable.</summary>
public static class CardOrder
{
    public const CardSortOrder Default = CardSortOrder.NeedsYouFirst;

    // Stable keys shared with conductor.js and settings.json: never rename one.
    private static readonly (CardSortOrder Order, string Key)[] Keys =
    [
        (CardSortOrder.NeedsYouFirst, "needs-you"),
        (CardSortOrder.Name, "name"),
        (CardSortOrder.RecentlyWorkedOn, "recent"),
        (CardSortOrder.OpenInMtFirst, "open"),
        (CardSortOrder.MostTasks, "tasks"),
    ];

    public static string Key(CardSortOrder order)
    {
        foreach (var k in Keys)
            if (k.Order == order) return k.Key;
        return Keys[0].Key;   // the default's key
    }

    /// <summary>The order for a key, or null if the key isn't one we know.</summary>
    public static CardSortOrder? Parse(string? key)
    {
        foreach (var k in Keys)
            if (string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase)) return k.Order;
        return null;
    }

    public static IReadOnlyList<ProjectCard> Apply(IEnumerable<ProjectCard> cards, CardSortOrder order)
    {
        // OrderBy(c => 0) keeps the input order (the sort is stable) and only starts the chain,
        // so every order can end with the same name tie-break below.
        var sorted = order switch
        {
            CardSortOrder.Name => cards.OrderBy(c => 0),
            CardSortOrder.RecentlyWorkedOn => cards
                .OrderBy(c => c.LeftOffAt is null)                          // nothing recorded goes last
                .ThenByDescending(c => c.LeftOffAt ?? DateTime.MinValue),
            CardSortOrder.OpenInMtFirst => NeedsYouFirst(cards.OrderByDescending(c => c.OpenInMt)),
            CardSortOrder.MostTasks => cards.OrderByDescending(c => c.InProgress + c.ToDo),
            _ => NeedsYouFirst(cards.OrderBy(c => 0)),
        };
        return sorted.ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
    }

    // Today's order: needs you, an agent at work, pinned, parked last.
    private static IOrderedEnumerable<ProjectCard> NeedsYouFirst(IOrderedEnumerable<ProjectCard> cards) => cards
        .ThenByDescending(c => c.NeedsYou)
        .ThenByDescending(c => c.ActiveAgents.Count > 0)
        .ThenByDescending(c => c.IsPinned)
        .ThenBy(c => string.Equals(c.Status, "Parked", StringComparison.OrdinalIgnoreCase));
}
