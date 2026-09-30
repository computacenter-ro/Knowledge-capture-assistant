using KnowledgeCapture.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace KnowledgeCapture.Views;

public sealed partial class ExportPage : Page
{
    public ExportViewModel ViewModel { get; } = AppHost.Export;

    public ExportPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => ViewModel.UpdateMatches();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = $"knowledge-capture-{DateTime.Now:yyyyMMdd-HHmm}" };
        picker.FileTypeChoices.Add("JSON Lines", [".jsonl"]);
        // WinUI 3 desktop pickers need the window handle
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await using var stream = await file.OpenStreamForWriteAsync();
        stream.SetLength(0);
        await ViewModel.ExportAsync(stream, file.Path);
    }
}
