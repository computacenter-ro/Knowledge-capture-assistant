using System.Text.RegularExpressions;
using KnowledgeCapture.Core.Models;

namespace KnowledgeCapture.Core.Services;

public sealed record GuardResult(string Text, bool AddedQuestion, bool ReplacedPersonalQuestion);

/// <summary>
/// Deterministic safety net around the small model's reply: exactly one question, never a request for personal data.
/// (The stream itself is already cut right after the first "?".)
/// </summary>
public static class ReplyGuard
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex[] PersonalRequests =
    [
        new(@"\byour\s+(?:full\s+|last\s+|first\s+)?(?:name|surname|e-?mail(?:\s+address)?|phone(?:\s+number)?|mobile(?:\s+number)?|(?:home\s+)?address|salary|pay|income|age|birthday|date\s+of\s+birth|health|medical|family|personal\s+\w+|id\s+number|national\s+id|cnp|iban|bank\s+account|contact\s+(?:details|information|info))\b", O),
        new(@"\b(?:who\s+are\s+you|how\s+old|where\s+do\s+you\s+live|what(?:'s|\s+is)\s+(?:his|her|their)\s+(?:full\s+)?name)\b", O),
        new(@"\b(?:names?|contact\s+details|e-?mails?|phone\s+numbers?)\s+of\s+(?:the\s+|your\s+|this\s+|that\s+)?(?:people|persons?|colleagues?|clients?|customers?|manager|boss|team\s+members?|employees?|suppliers?)\b", O),
        new(@"\bwho\s+(?:exactly\s+|specifically\s+)?(?:is|was)\s+(?:the\s+|your\s+)?(?:person|colleague|manager|client|customer)\b", O),
        new(@"\b(?:numele|prenumele|adresa|telefonul|num[aă]rul\s+de\s+telefon|salariul|v[aâ]rsta|cnp-ul|e-?mailul|data\s+na[sșş]terii|datele\s+de\s+contact)\s+(?:t[aă]u|ta|dumneavoastr[aă]|dvs\.?|lui|ei|lor|colegului|colegei|clientului|clientei|managerului|[sșş]efului)\b", O),
        new(@"\b(?:cum\s+(?:te|v[aă])\s+(?:nume[sșş]ti|numi[tț]i|cheam[aă])|c[aâ][tț]i\s+ani|date(?:le)?\s+personale|cine\s+(?:este|e)\s+(?:colegul|colega|clientul|managerul|[sșş]eful))\b", O),
    ];

    private static readonly Regex RolePrefix = new(@"^\s*(?:\*\*)?(?:interviewer|assistant|intervievator|asistent)(?:\*\*)?\s*:\s*", O);

    public static bool AsksForPersonalData(string text) => PersonalRequests.Any(r => r.IsMatch(text));

    public static int CountQuestions(string text) => text.Count(c => c == '?');

    public static GuardResult Apply(string reply, SlotInfo target, bool romanian, bool summaryMode)
    {
        var text = RolePrefix.Replace(JsonHelpers.StripThink(reply), "").Trim();
        bool added = false, replaced = false;

        if (summaryMode)
        {
            if (!text.Contains('?'))
            {
                text = (text + "\n\n" + (romanian
                    ? "Este corect acest rezumat, sau vrei să corectezi ceva?"
                    : "Is this summary correct, or would you like to correct anything?")).Trim();
                added = true;
            }
            return new GuardResult(text, added, false);
        }

        var q = text.IndexOf('?');
        if (q >= 0) text = text[..(q + 1)];
        var fallback = romanian ? FallbackRo(target.Slot) : target.FallbackQuestion;

        if (q < 0)
        {
            text = (text.TrimEnd() + (text.Length > 0 ? " " : "") + fallback).Trim();
            added = true;
        }
        else
        {
            var (ack, question) = SplitLastSentence(text);
            if (AsksForPersonalData(question))
            {
                text = (ack + " " + fallback).Trim();
                replaced = true;
            }
        }
        return new GuardResult(text, added, replaced);
    }

    private static (string Ack, string Question) SplitLastSentence(string text)
    {
        var cut = text.LastIndexOfAny(['.', '!', '\n'], Math.Max(0, text.Length - 2));
        return cut < 0 ? ("", text) : (text[..(cut + 1)].Trim(), text[(cut + 1)..].Trim());
    }

    public static string FallbackRo(CoverageSlot slot) => slot switch
    {
        CoverageSlot.Goal => "Care este scopul acestei activități și ce s-ar întâmpla dacă nu ar face-o nimeni?",
        CoverageSlot.Steps => "Poți să-mi descrii pașii principali, de la început până la sfârșit?",
        CoverageSlot.Tools => "Ce instrumente sau sisteme folosești și la ce folosești fiecare?",
        CoverageSlot.Decisions => "Ce decizii iei pe parcurs și ce reguli sau praguri te ghidează?",
        CoverageSlot.Pitfalls => "Ce merge de obicei prost și cum procedezi atunci?",
        CoverageSlot.Example => "Poți descrie un caz concret recent, cu cifrele reale?",
        _ => "Ce sfat i-ai da unui coleg nou care face asta pentru prima dată?",
    };
}

public static class Lang
{
    private static readonly HashSet<string> Ro = new(StringComparer.OrdinalIgnoreCase)
    {
        "și", "si", "este", "că", "pentru", "nu", "cu", "sunt", "care", "în", "pe", "un", "o", "mai", "am", "facem",
        "noi", "eu", "când", "cand", "dacă", "daca", "de", "la", "din", "apoi", "trebuie", "fac", "verific", "sau", "mă",
    };
    private static readonly HashSet<string> En = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "is", "i", "we", "to", "of", "in", "for", "with", "when", "then", "it", "that", "this", "my", "our",
        "a", "an", "are", "if", "on",
    };

    public static bool IsRomanian(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var diacritics = text.Count(c => "ăâîșțşţĂÂÎȘȚŞŢ".Contains(c));
        var words = Regex.Matches(text, @"\p{L}+").Select(m => m.Value).ToList();
        var ro = words.Count(Ro.Contains);
        var en = words.Count(En.Contains);
        return diacritics >= 3 || ro > en;
    }

    public static string Name(bool romanian) => romanian ? "Romanian" : "English";
}
