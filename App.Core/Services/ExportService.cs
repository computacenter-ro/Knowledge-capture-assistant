using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KnowledgeCapture.Core.Models;

namespace KnowledgeCapture.Core.Services;

public sealed record ExportResult(int Conversations, int Messages, string Path);

/// <summary>Chat-format JSONL for fine-tuning: {"messages":[{"role","content"}...],"metadata":{...}}. No system prompts.</summary>
public static class ExportService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        // keep "<PERSON_1>" and diacritics readable (and any leak greppable); JSONL is not embedded in HTML
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static List<ConversationSummary> Select(ConversationStore store, ExportFilter f) =>
        store.ListConversations().Where(c =>
                (f.FromUtc is null || c.CreatedUtc >= f.FromUtc) &&
                (f.ToUtc is null || c.CreatedUtc <= f.ToUtc) &&
                c.UserTurns >= f.MinUserTurns &&
                c.CoveragePercent >= f.MinCoveragePercent)
            .OrderBy(c => c.CreatedUtc).ToList();

    public static async Task<ExportResult> ExportJsonlAsync(ConversationStore store, ExportFilter f, Stream output, string pathForDisplay)
    {
        var selected = Select(store, f);
        var messages = 0;
        await using var w = new StreamWriter(output, new UTF8Encoding(false));
        foreach (var c in selected)
        {
            var conv = store.Load(c.Id);
            if (conv is null || conv.Messages.Count == 0) continue;
            var line = new
            {
                messages = conv.Messages.Select(m => new { role = m.Role == ChatRole.User ? "user" : "assistant", content = m.Content }),
                metadata = new
                {
                    topic = conv.Topic,
                    conversation_id = conv.Id,
                    created_utc = conv.CreatedUtc.ToString("O"),
                    coverage = CoverageTracker.FromKeys(conv.CoverageKeys).Covered.Select(s => Slots.Get(s).Key).OrderBy(k => k),
                    coverage_percent = c.CoveragePercent,
                    model_id = conv.Messages[^1].ModelId,
                    anonymizer_version = conv.Messages[^1].AnonymizerVersion,
                },
            };
            await w.WriteLineAsync(JsonSerializer.Serialize(line, Json));
            messages += conv.Messages.Count;
        }
        AppLog.Info($"Exported {selected.Count} conversations / {messages} messages to JSONL");
        return new ExportResult(selected.Count, messages, pathForDisplay);
    }
}
