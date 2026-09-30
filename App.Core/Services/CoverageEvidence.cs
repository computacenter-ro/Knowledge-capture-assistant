using System.Text.RegularExpressions;
using KnowledgeCapture.Core.Models;

namespace KnowledgeCapture.Core.Services;

/// <summary>
/// Deterministic corroboration of the small model's coverage claims. A slot is only ticked when the answer shows some
/// evidence for it; a false negative just means the interviewer asks about that slot, which is what we want.
/// </summary>
public static class CoverageEvidence
{
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // a concrete case: an explicit example/time reference, or a number with a unit/amount
    private static readonly Regex Example = new(
        @"\b(?:for example|for instance|e\.g\.|example|last (?:week|month|year|time|monday|tuesday|wednesday|thursday|friday|january|february|march|april|may|june|july|august|september|october|november|december)|yesterday|recently|once|de exemplu|exemplu|ieri|recent|odat[aă]|s[aă]pt[aă]m[aâ]na trecut[aă]|luna trecut[aă]|anul trecut)\b" +
        @"|\d[\d.,]*\s*(?:EUR|RON|USD|lei|€|\$|%|minutes?|mins?|hours?|days?|weeks?|minute|ore|zile|invoices?|facturi|items?|clien[tț]i|clients?)\b", I);

    // a decision: a threshold with a number, or a condition followed by an action in the same sentence
    private static readonly Regex Threshold = new(
        @"\b(?:over|above|below|under|more than|less than|at least|at most|exceeds?|threshold|limit|peste|sub|dep[aă][sș]e[sș]te|prag|limit[aă]|minim|maxim)\b[^.!?\n]{0,30}\d", I);
    private static readonly Regex ConditionAction = new(
        @"\b(?:if|when|unless|whenever|dac[aă]|c[aâ]nd|[iî]n cazul [iî]n care)\b[^.!?\n]{0,120}\b(?:approv\w*|reject\w*|escalat\w*|ask\w*|flag\w*|block\w*|decid\w*|choos\w*|stop\w*|call\w*|send\w*|check\w*|compar\w*|renew\w*|aprob\w*|resping\w*|escalad\w*|cer|cerem|verific\w*|trimit\w*|bloc\w*|decid\w*|aleg\w*|opresc|sun|compar\w*)\b", I);
    private static readonly Regex DecisionWords = new(@"\b(?:decide|decision|criteria|criterion|rule|priorit\w*|decid\w*|decizi\w*|criteri\w*|regul\w*)\b", I);

    private static readonly Regex Pitfalls = new(
        @"\b(?:problem\w*|issue\w*|wrong|fail\w*|error\w*|mistake\w*|pitfall\w*|risk\w*|careful|break\w*|drops?|duplicate\w*|missing|exception\w*|block\w*|stuck|expired?|problem[aă]|probleme|gre[sș]eal\w*|eroare|erori|risc\w*|aten[tț]ie|lipse[sș]te|[iî]nt[aâ]rzi\w*|excep[tț]i\w*|blocat\w*|nu merge|dublur\w*|expirat\w*)\b", I);

    // advice: explicit markers, or an imperative always/never that starts a sentence
    private static readonly Regex Tips = new(
        @"\b(?:tip|tips|advice|advise|recommend\w*|suggest\w*|new colleague|make sure|remember|don't forget|my advice|sfat\w*|recomand\w*|sugerez|coleg nou|asigur[aă]-te|nu uita|[tț]ine minte)\b|(?:^|[.!?:\n]\s*)(?:always|never|[iî]ntotdeauna|niciodat[aă])\b", I);

    // explicit markers: strong enough to tick a slot even when the model does not claim it
    private static readonly Regex GoalMarker = new(
        @"\b(?:my goal|our goal|the goal|goal is|the purpose|purpose of|the aim|our aim|objective|scopul|obiectivul|rostul|scopul meu|[iî]n scopul)\b", I);
    private static readonly Regex StepsMarker = new(
        @"\b(?:steps?|first|then|after that|next|finally|every (?:morning|day|week|month|monday|friday)|pa[sș]ii|pasul|mai [iî]nt[aâ]i|apoi|dup[aă] (?:aceea|asta)|[iî]n final)\b", I);
    // a real case (time reference or "Example:" heading) - not "for example" used as "e.g." before a value
    private static readonly Regex ExampleMarker = new(
        @"(?:^|[.!?\n]\s*)(?:example|exemplu|for example,|de exemplu,)|\b(?:last (?:week|month|year|time|monday|tuesday|wednesday|thursday|friday|january|february|march|april|may|june|july|august|september|october|november|december)|yesterday|the other day|ieri|s[aă]pt[aă]m[aâ]na trecut[aă]|luna trecut[aă]|anul trecut)\b", I);
    private static readonly Regex PitfallMarker = new(
        @"\b(?:pitfall|problem|issue|mistake|goes wrong|went wrong|recurring|capcan\w*|problem[aă]|probleme|gre[sș]eal\w*)\b", I);

    /// <summary>Slots an answer covers by explicit, unambiguous markers (deterministic, independent of the model).</summary>
    public static IEnumerable<CoverageSlot> Explicit(string answer, IEnumerable<string> toolTerms)
    {
        if (GoalMarker.IsMatch(answer)) yield return CoverageSlot.Goal;
        if (StepsMarker.Matches(answer).Count >= 2) yield return CoverageSlot.Steps;
        if (toolTerms.Any(term => Regex.IsMatch(answer, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase)))
            yield return CoverageSlot.Tools;
        if (Threshold.IsMatch(answer) || ConditionAction.IsMatch(answer)) yield return CoverageSlot.Decisions;
        if (PitfallMarker.IsMatch(answer)) yield return CoverageSlot.Pitfalls;
        if (ExampleMarker.IsMatch(answer)) yield return CoverageSlot.Example;
        if (Tips.IsMatch(answer)) yield return CoverageSlot.Tips;
    }

    public static bool Supports(CoverageSlot slot, string answer) => slot switch
    {
        CoverageSlot.Example => Example.IsMatch(answer),
        CoverageSlot.Decisions => Threshold.IsMatch(answer) || ConditionAction.IsMatch(answer) || DecisionWords.IsMatch(answer),
        CoverageSlot.Pitfalls => Pitfalls.IsMatch(answer),
        CoverageSlot.Tips => Tips.IsMatch(answer),
        _ => true, // goal, steps, tools: trust the model
    };
}
