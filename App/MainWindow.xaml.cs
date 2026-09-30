using System.Runtime.InteropServices;
using KnowledgeCapture.ViewModels;
using KnowledgeCapture.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace KnowledgeCapture;

public sealed partial class MainWindow : Window
{
    public ShellViewModel Shell => AppHost.Shell;
    private bool _started;

    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var w = Math.Max(1000, Math.Min((int)(1440 * scale), (int)(work.Width * 0.95)));
        var h = Math.Max(700, Math.Min((int)(900 * scale), (int)(work.Height * 0.95)));
        AppWindow.MoveAndResize(new RectInt32(work.X + Math.Max(0, (work.Width - w) / 2), work.Y + Math.Max(0, (work.Height - h) / 2), w, h));
        Core.Services.AppLog.Info($"Window {w}x{h} (dpi {dpi}, work area {work.Width}x{work.Height})");
        ContentFrame.Navigate(typeof(ChatPage));
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        await Shell.InitAsync(); // discovery + NPU load + warm-up, off the UI thread
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = (args.SelectedItem as NavigationViewItem)?.Tag switch
        {
            "stored" => typeof(StoredDataPage),
            "export" => typeof(ExportPage),
            _ => typeof(ChatPage),
        };
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
