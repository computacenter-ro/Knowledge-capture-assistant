namespace KnowledgeCapture.Core.Models;

public enum ChatRole { User, Assistant }

public enum StoreState
{
    /// <summary>Anonymization/storage still running.</summary>
    Pending,
    /// <summary>Anonymized and written to SQLite.</summary>
    Stored,
    /// <summary>Anonymized, but storage is switched off (memory-only).</summary>
    NotStoredOptOut,
    /// <summary>Anonymization failed; nothing written (fail closed).</summary>
    NotStoredFailed,
    /// <summary>Loaded from SQLite (already anonymized).</summary>
    FromStorage,
}

public sealed record ConversationSummary(
    string Id, string Title, string Topic, DateTime CreatedUtc, DateTime UpdatedUtc,
    string CoverageKeys, int UserTurns, int MessageCount, Dictionary<string, int> EntityCounts)
{
    public int CoveragePercent => CoverageTracker.FromKeys(CoverageKeys).Percent;
}

public sealed record StoredMessage(
    long Id, string ConversationId, int Seq, ChatRole Role, string Content, DateTime CreatedUtc,
    string ModelId, string AnonymizerVersion, Dictionary<string, int> EntityCounts);

public sealed record ExportFilter(DateTime? FromUtc, DateTime? ToUtc, int MinUserTurns, int MinCoveragePercent);
