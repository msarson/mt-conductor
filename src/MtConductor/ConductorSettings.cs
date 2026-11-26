using System.Text.Json;
using System.Text.Json.Nodes;
using MtConductor.Cards;

namespace MtConductor;

/// <summary>
/// The Owner's choices that outlive a run, in %LOCALAPPDATA%\MtConductor\settings.json. A missing
/// or unreadable file, or a value we don't know, means the default: settings never stop the app.
/// Keys this version doesn't know are kept when saving, so an older build doesn't wipe a newer one's.
/// </summary>
public sealed class ConductorSettings
{
    private const string CardOrderKey = "cardOrder";
    private readonly string _path;

    public ConductorSettings(string path) => _path = path;

    public static ConductorSettings CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MtConductor", "settings.json"));

    public CardSortOrder LoadCardOrder() =>
        CardOrder.Parse(Read()?[CardOrderKey] is JsonValue v && v.TryGetValue<string>(out var key) ? key : null)
        ?? CardOrder.Default;

    /// <summary>Saves the order; returns false (and logs) if the file couldn't be written.</summary>
    public bool SaveCardOrder(CardSortOrder order)
    {
        var root = Read() ?? new JsonObject();
        root[CardOrderKey] = CardOrder.Key(order);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Write beside it and swap in, so a crash mid-write can't leave half a file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex)   // runs in the page's message handler: report, never throw
        {
            System.Diagnostics.Trace.TraceWarning($"Couldn't save {_path}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private JsonObject? Read()
    {
        try
        {
            return File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject : null;
        }
        catch (Exception ex)   // read at startup: whatever is wrong with the file, start with the defaults
        {
            System.Diagnostics.Trace.TraceWarning($"Couldn't read {_path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
