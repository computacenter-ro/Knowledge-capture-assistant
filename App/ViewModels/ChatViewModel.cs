using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KnowledgeCapture.Core.Models;
using KnowledgeCapture.Core.Services;
using Microsoft.UI.Dispatching;

namespace KnowledgeCapture.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<int, (MessageItem Msg, StoredItem Stored)> _items = new();
    private InterviewSession? _session;
    private CancellationTokenSource? _cts;

    public ShellViewModel Shell => AppHost.Shell;
    public ObservableCollection<ConversationItem> Conversations { get; } = [];
    public ObservableCollection<MessageItem> Messages { get; } = [];
    public ObservableCollection<StoredItem> StoredItems { get; } = [];
    public ObservableCollection<SlotItem> SlotItems { get; } =
        new(Slots.All.Select(s => new SlotItem { Slot = s.Slot, Title = s.Title }));
    public IReadOnlyList<string> Topics => AppHost.Settings.Interview.Topics;
    /// <summary>What fits the model's prompt limit (the NPU build is strict), capped by the configured maximum.</summary>
    public int MaxInputChars => _session?.MaxInputChars ?? AppHost.Settings.Interview.MaxInputChars;

    public string Example1Label => DemoData.Examples[0].Label;
    public string Example2Label => DemoData.Examples[1].Label;
    public string Example3Label => DemoData.Examples[2].Label;

    [ObservableProperty] public partial string SelectedTopic { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(InputCounter))]
    public partial string Input { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanType), nameof(CanStartNew))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand), nameof(NewConversationCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial string Stats { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StorageTitle), nameof(StorageMessage), nameof(StorageIsWarning))]
    public partial bool StoreEnabled { get; set; }

    [ObservableProperty] public partial bool IsResumed { get; set; }
    [ObservableProperty] public partial string ConversationTitle { get; set; } = "Knowledge interview";
    [ObservableProperty] public partial string ConversationSubtitle { get; set; } = "Pick a topic and start a new conversation.";
    [ObservableProperty] public partial int CoveragePercent { get; set; }
    [ObservableProperty] public partial string CoverageText { get; set; } = "0 of 7";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty), nameof(CanType))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool HasSession { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsEmpty => !HasSession;
    public bool CanType => HasSession && !IsBusy;
    public bool CanStartNew => !IsBusy;
    public string InputCounter => $"{Input.Length} / {MaxInputChars}";

    public string StorageTitle => StoreEnabled ? "Stored anonymized, on this PC" : "Memory only";
    public string StorageMessage => StoreEnabled
        ? "Each turn is anonymized on-device (names, IDs, contacts → placeholders) and saved to improve internal models. The original text is never saved."
        : "Storage is off: nothing from this conversation is saved. It disappears when you close it.";
    public bool StorageIsWarning => !StoreEnabled;

    public ChatViewModel()
    {
        SelectedTopic = Topics.FirstOrDefault() ?? "My role and daily work";
        StoreEnabled = AppHost.Store.StoreEnabled;
        Shell.Ready += () => _dq.TryEnqueue(async () =>
        {
            RunCommand.NotifyCanExecuteChanged();
            RefreshConversations();
            if (_session is null) await NewConversationAsync();
        });
        RefreshConversations();
    }

    partial void OnStoreEnabledChanged(bool value)
    {
        AppHost.Store.StoreEnabled = value;
        if (_session is not null) _session.StoreEnabled = value;
    }

    // ================================================================= commands

    [RelayCommand(CanExecute = nameof(CanStartNew))]
    private async Task NewConversationAsync()
    {
        if (!Shell.IsReady) { Error = "The local model is not ready yet."; return; }
        await CloseSessionAsync();
        var session = InterviewSession.New(AppHost.Deps, SelectedTopic);
        session.StoreEnabled = StoreEnabled;
        Attach(session);
        IsResumed = false;
        ConversationTitle = SelectedTopic;
        ConversationSubtitle = "New conversation · the assistant asks, you explain how you work";

        var item = AddPair(0, isUser: false);
        if (!await RunModelAsync(ct => session.StartAsync(t => SetText(item, t), ct)))
        {
            RemovePair(0);
            _session = null;
            HasSession = false;
        }
    }

    private bool CanRun() => Shell.IsReady && HasSession && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (_session is null) return;
        var text = Input.Trim();
        if (text.Length == 0) { Error = "Please type an answer first."; return; }
        if (text.Length > MaxInputChars)
        {
            Error = $"That answer is {text.Length} characters. Please keep it under {MaxInputChars} characters, or split it into two answers.";
            return;
        }
        var baseIndex = _session.Messages.Count;
        var user = AddPair(baseIndex, isUser: true);
        user.Text = text;
        var reply = AddPair(baseIndex + 1, isUser: false);
        Input = "";
        var ok = await RunModelAsync(ct => _session.SendAsync(text, t => SetText(reply, t), ct));
        if (!ok && _session.Messages.Count == baseIndex)
        {
            // the session rolled the turn back (runtime down): restore the answer so the employee can retry
            RemovePair(baseIndex + 1);
            RemovePair(baseIndex);
            Input = text;
        }
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void FillExample(string index)
    {
        if (int.TryParse(index, out var i) && i >= 0 && i < DemoData.Examples.Length) Input = DemoData.Examples[i].Text;
    }

    [RelayCommand]
    private async Task OpenConversationAsync(ConversationItem? item)
    {
        if (item is null || IsBusy || item.Id == _session?.Id) return;
        var stored = AppHost.Store.Load(item.Id);
        if (stored is null) { Error = "That conversation could not be loaded."; return; }
        await CloseSessionAsync();
        var session = InterviewSession.Resume(AppHost.Deps, stored);
        session.StoreEnabled = StoreEnabled;
        Attach(session);
        IsResumed = true;
        ConversationTitle = string.IsNullOrWhiteSpace(stored.Title) ? stored.Topic : stored.Title;
        ConversationSubtitle = $"{stored.Topic} · started {stored.CreatedUtc.ToLocalTime():g} · resumed";
        foreach (var m in session.Messages)
        {
            var item2 = AddPair(m.Index, m.Role == ChatRole.User);
            item2.Text = m.Content;
            ApplyState(m);
        }
        UpdateCoverage();
    }

    [RelayCommand]
    public void RefreshConversations()
    {
        List<ConversationSummary> list;
        try { list = AppHost.Store.ListConversations(); }
        catch (Exception ex) { AppLog.Error("list conversations", ex); return; }
        Conversations.Clear();
        foreach (var c in list)
            Conversations.Add(new ConversationItem(c.Id,
                string.IsNullOrWhiteSpace(c.Title) ? c.Topic : c.Title,
                $"{c.Topic} · {c.UpdatedUtc.ToLocalTime():d MMM, HH:mm} · {c.CoveragePercent}%",
                c.CoveragePercent,
                string.Join(" · ", c.EntityCounts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value} {kv.Key}"))));
    }

    // ================================================================= plumbing

    private async Task<bool> RunModelAsync(Func<CancellationToken, Task> work)
    {
        Error = null;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            await work(_cts.Token);
            if (_session?.LastStats is { } st)
                Stats = $"TTFT {st.TtftSeconds ?? 0:0.00}s · {st.TokensPerSecond:0.0} tok/s · {st.Tokens} tokens · {AppHost.Llm.ShortModelName} on {AppHost.Llm.Device}";
            return true;
        }
        catch (OperationCanceledException)
        {
            Stats = "Stopped. The partial reply is shown, but it is not stored.";
            return true;
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message; // validation message written by us, no user text
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("chat turn", ex);
            Error = "The local model did not answer. Your answer was kept in the box: fix the runtime and press Send again.";
            Shell.ReportFailure();
            return false;
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Attach(InterviewSession session)
    {
        _session = session;
        HasSession = true;
        OnPropertyChanged(nameof(MaxInputChars));
        OnPropertyChanged(nameof(InputCounter));
        Messages.Clear();
        StoredItems.Clear();
        _items.Clear();
        Stats = "";
        Error = null;
        session.MessageUpdated += m => _dq.TryEnqueue(() => { if (_session == session) { ApplyState(m); MaybeRefresh(m); } });
        session.CoverageChanged += () => _dq.TryEnqueue(() => { if (_session == session) UpdateCoverage(); });
        session.TitleChanged += () => _dq.TryEnqueue(() =>
        {
            if (_session != session) return;
            ConversationTitle = session.Title;
            RefreshConversations();
        });
        UpdateCoverage();
    }

    private async Task CloseSessionAsync()
    {
        var old = _session;
        _session = null;
        HasSession = false;
        Messages.Clear();
        StoredItems.Clear();
        _items.Clear();
        if (old is not null) await old.CloseAsync(); // lets the last turn finish storing, then drops the mapping
        RefreshConversations();
    }

    private MessageItem AddPair(int index, bool isUser)
    {
        var msg = new MessageItem { Index = index, IsUser = isUser, Badge = isUser ? "" : "" };
        var stored = new StoredItem { Index = index, RoleLabel = isUser ? "Employee" : "Assistant" };
        _items[index] = (msg, stored);
        Messages.Add(msg);
        StoredItems.Insert(0, stored); // newest on top
        return msg;
    }

    private void RemovePair(int index)
    {
        if (!_items.Remove(index, out var pair)) return;
        Messages.Remove(pair.Msg);
        StoredItems.Remove(pair.Stored);
    }

    private void SetText(MessageItem item, string text)
    {
        if (_dq.HasThreadAccess) item.Text = text;
        else _dq.TryEnqueue(() => item.Text = text);
    }

    private void ApplyState(SessionMessage m)
    {
        if (!_items.TryGetValue(m.Index, out var pair)) return;
        pair.Stored.Apply(m);
        pair.Msg.Badge = m.State switch
        {
            StoreState.Stored => "✓ stored anonymized",
            StoreState.FromStorage => "stored (anonymized)",
            StoreState.NotStoredOptOut => "memory only",
            StoreState.NotStoredFailed => "not stored",
            _ => m.Anonymized is null ? "anonymizing…" : "anonymized · saved with your first answer",
        };
    }

    private void MaybeRefresh(SessionMessage m)
    {
        if (m.State == StoreState.Stored && m.Role == ChatRole.Assistant) RefreshConversations();
    }

    private void UpdateCoverage()
    {
        var cov = _session?.Coverage ?? new CoverageTracker();
        foreach (var s in SlotItems) s.IsCovered = cov.IsCovered(s.Slot);
        CoveragePercent = cov.Percent;
        CoverageText = $"{cov.Count} of {Slots.All.Count}";
    }
}
