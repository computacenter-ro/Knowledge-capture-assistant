using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KnowledgeCapture.Core.Models;
using KnowledgeCapture.Core.Services;

namespace KnowledgeCapture.ViewModels;

public sealed record StoredConversationItem(string Id, string Title, string Subtitle, int CoveragePercent, string Entities, string Coverage);

public sealed record StoredMessageItem(string RoleLabel, string Text, string Meta);

/// <summary>Read-only view of what is on disk. Everything shown here is already anonymized.</summary>
public partial class StoredDataViewModel : ObservableObject
{
    public ObservableCollection<StoredConversationItem> Conversations { get; } = [];
    public ObservableCollection<StoredMessageItem> SelectedMessages { get; } = [];

    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string SelectedTitle { get; set; } = "Select a conversation";
    [ObservableProperty] public partial string SelectedMeta { get; set; } = "Only anonymized text is ever written to disk.";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))] public partial int Count { get; set; }
    public bool IsEmpty => Count == 0;

    [RelayCommand]
    public void Refresh()
    {
        List<ConversationSummary> list;
        try { list = AppHost.Store.ListConversations(); }
        catch (Exception ex) { AppLog.Error("stored list", ex); Summary = "Could not read the local database."; return; }

        Conversations.Clear();
        foreach (var c in list)
        {
            var cov = CoverageTracker.FromKeys(c.CoverageKeys);
            Conversations.Add(new StoredConversationItem(c.Id,
                string.IsNullOrWhiteSpace(c.Title) ? c.Topic : c.Title,
                $"{c.Topic} · {c.CreatedUtc.ToLocalTime():d MMM yyyy, HH:mm} · {c.UserTurns} answers",
                c.CoveragePercent,
                c.EntityCounts.Count == 0 ? "no entities replaced"
                    : string.Join(" · ", c.EntityCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}")),
                $"{cov.Count}/7 slots: " + (cov.Count == 0 ? "—" : string.Join(", ", Slots.All.Where(s => cov.IsCovered(s.Slot)).Select(s => s.Title)))));
        }
        Count = list.Count;
        var entities = list.SelectMany(c => c.EntityCounts).Sum(kv => kv.Value);
        Summary = $"{list.Count} conversations · {list.Sum(c => c.MessageCount)} messages · {entities} personal-data spans replaced";
    }

    [RelayCommand]
    public void Select(StoredConversationItem? item)
    {
        SelectedMessages.Clear();
        if (item is null) return;
        var conv = AppHost.Store.Load(item.Id);
        if (conv is null) return;
        SelectedTitle = item.Title;
        SelectedMeta = $"{item.Subtitle} · {item.Coverage}";
        if (!string.IsNullOrWhiteSpace(conv.RollingSummary))
            SelectedMessages.Add(new StoredMessageItem("Rolling summary", conv.RollingSummary, "context memory, anonymized"));
        foreach (var m in conv.Messages)
            SelectedMessages.Add(new StoredMessageItem(m.Role == ChatRole.User ? "Employee" : "Assistant", m.Content,
                $"{m.CreatedUtc.ToLocalTime():HH:mm} · {m.AnonymizerVersion}" +
                (m.EntityCounts.Count == 0 ? "" : " · " + string.Join(", ", m.EntityCounts.Select(kv => $"{kv.Value} {kv.Key}")))));
    }
}
