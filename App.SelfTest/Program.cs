using System.Text;
using KnowledgeCapture.Core.Models;
using KnowledgeCapture.Core.Services;

namespace KnowledgeCapture.SelfTest;

/// <summary>
/// Headless check path. Usage:
///   KnowledgeCapture.SelfTest [--offline] [--data-dir DIR] [--export FILE.jsonl]
///   KnowledgeCapture.SelfTest --print-pii        (prints the synthetic demo PII values for leakcheck.ps1)
/// Prints only anonymized conversation text.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Contains("--print-pii"))
        {
            foreach (var v in DemoData.PiiValues) Console.WriteLine(v);
            return 0;
        }

        if (Arg(args, "--dump") is { } dumpDb)
        {
            // prints what is stored (anonymized by construction)
            var store = new ConversationStore(Path.GetFullPath(dumpDb));
            foreach (var c in store.ListConversations())
            {
                var conv = store.Load(c.Id)!;
                Console.WriteLine($"\n## {c.Title} | {c.Topic} | coverage {c.CoveragePercent}% [{c.CoverageKeys}] | {c.MessageCount} messages");
                if (!string.IsNullOrWhiteSpace(conv.RollingSummary)) Console.WriteLine($"   [rolling summary] {conv.RollingSummary.Replace("\n", " / ")}");
                foreach (var m in conv.Messages) Console.WriteLine($"   [{m.Role}] {m.Content.Replace("\n", " / ")}");
            }
            return 0;
        }

        var dataDir = Arg(args, "--data-dir");
        if (dataDir is not null) Environment.SetEnvironmentVariable("KC_DATA_DIR", Path.GetFullPath(dataDir));
        AppLog.Init(DataPaths.Logs);
        AppLog.Info("Self-test started");

        var settings = AppSettings.Load();
        var t = new Tests();
        Console.WriteLine($"Data dir : {DataPaths.Root}");
        Console.WriteLine($"Config   : model={settings.Llm.Model} device={settings.Llm.Device} context={settings.Llm.ContextTokens}");

        OfflineTests.Run(t, settings);
        if (args.Contains("--offline")) t.Info("Online (LLM) tests skipped: --offline");
        else await OnlineTests.RunAsync(t, settings, Arg(args, "--export"));

        AppLog.Info($"Self-test finished: {t.Passed} passed, {t.Failed} failed");
        return t.Summary();
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

internal sealed class Tests
{
    public int Passed, Failed;
    private readonly List<string> _failures = [];
    private readonly List<(string Rule, int Pass, int Total, string Note)> _rates = [];

    public void Section(string name) => Console.WriteLine($"\n=== {name} ===");
    public void Info(string s) => Console.WriteLine("  " + s);

    public bool Check(string name, bool ok, string? detail = null)
    {
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(ok ? "  [PASS] " : "  [FAIL] ");
        Console.ResetColor();
        Console.WriteLine(detail is null ? name : $"{name} — {detail}");
        if (ok) Passed++; else { Failed++; _failures.Add(name); }
        return ok;
    }

    public void Rate(string rule, int pass, int total, string note = "") => _rates.Add((rule, pass, total, note));

    public int Summary()
    {
        if (_rates.Count > 0)
        {
            Console.WriteLine("\n=== Elicitation pass rate per rule ===");
            foreach (var (rule, pass, total, note) in _rates)
                Console.WriteLine($"  {rule,-58} {pass}/{total} ({(total == 0 ? 0 : 100.0 * pass / total):0}%) {note}");
        }
        Console.WriteLine($"\n=== RESULT: {Passed} passed, {Failed} failed ===");
        foreach (var f in _failures) Console.WriteLine($"  failed: {f}");
        return Failed == 0 ? 0 : 1;
    }
}
