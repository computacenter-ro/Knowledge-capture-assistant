using CommunityToolkit.Mvvm.ComponentModel;
using KnowledgeCapture.Core.Models;
using KnowledgeCapture.Core.Services;

namespace KnowledgeCapture.ViewModels;

public partial class ExportViewModel : ObservableObject
{
    [ObservableProperty] public partial DateTimeOffset? FromDate { get; set; }
    [ObservableProperty] public partial DateTimeOffset? ToDate { get; set; }
    [ObservableProperty] public partial double MinTurns { get; set; } = 1;
    [ObservableProperty] public partial double MinCoverage { get; set; }
    [ObservableProperty] public partial string MatchText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMatches))] public partial int MatchCount { get; set; }
    [ObservableProperty] public partial string? ResultMessage { get; set; }
    [ObservableProperty] public partial bool ResultIsError { get; set; }
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial bool IsExporting { get; set; }

    public bool HasMatches => MatchCount > 0;

    partial void OnFromDateChanged(DateTimeOffset? value) => UpdateMatches();
    partial void OnToDateChanged(DateTimeOffset? value) => UpdateMatches();
    partial void OnMinTurnsChanged(double value) => UpdateMatches();
    partial void OnMinCoverageChanged(double value) => UpdateMatches();

    public ExportFilter Filter => new(
        FromDate?.Date.ToUniversalTime(),
        ToDate?.Date.AddDays(1).AddTicks(-1).ToUniversalTime(),
        double.IsNaN(MinTurns) ? 0 : (int)MinTurns,
        (int)MinCoverage);

    public void UpdateMatches()
    {
        try
        {
            var sel = ExportService.Select(AppHost.Store, Filter);
            MatchCount = sel.Count;
            MatchText = sel.Count == 0 ? "No conversations match these filters."
                : $"{sel.Count} conversation(s) · {sel.Sum(c => c.MessageCount)} messages will be exported.";
        }
        catch (Exception ex)
        {
            AppLog.Error("export preview", ex);
            MatchText = "Could not read the local database.";
        }
    }

    /// <summary>Called by the view after the FileSavePicker returned a writable stream.</summary>
    public async Task ExportAsync(Stream output, string path)
    {
        IsExporting = true;
        try
        {
            var res = await ExportService.ExportJsonlAsync(AppHost.Store, Filter, output, path);
            ResultIsError = false;
            ResultMessage = $"Exported {res.Conversations} conversation(s), {res.Messages} messages → {res.Path}";
        }
        catch (Exception ex)
        {
            AppLog.Error("export", ex);
            ResultIsError = true;
            ResultMessage = "Export failed: " + ex.Message;
        }
        finally
        {
            HasResult = true;
            IsExporting = false;
        }
    }
}
