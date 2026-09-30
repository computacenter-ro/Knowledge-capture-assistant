using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnowledgeCapture.Core.Models;

namespace KnowledgeCapture.Core.Services.Speech;

/// <param name="NoSpeech">True when the recording held no speech, so Whisper was not called.</param>
public sealed record Transcript(string Text, double AudioSeconds, double ElapsedSeconds, int Pieces, bool NoSpeech);

/// <summary>
/// Local speech-to-text: Whisper through Foundry Local (<c>foundry transcribe</c>). Foundry 0.10 serves Whisper on the CPU
/// and not over its REST API, so each piece goes through the CLI, which hands it to the running daemon (model stays loaded).
/// Privacy: audio pieces are written with random names to <see cref="DataPaths.AudioTemp"/> (%TEMP%) and overwritten + deleted right
/// after use; the transcript is never logged (Foundry's own log records only the file name and language).
/// </summary>
public sealed class SpeechService
{
    private SpeechSettings _settings = new();

    public string Model { get; private set; } = "";
    public string Device { get; private set; } = "";
    public bool IsReady => Model.Length > 0;
    public string ShortModelName => Regex.Replace(Model, @"^openai-|(-generic)?-(cpu|gpu|npu)(:\d+)?$", "", RegexOptions.IgnoreCase);

    /// <summary>Finds the Whisper model in the Foundry cache and loads it into the daemon, so the first answer is fast.</summary>
    public async Task InitAsync(SpeechSettings settings, CancellationToken ct = default)
    {
        _settings = settings;
        Model = "";
        PurgeTemp();
        var (code, output) = await FoundryCli.RunAsync("model list --cached -o json", TimeSpan.FromMinutes(2), ct);
        if (code == -2) throw new InvalidOperationException("Voice input needs Foundry Local. Install it with: winget install Microsoft.FoundryLocal");
        var models = (code == 0 ? ParseSpeechModels(output) : null)
            ?? throw new InvalidOperationException("Voice input needs Foundry Local 0.10 or newer (foundry model list -o json).");
        var want = string.IsNullOrWhiteSpace(settings.Model) ? "whisper-small" : settings.Model.Trim();
        var pick = models.FirstOrDefault(m => m.Alias.Equals(want, StringComparison.OrdinalIgnoreCase) || m.Id.Equals(want, StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(m => m.Id.Contains(want, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Voice input needs a Whisper model. Run: foundry model download {want}");

        var sw = Stopwatch.StartNew();
        var (loadCode, _) = await FoundryCli.RunAsync(["model", "load", pick.Id], TimeSpan.FromMinutes(5), ct);
        if (loadCode != 0) throw new InvalidOperationException($"Foundry could not load {pick.Id}. Run: foundry model load {pick.Id}");
        Model = pick.Id;
        Device = LlmService.DeviceOf(pick.Id + " " + pick.Device);
        AppLog.Info($"Speech model={Model} device={Device} language={settings.Language} loaded in {sw.Elapsed.TotalSeconds:0.0}s");
    }

    /// <param name="language">ISO 639-1 code ("en", "ro"), or null/"auto" to let Whisper detect it.</param>
    public async Task<Transcript> TranscribeAsync(short[] samples, int sampleRate, string? language, CancellationToken ct = default)
    {
        if (!IsReady) throw new InvalidOperationException("The speech model is not ready.");
        var sw = Stopwatch.StartNew();
        var audioSeconds = samples.Length / (double)sampleRate;
        var speech = WavAudio.TrimSilence(samples, sampleRate);
        try
        {
            if (!WavAudio.HasSpeech(speech, sampleRate)) return new Transcript("", audioSeconds, sw.Elapsed.TotalSeconds, 0, NoSpeech: true);
            WavAudio.Normalize(speech);
            var pieces = WavAudio.Split(speech, sampleRate, Math.Clamp(_settings.ChunkSeconds, 5, 29));
            var parts = new List<string>();
            var sent = 0;
            foreach (var (offset, count) in pieces)
            {
                if (!WavAudio.HasSpeech(speech, offset, count, sampleRate)) continue;
                var text = await TranscribePieceAsync(WavAudio.Encode(speech, offset, count, sampleRate), language, ct);
                sent++;
                if (CleanTranscript(text) is { Length: > 0 } clean) parts.Add(clean);
            }
            var joined = string.Join(" ", parts);
            AppLog.Info($"Transcribed {audioSeconds:0.0}s audio in {pieces.Count} piece(s) ({sent} sent) in {sw.Elapsed.TotalSeconds:0.0}s, {joined.Length} chars");
            return new Transcript(joined, audioSeconds, sw.Elapsed.TotalSeconds, sent, NoSpeech: joined.Length == 0);
        }
        finally
        {
            Array.Clear(speech);
        }
    }

    private async Task<string> TranscribePieceAsync(byte[] wav, string? language, CancellationToken ct)
    {
        Directory.CreateDirectory(DataPaths.AudioTemp);
        var path = Path.Combine(DataPaths.AudioTemp, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await File.WriteAllBytesAsync(path, wav, ct);
            List<string> args = ["transcribe", "-m", Model, "-f", path, "-o", "json"];
            if (!string.IsNullOrWhiteSpace(language) && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
                args.AddRange(["-l", language.Trim().ToLowerInvariant()]);
            for (var attempt = 0; ; attempt++)
            {
                var (code, output) = await FoundryCli.RunAsync(args, TimeSpan.FromMinutes(3), ct);
                if (code == 0 && ParseText(output) is { } text) return text;
                if (code == -2) throw new InvalidOperationException("Foundry Local is not installed.");
                AppLog.Warn($"Speech transcribe failed (exit {code}, {ParseErrorCode(output) ?? "no error code"}), attempt {attempt + 1}");
                if (attempt >= 1) throw new InvalidOperationException("Whisper could not transcribe the recording. Check that Foundry Local is running (foundry server status), then try again.");
                await Task.Delay(TimeSpan.FromSeconds(1), ct); // the daemon can reject a call while it is busy loading
            }
        }
        finally
        {
            Array.Clear(wav);
            Wipe(path);
        }
    }

    // ---------------------------------------------------------------- text cleanup

    private static readonly Regex Markers = new(
        @"\[[^\]]{0,40}\]|\((?:music|applause|laughs?|laughter|silence|noise|inaudible|coughs?|blank[ _]audio)\)|[♪♫]+|\*(?:music|silence)\*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>Phrases Whisper is known to invent on noise; never plausible in a work interview.</summary>
    private static readonly Regex Hallucinations = new(
        @"^(?:thanks? (?:you )?for watching[.!]*|please subscribe[.!]*|subtitles? by .*|.*amara\.org.*|sous-titr.*|mulțumesc pentru vizionare[.!]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Drops non-speech markers ([BLANK_AUDIO], (music), ♪) and known silence hallucinations; collapses whitespace.</summary>
    public static string CleanTranscript(string text)
    {
        var t = Spaces.Replace(Markers.Replace(text, " "), " ").Trim();
        return Hallucinations.IsMatch(t) ? "" : t;
    }

    /// <summary>`foundry transcribe -o json` → {"model":…,"text":" …","language":"en"}; errors come back as {"error":{…}}.</summary>
    public static string? ParseText(string output)
    {
        var block = JsonHelpers.ExtractFirst(output, '{', '}');
        if (block is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(block);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _)) return null;
            return root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The error code only (e.g. INVALID_ARGUMENT): safe to log, unlike the message.</summary>
    private static string? ParseErrorCode(string output)
    {
        var block = JsonHelpers.ExtractFirst(output, '{', '}');
        if (block is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(block);
            return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed record SpeechModel(string Id, string Alias, string Device);

    private static List<SpeechModel>? ParseSpeechModels(string output)
    {
        var block = JsonHelpers.ExtractFirst(output, '{', '}');
        if (block is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(block);
            if (!doc.RootElement.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<SpeechModel>();
            foreach (var m in arr.EnumerateArray())
            {
                string Str(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var id = Str("id");
                if (id.Length == 0) continue;
                if (!Str("type").Equals("Speech", StringComparison.OrdinalIgnoreCase) && !id.Contains("whisper", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new SpeechModel(id, Str("alias"), Str("device")));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    // ---------------------------------------------------------------- temp audio

    /// <summary>Removes audio pieces left behind by a crash.</summary>
    public static void PurgeTemp()
    {
        try
        {
            if (!Directory.Exists(DataPaths.AudioTemp)) return;
            foreach (var f in Directory.EnumerateFiles(DataPaths.AudioTemp)) Wipe(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Error("purge audio temp", ex); }
    }

    /// <summary>Overwrites the file with zeros before deleting it (the recording is raw personal data).</summary>
    private static void Wipe(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[64 * 1024];
                for (var left = fs.Length; left > 0; left -= zeros.Length) fs.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
                fs.Flush(true);
            }
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Error("wipe audio piece", ex); }
    }
}
