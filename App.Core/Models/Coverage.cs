namespace KnowledgeCapture.Core.Models;

public enum CoverageSlot { Goal, Steps, Tools, Decisions, Pitfalls, Example, Tips }

public sealed record SlotInfo(CoverageSlot Slot, string Key, string Title, string Hint, string FallbackQuestion);

public static class Slots
{
    public static readonly IReadOnlyList<SlotInfo> All =
    [
        new(CoverageSlot.Goal, "goal", "Goal / Context", "the purpose of the work and why it matters",
            "What is the goal of this work, and what would go wrong if nobody did it?"),
        new(CoverageSlot.Steps, "steps", "Steps", "the steps, in order, from start to finish",
            "Could you walk me through the main steps, from start to finish?"),
        new(CoverageSlot.Tools, "tools", "Tools & Systems", "the tools, systems and files used",
            "Which tools or systems do you use for this, and what do you use each one for?"),
        new(CoverageSlot.Decisions, "decisions", "Decisions & Criteria", "decisions, rules, thresholds and the reasons behind them",
            "What decisions do you make along the way, and what rules or thresholds guide them?"),
        new(CoverageSlot.Pitfalls, "pitfalls", "Exceptions & Pitfalls", "exceptions, common mistakes and how to handle them",
            "What usually goes wrong, and how do you handle it when it does?"),
        new(CoverageSlot.Example, "example", "Concrete Example", "one concrete real case with numbers",
            "Can you describe one concrete recent case, with the actual numbers involved?"),
        new(CoverageSlot.Tips, "tips", "Tips for a New Colleague", "advice for a new colleague",
            "What advice would you give a new colleague doing this for the first time?"),
    ];

    public static SlotInfo Get(CoverageSlot s) => All[(int)s];

    public static CoverageSlot? FromKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim().ToLowerInvariant();
        foreach (var s in All)
            if (s.Key == k || s.Title.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase)) return s.Slot;
        // tolerate small variations from the model
        if (k.StartsWith("tool") || k.StartsWith("system")) return CoverageSlot.Tools;
        if (k.StartsWith("step") || k.StartsWith("process")) return CoverageSlot.Steps;
        if (k.StartsWith("decision") || k.StartsWith("criteri") || k.StartsWith("rule")) return CoverageSlot.Decisions;
        if (k.StartsWith("pitfall") || k.StartsWith("exception") || k.StartsWith("problem")) return CoverageSlot.Pitfalls;
        if (k.StartsWith("example") || k.StartsWith("case")) return CoverageSlot.Example;
        if (k.StartsWith("tip") || k.StartsWith("advice")) return CoverageSlot.Tips;
        if (k.StartsWith("goal") || k.StartsWith("context") || k.StartsWith("purpose")) return CoverageSlot.Goal;
        return null;
    }
}

public sealed class CoverageTracker
{
    private readonly HashSet<CoverageSlot> _covered = [];

    public IReadOnlyCollection<CoverageSlot> Covered => _covered;
    public bool IsCovered(CoverageSlot s) => _covered.Contains(s);
    public bool AllCovered => _covered.Count == Slots.All.Count;
    public int Count => _covered.Count;
    public int Percent => (int)Math.Round(100.0 * _covered.Count / Slots.All.Count);
    public IEnumerable<SlotInfo> Missing => Slots.All.Where(s => !_covered.Contains(s.Slot));

    /// <summary>Returns the slots that were newly covered.</summary>
    public IReadOnlyList<CoverageSlot> Apply(IEnumerable<CoverageSlot> slots) =>
        slots.Distinct().Where(_covered.Add).ToList();

    public string ToKeys() => string.Join(",", Slots.All.Where(s => _covered.Contains(s.Slot)).Select(s => s.Key));

    public static CoverageTracker FromKeys(string? keys)
    {
        var t = new CoverageTracker();
        foreach (var k in (keys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Slots.FromKey(k) is { } s) t._covered.Add(s);
        return t;
    }
}
