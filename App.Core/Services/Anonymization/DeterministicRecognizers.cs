using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using KnowledgeCapture.Core.Models;

namespace KnowledgeCapture.Core.Services.Anonymization;

/// <summary>
/// Regex + checksum recognizers. Everything that can be decided in code is decided here, not by the LLM.
/// </summary>
public sealed class DeterministicRecognizers
{
    private const RegexOptions O = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private const RegexOptions OI = O | RegexOptions.IgnoreCase;

    private static readonly Regex Email = new(@"(?<![\w.%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", O);
    private static readonly Regex Url = new(@"\b(?:https?://|ftp://|www\.)[^\s<>""'`]+", OI);
    private static readonly Regex Domain = new(
        @"(?<![\w@./-])(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+(?:ro|com|net|org|eu|io|info|biz|local|internal|intra|corp|lan|co\.uk|de|fr|md)(?![\w-])(?:/[^\s<>""']*)?", OI);
    private static readonly Regex Ipv4 = new(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:/\d{1,2})?(?![\d.]*\d)", O);
    private static readonly Regex Ipv6Candidate = new(@"(?<![\w:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])", O);
    private static readonly Regex Cnp = new(@"(?<!\d)[1-9]\d{12}(?!\d)", O);
    private static readonly Regex Iban = new(@"(?<![A-Za-z0-9])RO\s?\d{2}(?:\s?[A-Za-z0-9]{4}){5}(?![A-Za-z0-9])", OI);
    // A run of digit groups ("+40 721 234 567 / 0744-123-456"); phone/card windows are searched inside it,
    // so two adjacent numbers can never hide each other by failing validation as one candidate.
    private static readonly Regex DigitRun = new(@"(?<![\p{L}\p{N}_])[+(]?\d+\)?(?:[ \t.\-/]{1,3}[+(]?\d+\)?)*", O);
    private static readonly Regex DigitGroup = new(@"[+(]?\d+\)?", O);
    private static readonly Regex CuiKeyword = new(
        @"\b(?:CUI|CIF|C\.U\.I\.?|C\.I\.F\.?|cod(?:ul)?\s+(?:de\s+identificare\s+)?fiscal|cod(?:ul)?\s+unic(?:\s+de\s+[iî]nregistrare)?|VAT(?:\s+(?:no\.?|number|id))?|tax\s+id)\s*[:#.\-]?\s*(?<v>(?:RO\s?)?\d{2,10})(?!\d)", OI);
    private static readonly Regex CuiRoPrefix = new(@"(?<![A-Za-z0-9])RO\s?(?<v>\d{2,10})(?![A-Za-z0-9])", O);

    // Deterministic PERSON / ORG boosters (recall safety net on top of the LLM recognizer).
    private static readonly Regex TitledPerson = new(
        @"(?i:\b(?:mr|mrs|ms|miss|dr|dl|dna|d-na|d-l|domnul|doamna|domnișoara|domnisoara|colegul|colega|managerul|managera|șeful|seful|șefa|sefa|colleague|manager|boss)\.?\s+)(?<v>\p{Lu}[\p{Ll}'’-]+(?:\s+\p{Lu}[\p{Ll}'’-]+){0,2})", O);
    private static readonly Regex SelfIntroPerson = new(
        @"(?i:\b(?:my name is|i am|i'm|numele meu (?:este|e)|mă numesc|ma numesc|mă cheamă|ma cheama|eu sunt|sunt|signed|semnat)\s*,?\s+)(?<v>\p{Lu}[\p{Ll}'’-]+(?:\s+\p{Lu}[\p{Ll}'’-]+){0,2})", O);
    private static readonly Regex CompanySuffix = new(
        @"(?<v>\p{Lu}[\p{L}\p{N}&.'’-]*(?:\s+\p{Lu}[\p{L}\p{N}&.'’-]*){0,3}\s+(?:S\.?R\.?L\.?|S\.?A\.?|SRL|SA|Ltd\.?|LLC|GmbH|Inc\.?|PFA|plc))(?![\p{L}])", O);

    private readonly List<(Regex Rx, string Type)> _denyList = [];

    public DeterministicRecognizers(AnonymizationSettings settings)
    {
        foreach (var p in settings.Projects.Where(t => !string.IsNullOrWhiteSpace(t)).OrderByDescending(t => t.Length))
            _denyList.Add((TextPatterns.WholeTerm(p), EntityTypes.Project));
        foreach (var c in settings.Clients.Where(t => !string.IsNullOrWhiteSpace(t)).OrderByDescending(t => t.Length))
            _denyList.Add((TextPatterns.WholeTerm(c), EntityTypes.Client));
    }

    public IReadOnlyList<EntitySpan> Find(string text)
    {
        var spans = new List<EntitySpan>();
        void Add(int start, int length, string type) =>
            spans.Add(new EntitySpan(start, length, type, text.Substring(start, length)));

        foreach (Match m in Email.Matches(text)) Add(m.Index, m.Length, EntityTypes.Email);

        foreach (Match m in Url.Matches(text))
        {
            var v = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'');
            Add(m.Index, v.Length, EntityTypes.Url);
        }
        foreach (Match m in Domain.Matches(text))
        {
            var v = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
            Add(m.Index, v.Length, EntityTypes.Url);
        }

        foreach (Match m in Iban.Matches(text))
            if (Validators.IsValidRoIban(m.Value)) Add(m.Index, m.Length, EntityTypes.Iban);

        foreach (Match m in Cnp.Matches(text))
            if (Validators.IsValidCnp(m.Value)) Add(m.Index, m.Length, EntityTypes.Cnp);

        foreach (Match run in DigitRun.Matches(text))
            FindPhonesAndCards(text, run, Add);

        foreach (Match m in Ipv4.Matches(text)) Add(m.Index, m.Length, EntityTypes.Ip);
        foreach (Match m in Ipv6Candidate.Matches(text))
            if (m.Value.Count(c => c == ':') >= 2 && IPAddress.TryParse(m.Value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
                Add(m.Index, m.Length, EntityTypes.Ip);

        // CUI/CIF: with a keyword the context is strong enough; a bare "RO123..." must also pass the checksum.
        foreach (Match m in CuiKeyword.Matches(text))
        {
            var g = m.Groups["v"];
            Add(g.Index, g.Length, EntityTypes.Cui);
        }
        foreach (Match m in CuiRoPrefix.Matches(text))
            if (Validators.IsValidCui(m.Groups["v"].Value)) Add(m.Index, m.Length, EntityTypes.Cui);

        foreach (var (rx, type) in _denyList)
            foreach (Match m in rx.Matches(text)) Add(m.Index, m.Length, type);

        foreach (Match m in CompanySuffix.Matches(text)) { var g = m.Groups["v"]; Add(g.Index, g.Length, EntityTypes.Org); }
        foreach (Match m in TitledPerson.Matches(text)) { var g = m.Groups["v"]; Add(g.Index, g.Length, EntityTypes.Person); }
        foreach (Match m in SelfIntroPerson.Matches(text)) { var g = m.Groups["v"]; Add(g.Index, g.Length, EntityTypes.Person); }

        return ResolveOverlaps(spans);
    }

    private static void FindPhonesAndCards(string text, Match run, Action<int, int, string> add)
    {
        var groups = DigitGroup.Matches(run.Value).Select(g => (Start: run.Index + g.Index, End: run.Index + g.Index + g.Length)).ToList();
        var i = 0;
        while (i < groups.Count)
        {
            var found = false;
            for (var j = groups.Count - 1; j >= i && !found; j--)
            {
                var start = groups[i].Start;
                var s = text[start..groups[j].End];
                var type = IsPhone(s) ? EntityTypes.Phone : IsCard(s) ? EntityTypes.Card : null;
                if (type is null) continue;
                // a leading "(" that is not closed inside the span belongs to the prose, not the number
                if (s[0] == '(' && !s.Contains(')')) { start++; s = s[1..]; }
                add(start, s.Length, type);
                i = j + 1;
                found = true;
            }
            if (!found) i++;
        }
    }

    private static bool IsPhone(string s)
    {
        if (Validators.NormalizeRoPhone(s) is not null) return true;
        var t = s.TrimStart('(');
        var digits = TextPatterns.DigitsOnly(s);
        // international numbers need an explicit + / 00 prefix
        return (t.StartsWith('+') || t.StartsWith("00")) && digits.Length is >= 8 and <= 15;
    }

    private static bool IsCard(string s) =>
        !s.Any(c => c is '+' or '(' or ')' or '.' or '/') && Validators.IsValidLuhn(s);

    /// <summary>Earliest start wins, then the longest span, then the type priority.</summary>
    public static IReadOnlyList<EntitySpan> ResolveOverlaps(IEnumerable<EntitySpan> spans)
    {
        var result = new List<EntitySpan>();
        var end = -1;
        foreach (var s in spans.Where(s => s.Length > 0)
                     .OrderBy(s => s.Start).ThenByDescending(s => s.Length).ThenBy(s => EntityTypes.Rank(s.Type)))
        {
            if (s.Start < end) continue;
            result.Add(s);
            end = s.End;
        }
        return result;
    }
}
