using MtConductor.Github;

namespace MtConductor.Tests;

public class ToolPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mtc-toolpath-" + Guid.NewGuid().ToString("N"));

    public ToolPathTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Make(string name, bool withExe = true)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (withExe) File.WriteAllText(Path.Combine(dir, "gh.exe"), "");
        return dir;
    }

    [Fact]
    public void Finds_the_first_absolute_path_entry_with_the_exe()
    {
        var empty = Make("empty", withExe: false);
        var real = Make("real");
        var path = string.Join(Path.PathSeparator, empty, real);

        Assert.Equal(Path.Combine(real, "gh.exe"), ToolPath.Resolve("gh", path, @"C:\app", @"C:\cwd"));
    }

    [Fact]
    public void Never_uses_the_app_folder_or_current_directory()
    {
        var app = Make("app");
        var cwd = Make("cwd");
        var path = string.Join(Path.PathSeparator, app + @"\", cwd, ".", "");

        Assert.Null(ToolPath.Resolve("gh", path, app, cwd));
    }

    [Fact]
    public void Relative_entries_and_missing_tools_give_null()
    {
        Make("rel");
        Assert.Null(ToolPath.Resolve("gh", "rel" + Path.PathSeparator + @"..\rel", @"C:\app", @"C:\cwd"));
        Assert.Null(ToolPath.Resolve("gh", null, @"C:\app", @"C:\cwd"));
        Assert.Null(ToolPath.Resolve("nope", Make("real"), @"C:\app", @"C:\cwd"));
    }

    [Fact]
    public void A_miss_is_retried_after_a_while_but_a_hit_is_kept()
    {
        var clock = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var installed = false;
        var lookups = 0;
        var cache = new ToolPathCache(_ => { lookups++; return installed ? @"C:\tools\gh.exe" : null; }, () => clock, TimeSpan.FromMinutes(2));

        Assert.Null(cache.Find("gh"));
        installed = true;
        clock = clock.AddMinutes(1);
        Assert.Null(cache.Find("gh"));          // still remembered as missing
        Assert.Equal(1, lookups);

        clock = clock.AddMinutes(2);
        Assert.Equal(@"C:\tools\gh.exe", cache.Find("gh"));   // looked again, found
        installed = false;
        clock = clock.AddHours(5);
        Assert.Equal(@"C:\tools\gh.exe", cache.Find("gh"));   // a hit isn't re-resolved
        Assert.Equal(2, lookups);
    }
}