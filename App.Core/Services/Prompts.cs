using System.Text;
using KnowledgeCapture.Core.Models;

namespace KnowledgeCapture.Core.Services;

/// <summary>All prompts live here. Short and explicit: small NPU models follow short instructions best.</summary>
public static class Prompts
{
    private const string Role =
        "You are a friendly knowledge-capture interviewer. You help an employee explain how they do their work, " +
        "so a new colleague could learn it.";

    private const string PersonalDataRule =
        "Never ask for names, contact details, ID numbers, salaries, health or any other personal data. " +
        "Never repeat personal data the employee mentions, and do not address anyone by name.";

    private static string Facts(IReadOnlyCollection<string> tools) =>
        tools.Count == 0 ? "" : $"Tools and systems the employee uses: {string.Join(", ", tools)}\n";

    /// <summary>Interviewer system prompt, rebuilt every turn in C#.</summary>
    public static string Interviewer(string topic, CoverageTracker coverage, SlotInfo target, bool lastAnswerShort,
        string language, string? rollingSummary, IReadOnlyCollection<string> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Role);
        sb.AppendLine($"Topic: {topic}");
        var missing = coverage.Missing.Select(s => s.Title).ToList();
        sb.AppendLine($"Still missing: {(missing.Count == 0 ? "nothing" : string.Join(", ", missing))}");
        sb.Append(Facts(tools));
        if (!string.IsNullOrWhiteSpace(rollingSummary))
            sb.AppendLine($"Notes from earlier in the interview:\n{rollingSummary.Trim()}");
        sb.AppendLine("Rules:");
        sb.AppendLine("1. Start with ONE short sentence that acknowledges the answer.");
        sb.AppendLine("2. Then ask exactly ONE open question (\"How do you…\", \"Walk me through…\", \"What happens when…\"). End with \"?\" and write nothing after it.");
        sb.AppendLine(lastAnswerShort
            ? "3. The last answer was short or vague: ask for a concrete example, a number, a threshold or the reason, on the SAME point."
            : $"3. Ask about: {target.Title} ({target.Hint}). Ask for specifics: examples, numbers, thresholds, reasons.");
        sb.AppendLine($"4. {PersonalDataRule}");
        sb.AppendLine($"5. Reply in {language}. Keep it under 60 words.");
        return sb.ToString();
    }

    public static string Opening(string topic, string language) =>
        $"{Role}\nTopic: {topic}\n" +
        "Write one warm sentence of welcome, then ask ONE open question that starts the interview about this topic. " +
        $"End with \"?\" and write nothing after it. {PersonalDataRule} Reply in {language}. Under 45 words.";

    public static string FinalSummary(string topic, string language, string? rollingSummary, IReadOnlyCollection<string> tools) =>
        $"{Role}\nTopic: {topic}\nAll areas are now covered.\n" + Facts(tools) +
        (string.IsNullOrWhiteSpace(rollingSummary) ? "" : $"Notes from earlier in the interview:\n{rollingSummary.Trim()}\n") +
        "Write a structured summary of EVERYTHING the employee explained in the whole interview (notes + conversation), " +
        "including any correction or addition in their last message, with these headings and 1-2 short bullets each:\n" +
        string.Join("\n", Slots.All.Select(s => $"- {s.Title}")) +
        $"\nUse only facts the employee gave. No names or personal data. End with ONE question asking the employee to confirm or correct the summary. Reply in {language}.";

    // ------------------------------------------------------------------ JSON tasks

    public const string Coverage =
        """
        Classify which topics an interview answer covers.
        Topics:
        goal = purpose or context of the work
        steps = how the work is done, step by step
        tools = software, systems, files or tools used
        decisions = decisions, rules, criteria, thresholds
        pitfalls = exceptions, problems, mistakes and how they are handled
        example = one concrete real case
        tips = advice for a new colleague
        Only include a topic if the answer gives specific details about it, not just a mention.
        Return ONLY JSON: {"covered":["<topic>", ...]}
        Example:
        Answer: Every Monday I export the orders from SAP and check them in Excel. If an amount is over 10,000 lei I ask for approval.
        {"covered":["steps","tools","decisions"]}
        """;

    public static string CoverageUser(string question, string answer) =>
        $"Question: {question}\nAnswer: {answer}";

    public const string Ner =
        """
        Find the names of people, organizations and places in the text.
        Return ONLY a JSON array. Each item: {"text": "<exact words from the text>", "type": "PERSON" | "ORGANIZATION" | "LOCATION"}
        Include first names and surnames on their own. Include companies, banks, clients, suppliers, cities, countries, offices.
        Do NOT include software, tools, job titles or placeholders like <EMAIL_1>. Return [] if there are none.
        Examples:
        Text: Yesterday Ana Pop from Contoso sent the invoice to our Cluj office via SAP, and Mihai approved it.
        [{"text":"Ana Pop","type":"PERSON"},{"text":"Contoso","type":"ORGANIZATION"},{"text":"Cluj","type":"LOCATION"},{"text":"Mihai","type":"PERSON"}]
        Text: Trimit documentele la Ion Marin de la Banca Exemplu din Brașov.
        [{"text":"Ion Marin","type":"PERSON"},{"text":"Banca Exemplu","type":"ORGANIZATION"},{"text":"Brașov","type":"LOCATION"}]
        """;

    public static string NerUser(string chunk) => $"Text: {chunk}";

    public const string JsonRetry = "Return ONLY valid JSON, nothing else.";

    public const string RollingSummary =
        "Summarize this interview in at most 8 short bullet points. Keep the facts about the work: steps, " +
        "decisions, numbers, problems, examples, tips, and ALWAYS keep the names of tools and systems (e.g. SAP, Excel). " +
        "Use ONLY facts stated in the text; never add anything. Keep placeholders like <PERSON_1> exactly as written. " +
        "Do not add names or personal data. Return only the bullets.";

    public const string Title =
        "Write a short title (at most 6 words) for this work interview. Return only the title, no quotes.";
}
