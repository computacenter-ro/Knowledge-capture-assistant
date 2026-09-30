using KnowledgeCapture.Core.Services;
using Microsoft.UI.Xaml;

namespace KnowledgeCapture;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        // demo safety net: never show a crash dialog; log the type only (messages can contain user text)
        UnhandledException += (_, e) =>
        {
            AppLog.Error("unhandled", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Init(DataPaths.Logs);
        AppLog.Info("App started");
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
