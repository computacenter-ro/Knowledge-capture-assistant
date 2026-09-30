namespace KnowledgeCapture.Core.Services.Anonymization;

public sealed record MapEntry(string Type, string Value, string Placeholder);

/// <summary>
/// Original value -> typed, numbered placeholder, consistent across one conversation.
/// Lives in memory only: it is never serialized, logged or stored, and is cleared when the conversation closes.
/// </summary>
public sealed class PlaceholderMap
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _counters = new();
    private readonly Dictionary<string, string> _byKey = new();
    private readonly List<MapEntry> _entries = [];

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Snapshot of known surface forms, longest first (for sweeping later text).</summary>
    public IReadOnlyList<MapEntry> Entries
    {
        get { lock (_gate) return _entries.OrderByDescending(e => e.Value.Length).ToList(); }
    }

    public string GetOrCreate(string type, string value)
    {
        lock (_gate)
        {
            var key = type + "|" + Key(type, value);
            if (_byKey.TryGetValue(key, out var existing)) return existing;

            var placeholder = type == EntityTypes.Person ? FindLinkedPerson(value) : null;
            if (placeholder is null)
            {
                var n = _counters.GetValueOrDefault(type) + 1;
                _counters[type] = n;
                placeholder = $"<{type}_{n}>";
            }
            _byKey[key] = placeholder;
            _entries.Add(new MapEntry(type, value.Trim(), placeholder));
            return placeholder;
        }
    }

    /// <summary>
    /// Resumed conversations: the old mapping is gone by design, so new values continue numbering
    /// after the highest index already present in the stored (anonymized) text.
    /// </summary>
    public void SeedCountersFrom(IEnumerable<string?> storedTexts)
    {
        lock (_gate)
        {
            foreach (var t in storedTexts)
            {
                if (string.IsNullOrEmpty(t)) continue;
                foreach (System.Text.RegularExpressions.Match m in EntityTypes.PlaceholderRegex.Matches(t))
                {
                    var type = m.Groups[1].Value;
                    var n = int.Parse(m.Groups[2].Value);
                    if (n > _counters.GetValueOrDefault(type)) _counters[type] = n;
                }
            }
        }
    }

    public int LastIndex(string type) { lock (_gate) return _counters.GetValueOrDefault(type); }

    public void Clear()
    {
        lock (_gate) { _counters.Clear(); _byKey.Clear(); _entries.Clear(); }
    }

    /// <summary>"Popescu" or "Maria" after "Maria Popescu" (and vice versa) is the same person.</summary>
    private string? FindLinkedPerson(string value)
    {
        var tokens = NameTokens(value);
        if (tokens.Count == 0) return null;
        foreach (var e in _entries.Where(e => e.Type == EntityTypes.Person))
        {
            var other = NameTokens(e.Value);
            if (other.Count == 0) continue;
            if (tokens.IsSubsetOf(other) || other.IsSubsetOf(tokens)) return e.Placeholder;
        }
        return null;
    }

    internal static HashSet<string> NameTokens(string value) =>
        TextPatterns.Normalize(value).Split([' ', '-', '.', ','], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3).ToHashSet();

    private static string Key(string type, string value) => type switch
    {
        EntityTypes.Phone => Validators.NormalizeRoPhone(value) ?? TextPatterns.DigitsOnly(value),
        EntityTypes.Cnp or EntityTypes.Card => TextPatterns.DigitsOnly(value),
        EntityTypes.Iban => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant(),
        EntityTypes.Cui => TextPatterns.DigitsOnly(value),
        _ => TextPatterns.Normalize(value),
    };
}
