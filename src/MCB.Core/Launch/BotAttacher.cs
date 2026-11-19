using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using MCB.Core.Models;
using MCB.Core.Storage;

namespace MCB.Core.Launch;

/// <summary>
/// Аттач CopilotBuddy к запущенному клиенту (Этап 1): CopilotBuddy.exe
/// /pid=&lt;wow pid&gt; /autostart /loadprofile=… /botname=… [/customclass=…]
/// — рецепт HBRelog из upstream README. Один CopilotBuddy на сессию.
/// </summary>
public static class BotAttacher
{
    /// <summary>
    /// Путь к CopilotBuddy.exe: из конфига или автодетект — корень бота
    /// относительно сборки MCB (&lt;bot&gt;\MultiCopilotBuddy\build → &lt;bot&gt;).
    /// </summary>
    public static string? ResolveCopilotBuddyExe(MultiAccountStore store)
    {
        var configured = store.Config.Launch.CopilotBuddyExePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        var botRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\.."));
        var candidate = Path.Combine(botRoot, "CopilotBuddy.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>Аргументы запуска CopilotBuddy для сессии (детерминированно — тестируется).</summary>
    public static string BuildArguments(int wowPid, SessionModel session)
    {
        var sb = new StringBuilder();
        sb.Append("/pid=").Append(wowPid);
        sb.Append(" /autostart");

        if (!string.IsNullOrWhiteSpace(session.BotName))
        {
            sb.Append(" /botname=").Append(Quote(session.BotName));
        }

        if (!string.IsNullOrWhiteSpace(session.ProfilePath))
        {
            sb.Append(" /loadprofile=").Append(Quote(session.ProfilePath));
        }

        if (!string.IsNullOrWhiteSpace(session.RoutineName))
        {
            sb.Append(" /customclass=").Append(Quote(session.RoutineName));
        }

        return sb.ToString();
    }

    private static string Quote(string value)
        => value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>
    /// Запускает CopilotBuddy для сессии. Возвращает PID или 0 при неудаче
    /// (бот не найден / не стартовал) — Pulse повторит попытку.
    /// После запуска пишет sessions.json для MCBBridge-плагина (Этап 2).
    /// </summary>
    public static int Attach(MultiAccountStore store, SessionModel session)
    {
        var originalExe = ResolveCopilotBuddyExe(store);
        if (originalExe is null || session.WowProcessId <= 0)
        {
            return 0;
        }

        // Этап 5: опциональная копия бота на сессию (изоляция общих файлов).
        var exe = BotCopyManager.ResolveBotExe(store, session, originalExe);
        if (exe is null)
        {
            return 0;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, BuildArguments(session.WowProcessId, session))
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            });
            var pid = process?.Id ?? 0;
            if (pid > 0)
            {
                session.BotProcessId = pid;
                // sessions.json — в папку конкретного бота (оригинал или копия).
                WriteSessionsFile(store, Path.GetDirectoryName(exe)!);
            }

            return pid;
        }
        catch (Win32Exception)
        {
            // CopilotBuddy.exe имеет манифест requireAdministrator: из
            // не-админской сессии прямой старт даёт ERROR_CANCELLED.
            // Fallback: ShellExecute с runas — Windows покажет UAC-запрос.
            return StartElevated(store, session, exe);
        }
        catch (Exception ex)
        {
            LastAttachError = ex.GetType().Name + ": " + ex.Message;
            return 0;
        }
    }

    private static int StartElevated(MultiAccountStore store, SessionModel session, string exe)
    {
        try
        {
            var before = Process.GetProcessesByName("CopilotBuddy").Select(p => p.Id).ToHashSet();
            using var process = Process.Start(new ProcessStartInfo(exe, BuildArguments(session.WowProcessId, session))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
            });

            var pid = 0;
            try
            {
                pid = process?.Id ?? 0;
            }
            catch
            {
                // elevated-процесс: Id может быть недоступен — определяем по диффу
            }

            if (pid <= 0)
            {
                for (var attempt = 0; attempt < 10 && pid <= 0; attempt++)
                {
                    Thread.Sleep(1000);
                    pid = Process.GetProcessesByName("CopilotBuddy")
                        .FirstOrDefault(p => !before.Contains(p.Id))?.Id ?? 0;
                }
            }

            if (pid > 0)
            {
                session.BotProcessId = pid;
                WriteSessionsFile(store, Path.GetDirectoryName(exe)!);
            }
            else
            {
                LastAttachError = "UAC-запрос отклонён или бот не появился в процессах.";
            }

            return pid;
        }
        catch (Exception ex)
        {
            LastAttachError = "elevated: " + ex.GetType().Name + ": " + ex.Message;
            return 0;
        }
    }

    /// <summary>Последняя ошибка Attach (для диагностики из CLI/UI).</summary>
    public static string? LastAttachError { get; private set; }

    // ---------- MCBBridge: sessions.json ----------

    /// <summary>Базовый порт диапазона мостов MCBBridge.</summary>
    public const int BridgePortBase = 47201;

    /// <summary>Назначает сессии свободный порт моста, если ещё нет.</summary>
    public static int AssignBridgePort(MultiAccountStore store, SessionModel session)
    {
        if (session.BridgePort > 0)
        {
            return session.BridgePort;
        }

        var taken = store.Config.Sessions
            .Where(s => !ReferenceEquals(s, session) && s.BridgePort > 0)
            .Select(s => s.BridgePort)
            .ToHashSet();

        for (var offset = 0; offset < 99; offset++)
        {
            var candidate = BridgePortBase + offset;
            if (!taken.Contains(candidate))
            {
                session.BridgePort = candidate;
                return candidate;
            }
        }

        return 0;
    }

    /// <summary>
    /// Пишет Plugins/MCBBridge/sessions.json: по записи на каждую живую сессию
    /// (порт + PID WoW). Плагин выбирает свою запись по PID приаттаченного
    /// процесса WoW. Один файл на все копии бота — они не конфликтуют.
    /// </summary>
    public static void WriteSessionsFile(MultiAccountStore store, string botRoot)
    {
        try
        {
            var dir = Path.Combine(botRoot, "Plugins", "MCBBridge");
            Directory.CreateDirectory(dir);

            var entries = store.Config.Sessions
                .Where(s => s.BotProcessId > 0 && s.BridgePort > 0 && WowLauncher.IsProcessAlive(s.WowProcessId, s.WowImageName))
                .Select(s => new Dictionary<string, object>
                {
                    ["port"] = s.BridgePort,
                    ["wowPid"] = s.WowProcessId,
                    ["account"] = s.AccountLogin,
                    ["session"] = s.Id,
                })
                .ToList();

            var json = JsonSerializer.Serialize(entries);
            File.WriteAllText(Path.Combine(dir, "sessions.json"), json);
        }
        catch
        {
            // файл-подсказка — не критично
        }
    }
}
