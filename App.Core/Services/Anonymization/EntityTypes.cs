using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeCapture.Core.Services.Anonymization;

public static class EntityTypes
{
    public const string Person = "PERSON", Org = "ORG", Location = "LOCATION";
    public const string Email = "EMAIL", Url = "URL", Ip = "IP", Cnp = "CNP", Iban = "IBAN", Phone = "PHONE",
        Cui = "CUI", Card = "CARD", Project = "PROJECT", Client = "CLIENT", Other = "PII",
        /// <summary>Proper noun caught by the capitalization safety net whose kind is unknown.</summary>
        Name = "NAME";

    /// <summary>Lower index = wins when two spans overlap with the same start and length.</summary>
    public static readonly string[] Priority =
        [Email, Url, Iban, Cnp, Card, Phone, Ip, Cui, Project, Client, Person, Org, Location, Name, Other];

    public static int Rank(string type) { var i = Array.IndexOf(Priority, type); return i < 0 ? Priority.Length : i; }

    public static readonly Regex PlaceholderRegex = new(@"<([A-Z]+)_(\d+)>", RegexOptions.Compiled);
}

public sealed record EntitySpan(int Start, int Length, string Type, string Value)
{
    public int End => Start + Length;
}

internal static class TextPatterns
{
    private const string NotWordBefore = @"(?<![\p{L}\p{N}_])";
    private const string NotWordAfter = @"(?![\p{L}\p{N}_])";

    /// <summary>Whole-term, case-insensitive, Romanian-diacritic-tolerant, flexible-whitespace matcher.</summary>
    public static Regex WholeTerm(string term) =>
        new(NotWordBefore + TermBody(term) + NotWordAfter, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Same as <see cref="WholeTerm"/> but case-sensitive on the first letter (used for name tokens).</summary>
    public static Regex CapitalizedToken(string token)
    {
        var t = token.Trim();
        var first = char.ToUpperInvariant(t[0]);
        var body = TermBody(t[1..]);
        return new(NotWordBefore + "(?-i:" + CharClass(first, upperOnly: true) + ")(?i:" + body + ")" + NotWordAfter,
            RegexOptions.CultureInvariant);
    }

    private static string TermBody(string term)
    {
        var sb = new StringBuilder();
        var lastWasSpace = false;
        foreach (var ch in term.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) sb.Append(@"\s+");
                lastWasSpace = true;
                continue;
            }
            lastWasSpace = false;
            sb.Append(CharClass(ch, upperOnly: false));
        }
        return sb.ToString();
    }

    private static string CharClass(char c, bool upperOnly)
    {
        var cls = char.ToLowerInvariant(c) switch
        {
            'a' or 'ă' or 'â' => "aăâ",
            's' or 'ș' or 'ş' => "sșş",
            't' or 'ț' or 'ţ' => "tțţ",
            'i' or 'î' => "iî",
            _ => null,
        };
        if (cls is null) return Regex.Escape(c.ToString());
        return "[" + (upperOnly ? cls.ToUpperInvariant() : cls + cls.ToUpperInvariant()) + "]";
    }

    /// <summary>Lower-case, strip diacritics, collapse whitespace — the key used for "same entity" checks.</summary>
    public static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        var lastSpace = false;
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(ch)) { if (!lastSpace && sb.Length > 0) sb.Append(' '); lastSpace = true; continue; }
            lastSpace = false;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString().Trim();
    }

    public static string DigitsOnly(string s) => new(s.Where(char.IsDigit).ToArray());
}
