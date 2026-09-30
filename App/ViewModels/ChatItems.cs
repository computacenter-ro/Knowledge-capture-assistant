using CommunityToolkit.Mvvm.ComponentModel;
using KnowledgeCapture.Core.Models;
using KnowledgeCapture.Core.Services;

namespace KnowledgeCapture.ViewModels;

public partial class MessageItem : ObservableObject
{
    public required int Index { get; init; }
    public required bool IsUser { get; init; }
    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial string Badge { get; set; } = "";
}

/// <summary>Spoken language offered to Whisper ("auto" = detect).</summary>
public sealed record VoiceLanguage(string Code, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row of the "What gets stored" panel: the anonymized text exactly as written to SQLite.</summary>
public partial class StoredItem : ObservableObject
{
    public required int Index { get; init; }
    public required string RoleLabel { get; init; }
    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial string State { get; set; } = "Anonymizing on device…";
    [ObservableProperty] public partial string Counts { get; set; } = "";
    [ObservableProperty] public partial bool IsPending { get; set; } = true;

    public void Apply(SessionMessage m)
    {
        IsPending = m.State == StoreState.Pending && m.Anonymized is null;
        Text = m.Anonymized ?? (m.State == StoreState.NotStoredFailed ? "— nothing stored —" : "");
        State = StateLabel(m);
        Counts = m.EntityCounts.Count == 0 ? (m.Anonymized is null ? "" : "no personal data found")
            : string.Join(" · ", m.EntityCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value}× {kv.Key}"));
    }

    public static string StateLabel(SessionMessage m) => m.State switch
    {
        StoreState.Stored => "stored",
        StoreState.FromStorage => "stored earlier",
        StoreState.NotStoredOptOut => "not stored · storage off",
        StoreState.NotStoredFailed => "not stored · " + (m.Note ?? "anonymization failed"),
        _ => m.Anonymized is null ? "anonymizing on device…" : m.Note ?? "anonymized",
    };
}

public partial class SlotItem : ObservableObject
{
    public required CoverageSlot Slot { get; init; }
    public required string Title { get; init; }
    [ObservableProperty] public partial bool IsCovered { get; set; }
}

public sealed record ConversationItem(string Id, string Title, string Subtitle, int CoveragePercent, string Entities);
