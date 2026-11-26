using MtConductor.Cards;

namespace MtConductor.Tests;

public class CardOrderTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Local);

    private static ProjectCard Card(string id, string name, bool needsYou = false, bool active = false,
        bool pinned = false, string status = "Active", bool open = false, int inProgress = 0, int toDo = 0,
        DateTime? leftOffAt = null) =>
        new(id, name, null, status, pinned, needsYou, needsYou ? ["x"] : [], null, null,
            inProgress, toDo, 0, 0, null, 0, false, active ? ["Bob"] : [], "",
            OpenInMt: open, LeftOffAt: leftOffAt);

    private static string[] Ids(IEnumerable<ProjectCard> cards, CardSortOrder order) =>
        CardOrder.Apply(cards, order).Select(c => c.Id).ToArray();

    private static readonly ProjectCard[] Mixed =
    [
        Card("parked", "Aaa parked", status: "Parked", inProgress: 1, leftOffAt: Now.AddDays(-1)),
        Card("plain", "bbb plain", toDo: 5),
        Card("pinned", "Ccc pinned", pinned: true, open: true, leftOffAt: Now.AddHours(-1)),
        Card("active", "Ddd active", active: true, inProgress: 2, toDo: 1, leftOffAt: Now.AddMinutes(-5)),
        Card("needs", "Eee needs", needsYou: true, toDo: 1, open: true, leftOffAt: Now.AddDays(-3)),
    ];

    [Fact]
    public void Default_is_needs_you_then_active_then_pinned_then_parked_last()
    {
        Assert.Equal(["needs", "active", "pinned", "plain", "parked"], Ids(Mixed, CardSortOrder.NeedsYouFirst));
    }

    [Fact]
    public void Name_is_alphabetical_ignoring_case_and_status()
    {
        Assert.Equal(["parked", "plain", "pinned", "active", "needs"], Ids(Mixed, CardSortOrder.Name));
    }

    [Fact]
    public void Recently_worked_on_is_newest_first_with_nothing_recorded_last()
    {
        Assert.Equal(["active", "pinned", "parked", "needs", "plain"], Ids(Mixed, CardSortOrder.RecentlyWorkedOn));
    }

    [Fact]
    public void Open_in_mt_first_keeps_the_default_order_within_each_group()
    {
        Assert.Equal(["needs", "pinned", "active", "plain", "parked"], Ids(Mixed, CardSortOrder.OpenInMtFirst));
    }

    [Fact]
    public void Most_tasks_counts_in_progress_and_to_do()
    {
        // plain 5, active 3, then parked and needs with 1 each (by name), pinned 0.
        Assert.Equal(["plain", "active", "parked", "needs", "pinned"], Ids(Mixed, CardSortOrder.MostTasks));
    }

    [Fact]
    public void Most_tasks_ties_go_to_name_whatever_the_mix_of_tasks()
    {
        Assert.Equal(["alpha", "zebra"], Ids(
            [Card("zebra", "Zebra", inProgress: 1), Card("alpha", "Alpha", toDo: 1)], CardSortOrder.MostTasks));
    }

    [Fact]
    public void Ties_fall_back_to_name()
    {
        Assert.Equal(["a", "b", "c"],
            Ids([Card("c", "Cherry"), Card("a", "apple"), Card("b", "Banana")], CardSortOrder.MostTasks));
    }

    [Theory]
    [InlineData(CardSortOrder.NeedsYouFirst, "needs-you")]
    [InlineData(CardSortOrder.Name, "name")]
    [InlineData(CardSortOrder.RecentlyWorkedOn, "recent")]
    [InlineData(CardSortOrder.OpenInMtFirst, "open")]
    [InlineData(CardSortOrder.MostTasks, "tasks")]
    public void Keys_round_trip(CardSortOrder order, string key)
    {
        Assert.Equal(key, CardOrder.Key(order));
        Assert.Equal(order, CardOrder.Parse(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("random")]
    public void Unknown_keys_parse_to_null(string? key)
    {
        Assert.Null(CardOrder.Parse(key));
    }

    [Fact]
    public void Every_order_has_a_key()
    {
        foreach (var order in Enum.GetValues<CardSortOrder>())
            Assert.Equal(order, CardOrder.Parse(CardOrder.Key(order)));
    }
}
