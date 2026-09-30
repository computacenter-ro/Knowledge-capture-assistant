using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeCapture.Core.Models;
using OpenAI;
using OpenAI.Chat;

namespace KnowledgeCapture.Core.Services;

public sealed class StreamStats
{
    public int Tokens { get; set; }
    public double? TtftSeconds { get; set; }
    public double TotalSeconds { get; set; }
    public ChatFinishReason? FinishReason { get; set; }
    public bool Truncated => FinishReason == ChatFinishReason.Length;
    public double TokensPerSecond => Tokens / Math.Max(0.01, TotalSeconds - (TtftSeconds ?? 0));
}

public sealed record LlmResult(string Text, bool Truncated, StreamStats Stats);

/// <summary>Local OpenAI-compatible client (Foundry Local / GenieX on localhost). Never talks to the cloud.</summary>
public sealed class LlmService
{
    private ChatClient? _chat;
    private readonly LlmGate _gate = new();

    public string BaseUrl { get; private set; } = "";
    public string Model { get; private set; } = "";
    public string Provider { get; private set; } = "foundry";
    public string Device { get; private set; } = "";
    public int MaxInputTokens { get; private set; } = 4096;
    public int MaxOutputTokens { get; private set; } = 1024;
    public bool IsReady => _chat is not null;

    public string ShortModelName => Regex.Replace(Model, @"(-instruct)?(-(openvino|qnn|generic|cuda|vitis))?-(npu|gpu|cpu)(:\d+)?$", "", RegexOptions.IgnoreCase);

    public async Task InitAsync(LlmSettings o, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        _chat = null;
        Provider = string.IsNullOrWhiteSpace(o.Provider) ? "foundry" : o.Provider.Trim().ToLowerInvariant();
        progress?.Report("Finding the local model service…");
        BaseUrl = !string.IsNullOrWhiteSpace(o.BaseUrl) ? o.BaseUrl.TrimEnd('/')
                : Provider == "geniex" ? "http://127.0.0.1:18181/v1"
                : await DiscoverFoundryAsync(ct);
        GuardLocal(BaseUrl);

        var alias = string.IsNullOrWhiteSpace(o.Model) ? "phi-3.5-mini" : o.Model.Trim();
        var device = string.IsNullOrWhiteSpace(o.Device) ? "npu" : o.Device.Trim().ToLowerInvariant();
        var pick = Provider == "foundry" ? await ResolveFoundryModelAsync(alias, device, progress, ct) : null;
        var apiModels = await ListModelsAsync(BaseUrl, ct);
        pick ??= Pick(apiModels, alias, device); // GenieX / custom / Foundry < 0.10
        if (pick is null)
            throw new InvalidOperationException(
                $"No {device.ToUpperInvariant()} model is available at {BaseUrl}. " +
                $"Run: foundry model list --device {device}  then  foundry model load <model id>");
        Model = pick.Id;
        Device = DeviceOf(pick.Id + " " + pick.Device);

        // token limits: API (Foundry < 0.10) -> the model's own genai_config (static NPU shapes) -> defaults
        var api = apiModels.FirstOrDefault(m => m.Id.Equals(Model, StringComparison.OrdinalIgnoreCase)) ?? pick;
        var (cfgIn, cfgOut) = ReadCachedLimits(Model);
        MaxInputTokens = api.MaxInputTokens > 0 ? api.MaxInputTokens : cfgIn > 0 ? cfgIn : 4096;
        MaxOutputTokens = api.MaxOutputTokens > 0 ? api.MaxOutputTokens : cfgOut > 0 ? cfgOut : 1024;

        var client = new OpenAIClient(new ApiKeyCredential(string.IsNullOrWhiteSpace(o.ApiKey) ? "local" : o.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(BaseUrl),
                NetworkTimeout = TimeSpan.FromMinutes(10),
                RetryPolicy = new ClientRetryPolicy(0),
            });
        var chat = client.GetChatClient(Model);

        progress?.Report($"Loading model on {Device}…");
        AppLog.Info($"LLM provider={Provider} base={BaseUrl} model={Model} device={Device} maxIn={MaxInputTokens} maxOut={MaxOutputTokens}");
        var sw = Stopwatch.StartNew();
        try
        {
            await WarmUpAsync(chat, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsTransient(ex) && Provider == "foundry")
        {
            // Foundry 0.10 does not load cached models on demand: load explicitly, then retry once.
            AppLog.Warn($"Warm-up failed ({ex.GetType().Name}); loading {Model} via foundry CLI");
            var (code, _) = await FoundryCli.RunAsync($"model load {Model}", TimeSpan.FromMinutes(10), ct);
            if (code != 0) throw new InvalidOperationException($"Could not load {Model} on the {Device}. Run: foundry model load {Model}");
            await WarmUpAsync(chat, ct);
        }
        AppLog.Info($"LLM warm-up done in {sw.Elapsed.TotalSeconds:0.0}s");
        _chat = chat;
    }

    private static async Task WarmUpAsync(ChatClient chat, CancellationToken ct)
    {
        var opts = new ChatCompletionOptions { MaxOutputTokenCount = 1, Temperature = 0f };
        var (e, _) = await OpenStreamAsync(chat, [new UserChatMessage("hi")], opts, ct);
        try { while (await e.MoveNextAsync()) { } }
        finally { await e.DisposeAsync(); }
    }

    public async IAsyncEnumerable<string> StreamAsync(IEnumerable<ChatMessage> msgs, float temperature = 0.4f,
        int maxTokens = 800, bool interactive = true, StreamStats? stats = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var chat = _chat ?? throw new InvalidOperationException("The local model is not ready.");
        var opts = new ChatCompletionOptions
        {
            Temperature = temperature,
            MaxOutputTokenCount = Math.Max(1, Math.Min(maxTokens, MaxOutputTokens)),
        };
        using var _ = await _gate.EnterAsync(interactive, ct);
        var sw = Stopwatch.StartNew();
        var msgList = msgs as IList<ChatMessage> ?? msgs.ToList();
        var (e, hasFirst) = await OpenStreamAsync(chat, msgList, opts, ct);
        try
        {
            var first = true;
            while (true)
            {
                StreamingChatCompletionUpdate u;
                if (first)
                {
                    first = false;
                    if (!hasFirst) break;
                    u = e.Current;
                }
                else
                {
                    try
                    {
                        if (!await e.MoveNextAsync()) break;
                        u = e.Current;
                    }
                    catch (ClientResultException ex) when (IsPromptTooLong(ex))
                    {
                        throw new PromptTooLongException();
                    }
                }
                if (u.FinishReason is { } fr && stats is not null) stats.FinishReason = fr;
                foreach (var part in u.ContentUpdate)
                {
                    if (string.IsNullOrEmpty(part.Text)) continue;
                    if (stats is not null)
                    {
                        stats.TtftSeconds ??= sw.Elapsed.TotalSeconds;
                        stats.Tokens++;
                        stats.TotalSeconds = sw.Elapsed.TotalSeconds;
                    }
                    yield return part.Text;
                }
            }
        }
        finally
        {
            await e.DisposeAsync();
        }
        if (stats is not null) stats.TotalSeconds = sw.Elapsed.TotalSeconds;
    }

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6)];

    /// <summary>
    /// Starts the stream and reads the first update. Transient failures before any token arrived are retried with backoff:
    /// Foundry rejects a request while it is still generating for another one (e.g. a second app instance).
    /// </summary>
    private static async Task<(IAsyncEnumerator<StreamingChatCompletionUpdate> E, bool HasFirst)> OpenStreamAsync(
        ChatClient chat, IList<ChatMessage> msgs, ChatCompletionOptions opts, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var e = chat.CompleteChatStreamingAsync(msgs, opts, ct).GetAsyncEnumerator(ct);
            try
            {
                return (e, await e.MoveNextAsync());
            }
            catch (ClientResultException ex) when (IsPromptTooLong(ex))
            {
                await e.DisposeAsync();
                throw new PromptTooLongException();
            }
            catch (Exception ex) when (attempt < Backoff.Length && !ct.IsCancellationRequested && IsTransient(ex))
            {
                await e.DisposeAsync();
                AppLog.Warn($"Transient model error ({ex.GetType().Name}); retry {attempt + 1}/{Backoff.Length} in {Backoff[attempt].TotalSeconds}s");
                await Task.Delay(Backoff[attempt], ct);
            }
            catch
            {
                await e.DisposeAsync();
                throw;
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is IOException or HttpRequestException || ex is ClientResultException { Status: >= 500 or 0 };

    private static bool IsPromptTooLong(ClientResultException ex)
    {
        var body = ex.Message + " " + (ex.GetRawResponse()?.Content?.ToString() ?? "");
        return body.Contains("MAX_PROMPT_LEN", StringComparison.OrdinalIgnoreCase)
            || body.Contains("prompt is longer", StringComparison.OrdinalIgnoreCase)
            || body.Contains("context length", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Non-interactive completion (collects the stream). Used for JSON calls, NER, summaries, titles.</summary>
    public async Task<LlmResult> CompleteAsync(IEnumerable<ChatMessage> msgs, float temperature, int maxTokens,
        bool interactive = false, CancellationToken ct = default)
    {
        var stats = new StreamStats();
        var sb = new StringBuilder();
        await foreach (var t in StreamAsync(msgs, temperature, maxTokens, interactive, stats, ct)) sb.Append(t);
        return new LlmResult(sb.ToString(), stats.Truncated, stats);
    }

    /// <summary>
    /// Conservative token estimate (~3 chars/token) for budgeting: Romanian text and &lt;PERSON_1&gt;-style placeholders
    /// tokenize densely, and a static-shape NPU model rejects any prompt over its limit.
    /// </summary>
    public static int EstimateTokens(string? s) => string.IsNullOrEmpty(s) ? 0 : (s.Length + 2) / 3 + 4;

    public static string DeviceOf(string modelId) =>
        modelId.Contains("npu", StringComparison.OrdinalIgnoreCase) ? "NPU"
        : modelId.Contains("gpu", StringComparison.OrdinalIgnoreCase) ? "GPU"
        : modelId.Contains("cpu", StringComparison.OrdinalIgnoreCase) ? "CPU" : "local";

    // ---------------------------------------------------------------- discovery

    private sealed record ModelInfo(string Id, int MaxInputTokens, int MaxOutputTokens, string Alias = "", string Device = "");

    private static ModelInfo? Pick(IEnumerable<ModelInfo> models, string alias, string device)
    {
        var candidates = models.Where(m => device == "any"
            || m.Device.Equals(device, StringComparison.OrdinalIgnoreCase)
            || m.Id.Contains(device, StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.FirstOrDefault(m => m.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(m => m.Id.Contains(alias, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Foundry Local 0.10+: the exact id to send is the LOADED variant id (e.g. phi-4-mini-instruct-openvino-npu:1);
    /// /v1/models lists ids without the version suffix, which the service rejects as "not loaded".
    /// If no matching model is loaded, load a cached one for the required device.
    /// </summary>
    private static async Task<ModelInfo?> ResolveFoundryModelAsync(string alias, string device, IProgress<string>? progress, CancellationToken ct)
    {
        var loaded = await FoundryModelsAsync("model list --loaded -o json", ct);
        if (loaded is null) return null; // older CLI without JSON output
        var pick = Pick(loaded, alias, device);
        if (pick is not null) return pick;

        var cached = await FoundryModelsAsync($"model list --cached -o json{(device == "any" ? "" : " --device " + device)}", ct) ?? [];
        var candidate = Pick(cached, alias, device);
        if (candidate is null) return null;
        progress?.Report($"Loading {candidate.Id} on {DeviceOf(candidate.Id + " " + candidate.Device)} (first load can take a minute)…");
        AppLog.Info($"Loading cached model {candidate.Id} via foundry CLI");
        var (code, _) = await FoundryCli.RunAsync($"model load {candidate.Id}", TimeSpan.FromMinutes(10), ct);
        if (code != 0)
            throw new InvalidOperationException($"Foundry could not load {candidate.Id} on the {device.ToUpperInvariant()}. Run: foundry model load {candidate.Id}");
        return candidate;
    }

    private static async Task<List<ModelInfo>?> FoundryModelsAsync(string args, CancellationToken ct)
    {
        var (code, output) = await FoundryCli.RunAsync(args, TimeSpan.FromMinutes(2), ct);
        var block = code == 0 ? JsonHelpers.ExtractFirst(output, '{', '}') : null;
        if (block is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(block);
            if (!doc.RootElement.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<ModelInfo>();
            foreach (var m in arr.EnumerateArray())
            {
                var id = m.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                list.Add(new ModelInfo(id, 0, 0,
                    m.TryGetProperty("alias", out var a) ? a.GetString() ?? "" : "",
                    m.TryGetProperty("device", out var d) ? d.GetString() ?? "" : ""));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Static-shape NPU builds reject prompts over NPUW_LLM_MAX_PROMPT_LEN. Foundry 0.10 no longer reports limits over the
    /// API, so read them from the model's cached genai_config.json (MAX_PROMPT_LEN / MIN_RESPONSE_LEN / context_length).
    /// </summary>
    private static (int MaxIn, int MaxOut) ReadCachedLimits(string modelId)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "cache", "models");
            if (!Directory.Exists(root)) return (0, 0);
            var folder = modelId.Replace(':', '-');
            var cfg = Directory.EnumerateDirectories(root, folder, SearchOption.AllDirectories)
                .Select(d => Directory.EnumerateFiles(d, "genai_config.json", SearchOption.AllDirectories).FirstOrDefault())
                .FirstOrDefault(f => f is not null);
            if (cfg is null) return (0, 0);
            var text = File.ReadAllText(cfg);
            int Find(string key)
            {
                var m = Regex.Match(text, $@"\\?""{key}\\?""\s*:\s*\\?""?(\d+)");
                return m.Success ? int.Parse(m.Groups[1].Value) : 0;
            }
            var ctx = Find("context_length");
            var maxPrompt = Find("MAX_PROMPT_LEN");
            var minResponse = Find("MIN_RESPONSE_LEN");
            var maxIn = maxPrompt > 0 ? maxPrompt : ctx > 0 ? ctx * 3 / 4 : 0;
            var maxOut = minResponse > 0 ? minResponse : ctx > 0 ? ctx - maxIn : 0;
            return (maxIn, maxOut);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private static async Task<List<ModelInfo>> ListModelsAsync(string baseUrl, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var doc = JsonDocument.Parse(await http.GetStringAsync(baseUrl + "/models", ct));
        var list = new List<ModelInfo>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;
        foreach (var m in data.EnumerateArray())
        {
            var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            list.Add(new ModelInfo(id, Int(m, "maxInputTokens"), Int(m, "maxOutputTokens")));
        }
        return list;

        static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
    }

    private static readonly Regex LocalUrl = new(@"http://(?:127\.0\.0\.1|localhost):\d+", RegexOptions.IgnoreCase);

    private static async Task<string> DiscoverFoundryAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // Foundry Local 0.10+: `foundry server status -o json` -> {"running":true,"webUrls":["http://127.0.0.1:PORT"]}
            var (code, json) = await FoundryCli.RunAsync("server status -o json", TimeSpan.FromSeconds(60), ct);
            if (code == 0 && TryParseServerJson(json) is { } url) return url + "/v1";
            // Foundry Local 0.7-0.9: `foundry service status` prints the endpoint
            var (_, text) = await FoundryCli.RunAsync("service status", TimeSpan.FromSeconds(60), ct);
            var m = LocalUrl.Match(text);
            if (m.Success && !text.Contains("not running", StringComparison.OrdinalIgnoreCase)) return m.Value + "/v1";

            if (attempt == 0)
            {
                AppLog.Info("Foundry service not running; starting it");
                var (startCode, _) = await FoundryCli.RunAsync("server start", TimeSpan.FromMinutes(3), ct);
                if (startCode != 0) await FoundryCli.RunAsync("service start", TimeSpan.FromMinutes(3), ct);
            }
        }
        throw new InvalidOperationException("Foundry Local is not running. Run: foundry server start (then: foundry model load <npu model id>)");
    }

    private static string? TryParseServerJson(string output)
    {
        var block = JsonHelpers.ExtractFirst(output, '{', '}');
        if (block is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(block);
            var root = doc.RootElement;
            if (root.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.False) return null;
            if (root.TryGetProperty("webUrls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                foreach (var u in urls.EnumerateArray())
                    if (u.GetString() is { } s && LocalUrl.Match(s) is { Success: true } m) return m.Value;
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Hard guard: this app only ever talks to a model on this machine.</summary>
    private static void GuardLocal(string url)
    {
        var host = new Uri(url).Host;
        if (host is not ("127.0.0.1" or "localhost" or "::1" or "[::1]"))
            throw new InvalidOperationException($"Refusing non-local model endpoint '{host}'. All inference must run on this device.");
    }
}
