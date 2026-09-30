using KnowledgeCapture.Core.Models;
using KnowledgeCapture.Core.Services;
using KnowledgeCapture.Core.Services.Anonymization;
using KnowledgeCapture.Core.Services.Speech;
using KnowledgeCapture.ViewModels;

namespace KnowledgeCapture;

/// <summary>App-wide singletons (no DI container needed for a single-window app).</summary>
public static class AppHost
{
    public static AppSettings Settings { get; } = AppSettings.Load();
    public static LlmService Llm { get; } = new();
    public static SpeechService Speech { get; } = new();
    public static Anonymizer Anonymizer { get; } =
        new(Settings.Anonymization, new LlmEntityRecognizer(Llm, Settings.Anonymization.NerChunkChars));

    private static ConversationStore? _store;
    public static ConversationStore Store => _store ??= new ConversationStore(DataPaths.Database);

    private static string? _employeeHash;
    public static string EmployeeHash => _employeeHash ??= DataPaths.EmployeeHash();

    public static SessionDeps Deps => new(Settings, Llm, Anonymizer, Store, EmployeeHash);

    // view models are created lazily on the UI thread (they capture its DispatcherQueue)
    private static ShellViewModel? _shell;
    public static ShellViewModel Shell => _shell ??= new ShellViewModel();
    private static ChatViewModel? _chat;
    public static ChatViewModel Chat => _chat ??= new ChatViewModel();
    private static StoredDataViewModel? _stored;
    public static StoredDataViewModel StoredData => _stored ??= new StoredDataViewModel();
    private static ExportViewModel? _export;
    public static ExportViewModel Export => _export ??= new ExportViewModel();
}
