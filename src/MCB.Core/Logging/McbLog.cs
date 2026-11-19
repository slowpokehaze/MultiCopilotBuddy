using System.IO;
using System.Text;

namespace MCB.Core.Logging;

/// <summary>
/// Простой файловый логгер MCB. Пишет в logs\mcb-yyyy-MM-dd.log рядом с
/// папкой сборки (MultiCopilotBuddy\logs), дублирует в консоль.
///
/// Формат строки (одна строка = одно событие, удобно grep'ать):
///   2026-11-19 22:05:12.345 [INFO] [Launch] Сообщение
///
/// Категории: Launch, Autologin, Session, Attach, Bridge, Roster, Import, Cli.
/// Уровни: DEBUG, INFO, WARN, ERROR.
/// Потокобезопасно; несколько процессов (CLI, watch, Manager) пишут в один файл.
/// </summary>
public static class McbLog
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private static readonly object Sync = new();
    private static string? _logDirectory;
    private static bool _consoleMirror = true;

    /// <summary>Папка логов: MultiCopilotBuddy\logs (рядом с build\).</summary>
    public static string LogDirectory
    {
        get
        {
            if (_logDirectory is not null)
            {
                return _logDirectory;
            }

            var root = AppContext.BaseDirectory;
            // build\ → MultiCopilotBuddy\logs. Если сборка лежит не в build\ —
            // просто logs рядом.
            var parent = Path.GetFullPath(Path.Combine(root, ".."));
            var dir = Path.Combine(parent.EndsWith("build", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(Path.Combine(parent, ".."))
                : parent, "logs");
            _logDirectory = dir;
            return dir;
        }
        set => _logDirectory = value;
    }

    /// <summary>Путь к сегодняшнему файлу лога.</summary>
    public static string CurrentFile => Path.Combine(LogDirectory, $"mcb-{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>Зеркалить записи в консоль (по умолчанию да; GUI может выключить).</summary>
    public static bool ConsoleMirror
    {
        get => _consoleMirror;
        set => _consoleMirror = value;
    }

    public static void Debug(string category, string message) => Write("DEBUG", category, message);
    public static void Info(string category, string message) => Write("INFO", category, message);
    public static void Warn(string category, string message) => Write("WARN", category, message);
    public static void Error(string category, string message) => Write("ERROR", category, message);

    public static void Error(string category, string message, Exception ex)
        => Write("ERROR", category, $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string category, string message)
    {
        var line = $"{DateTime.Now.ToString(TimestampFormat)} [{level}] [{category}] {message}";

        try
        {
            Directory.CreateDirectory(LogDirectory);
            lock (Sync)
            {
                File.AppendAllText(CurrentFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Лог не должен ронять основную работу (диск/права/занятый файл).
        }

        if (_consoleMirror)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
                // Консоль может отсутствовать (GUI) — не критично.
            }
        }
    }

    /// <summary>Последние n строк сегодняшнего лога (или последнего существующего файла).</summary>
    public static IReadOnlyList<string> Tail(int count)
    {
        var file = CurrentFile;
        if (!File.Exists(file))
        {
            // Сегодняшнего нет — берём самый свежий из имеющихся.
            var dir = LogDirectory;
            if (Directory.Exists(dir))
            {
                var newest = Directory.GetFiles(dir, "mcb-*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();
                file = newest?.FullName ?? string.Empty;
            }
        }

        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            return Array.Empty<string>();
        }

        var allLines = File.ReadAllLines(file, Encoding.UTF8);
        return allLines[^Math.Min(count, allLines.Length)..];
    }
}
