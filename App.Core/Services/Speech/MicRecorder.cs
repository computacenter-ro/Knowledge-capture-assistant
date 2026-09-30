using NAudio.Wave;

namespace KnowledgeCapture.Core.Services.Speech;

public sealed class MicrophoneException(string message) : Exception(message);

/// <summary>
/// Microphone capture straight into memory, in the format Whisper wants (16 kHz, mono, 16-bit). The full recording never
/// touches the disk; the buffer is zeroed after it is handed over.
/// </summary>
public sealed class MicRecorder : IDisposable
{
    public const int SampleRate = 16000;
    private const string PrivacyHint = "Check Settings › Privacy & security › Microphone › \"Let desktop apps access your microphone\".";

    private readonly object _gate = new();
    private MemoryStream _pcm = new();
    private WaveInEvent? _wave;
    private TaskCompletionSource<Exception?>? _stopped;

    /// <summary>Peak level (0..1) of each ~50 ms buffer. Raised on the capture thread.</summary>
    public event Action<double>? LevelChanged;

    public bool IsRecording => _wave is not null;
    public double Seconds { get { lock (_gate) return _pcm.Length / 2.0 / SampleRate; } }

    public void Start()
    {
        if (_wave is not null) return;
        if (WaveInEvent.DeviceCount == 0) throw new MicrophoneException("No microphone was found. Connect one, then try again.");
        lock (_gate) Reset();
        var wave = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 50, NumberOfBuffers = 4 };
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        wave.DataAvailable += OnData;
        wave.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
        try
        {
            wave.StartRecording();
        }
        catch (NAudio.MmException ex)
        {
            wave.Dispose();
            AppLog.Error("mic start", ex);
            throw new MicrophoneException($"The microphone could not be opened. {PrivacyHint}");
        }
        _stopped = stopped;
        _wave = wave;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var peak = 0;
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(e.Buffer, i)));
        lock (_gate) _pcm.Write(e.Buffer, 0, e.BytesRecorded);
        Array.Clear(e.Buffer, 0, e.BytesRecorded);
        LevelChanged?.Invoke(peak / 32768.0);
    }

    /// <summary>Stops and returns the recorded samples (the internal buffer is wiped).</summary>
    public async Task<short[]> StopAsync()
    {
        var wave = _wave;
        if (wave is null) return [];
        _wave = null;
        wave.StopRecording();
        var done = await Task.WhenAny(_stopped!.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        var error = done == _stopped.Task ? await _stopped.Task : null;
        wave.DataAvailable -= OnData;
        wave.Dispose();
        short[] samples;
        lock (_gate)
        {
            samples = new short[_pcm.Length / 2];
            Buffer.BlockCopy(_pcm.GetBuffer(), 0, samples, 0, samples.Length * 2);
            Reset();
        }
        if (error is not null)
        {
            AppLog.Error("mic recording", error);
            Array.Clear(samples);
            throw new MicrophoneException($"The microphone stopped unexpectedly. {PrivacyHint}");
        }
        if (samples.Length > SampleRate / 2 && WavAudio.IsDigitalSilence(samples))
            throw new MicrophoneException($"The microphone delivered no sound at all (it may be blocked or muted). {PrivacyHint}");
        return samples;
    }

    /// <summary>Stops and throws the recording away.</summary>
    public void Cancel()
    {
        var wave = _wave;
        _wave = null;
        if (wave is not null)
        {
            wave.DataAvailable -= OnData;
            try { wave.StopRecording(); } catch (NAudio.MmException) { }
            wave.Dispose();
        }
        lock (_gate) Reset();
    }

    public void Dispose() => Cancel();

    private void Reset()
    {
        Array.Clear(_pcm.GetBuffer());
        _pcm.Dispose();
        _pcm = new MemoryStream();
    }
}
