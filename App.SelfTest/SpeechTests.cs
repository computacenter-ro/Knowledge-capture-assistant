using System.Buffers.Binary;
using System.Diagnostics;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using KnowledgeCapture.Core.Models;
using KnowledgeCapture.Core.Services;
using KnowledgeCapture.Core.Services.Speech;

namespace KnowledgeCapture.SelfTest;

/// <summary>Voice answers: deterministic audio helpers (offline) and Whisper via Foundry Local on synthesized speech (online).</summary>
internal static class SpeechTests
{
    private const int Rate = 16000;

    public static void Offline(Tests t)
    {
        t.Section("Speech: audio helpers (deterministic)");

        var tone = Tone(1.0, 0.3);
        var (back, rate) = WavAudio.Decode(WavAudio.Encode(tone, Rate));
        t.Check("WAV encode → decode round-trip", rate == Rate && back.AsSpan().SequenceEqual(tone), $"{back.Length} samples @ {rate} Hz");
        var header = WavAudio.Encode(tone, Rate);
        t.Check("encoded WAV has a canonical 44-byte header", header.Length == 44 + tone.Length * 2
            && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16)) == 16 && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40)) == tone.Length * 2);

        var odd = OddWav(tone);
        var (mono, oddRate) = WavAudio.Decode(odd);
        t.Check("decodes fmt size 18 + LIST chunk + stereo (mixed to mono)", oddRate == Rate && mono.Length == tone.Length && mono[100] == tone[100]);
        t.Check("non-PCM / garbage is rejected", Throws(() => WavAudio.Decode([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13])));

        var silence = new short[Rate * 2];
        var hiss = Noise(2.0, 0.002);
        t.Check("digital silence is detected (blocked mic)", WavAudio.IsDigitalSilence(silence) && !WavAudio.IsDigitalSilence(hiss));
        t.Check("silence and room hiss are not speech", !WavAudio.HasSpeech(silence, Rate) && !WavAudio.HasSpeech(hiss, Rate));
        t.Check("a voiced second is speech", WavAudio.HasSpeech(tone, Rate));

        var padded = Concat(Noise(3.0, 0.002), tone, Noise(3.0, 0.002));
        var trimmed = WavAudio.TrimSilence(padded, Rate);
        t.Check("leading/trailing silence is trimmed (with padding)", trimmed.Length is > Rate and < (int)(Rate * 1.8), $"{padded.Length / (double)Rate:0.0}s → {trimmed.Length / (double)Rate:0.00}s");

        var quiet = Tone(1.0, 0.02);
        WavAudio.Normalize(quiet);
        t.Check("quiet recording is amplified (≤ 8×)", quiet.Max() is > 4000 and <= 23000, $"peak {quiet.Max()}");

        // 70 s "speech" with a short pause every 5 s: every cut must land inside a pause
        var parts = new List<short[]>();
        for (var i = 0; i < 14; i++) { parts.Add(Tone(4.6, 0.3)); parts.Add(new short[(int)(0.4 * Rate)]); }
        var longClip = Concat([.. parts]);
        var pieces = WavAudio.Split(longClip, Rate, 28);
        var contiguous = pieces[0].Offset == 0 && pieces.Zip(pieces.Skip(1)).All(p => p.First.Offset + p.First.Count == p.Second.Offset)
            && pieces[^1].Offset + pieces[^1].Count == longClip.Length;
        var inPause = pieces.Skip(1).All(p => Math.Abs((int)longClip[p.Offset]) < 10);
        t.Check("long recording is split into ≤ 28 s pieces, contiguous, cut in pauses",
            pieces.Count == 3 && pieces.All(p => p.Count <= 28 * Rate) && contiguous && inPause,
            string.Join(" + ", pieces.Select(p => $"{p.Count / (double)Rate:0.0}s")));
        t.Check("short recording stays one piece", WavAudio.Split(tone, Rate, 28).Count == 1);

        t.Section("Speech: transcript cleanup");
        t.Check("non-speech markers are removed", SpeechService.CleanTranscript(" [BLANK_AUDIO] I use SAP (music) daily ♪ ") == "I use SAP daily");
        t.Check("known silence hallucination is dropped", SpeechService.CleanTranscript(" Thanks for watching!") == "");
        t.Check("normal speech is kept", SpeechService.CleanTranscript(" Every month I reconcile invoices.") == "Every month I reconcile invoices.");
        t.Check("CLI JSON → text", SpeechService.ParseText("{\"model\":\"m\",\"text\":\" Hello there.\",\"language\":\"en\"}\n") == " Hello there.");
        t.Check("CLI error JSON → null", SpeechService.ParseText("{\"error\":{\"code\":\"SERVER_ERROR\",\"message\":\"x\"}}") is null);
        t.Check("garbage → null", SpeechService.ParseText("not json") is null);
    }

    public static async Task OnlineAsync(Tests t, SpeechSettings settings)
    {
        t.Section($"Speech: Whisper via Foundry Local ({settings.Model})");
        var speech = new SpeechService();
        var sw = Stopwatch.StartNew();
        try { await speech.InitAsync(settings); }
        catch (Exception ex) { t.Check("speech model initialised", false, ex.Message); return; }
        t.Check("speech model initialised", true, $"{speech.Model} on {speech.Device} ({sw.Elapsed.TotalSeconds:0.0}s)");

        async Task<Transcript> Say(string name, short[] audio, string? lang, params string[] mustHear)
        {
            var r = await speech.TranscribeAsync(audio, Rate, lang);
            var missing = mustHear.Where(w => !r.Text.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
            t.Check(name, missing.Count == 0 && !r.NoSpeech,
                $"{r.AudioSeconds:0.0}s audio → {r.ElapsedSeconds:0.0}s, {r.Pieces} piece(s): \"{Short(r.Text)}\"" + (missing.Count > 0 ? $"  MISSING: {string.Join(", ", missing)}" : ""));
            return r;
        }

        var shortAnswer = Synthesize("Every month I reconcile the supplier invoices in SAP and export the report to Excel before the fifth working day.");
        // key words only: Whisper hears the robotic SAPI voice's "invoices" as "in voices" now and then
        var first = await Say("short answer (en) is transcribed", shortAnswer, "en", "reconcile", "SAP", "Excel", "report");
        await Say("short answer with auto-detected language", shortAnswer, "auto", "reconcile", "Excel", "report");

        var longAnswer = Synthesize(
            "Step one. Every month I reconcile the supplier invoices in SAP. Step two. I download the open items list and compare it with the bank statement in Excel. " +
            "Step three. When the difference is larger than five hundred euro, I open a ticket and escalate it to my team lead. " +
            "Step four. For smaller differences I post a correction entry myself, and I attach the evidence to the ticket. " +
            "Step five. Before the fifth working day I export the final report and send it to the controlling team. " +
            "A common pitfall is that the bank statement arrives late on Mondays, so I always check the date first. " +
            "My tip for a new colleague is to keep a checklist and to never close the period before the controller confirms the numbers. " +
            "The final marker word is pineapple.");
        var longRun = await Say("60 s answer is split and fully transcribed (end marker heard)", longAnswer, "en", "invoices", "controlling", "pineapple");
        t.Check("long answer needed more than one Whisper call", longRun.Pieces >= 2, $"{longRun.Pieces} pieces");
        t.Info($"Speed: {first.AudioSeconds / Math.Max(0.01, first.ElapsedSeconds):0.0}× real time (short), {longRun.AudioSeconds / Math.Max(0.01, longRun.ElapsedSeconds):0.0}× (long)");

        var quiet = Concat(new short[Rate], Noise(3.0, 0.002));
        var q = await speech.TranscribeAsync(quiet, Rate, "en");
        t.Check("silence is not sent to Whisper (no invented text)", q.NoSpeech && q.Pieces == 0 && q.Text.Length == 0);

        var left = Directory.Exists(DataPaths.AudioTemp) ? Directory.GetFiles(DataPaths.AudioTemp).Length : 0;
        t.Check("no audio file is left on disk after transcription", left == 0, $"{left} file(s) in {DataPaths.AudioTemp}");
    }

    // ------------------------------------------------------------------ helpers

    private static short[] Synthesize(string text)
    {
        using var synth = new SpeechSynthesizer();
        using var ms = new MemoryStream();
        synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Speak(text);
        var bytes = ms.ToArray();
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    /// <summary>A "voiced" signal: 180 Hz tone with a slow envelope.</summary>
    private static short[] Tone(double seconds, double amplitude)
    {
        var n = (int)(seconds * Rate);
        var s = new short[n];
        for (var i = 0; i < n; i++)
            s[i] = (short)(amplitude * 32767 * Math.Sin(2 * Math.PI * 180 * i / Rate) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 3 * i / Rate)));
        return s;
    }

    private static short[] Noise(double seconds, double amplitude)
    {
        var rng = new Random(7);
        var s = new short[(int)(seconds * Rate)];
        for (var i = 0; i < s.Length; i++) s[i] = (short)((rng.NextDouble() * 2 - 1) * amplitude * 32767);
        return s;
    }

    private static short[] Concat(params short[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Stereo, 18-byte fmt chunk and a LIST chunk before data, like many real recorders write.</summary>
    private static byte[] OddWav(short[] mono)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var data = mono.Length * 4;
        w.Write("RIFF"u8); w.Write(4 + 26 + 20 + 8 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(18); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16); w.Write((short)0);
        w.Write("LIST"u8); w.Write(12); w.Write("INFOISFT"u8); w.Write(0);
        w.Write("data"u8); w.Write(data);
        foreach (var v in mono) { w.Write(v); w.Write(v); }
        w.Flush();
        return ms.ToArray();
    }

    private static bool Throws(Action a)
    {
        try { a(); return false; } catch (InvalidDataException) { return true; }
    }

    private static string Short(string s) => s.Length <= 140 ? s : s[..70] + " … " + s[^60..];
}
