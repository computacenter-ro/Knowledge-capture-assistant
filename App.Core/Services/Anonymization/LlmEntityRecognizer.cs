using KnowledgeCapture.Core.Models;
using OpenAI.Chat;

namespace KnowledgeCapture.Core.Services.Anonymization;

public sealed record NerEntity(string Text, string Type);

public sealed record NerResult(bool Success, IReadOnlyList<NerEntity> Entities, string? Error)
{
    public static NerResult Ok(IReadOnlyList<NerEntity> e) => new(true, e, null);
    public static NerResult Fail(string error) => new(false, [], error);
}

public interface IEntityRecognizer
{
    string Name { get; }
    Task<NerResult> RecognizeAsync(string text, CancellationToken ct);
}

/// <summary>PERSON / ORGANIZATION / LOCATION via the local model. JSON array, parsed defensively, retried once.</summary>
public sealed class LlmEntityRecognizer(LlmService llm, int chunkChars = 350) : IEntityRecognizer
{
    private const int MaxSplitDepth = 3;
    private sealed class Item { public string? Text { get; set; } public string? Type { get; set; } }

    public string Name => "llm:" + llm.Model;

    public async Task<NerResult> RecognizeAsync(string text, CancellationToken ct)
    {
        var all = new List<NerEntity>();
        foreach (var chunk in Chunker.Split(text, chunkChars))
        {
            var r = await RecognizeChunkAsync(chunk, 0, ct);
            if (!r.Success) return r;
            all.AddRange(r.Entities);
        }
        return NerResult.Ok(all);
    }

    private async Task<NerResult> RecognizeChunkAsync(string chunk, int depth, CancellationToken ct)
    {
        if (!chunk.Any(char.IsLetter)) return NerResult.Ok([]);
        var truncated = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var user = Prompts.NerUser(chunk) + (attempt > 0 ? "\n\n" + Prompts.JsonRetry : "");
            LlmResult res;
            try
            {
                res = await llm.CompleteAsync([new SystemChatMessage(Prompts.Ner), new UserChatMessage(user)],
                    temperature: 0.2f, maxTokens: 256, interactive: false, ct: ct);
            }
            catch (PromptTooLongException)
            {
                truncated = true; // same remedy as a cut-off answer: smaller chunks
                break;
            }
            var raw = JsonHelpers.Clean(res.Text);
            if (res.Truncated || JsonHelpers.LooksTruncated(raw, '[', ']'))
            {
                // A cut-off list may be missing entities: never accept it. Split the chunk and try smaller pieces.
                truncated = true;
                break;
            }
            if (JsonHelpers.TryParse<List<Item>>(raw, '[', ']', out var items))
                return NerResult.Ok(items!.Where(i => !string.IsNullOrWhiteSpace(i.Text))
                    .Select(i => new NerEntity(i.Text!.Trim(), MapType(i.Type))).ToList());
        }

        if (truncated && depth < MaxSplitDepth && chunk.Length > 80)
        {
            var parts = Chunker.Split(chunk, Math.Max(60, chunk.Length / 2 + 1));
            var list = new List<NerEntity>();
            foreach (var p in parts)
            {
                var r = await RecognizeChunkAsync(p, depth + 1, ct);
                if (!r.Success) return r;
                list.AddRange(r.Entities);
            }
            return NerResult.Ok(list);
        }
        return NerResult.Fail(truncated ? "entity list truncated" : "entity JSON invalid after retry");
    }

    private static string MapType(string? t) => (t ?? "").Trim().ToUpperInvariant() switch
    {
        "PERSON" or "PER" or "NAME" or "PEOPLE" or "PERSOANA" => EntityTypes.Person,
        "ORGANIZATION" or "ORGANISATION" or "ORG" or "COMPANY" or "BANK" or "CLIENT" or "CUSTOMER" or "SUPPLIER" => EntityTypes.Org,
        "LOCATION" or "LOC" or "PLACE" or "CITY" or "COUNTRY" or "GPE" or "ADDRESS" or "OFFICE" => EntityTypes.Location,
        _ => EntityTypes.Other,
    };
}

public static class Chunker
{
    /// <summary>Splits on sentence/line boundaries into chunks of at most <paramref name="maxChars"/>.</summary>
    public static List<string> Split(string text, int maxChars)
    {
        var result = new List<string>();
        if (text.Length <= maxChars) { result.Add(text); return result; }
        var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?\n])\s+");
        var cur = new System.Text.StringBuilder();
        foreach (var s in sentences)
        {
            foreach (var piece in HardSplit(s, maxChars))
            {
                if (cur.Length > 0 && cur.Length + 1 + piece.Length > maxChars) { result.Add(cur.ToString()); cur.Clear(); }
                if (cur.Length > 0) cur.Append(' ');
                cur.Append(piece);
            }
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    private static IEnumerable<string> HardSplit(string s, int max)
    {
        while (s.Length > max)
        {
            var cut = s.LastIndexOf(' ', max);
            if (cut <= 0) cut = max;
            yield return s[..cut];
            s = s[cut..].TrimStart();
        }
        if (s.Length > 0) yield return s;
    }
}
