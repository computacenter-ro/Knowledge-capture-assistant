using System.Text.Json;

namespace KnowledgeCapture.Core.Models;

public sealed class AppSettings
{
    public LlmSettings Llm { get; set; } = new();
    public InterviewSettings Interview { get; set; } = new();
    public AnonymizationSettings Anonymization { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new AppSettings();
        return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
    }
}

public sealed class LlmSettings
{
    /// <summary>foundry | geniex | custom</summary>
    public string Provider { get; set; } = "foundry";
    /// <summary>Empty = auto-discover (the Foundry port is dynamic).</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>Alias, matched as a substring of the full model id.</summary>
    public string Model { get; set; } = "phi-3.5-mini";
    public string ApiKey { get; set; } = "local";
    /// <summary>Required device: "npu" (default, strict), "gpu", "cpu" or "any".</summary>
    public string Device { get; set; } = "npu";
    /// <summary>Prompt budget in tokens (~4 chars/token). Capped by the model's own input limit.</summary>
    public int ContextTokens { get; set; } = 3000;
}

public sealed class InterviewSettings
{
    public List<string> Topics { get; set; } =
        ["My role and daily work", "A process I own", "Solving a recurring problem"];
    /// <summary>Answers shorter than this (in words) get a follow-up on the same point.</summary>
    public int ShortAnswerWords { get; set; } = 12;
    /// <summary>"English" (default: small NPU models write poor Romanian) or "auto" (reply in the employee's language).</summary>
    public string ReplyLanguage { get; set; } = "English";
    public int MaxInputChars { get; set; } = 4000;
}

public sealed class AnonymizationSettings
{
    /// <summary>Internal project names, replaced with &lt;PROJECT_n&gt;.</summary>
    public List<string> Projects { get; set; } = [];
    /// <summary>Client names, replaced with &lt;CLIENT_n&gt;.</summary>
    public List<string> Clients { get; set; } = [];
    /// <summary>Tool/product names the LLM recognizer must NOT anonymize (e.g. SAP, Excel).</summary>
    public List<string> KeepTerms { get; set; } = [];
    /// <summary>
    /// Safety net: anonymize capitalized proper-noun runs the LLM recognizer missed (typed via small gazetteers, else NAME).
    /// Over-anonymizes unknown capitalized product names (add them to KeepTerms); keep on unless you accept NER misses.
    /// </summary>
    public bool CapitalizedNameFallback { get; set; } = true;
    /// <summary>Max characters per LLM recognizer call; small models find more entities in short chunks.</summary>
    public int NerChunkChars { get; set; } = 350;
}
