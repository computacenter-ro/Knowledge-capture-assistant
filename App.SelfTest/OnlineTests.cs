using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeCapture.Core.Models;
using KnowledgeCapture.Core.Services;
using KnowledgeCapture.Core.Services.Anonymization;

namespace KnowledgeCapture.SelfTest;

internal static class OnlineTests
{
    public static async Task RunAsync(Tests t, AppSettings settings, string? exportPath)
    {
        t.Section("Local model");
        var llm = new LlmService();
        var sw = Stopwatch.StartNew();
        try
        {
            await llm.InitAsync(settings.Llm, new Progress<string>(s => Console.WriteLine("  â€¦ " + s)));
        }
        catch (Exception ex)
        {
            t.Check("local model initialised", false, ex.Message);
            return;
        }
        t.Check("local model initialised", true, $"{llm.Model} on {llm.Device} @ {llm.BaseUrl} ({sw.Elapsed.TotalSeconds:0.0}s incl. warm-up)");
        t.Check($"model runs on the required device ({settings.Llm.Device})",
            settings.Llm.Device == "any" || llm.Device.Equals(settings.Llm.Device, StringComparison.OrdinalIgnoreCase), llm.Device);
        t.Info($"Model limits: input {llm.MaxInputTokens} tokens, output {llm.MaxOutputTokens} tokens");

        var anonymizer = new Anonymizer(settings.Anonymization, new LlmEntityRecognizer(llm, settings.Anonymization.NerChunkChars));
        await RecognizerTests(t, anonymizer);

        var store = new ConversationStore(DataPaths.Database);
        var deps = new SessionDeps(settings, llm, anonymizer, store, DataPaths.EmployeeHash());
        var interview = await ScriptedInterview(t, deps);
        if (interview is not null) await ResumeTest(t, deps, store, interview.Id);
        await BudgetTest(t, settings, llm, anonymizer, store);
        await ExportTest(t, store, exportPath);
    }

    // ------------------------------------------------------------------ NER

    private static async Task RecognizerTests(Tests t, Anonymizer anonymizer)
    {
        t.Section("LLM recognizer: Romanian + English names, orgs, places");
        var cases = new (string Name, string Text, string[] Pii)[]
        {
            ("English names", "Yesterday John Smith and Sarah Connor from Northwind reviewed the report in Leeds.",
                ["John", "Smith", "Sarah", "Connor", "Northwind", "Leeds"]),
            ("Romanian names", "Ieri Ioana Marinescu È™i Mihai Constantinescu de la Dedeman au verificat raportul la IaÈ™i.",
                ["Ioana", "Marinescu", "Mihai", "Constantinescu", "Dedeman", "IaÈ™i"]),
        };
        foreach (var (name, text, pii) in cases)
        {
            var sw = Stopwatch.StartNew();
            var r = await anonymizer.AnonymizeAsync(text, new PlaceholderMap());
            var leaked = pii.Where(v => r.Text.Contains(v, StringComparison.OrdinalIgnoreCase)).ToList();
            t.Check(name, r.Success && leaked.Count == 0,
                (r.Success ? r.Text : "failed: " + r.Error) + (leaked.Count > 0 ? $"  LEAKED: {string.Join(", ", leaked)}" : "") + $"  ({sw.Elapsed.TotalSeconds:0.0}s)");
        }
        for (var i = 0; i < DemoData.Examples.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await anonymizer.AnonymizeAsync(DemoData.Examples[i].Text, new PlaceholderMap());
            var leaked = Leaks(r.Text);
            t.Check($"demo input {i + 1} ({DemoData.Examples[i].Label}) fully anonymized", r.Success && leaked.Count == 0,
                (r.Success ? $"{r.Total} entities" : "failed: " + r.Error) + (leaked.Count > 0 ? $"  LEAKED: {string.Join(", ", leaked)}" : "") + $" ({sw.Elapsed.TotalSeconds:0.0}s)");
            if (r.Success) t.Info("stored form: " + r.Text);
        }
    }

    // ------------------------------------------------------------------ scripted interview

    private static async Task<InterviewSession?> ScriptedInterview(Tests t, SessionDeps deps)
    {
        t.Section("Scripted 5-turn interview (real model, real storage)");
        var s = InterviewSession.New(deps, DemoData.ScriptTopic);
        var replies = new List<SessionMessage>();
        var users = new List<SessionMessage>();
        var coverage = new List<int>();
        try
        {
            var sw = Stopwatch.StartNew();
            replies.Add(await s.StartAsync(_ => { }));
            await s.WhenIdleAsync();
            t.Info($"[opening] {replies[0].Anonymized}  ({sw.Elapsed.TotalSeconds:0.0}s)");
            coverage.Add(s.Coverage.Count);

            var answers = DemoData.ScriptedAnswers.ToList();
            for (var i = 0; i < answers.Count; i++)
            {
                sw.Restart();
                var reply = await s.SendAsync(answers[i], _ => { });
                var chat = sw.Elapsed.TotalSeconds;
                var stats = s.LastStats;
                await s.WhenIdleAsync();
                replies.Add(reply);
                users.Add(s.Messages[reply.Index - 1]);
                coverage.Add(s.Coverage.Count);
                t.Info($"[turn {i + 1}] reply {chat:0.0}s (TTFT {stats?.TtftSeconds:0.00}s, {stats?.TokensPerSecond:0.0} tok/s), background {sw.Elapsed.TotalSeconds - chat:0.0}s, coverage {s.Coverage.Percent}% [{s.Coverage.ToKeys()}]");
                t.Info($"   employee (stored): {users[^1].Anonymized ?? "(not stored: " + users[^1].Note + ")"}");
                t.Info($"   assistant(stored): {reply.Anonymized ?? "(not stored: " + reply.Note + ")"}{(reply.IsSummary ? "  [SUMMARY]" : "")}");
                if (i == answers.Count - 1 && s.Coverage.AllCovered && !replies.Any(r => r.IsSummary) && answers.Count < 7)
                    answers.Add("I think that covers everything I can think of."); // reach the summary turn
            }
        }
        catch (Exception ex)
        {
            t.Check("scripted interview completed", false, $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
        t.Check("scripted interview completed", true, $"{users.Count} answers, {replies.Count} assistant replies, rolling summary updates: {s.RollingSummaryUpdates}");

        // --- elicitation rules
        var questions = replies.Where(r => !r.IsSummary).ToList();
        var oneFinal = questions.Count(r => ReplyGuard.CountQuestions(r.Content) == 1);
        var oneRaw = questions.Count(r => r.RawQuestionCount == 1 && !r.GuardAddedQuestion);
        t.Rate("R1 exactly one question per reply â€” final (after guard)", oneFinal, questions.Count);
        t.Rate("R1 exactly one question per reply â€” raw model output", oneRaw, questions.Count,
            $"(guard cut extra questions {questions.Count(r => r.RawQuestionCount > 1)}x, added a missing question {questions.Count(r => r.GuardAddedQuestion)}x)");
        t.Check("R1 every reply contains exactly one question (final)", oneFinal == questions.Count);

        var noPiiFinal = replies.Count(r => !ReplyGuard.AsksForPersonalData(r.Content));
        var noPiiRaw = replies.Count(r => !r.RawAskedPersonal);
        t.Rate("R2 no request for personal data â€” final", noPiiFinal, replies.Count);
        t.Rate("R2 no request for personal data â€” raw model output", noPiiRaw, replies.Count);
        t.Check("R2 no reply asks for personal information (final)", noPiiFinal == replies.Count);

        var nonDecreasing = coverage.Zip(coverage.Skip(1)).All(p => p.Second >= p.First);
        var increases = coverage.Zip(coverage.Skip(1)).Count(p => p.Second > p.First);
        t.Rate("R3 coverage increased after an answer", increases, coverage.Count - 1, $"(slots per step: {string.Join(" â†’ ", coverage)})");
        t.Check("R3 coverage increases across turns", nonDecreasing && coverage[^1] > coverage[0], string.Join(" â†’ ", coverage) + " of 7");

        var coveredAt = coverage.FindIndex(c => c == Slots.All.Count);  // step index after which all slots were covered
        var summaryIdx = replies.FindIndex(r => r.IsSummary);
        if (coveredAt < 0)
        {
            t.Rate("R4 summary once all slots are covered", 0, 1, $"(not all slots covered: {coverage[^1]}/7)");
            t.Check("R4 final reply is a summary once all slots are covered", false, $"only {coverage[^1]}/7 slots covered after the script");
        }
        else
        {
            // coverage is classified after each reply, so the first reply generated with all slots covered is reply coveredAt+1
            var ok = summaryIdx == coveredAt + 1 && replies[^1].IsSummary && replies.Skip(coveredAt + 1).All(r => r.IsSummary);
            t.Rate("R4 summary once all slots are covered", ok ? 1 : 0, 1);
            t.Check("R4 final reply is a summary once all slots are covered", ok,
                $"all covered after answer {coveredAt}, first summary at reply {summaryIdx}, final reply is summary: {replies[^1].IsSummary}");
        }

        // --- memory: fact from turn 1 referenced in turn 5
        var turn5 = replies.Count > 5 ? replies[5].Content : "";
        t.Check($"memory: turn-5 reply references the turn-1 fact \"{DemoData.MemoryFact}\"",
            turn5.Contains(DemoData.MemoryFact, StringComparison.OrdinalIgnoreCase), replies.Count > 5 ? replies[5].Anonymized : "no turn 5");
        t.Info($"rolling summary ({s.RollingSummaryUpdates} updates, folds {s.SummarizedCount} messages) keeps \"{DemoData.MemoryFact}\": " +
               (s.RollingSummary?.Contains(DemoData.MemoryFact, StringComparison.OrdinalIgnoreCase) ?? false));

        // --- anonymization in the live flow
        var stored = s.Messages.Where(m => m.State == StoreState.Stored).ToList();
        var failed = s.Messages.Where(m => m.State == StoreState.NotStoredFailed).ToList();
        t.Check("every turn anonymized and stored", failed.Count == 0, $"{stored.Count} stored, {failed.Count} not stored (fail-closed)");
        var liveLeaks = s.Messages.Where(m => m.Anonymized is not null).SelectMany(m => Leaks(m.Anonymized!)).Distinct().ToList();
        t.Check("no PII value in any anonymized message (incl. echoed PII in replies)", liveLeaks.Count == 0, liveLeaks.Count == 0 ? null : "LEAKED: " + string.Join(", ", liveLeaks));
        var p1 = FirstPerson(users.ElementAtOrDefault(0)?.Anonymized);
        var p3 = FirstPerson(users.ElementAtOrDefault(2)?.Anonymized);
        t.Check("same person in turn 1 and turn 3 gets the same placeholder", p1 is not null && p1 == p3, $"turn 1: {p1 ?? "-"}, turn 3: {p3 ?? "-"}");
        t.Check("title is generated and anonymized", s.Title.Length > 0 && Leaks(s.Title).Count == 0, s.Title);
        await s.CloseAsync();
        return s;
    }

    private static async Task ResumeTest(Tests t, SessionDeps deps, ConversationStore store, string id)
    {
        t.Section("Resume a stored conversation");
        var sc = store.Load(id);
        if (sc is null) { t.Check("stored conversation loads", false); return; }
        // highest index anywhere in stored text (messages, rolling summary, title) - exactly what the app seeds from
        var maxPerson = sc.Messages.Select(m => m.Content).Append(sc.RollingSummary ?? "").Append(sc.Title)
            .SelectMany(text => EntityTypes.PlaceholderRegex.Matches(text))
            .Where(m => m.Groups[1].Value == EntityTypes.Person).Select(m => int.Parse(m.Groups[2].Value)).DefaultIfEmpty(0).Max();
        var r = InterviewSession.Resume(deps, sc);
        t.Check("resumed context contains only stored (anonymized) text", r.Messages.All(m => m.State == StoreState.FromStorage && Leaks(m.Content).Count == 0),
            $"{r.Messages.Count} messages, highest PERSON index {maxPerson}");
        try
        {
            await r.SendAsync("A new colleague, Ioana Marinescu, now helps me prepare the Friday payment run.", _ => { });
            await r.WhenIdleAsync();
        }
        catch (Exception ex) { t.Check("resumed turn completes", false, ex.Message); return; }
        var reloaded = store.Load(id)!;
        var lastUser = reloaded.Messages.Last(m => m.Role == ChatRole.User).Content;
        var expected = $"<PERSON_{maxPerson + 1}>";
        t.Check($"new person in resumed conversation gets {expected}", lastUser.Contains(expected) && !lastUser.Contains("Ioana"), lastUser);
        await r.CloseAsync();
    }

    private static async Task BudgetTest(Tests t, AppSettings settings, LlmService llm, Anonymizer anonymizer, ConversationStore store)
    {
        t.Section("Context budget + rolling summary");
        var small = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        small.Llm.ContextTokens = 450;
        var s = InterviewSession.New(new SessionDeps(small, llm, anonymizer, store, DataPaths.EmployeeHash()), "My role and daily work");
        t.Info($"budget {s.Budget} tokens (~{s.Budget * 4} chars)");
        try
        {
            await s.StartAsync(_ => { });
            foreach (var answer in DemoData.Examples.Select(e => e.Text).Append("That is the main part of my job, day to day."))
            {
                var reply = await s.SendAsync(answer, _ => { });
                await s.WhenIdleAsync();
                t.Info($"reply ok ({reply.Content.Length} chars), window starts at message {s.SummarizedCount}, summary updates {s.RollingSummaryUpdates}");
            }
        }
        catch (Exception ex) { t.Check("conversation over budget completes", false, ex.Message); return; }
        var sc = store.Load(s.Id);
        t.Check("conversation over budget completes via the rolling summary", s.RollingSummaryUpdates > 0 && s.Messages.All(m => m.Content.Length > 0),
            $"{s.RollingSummaryUpdates} summary update(s)");
        t.Check("stored rolling summary is anonymized", sc?.RollingSummary is { Length: > 0 } rs && Leaks(rs).Count == 0, sc?.RollingSummary);
        await s.CloseAsync();
    }

    private static async Task ExportTest(Tests t, ConversationStore store, string? exportPath)
    {
        t.Section("Export");
        var path = exportPath ?? Path.Combine(DataPaths.Root, "export-selftest.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        ExportResult res;
        await using (var fs = File.Create(path))
            res = await ExportService.ExportJsonlAsync(store, new ExportFilter(null, null, 1, 0), fs, path);
        var lines = await File.ReadAllLinesAsync(path);
        var roles = lines.SelectMany(l => JsonDocument.Parse(l).RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString())).Distinct().ToList();
        t.Check("JSONL export written", lines.Length == res.Conversations && lines.Length > 0 && roles.All(r => r is "user" or "assistant"),
            $"{res.Conversations} conversations, {res.Messages} messages, roles [{string.Join(",", roles)}] -> {path}");
    }

    // ------------------------------------------------------------------ helpers

    private static List<string> Leaks(string text) =>
        DemoData.PiiValues.Where(v => text.Contains(v, StringComparison.OrdinalIgnoreCase)).ToList();

    private static string? FirstPerson(string? text) =>
        text is null ? null : Regex.Match(text, @"<PERSON_\d+>") is { Success: true } m ? m.Value : null;
}
