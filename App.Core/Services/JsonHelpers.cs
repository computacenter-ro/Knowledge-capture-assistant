using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnowledgeCapture.Core.Services;

/// <summary>Defensive JSON extraction for small local models.</summary>
public static class JsonHelpers
{
    private static readonly Regex ThinkBlock = new(@"<think>.*?(?:</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"```[a-zA-Z]*", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string StripThink(string s) => ThinkBlock.Replace(s, "").Trim();

    public static string Clean(string s) => Fence.Replace(StripThink(s), "").Trim();

    /// <summary>First balanced <paramref name="open"/>...<paramref name="close"/> block, string/escape aware; null if none is complete.</summary>
    public static string? ExtractFirst(string s, char open, char close)
    {
        var start = s.IndexOf(open);
        while (start >= 0)
        {
            int depth = 0; bool inStr = false, esc = false;
            for (var i = start; i < s.Length; i++)
            {
                var c = s[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') inStr = true;
                else if (c == open) depth++;
                else if (c == close && --depth == 0) return s[start..(i + 1)];
            }
            start = s.IndexOf(open, start + 1);
        }
        return null;
    }

    /// <summary>True when the text opens a block that never closes (typical for a max-token cut-off).</summary>
    public static bool LooksTruncated(string s, char open, char close) =>
        s.Contains(open) && ExtractFirst(s, open, close) is null;

    public static bool TryParse<T>(string raw, char open, char close, out T? value)
    {
        value = default;
        var block = ExtractFirst(Clean(raw), open, close);
        if (block is null) return false;
        try
        {
            value = JsonSerializer.Deserialize<T>(block, Options);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
