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
              ?? ResolveDefaultConfigPath();

        var window = new MultiAccountWindow(configPath);
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        // Необработанная ошибка UI не должна убивать менеджер: показываем и живём
        // дальше (иначе «куда ни нажмёшь — вылет», бой 2026-11-21).
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            _ = MessageBox.Show(
                "Ошибка интерфейса (менеджер продолжает работать):\r\n" + e.Exception,
                "MCB", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        app.Run(window);
        return 0;
    }

    /// <summary>
    /// Дефолтный путь конфига. Сборка живёт в &lt;bot&gt;\MultiCopilotBuddy\build,
    /// корень бота — на ДВА уровня выше (..&lt;bot&gt;). Три уровня — старая ошибка:
    /// выходили на C:\Games\WoW 3.3.5a\Settings\… (файла нет) и менеджер
    /// молча работал с пустым конфигом («пустая программа»).
    /// </summary>
    private static string ResolveDefaultConfigPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, @"..\..", "Settings", "MultiAccount.json")),
            Path.GetFullPath(Path.Combine(baseDir, @"..\..\..", "Settings", "MultiAccount.json")),
            Path.GetFullPath(Path.Combine(baseDir, "Settings", "MultiAccount.json")),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }
}
