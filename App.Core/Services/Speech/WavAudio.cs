using System.Buffers.Binary;

namespace KnowledgeCapture.Core.Services.Speech;

/// <summary>
/// Deterministic 16-bit PCM helpers for the Whisper path: canonical WAV encode/decode, speech detection, silence trim,
/// gain and splitting at quiet points (Whisper hears 30 s at a time). No LLM involved; covered by the offline tests.
/// </summary>
public static class WavAudio
{
    private const int FrameMs = 30;
    /// <summary>Frame RMS above this (≈ -42 dBFS) counts as voiced; a quiet room with a laptop mic sits well below it.</summary>
    public const double SpeechRms = 0.008;
    /// <summary>At least this much voiced audio (seconds) before a clip is worth sending to Whisper.</summary>
    public const double MinSpeechSeconds = 0.3;

    /// <summary>Canonical 44-byte RIFF header + mono 16-bit PCM (the Foundry audio decoder rejects unusual headers).</summary>
    public static byte[] Encode(short[] samples, int offset, int count, int sampleRate)
    {
        var dataBytes = count * 2;
        var wav = new byte[44 + dataBytes];
        var s = wav.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);          // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);           // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1);           // mono
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], sampleRate * 2); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);           // block align
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);          // bits per sample
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], dataBytes);
        Buffer.BlockCopy(samples, offset * 2, wav, 44, dataBytes);
        return wav;
    }

    public static byte[] Encode(short[] samples, int sampleRate) => Encode(samples, 0, samples.Length, sampleRate);

    /// <summary>Reads a 16-bit PCM WAV (any extra chunks, any fmt size); multi-channel audio is mixed down to mono.</summary>
    public static (short[] Samples, int SampleRate) Decode(byte[] wav)
    {
        var s = wav.AsSpan();
        if (s.Length < 12 || !s[..4].SequenceEqual("RIFF"u8) || !s[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        int channels = 0, rate = 0, bits = 0, format = 0;
        var pos = 12;
        while (pos + 8 <= s.Length)
        {
            var id = s.Slice(pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(s[(pos + 4)..]);
            var body = pos + 8;
            if (size < 0 || body > s.Length) break;
            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(s[body..]);
                channels = BinaryPrimitives.ReadInt16LittleEndian(s[(body + 2)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(s[(body + 4)..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(s[(body + 14)..]);
                if (format == 0xFFFE && size >= 26) format = BinaryPrimitives.ReadInt16LittleEndian(s[(body + 24)..]); // EXTENSIBLE
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format != 1 || bits != 16 || channels < 1 || rate <= 0)
                    throw new InvalidDataException("Only 16-bit PCM WAV is supported.");
                var bytes = Math.Min(size, s.Length - body);
                var frames = bytes / (2 * channels);
                var mono = new short[frames];
                for (var f = 0; f < frames; f++)
                {
                    var sum = 0;
                    for (var c = 0; c < channels; c++)
                        sum += BinaryPrimitives.ReadInt16LittleEndian(s[(body + (f * channels + c) * 2)..]);
                    mono[f] = (short)(sum / channels);
                }
                return (mono, rate);
            }
            pos = body + size + (size & 1); // chunks are word-aligned
        }
        throw new InvalidDataException("WAV file has no data chunk.");
    }

    /// <summary>True when every sample is exactly zero: a blocked microphone (privacy setting) records digital silence.</summary>
    public static bool IsDigitalSilence(short[] samples) => Array.TrueForAll(samples, v => v == 0);

    public static bool HasSpeech(short[] samples, int offset, int count, int sampleRate) =>
        VoicedSeconds(samples, offset, count, sampleRate) >= MinSpeechSeconds;

    public static bool HasSpeech(short[] samples, int sampleRate) => HasSpeech(samples, 0, samples.Length, sampleRate);

    public static double VoicedSeconds(short[] samples, int offset, int count, int sampleRate)
    {
        var frame = FrameLength(sampleRate);
        var voiced = 0;
        for (var i = offset; i + frame <= offset + count; i += frame)
            if (Rms(samples, i, frame) >= SpeechRms) voiced++;
        return voiced * frame / (double)sampleRate;
    }

    /// <summary>Normalized RMS (0..1).</summary>
    public static double Rms(short[] samples, int offset, int count)
    {
        if (count <= 0) return 0;
        double sum = 0;
        for (var i = offset; i < offset + count; i++) sum += (double)samples[i] * samples[i];
        return Math.Sqrt(sum / count) / 32768.0;
    }

    /// <summary>Cuts leading/trailing silence (keeping a little padding): Whisper tends to invent text in long silences.</summary>
    public static short[] TrimSilence(short[] samples, int sampleRate, double paddingSeconds = 0.3)
    {
        var frame = FrameLength(sampleRate);
        int first = -1, last = -1;
        for (var i = 0; i + frame <= samples.Length; i += frame)
        {
            if (Rms(samples, i, frame) < SpeechRms) continue;
            if (first < 0) first = i;
            last = i + frame;
        }
        if (first < 0) return [];
        var pad = (int)(paddingSeconds * sampleRate);
        var start = Math.Max(0, first - pad);
        var end = Math.Min(samples.Length, last + pad);
        return samples[start..end];
    }

    /// <summary>Scales a quiet recording up so its peak reaches ~ -3 dBFS (at most <paramref name="maxGain"/>×). In place.</summary>
    public static void Normalize(short[] samples, double maxGain = 8)
    {
        var peak = 0;
        foreach (var v in samples) peak = Math.Max(peak, Math.Abs((int)v));
        if (peak == 0) return;
        var gain = Math.Min(maxGain, 0.7 * 32767 / peak);
        if (gain <= 1.05) return;
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// Splits into pieces of at most <paramref name="maxSeconds"/>. Each cut is placed in the quietest 30 ms frame of the
    /// last <paramref name="searchSeconds"/> before the limit, so words are not cut in half.
    /// </summary>
    public static List<(int Offset, int Count)> Split(short[] samples, int sampleRate, double maxSeconds, double searchSeconds = 4)
    {
        var pieces = new List<(int, int)>();
        var max = Math.Max(1, (int)(maxSeconds * sampleRate));
        var search = Math.Min(max / 2, (int)(searchSeconds * sampleRate));
        var frame = FrameLength(sampleRate);
        var hop = Math.Max(1, frame / 3);
        var pos = 0;
        while (samples.Length - pos > max)
        {
            var limit = pos + max;
            var cut = limit;
            var best = double.MaxValue;
            for (var i = limit - frame; i >= limit - search; i -= hop)
            {
                var r = Rms(samples, i, frame);
                if (r < best) { best = r; cut = i + frame / 2; }
            }
            pieces.Add((pos, cut - pos));
            pos = cut;
        }
        if (samples.Length > pos) pieces.Add((pos, samples.Length - pos));
        return pieces;
    }

    private static int FrameLength(int sampleRate) => Math.Max(1, sampleRate * FrameMs / 1000);
}
