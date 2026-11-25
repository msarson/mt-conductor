using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MtConductor.Github;

/// <summary>
/// The one place gh and git are launched: no shell, arguments passed as a list, a locked-down
/// environment, and a timeout that kills the whole process tree. Throws <see cref="Win32Exception"/>
/// when the tool can't be started; callers map that to "unknown".
/// </summary>
internal static class ProcessRunner
{
    /// <summary>Runs <paramref name="tool"/> and returns its exit code and stdout, or (-1, "") on timeout.</summary>
    public static async Task<(int ExitCode, string StdOut)> RunAsync(
        string tool, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        // Absolute path from PATH only; a missing tool reads as "not installed", like before.
        var exe = ToolPath.Find(tool) ?? throw new Win32Exception($"{tool}.exe was not found on PATH.");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_CONFIG_COUNT"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["MULTITERMINAL_GH_SIGN"] = "0";
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // Drain both streams concurrently so a chatty stderr can't block the child.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            return (process.ExitCode, stdout);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            return (-1, ""); // timed out
        }
    }
}
