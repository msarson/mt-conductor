using System.Text.Json.Nodes;
using MtConductor.Cards;

namespace MtConductor.Tests;

public sealed class ConductorSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MtConductorTests", Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_gives_the_default()
    {
        Assert.Equal(CardOrder.Default, new ConductorSettings(SettingsPath).LoadCardOrder());
    }

    [Fact]
    public void Saved_order_is_loaded_by_a_new_instance()
    {
        Assert.True(new ConductorSettings(SettingsPath).SaveCardOrder(CardSortOrder.Name));
        Assert.Equal(CardSortOrder.Name, new ConductorSettings(SettingsPath).LoadCardOrder());
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Theory]
    [InlineData("not json {")]
    [InlineData("[1,2]")]
    [InlineData("""{"cardOrder": 3}""")]
    [InlineData("""{"cardOrder": "sideways"}""")]
    public void Bad_file_or_unknown_value_gives_the_default(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, content);
        Assert.Equal(CardOrder.Default, new ConductorSettings(SettingsPath).LoadCardOrder());
    }

    [Fact]
    public void Saving_keeps_keys_it_does_not_know()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{"somethingNewer": true, "cardOrder": "name"}""");

        new ConductorSettings(SettingsPath).SaveCardOrder(CardSortOrder.MostTasks);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.True(root["somethingNewer"]!.GetValue<bool>());
        Assert.Equal("tasks", root["cardOrder"]!.GetValue<string>());
    }

    [Fact]
    public void Saving_over_a_corrupt_file_replaces_it()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "garbage");

        Assert.True(new ConductorSettings(SettingsPath).SaveCardOrder(CardSortOrder.RecentlyWorkedOn));
        Assert.Equal(CardSortOrder.RecentlyWorkedOn, new ConductorSettings(SettingsPath).LoadCardOrder());
    }
}
