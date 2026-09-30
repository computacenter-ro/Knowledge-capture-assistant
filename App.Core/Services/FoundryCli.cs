using System.Diagnostics;
using System.Text;

namespace KnowledgeCapture.Core.Services;

/// <summary>Runs the local <c>foundry</c> CLI (discovery, model load, speech transcription).</summary>
internal static class FoundryCli
{
    /// <summary>Exit code -1 = timed out, -2 = foundry CLI not installed. Output = stdout + stderr.</summary>
    public static async Task<(int Code, string Output)> RunAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("foundry")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } ct.ThrowIfCancellationRequested(); return (-1, ""); }
            return (p.ExitCode, await stdout + "\n" + await stderr);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-2, ""); // foundry CLI not installed
        }
    }

    public static Task<(int Code, string Output)> RunAsync(string args, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(args.Split(' ', StringSplitOptions.RemoveEmptyEntries), timeout, ct);
}
