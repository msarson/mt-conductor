using System.Collections.Concurrent;

namespace MtConductor.Github;

/// <summary>
/// Finds gh.exe and git.exe on PATH and remembers the absolute path (a "not found" only for a couple
/// of minutes, so installing gh while the app runs is noticed). Windows would otherwise
/// look in the app's folder and the current directory first, so an exe planted there could run in
/// our place. Only absolute PATH folders count, and never the app folder or the current directory.
/// </summary>
internal static class ToolPath
{
    internal static readonly TimeSpan NotFoundRetry = TimeSpan.FromMinutes(2);

    private static readonly ToolPathCache Default = new(
        t => Resolve(t, Environment.GetEnvironmentVariable("PATH"), AppContext.BaseDirectory, Environment.CurrentDirectory),
        () => DateTime.UtcNow, NotFoundRetry);

    /// <summary>Absolute path of <paramref name="tool"/>.exe from PATH, or null when it isn't installed.</summary>
    public static string? Find(string tool) => Default.Find(tool);

    internal static string? Resolve(string tool, string? pathVariable, string appDirectory, string currentDirectory)
    {
        if (string.IsNullOrEmpty(pathVariable)) return null;
        var excluded = new[] { appDirectory, currentDirectory }
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(Normalize)
            .ToList();

        foreach (var raw in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) continue;   // relative entries mean "current directory"
            if (excluded.Contains(Normalize(dir), StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                var candidate = Path.Combine(dir, tool + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // illegal characters in a PATH entry: skip it
            }
        }
        return null;
    }

    private static string Normalize(string dir)
    {
        try { return Path.GetFullPath(dir).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return dir.TrimEnd('\\', '/'); }
    }
}

/// <summary>Remembers where each tool was found; a miss is forgotten after <c>notFoundRetry</c>.</summary>
internal sealed class ToolPathCache(Func<string, string?> resolve, Func<DateTime> utcNow, TimeSpan notFoundRetry)
{
    private readonly ConcurrentDictionary<string, (string? Path, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string? Find(string tool)
    {
        if (_cache.TryGetValue(tool, out var hit) && (hit.Path is not null || utcNow() - hit.At < notFoundRetry))
            return hit.Path;
        var path = resolve(tool);
        _cache[tool] = (path, utcNow());
        return path;
    }
}
