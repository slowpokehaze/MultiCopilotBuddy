using System.Diagnostics;
using MCB.Core.Models;
using MCB.Core.Storage;

namespace MCB.Core.Launch;

/// <summary>
/// Этап 5: изоляция параллельных ботов. Аудит CopilotBuddy (docs/stage5-audit.md):
/// статик-стейт бота живёт в процессе (StyxWoW/ObjectManager), ProcessMutex —
/// per-WoW-PID, логи per-PID → несколько процессов бота безопасны даже в одной
/// папке. Опционально (Launch.BotCopyPerSession) каждая сессия получает свою
/// КОПИЮ папки бота — на случай конфликтов общих файлов (кэш компиляции
/// плагинов, CharacterSettings при одинаковых именах персонажей).
/// </summary>
public static class BotCopyManager
{
    /// <summary>Папка с копиями бота.</summary>
    public static string CopiesRoot(string originalBotDirectory)
        => Path.Combine(originalBotDirectory, "MCBBots");

    /// <summary>
    /// Путь к exe бота для сессии: оригинал, либо копия (создаётся при
    /// первом использовании, Logs/Cache не копируются).
    /// </summary>
    public static string? ResolveBotExe(MultiAccountStore store, SessionModel session, string originalExe)
    {
        if (!store.Config.Launch.BotCopyPerSession)
        {
            return originalExe;
        }

        try
        {
            var originalDir = Path.GetDirectoryName(originalExe)!;
            var copyDir = Path.Combine(CopiesRoot(originalDir), session.Id.Length > 12 ? session.Id[..12] : session.Id);
            var copyExe = Path.Combine(copyDir, Path.GetFileName(originalExe));
            if (File.Exists(copyExe))
            {
                return copyExe;
            }

            CopyDirectory(originalDir, copyDir, copyDir);
            return File.Exists(copyExe) ? copyExe : null;
        }
        catch
        {
            // копия не удалась — работаем из оригинала (лучше, чем без бота)
            return originalExe;
        }
    }

    /// <summary>
    /// Копия папки бота без Logs/Cache/WTF/краш-дампов. excludePath — цель
    /// копии (она лежит внутри исходника: &lt;bot&gt;\MCBBots\&lt;id&gt;) и её
    /// поддерево не копируются (иначе рекурсия сама в себя).
    /// </summary>
    public static void CopyDirectory(string source, string target, string? excludePath = null)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Logs", "Cache", "WTF", "Crashes", "Bugs" };
        Directory.CreateDirectory(target);
        var excludeRoot = excludePath is null
            ? null
            : Path.GetFullPath(excludePath).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var dir in Directory.GetDirectories(source))
        {
            if (skip.Contains(Path.GetFileName(dir)))
            {
                continue;
            }

            var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            if (excludeRoot is not null
                && (full.Equals(excludeRoot, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(excludeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), excludePath);
        }

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }
}
