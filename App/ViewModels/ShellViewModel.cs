using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KnowledgeCapture.Core.Services;

namespace KnowledgeCapture.ViewModels;

public enum ModelStatus { Loading, Ready, Down }

/// <summary>Local model lifecycle: discovery, NPU load + warm-up, health pill, retry.</summary>
public partial class ShellViewModel : ObservableObject
{
    [ObservableProperty] public partial ModelStatus Status { get; set; } = ModelStatus.Loading;
    [ObservableProperty] public partial string StatusText { get; set; } = "Starting…";
    [ObservableProperty] public partial string LoadingText { get; set; } = "Loading model on NPU…";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInitError))]
    public partial string? InitError { get; set; }

    public bool HasInitError => !string.IsNullOrEmpty(InitError);
    public bool IsLoading => Status == ModelStatus.Loading;
    public bool IsReady => Status == ModelStatus.Ready;

    public event Action? Ready;

    partial void OnStatusChanged(ModelStatus value)
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsReady));
    }

    private bool _initializing;

    [RelayCommand]
    public async Task InitAsync()
    {
        if (_initializing) return;
        _initializing = true;
        Status = ModelStatus.Loading;
        InitError = null;
        StatusText = $"{AppHost.Settings.Llm.Device.ToUpperInvariant()} · loading…";
        LoadingText = $"Loading model on {AppHost.Settings.Llm.Device.ToUpperInvariant()}…";
        var progress = new Progress<string>(s => LoadingText = s); // created on the UI thread -> reports on it
        try
        {
            await Task.Run(() => AppHost.Llm.InitAsync(AppHost.Settings.Llm, progress));
            Status = ModelStatus.Ready;
            StatusText = $"{AppHost.Llm.Device} · {AppHost.Llm.ShortModelName}";
            Ready?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("model init", ex);
            Status = ModelStatus.Down;
            StatusText = $"{AppHost.Settings.Llm.Device.ToUpperInvariant()} · offline";
            InitError = ex is HttpRequestException
                ? "Foundry Local is not reachable. Start it with: foundry server start, then Retry."
                : ex.Message;
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>A chat call failed mid-conversation: flip the pill to red and offer Retry.</summary>
    public void ReportFailure()
    {
        Status = ModelStatus.Down;
        StatusText = $"{AppHost.Llm.Device} · offline";
        InitError = "The local model stopped responding. Check that Foundry Local is running, then Retry.";
    }
}
