using KnowledgeCapture.Core.Models;

namespace KnowledgeCapture.Core.Services.Anonymization;

public sealed record AnonymizationResult(bool Success, string Text, IReadOnlyDictionary<string, int> Counts, string? Error)
{
    public int Total => Counts.Values.Sum();
    /// <summary>The only way to obtain storable text. Throws if anonymization failed.</summary>
    public AnonText Anonymized => Success ? new AnonText(Text) : throw new InvalidOperationException("Anonymization failed; the text must not be stored.");
}

/// <summary>
/// Hybrid pipeline: deterministic recognizers -> sweep of values already seen in this conversation ->
/// LLM recognizer (PERSON/ORG/LOCATION) -> second sweep incl. name tokens -> verification.
/// Fails closed: any doubt returns Success=false and the caller must not store the text.
/// </summary>
public sealed class Anonymizer
{
    public const string Version = "anon-1.0";

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "me", "we", "us", "you", "he", "she", "they", "them", "eu", "noi", "tu", "voi", "el", "ea", "ei", "ele",
        "team", "echipa", "client", "clientul", "clienta", "manager", "managerul", "boss", "șeful", "seful", "șefa",
        "colleague", "colegul", "colega", "supplier", "furnizorul", "company", "firma", "compania", "office", "biroul",
        "bank", "banca", "the client", "the bank", "the company", "the team", "hr", "it",
    };

    private readonly DeterministicRecognizers _det;
    private readonly IEntityRecognizer? _ner;
    private readonly NameCandidates? _candidates;
    private readonly HashSet<string> _keep;

    public Anonymizer(AnonymizationSettings settings, IEntityRecognizer? ner)
    {
        _det = new DeterministicRecognizers(settings);
        _ner = ner;
        _keep = settings.KeepTerms.Select(TextPatterns.Normalize).ToHashSet();
        _candidates = settings.CapitalizedNameFallback ? new NameCandidates(settings.KeepTerms) : null;
    }

    public string VersionTag => $"{Version}+det+{_ner?.Name ?? "none"}{(_candidates is null ? "" : "+caps")}";

    private readonly record struct Replacement(int Start, int Length, string Type, string Placeholder);

    /// <param name="titleCase">Title Case text (generated titles): skip the capitalization safety net.</param>
    public async Task<AnonymizationResult> AnonymizeAsync(string text, PlaceholderMap map, CancellationToken ct = default, bool titleCase = false)
    {
        var counts = new Dictionary<string, int>();
        if (string.IsNullOrWhiteSpace(text)) return new(true, text ?? "", counts, null);
        try
        {
            // 1. deterministic recognizers (email, url, ip, CNP, IBAN, phone, CUI, card, deny-list, boosters)
            var t = Apply(text, ToReplacements(_det.Find(text), map), counts);

            // 2. values already mapped in this conversation (keeps placeholders consistent, catches echoes)
            t = Apply(t, SweepReplacements(t, map, withNameTokens: false), counts);

            // 3. LLM recognizer for PERSON / ORGANIZATION / LOCATION. It reads the natural (raw) text - a text full of
            //    placeholders makes small models answer [] - and its findings are located in the partly anonymized text.
            if (_ner is not null)
            {
                var ner = await _ner.RecognizeAsync(text, ct);
                if (!ner.Success) return Fail(ner.Error ?? "entity recognizer failed", counts);
                var spans = Locate(t, ner.Entities.Where(Keep));
                t = Apply(t, ToReplacements(spans, map), counts);
            }

            // 4. deterministic safety net for proper nouns the model missed (after linking known names and their tokens,
            //    so "Ionescu" after "Andrei Ionescu" keeps <PERSON_1> instead of becoming a new placeholder)
            if (_candidates is not null && !titleCase)
            {
                t = Apply(t, SweepReplacements(t, map, withNameTokens: true), counts);
                t = Apply(t, ToReplacements(_candidates.Find(t), map), counts);
            }

            // 5. sweep again with everything known so far, including single name tokens ("Popescu")
            t = Apply(t, SweepReplacements(t, map, withNameTokens: true), counts);

            // 6. verification: nothing deterministic and no known value may survive
            var left = _det.Find(t);
            if (left.Count > 0)
            {
                t = Apply(t, ToReplacements(left, map), counts);
                if (_det.Find(t).Count > 0) return Fail("deterministic re-check failed", counts);
            }
            foreach (var e in map.Entries)
                if (TextPatterns.WholeTerm(e.Value).IsMatch(t)) return Fail("a known value survived", counts);

            return new(true, t, counts, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.Error("anonymize", ex);
            return Fail(ex.GetType().Name, counts);
        }
    }

    private static AnonymizationResult Fail(string error, Dictionary<string, int> counts) => new(false, "", counts, error);

    private bool Keep(NerEntity e)
    {
        var s = e.Text.Trim().Trim('"', '\'', '.', ',', ';', ':');
        if (s.Length < 2 || s.Contains('<') || s.Contains('>')) return false;
        if (!s.Any(char.IsLetter)) return false;
        if (Stop.Contains(s)) return false;
        return !_keep.Contains(TextPatterns.Normalize(s));
    }

    /// <summary>Every occurrence of every entity the model reported; entities not literally in the text are ignored.</summary>
    private static IReadOnlyList<EntitySpan> Locate(string text, IEnumerable<NerEntity> entities)
    {
        var spans = new List<EntitySpan>();
        foreach (var e in entities)
        {
            var value = e.Text.Trim().Trim('"', '\'', '.', ',', ';', ':');
            foreach (System.Text.RegularExpressions.Match m in TextPatterns.WholeTerm(value).Matches(text))
                if (!InsidePlaceholder(text, m.Index)) spans.Add(new EntitySpan(m.Index, m.Length, e.Type, m.Value));
        }
        return DeterministicRecognizers.ResolveOverlaps(spans);
    }

    private IEnumerable<Replacement> SweepReplacements(string text, PlaceholderMap map, bool withNameTokens)
    {
        var found = new List<(EntitySpan Span, string Placeholder)>();
        foreach (var e in map.Entries)
        {
            foreach (System.Text.RegularExpressions.Match m in TextPatterns.WholeTerm(e.Value).Matches(text))
                if (!InsidePlaceholder(text, m.Index)) found.Add((new EntitySpan(m.Index, m.Length, e.Type, m.Value), e.Placeholder));
            if (!withNameTokens || e.Type != EntityTypes.Person) continue;
            foreach (var token in PlaceholderMap.NameTokens(e.Value))
            foreach (System.Text.RegularExpressions.Match m in TextPatterns.WholeTerm(token).Matches(text))
                if (!InsidePlaceholder(text, m.Index) && !_keep.Contains(TextPatterns.Normalize(m.Value)))
                    found.Add((new EntitySpan(m.Index, m.Length, e.Type, m.Value), e.Placeholder));
        }
        var kept = DeterministicRecognizers.ResolveOverlaps(found.Select(f => f.Span)).ToHashSet();
        return found.Where(f => kept.Contains(f.Span)).DistinctBy(f => f.Span.Start)
            .Select(f => new Replacement(f.Span.Start, f.Span.Length, f.Span.Type, f.Placeholder));
    }

    /// <summary>Placeholders are assigned in reading order so numbering follows the text.</summary>
    private static List<Replacement> ToReplacements(IEnumerable<EntitySpan> spans, PlaceholderMap map) =>
        spans.OrderBy(s => s.Start).Select(s => new Replacement(s.Start, s.Length, s.Type, map.GetOrCreate(s.Type, s.Value))).ToList();

    private static string Apply(string text, IEnumerable<Replacement> reps, Dictionary<string, int> counts)
    {
        var sb = new System.Text.StringBuilder(text);
        foreach (var r in reps.OrderByDescending(r => r.Start))
        {
            sb.Remove(r.Start, r.Length).Insert(r.Start, r.Placeholder);
            counts[r.Type] = counts.GetValueOrDefault(r.Type) + 1;
        }
        return sb.ToString();
    }

    private static bool InsidePlaceholder(string text, int index)
    {
        if (text.Length == 0) return false;
        var open = text.LastIndexOf('<', Math.Min(index, text.Length - 1));
        if (open < 0) return false;
        var close = text.IndexOf('>', open);
        return close >= index && EntityTypes.PlaceholderRegex.IsMatch(text[open..(close + 1)]);
    }
}
