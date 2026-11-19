using System.Windows;
using MCB.Core.Storage;

namespace MCB.Manager;

/// <summary>
/// Точка входа standalone-менеджера (Этап 0: каркас, без XAML —
/// WPF-UI построен кодом, т.к. на этой машине нет SDK/markup-компилятора).
/// Позже окно встраивается в CopilotBuddy как вкладка «Multi Account».
/// </summary>
public static class App
{
    [STAThread]
    public static int Main(string[] args)
    {
        var configPath = args.Length > 0
            ? args[0]
            : Environment.GetEnvironmentVariable("MCB_CONFIG")
              ?? MultiAccountStore.DefaultPath(
                  Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..")));

        var window = new MultiAccountWindow(configPath);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(window);
        return 0;
    }
}
