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
}
