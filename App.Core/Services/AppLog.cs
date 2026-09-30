namespace KnowledgeCapture.Core.Services;

/// <summary>
/// Metadata-only file log. NEVER pass conversation text, entity values or exception messages from LLM calls here:
/// only counts, timings, ids and exception type names. leakcheck.ps1 scans these files.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _dir;

    public static string? Directory => _dir;

    public static void Init(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        _dir = dir;
    }

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);

    /// <summary>Logs the exception type and HResult only; messages can echo request content.</summary>
    public static void Error(string what, Exception ex) =>
        Write("ERR", $"{what}: {ex.GetType().Name} (0x{ex.HResult:X8})");

    private static void Write(string level, string message)
    {
        if (_dir is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log"), line); }
            catch (IOException) { }
        }
    }
}
