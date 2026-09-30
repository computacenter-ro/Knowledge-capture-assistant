using System.Text;
using KnowledgeCapture.Core.Models;
using KnowledgeCapture.Core.Services.Anonymization;
using OpenAI.Chat;

namespace KnowledgeCapture.Core.Services;

public sealed class SessionMessage
{
    public required int Index { get; init; }
    public required ChatRole Role { get; init; }
    /// <summary>What the model sees and the chat shows: raw text for a live turn (memory only), anonymized for a resumed one.</summary>
    public string Content { get; set; } = "";
    /// <summary>Anonymized version: exactly what is (or would be) written to storage.</summary>
    public string? Anonymized { get; set; }
    public StoreState State { get; set; } = StoreState.Pending;
    public IReadOnlyDictionary<string, int> EntityCounts { get; set; } = new Dictionary<string, int>();
    public string? Note { get; set; }
    public bool IsSummary { get; set; }
    public bool IsOpening { get; set; }
    /// <summary>Guard diagnostics (self-test): what the model produced before the deterministic guard.</summary>
    public int RawQuestionCount { get; set; }
    public bool RawAskedPersonal { get; set; }
    public bool GuardAddedQuestion { get; set; }
    public bool GuardReplacedPersonal { get; set; }
    internal bool Persisted { get; set; }
    /// <summary>The anonymizer's output; the only thing the store accepts.</summary>
    internal AnonText? StorableText { get; set; }
}

public sealed record SessionDeps(AppSettings Settings, LlmService Llm, Anonymizer Anonymizer, ConversationStore? Store, string EmployeeHash);

public sealed class GuardStats
{
    public int Replies, ExtraTextCut, QuestionAdded, PersonalQuestionReplaced;
}

/// <summary>
/// One knowledge-capture interview. UI-independent: the WinUI view model and the headless self-test both drive it.
/// Raw text exists only inside this object; storage only ever receives anonymized text.
/// </summary>
public sealed class InterviewSession
{
    private enum Mode { Interview, Summary }

    private readonly SessionDeps _d;
    private readonly PlaceholderMap _map = new();
    private readonly List<SessionMessage> _messages = [];
    private readonly object _chainLock = new();
    private Task _chain = Task.CompletedTask;
    private Task _coverageTask = Task.CompletedTask;
    private int _followUps;
    /// <summary>Deterministic fact memory: tools/systems (KeepTerms) the employee named, kept across rolling summaries.</summary>
    private readonly List<string> _tools = [];

    public string Id { get; }
    public string Topic { get; }
    public string Title { get; private set; } = "";
    public bool IsResumed { get; }
    public bool StoreEnabled { get; set; } = true;
    public CoverageTracker Coverage { get; }
    public string? RollingSummary { get; private set; }
    /// <summary>Number of leading messages folded into <see cref="RollingSummary"/>.</summary>
    public int SummarizedCount { get; private set; }
    public int RollingSummaryUpdates { get; private set; }
    public GuardStats Guard { get; } = new();
    public StreamStats? LastStats { get; private set; }
    public IReadOnlyList<SessionMessage> Messages { get { lock (_messages) return _messages.ToList(); } }
    public int PlaceholderCount => _map.Count;

    /// <summary>Raised (from a background thread) when a message's anonymized text or store state changes.</summary>
    public event Action<SessionMessage>? MessageUpdated;
    public event Action? CoverageChanged;
    public event Action? TitleChanged;

    /// <summary>For tests: the last index used for a placeholder type.</summary>
    public int LastPlaceholderIndex(string type) => _map.LastIndex(type);

    private InterviewSession(SessionDeps d, string id, string topic, bool resumed, CoverageTracker coverage)
    {
        _d = d; Id = id; Topic = topic; IsResumed = resumed; Coverage = coverage;
    }

    public static InterviewSession New(SessionDeps d, string topic) =>
        new(d, Guid.NewGuid().ToString("N"), topic, false, new CoverageTracker());

    /// <summary>Resume from storage: context is the stored anonymized turns; numbering continues after the highest placeholder.</summary>
    public static InterviewSession Resume(SessionDeps d, StoredConversation sc)
    {
        var s = new InterviewSession(d, sc.Id, sc.Topic, true, CoverageTracker.FromKeys(sc.CoverageKeys))
        {
            Title = sc.Title,
            RollingSummary = sc.RollingSummary,
        };
        foreach (var m in sc.Messages)
            s._messages.Add(new SessionMessage
            {
                Index = s._messages.Count, Role = m.Role, Content = m.Content, Anonymized = m.Content,
                State = StoreState.FromStorage, EntityCounts = m.EntityCounts, Persisted = true,
            });
        s._map.SeedCountersFrom(sc.Messages.Select(m => m.Content).Append(sc.RollingSummary).Append(sc.Title));
        foreach (var m in sc.Messages.Where(m => m.Role == ChatRole.User)) s.NoteMentionedTools(m.Content);
        // the stored summary covers everything except the most recent turns that fit the budget
        s.SummarizedCount = string.IsNullOrWhiteSpace(sc.RollingSummary) ? 0 : Math.Max(0, s._messages.Count - 2);
        return s;
    }

    // =================================================================== turns

    /// <summary>Streams the opening question for the chosen topic.</summary>
    public async Task<SessionMessage> StartAsync(Action<string> onText, CancellationToken ct = default)
    {
        var msg = Add(ChatRole.Assistant, "");
        msg.IsOpening = true;
        var target = Slots.All[0];
        try
        {
            var raw = await StreamReplyAsync(msg,
                [new SystemChatMessage(Prompts.Opening(Topic, "English")), new UserChatMessage("Start the interview.")],
                cutAtQuestion: true, maxTokens: 100, onText, ct);
            Finish(msg, raw, target, romanian: false, summaryMode: false, onText);
        }
        catch (OperationCanceledException)
        {
            Finish(msg, msg.Content, target, false, false, onText);
        }
        catch
        {
            Remove(msg);
            throw;
        }
        // the opening is anonymized now and stored together with the first answer
        _ = Enqueue(() => ProcessTurnAsync(msg));
        return msg;
    }

    public async Task<SessionMessage> SendAsync(string userText, Action<string> onText, CancellationToken ct = default)
    {
        userText = (userText ?? "").Trim();
        if (userText.Length == 0) throw new ArgumentException("Please type an answer first.");
        if (userText.Length > MaxInputChars)
            throw new ArgumentException($"That answer is too long ({userText.Length} characters). Please keep it under {MaxInputChars} characters or split it into several answers.");

        // freshen the missing-slot list with the previous answer's coverage, but never wait long for it
        await Task.WhenAny(_coverageTask, Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None));

        var lastQuestion = LastAssistantText();
        var user = Add(ChatRole.User, userText);
        // reply language: English by default (small NPU models understand Romanian well but write it poorly)
        var romanian = _d.Settings.Interview.ReplyLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase) && Lang.IsRomanian(userText);
        // once every slot is covered, each reply is the (updated) summary + a confirm/correct question
        var mode = Coverage.AllCovered ? Mode.Summary : Mode.Interview;
        var target = Coverage.Missing.FirstOrDefault() ?? Slots.All[^1];
        var shortAnswer = CountWords(userText) < _d.Settings.Interview.ShortAnswerWords && _followUps == 0;
        _followUps = shortAnswer ? _followUps + 1 : 0;
        NoteMentionedTools(userText);

        var system = mode switch
        {
            Mode.Summary => Prompts.FinalSummary(Topic, Lang.Name(romanian), RollingSummary, _tools),
            _ => Prompts.Interviewer(Topic, Coverage, target, shortAnswer, Lang.Name(romanian), RollingSummary, _tools),
        };

        var reply = Add(ChatRole.Assistant, "");
        reply.IsSummary = mode == Mode.Summary;
        try
        {
            string raw;
            try
            {
                raw = await StreamReplyAsync(reply, BuildContext(system, Budget), cutAtQuestion: mode != Mode.Summary,
                    maxTokens: mode == Mode.Summary ? 450 : 110, onText, ct);
            }
            catch (PromptTooLongException)
            {
                // the estimate was optimistic for this text: retry once with half the history
                AppLog.Warn("Prompt over the model limit; retrying with a smaller context window");
                raw = await StreamReplyAsync(reply, BuildContext(system, Budget / 2), cutAtQuestion: mode != Mode.Summary,
                    maxTokens: mode == Mode.Summary ? 450 : 110, onText, ct);
            }
            Finish(reply, raw, target, romanian, mode == Mode.Summary, onText);
        }
        catch (OperationCanceledException)
        {
            // stopped by the employee: keep what was shown, but a half reply is not stored as training data
            reply.Content = reply.Content.Trim();
            MarkNotStored([user, reply], "stopped before the reply finished");
            _coverageTask = Enqueue(() => UpdateCoverageAsync(lastQuestion, userText));
            throw;
        }
        catch
        {
            // runtime down etc.: roll the turn back so the employee can retry the same answer
            Remove(reply); Remove(user);
            throw;
        }

        _coverageTask = Enqueue(() => UpdateCoverageAsync(lastQuestion, userText));
        _ = Enqueue(() => ProcessTurnAsync(user, reply));
        _ = Enqueue(MaintainContextAsync);
        return reply;
    }

    /// <summary>Completes when all queued background work (coverage, anonymization, storage, summaries) is done.</summary>
    public Task WhenIdleAsync() { lock (_chainLock) return _chain; }

    /// <summary>Waits for pending storage, then drops the in-memory mapping. Raw text leaves with this object.</summary>
    public async Task CloseAsync()
    {
        try { await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (TimeoutException) { AppLog.Warn("Closing conversation with background work still running"); }
        _map.Clear();
    }

    // =================================================================== streaming + guard

    private async Task<string> StreamReplyAsync(SessionMessage msg, List<ChatMessage> msgs, bool cutAtQuestion, int maxTokens,
        Action<string> onText, CancellationToken ct)
    {
        var stats = new StreamStats();
        var sb = new StringBuilder();
        var shownLength = -1;
        // Exactly one question: show text only up to the first "?". The stream is still read to the end - disconnecting
        // early makes Foundry keep generating and reject the next request - and the guard truncates the stored reply.
        await foreach (var t in _d.Llm.StreamAsync(msgs, 0.4f, maxTokens, interactive: true, stats, ct))
        {
            sb.Append(t);
            if (shownLength >= 0) continue;
            var text = JsonHelpers.StripThink(sb.ToString());
            var q = cutAtQuestion ? text.IndexOf('?') : -1;
            var shown = q >= 0 ? text[..(q + 1)] : text;
            if (q >= 0) shownLength = shown.Length;
            msg.Content = shown;
            onText(shown);
        }
        LastStats = stats;
        var raw = JsonHelpers.StripThink(sb.ToString());
        if (shownLength >= 0 && raw.Length > shownLength && raw[shownLength..].Trim().Length > 0) Guard.ExtraTextCut++;
        return raw;
    }

    private void Finish(SessionMessage msg, string raw, SlotInfo target, bool romanian, bool summaryMode, Action<string> onText)
    {
        msg.RawQuestionCount = ReplyGuard.CountQuestions(raw);
        msg.RawAskedPersonal = ReplyGuard.AsksForPersonalData(raw);
        var g = ReplyGuard.Apply(raw, target, romanian, summaryMode);
        msg.GuardAddedQuestion = g.AddedQuestion;
        msg.GuardReplacedPersonal = g.ReplacedPersonalQuestion;
        Guard.Replies++;
        if (g.AddedQuestion) Guard.QuestionAdded++;
        if (g.ReplacedPersonalQuestion) Guard.PersonalQuestionReplaced++;
        msg.Content = g.Text;
        onText(g.Text);
    }

    // =================================================================== context budget

    /// <summary>Prompt budget: the configured budget, capped at 80% of the model's own input limit.</summary>
    public int Budget => Math.Max(400, Math.Min(_d.Settings.Llm.ContextTokens, (int)(_d.Llm.MaxInputTokens * 0.8)));

    /// <summary>Longest answer that still fits next to the system prompt (static NPU shapes are strict).</summary>
    public int MaxInputChars => Math.Min(_d.Settings.Interview.MaxInputChars, Math.Max(600, (Budget - 380) * 3));

    private List<ChatMessage> BuildContext(string system, int totalBudget)
    {
        var window = Messages.Skip(SummarizedCount).Where(m => m.Content.Length > 0).ToList();
        var budget = totalBudget - LlmService.EstimateTokens(system);
        // keep the most recent turns that fit; older ones live on in the rolling summary + coverage checklist
        var picked = new List<SessionMessage>();
        var used = 0;
        for (var i = window.Count - 1; i >= 0; i--)
        {
            var cost = LlmService.EstimateTokens(window[i].Content);
            if (picked.Count > 0 && used + cost > budget) break;
            picked.Insert(0, window[i]);
            used += cost;
        }
        var msgs = new List<ChatMessage> { new SystemChatMessage(system) };
        foreach (var m in picked)
            msgs.Add(m.Role == ChatRole.User ? new UserChatMessage(m.Content) : new AssistantChatMessage(m.Content));
        return msgs;
    }

    /// <summary>When the window nears the budget, fold older turns into a rolling summary made from ANONYMIZED text.</summary>
    private async Task MaintainContextAsync()
    {
        var all = Messages;
        var windowTokens = 350 + all.Skip(SummarizedCount).Sum(m => LlmService.EstimateTokens(m.Content));
        if (windowTokens <= Budget * 0.85) return;
        var upto = all.Count - 2; // keep the last turn verbatim; everything older lives in the summary
        if (upto <= SummarizedCount) return;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(RollingSummary)) sb.AppendLine("Earlier notes:").AppendLine(RollingSummary);
        foreach (var m in all.Skip(SummarizedCount).Take(upto - SummarizedCount))
            if (!string.IsNullOrWhiteSpace(m.Anonymized))
                sb.AppendLine($"{(m.Role == ChatRole.User ? "Employee" : "Interviewer")}: {m.Anonymized}");
        var input = sb.ToString();
        var maxChars = Math.Max(600, (int)(_d.Llm.MaxInputTokens * 0.75 - 120) * 3);
        LlmResult? res = null;
        for (var attempt = 0; attempt < 2 && res is null; attempt++, maxChars /= 2)
        {
            // keep the most recent part; older facts are already in "Earlier notes"
            var text = input.Length > maxChars ? input[^maxChars..] : input;
            try
            {
                res = await _d.Llm.CompleteAsync(
                    [new SystemChatMessage(Prompts.RollingSummary), new UserChatMessage(text)], 0.2f, 180).ConfigureAwait(false);
            }
            catch (PromptTooLongException) { AppLog.Warn("Summary input over the model limit; retrying smaller"); }
        }
        if (res is null) return;
        var summary = JsonHelpers.StripThink(res.Text);
        if (summary.Length == 0) return;
        // the model only saw anonymized text, but the summary still goes through the full pipeline before use/storage
        var anon = await _d.Anonymizer.AnonymizeAsync(summary, _map).ConfigureAwait(false);
        if (!anon.Success)
        {
            AppLog.Warn($"Rolling summary not updated: anonymization failed ({anon.Error})");
            return;
        }
        RollingSummary = anon.Text;
        SummarizedCount = upto;
        RollingSummaryUpdates++;
        AppLog.Info($"Rolling summary updated: folded {upto} messages, {anon.Text.Length} chars");
        if (StoreEnabled && _d.Store is { } store && store.Exists(Id)) store.UpdateRollingSummary(Id, anon.Anonymized);
    }

    // =================================================================== coverage

    private async Task UpdateCoverageAsync(string question, string answer)
    {
        // the question only gives context: keep it short so the answer always fits the prompt limit
        if (question.Length > 300) question = question[^300..];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var user = Prompts.CoverageUser(question, answer) + (attempt > 0 ? "\n\n" + Prompts.JsonRetry : "");
            LlmResult res;
            try
            {
                res = await _d.Llm.CompleteAsync([new SystemChatMessage(Prompts.Coverage), new UserChatMessage(user)], 0.2f, 80)
                    .ConfigureAwait(false);
            }
            catch (PromptTooLongException)
            {
                answer = answer[..(answer.Length / 2)];
                continue;
            }
            if (!JsonHelpers.TryParse<CoverageJson>(res.Text, '{', '}', out var parsed) || parsed!.Covered is null) continue;
            // model claims are kept only with evidence; explicit markers count even when the model misses them
            var claimed = parsed.Covered.Select(Slots.FromKey).OfType<CoverageSlot>().Distinct().ToList();
            var supported = claimed.Where(s => CoverageEvidence.Supports(s, answer)).ToList();
            var markers = CoverageEvidence.Explicit(answer, ToolTerms).ToList();
            var added = Coverage.Apply(supported.Concat(markers));
            AppLog.Info($"Coverage +{added.Count} -> {Coverage.Percent}% (model claimed {claimed.Count}, {claimed.Count - supported.Count} without evidence; markers {markers.Count})");
            if (added.Count > 0)
            {
                if (StoreEnabled && _d.Store is { } store && store.Exists(Id)) store.UpdateCoverage(Id, Coverage.ToKeys());
                CoverageChanged?.Invoke();
            }
            return;
        }
        AppLog.Warn("Coverage JSON invalid after retry; coverage unchanged");
    }

    private sealed class CoverageJson { public List<string>? Covered { get; set; } }

    // =================================================================== anonymize + store

    private async Task ProcessTurnAsync(params SessionMessage[] turn)
    {
        var ok = true;
        foreach (var m in turn)
        {
            var res = await _d.Anonymizer.AnonymizeAsync(m.Content, _map).ConfigureAwait(false);
            if (!res.Success) { ok = false; AppLog.Warn($"Anonymization failed for message #{m.Index}: {res.Error}"); break; }
            m.StorableText = res.Anonymized;
            m.Anonymized = res.Text;
            m.EntityCounts = res.Counts;
        }
        if (!ok)
        {
            MarkNotStored(turn, "anonymization failed — not stored");
            return;
        }

        var hasAnswer = turn.Any(m => m.Role == ChatRole.User);
        if (!StoreEnabled || _d.Store is null)
        {
            foreach (var m in turn) { m.State = StoreState.NotStoredOptOut; m.Note = "storage off — memory only"; MessageUpdated?.Invoke(m); }
            return;
        }
        if (!hasAnswer)
        {
            // opening question: stored together with the first answer
            foreach (var m in turn) { m.Note = "stored with the first answer"; MessageUpdated?.Invoke(m); }
            return;
        }

        var toStore = Messages.Where(m => !m.Persisted && m.StorableText is not null && m.State == StoreState.Pending && m.Index <= turn.Max(t => t.Index)).ToList();
        try
        {
            _d.Store.AddMessages(Id, Topic, _d.EmployeeHash, toStore.Select(m => new MessageToStore(
                m.Role, m.StorableText!.Value, _d.Llm.Model, _d.Anonymizer.VersionTag, m.EntityCounts)).ToList());
            _d.Store.UpdateCoverage(Id, Coverage.ToKeys());
            foreach (var m in toStore) { m.Persisted = true; m.State = StoreState.Stored; m.Note = null; MessageUpdated?.Invoke(m); }
            AppLog.Info($"Stored {toStore.Count} anonymized messages ({toStore.Sum(m => m.EntityCounts.Values.Sum())} entities)");
        }
        catch (Exception ex)
        {
            AppLog.Error("store turn", ex);
            MarkNotStored(turn, "storage error — not stored");
            return;
        }

        if (string.IsNullOrEmpty(Title)) await MakeTitleAsync().ConfigureAwait(false);
    }

    private async Task MakeTitleAsync()
    {
        var firstAnswer = Messages.FirstOrDefault(m => m.Role == ChatRole.User && m.Anonymized is not null)?.Anonymized;
        if (firstAnswer is null) return;
        var input = $"Topic: {Topic}\nFirst answer: {(firstAnswer.Length > 600 ? firstAnswer[..600] : firstAnswer)}";
        var res = await _d.Llm.CompleteAsync([new SystemChatMessage(Prompts.Title), new UserChatMessage(input)], 0.3f, 20)
            .ConfigureAwait(false);
        var title = JsonHelpers.StripThink(res.Text).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?
            .Trim().Trim('"', '\'', '*', '#', '.', ' ') ?? "";
        title = System.Text.RegularExpressions.Regex.Replace(title, @"^(?:title|titlu)\s*:\s*", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim('"', '\'', ' ');
        if (title.Length == 0) title = Topic;
        if (title.Length > 60) title = title[..60].TrimEnd();
        // generated from anonymized text only, and anonymized again before storage (Title Case: no capitalization heuristic)
        var anon = await _d.Anonymizer.AnonymizeAsync(title, _map, titleCase: true).ConfigureAwait(false);
        if (!anon.Success) return;
        Title = anon.Text;
        if (StoreEnabled && _d.Store is { } store && store.Exists(Id)) store.UpdateTitle(Id, anon.Anonymized);
        TitleChanged?.Invoke();
    }

    // =================================================================== helpers

    private SessionMessage Add(ChatRole role, string content)
    {
        lock (_messages)
        {
            var m = new SessionMessage { Index = _messages.Count, Role = role, Content = content };
            _messages.Add(m);
            return m;
        }
    }

    private void Remove(SessionMessage m) { lock (_messages) _messages.Remove(m); }

    private void MarkNotStored(IEnumerable<SessionMessage> msgs, string note)
    {
        foreach (var m in msgs)
        {
            if (m.Persisted) continue;
            m.State = StoreState.NotStoredFailed;
            m.Note = note;
            MessageUpdated?.Invoke(m);
        }
    }

    /// <summary>KeepTerms that name tools/systems (currencies excluded).</summary>
    private IEnumerable<string> ToolTerms => _d.Settings.Anonymization.KeepTerms.Where(t => t is not ("EUR" or "RON" or "lei"));

    private void NoteMentionedTools(string text)
    {
        foreach (var term in ToolTerms)
            if (!_tools.Contains(term, StringComparer.OrdinalIgnoreCase) && TextPatterns.WholeTerm(term).IsMatch(text))
                _tools.Add(term);
    }

    private string LastAssistantText() =>
        Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.Length > 0)?.Content ?? "";

    private static int CountWords(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private Task Enqueue(Func<Task> work)
    {
        lock (_chainLock)
        {
            var prev = _chain;
            _chain = Task.Run(async () =>
            {
                try { await prev.ConfigureAwait(false); } catch { /* previous step already logged */ }
                try { await work().ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Error("background step", ex); }
            });
            return _chain;
        }
    }
}
