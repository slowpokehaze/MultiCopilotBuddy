using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MCB.Core.Logging;
using MCB.Core.Models;
using MCB.Core.Storage;

namespace MCB.Core.Launch;

/// <summary>
/// Запуск MCB.Agent для сессии (integration-cb-into-mcb.md, Этап B/C) —
/// замена BotAttacher при Launch.UseAgent=true. Агент — headless-хост
/// CopilotBuddy.dll: без UI, без своего UAC-манифеста, живёт под Job Object
/// менеджера и сам выходит при смерти клиента.
///
/// Элевация: если MCB запущен админом — прямой старт (дети наследуют
/// elevation, ноль UAC). Иначе — runas (один UAC-запрос на старт агента);
/// Этап D добавит requireAdministrator на менеджер и уберёт fallback.
/// </summary>
public static class AgentLauncher
{
    /// <summary>Последняя ошибка Attach (для диагностики из CLI/UI).</summary>
    public static string? LastAttachError { get; private set; }

    /// <summary>Имя пайпа агента сессии (детерминировано — агент и клиент сходятся без рукопожатия).</summary>
    public static string PipeName(SessionModel session) => "MCBAgent_" + session.Id;

    /// <summary>
    /// Путь к MCB.Agent.dll: рядом с CopilotBuddy.exe сессии (оригинал или
    /// копия BotCopyPerSession — сборка деплоит агент в корень бота).
    /// </summary>
    public static string? ResolveAgentDll(MultiAccountStore store, SessionModel session)
    {
        var configured = store.Config.Launch.AgentDllPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        var originalExe = BotAttacher.ResolveCopilotBuddyExe(store);
        if (originalExe is null)
        {
            return null;
        }

        var botExe = BotCopyManager.ResolveBotExe(store, session, originalExe);
        if (botExe is null)
        {
            return null;
        }

        var dll = Path.Combine(Path.GetDirectoryName(botExe)!, "MCB.Agent.dll");
        return File.Exists(dll) ? dll : null;
    }

    /// <summary>Аргументы агента. Соглашения живого бота (/pid= /botname= /loadprofile= /customclass=) + --pipe/--botdir.</summary>
    public static string BuildArguments(SessionModel session, string pipeName, string botDir)
    {
        var sb = new StringBuilder();
        sb.Append("/pid=").Append(session.WowProcessId);
        sb.Append(" --pipe=").Append(pipeName);
        sb.Append(" --botdir=\"").Append(botDir).Append('\"');

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

        if (session.BridgePort > 0)
        {
            sb.Append(" --bridge-port=").Append(session.BridgePort);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Запускает агент для сессии. Возвращает PID хоста (dotnet) или 0 при неудаче —
    /// Pulse повторит с тем же backoff, что и у BotAttacher.
    /// </summary>
    public static int Attach(MultiAccountStore store, SessionModel session)
    {
        var agentDll = ResolveAgentDll(store, session);
        if (agentDll is null || session.WowProcessId <= 0)
        {
            LastAttachError = agentDll is null
                ? "MCB.Agent.dll не найден (собери tools\\build-agent.ps1 — он деплоит агент в корень бота)"
                : "нет живого WoW-клиента для аттача";
            McbLog.Warn("Agent", $"{session.AccountLogin}: {LastAttachError}");
            return 0;
        }

        var botDir = Path.GetDirectoryName(agentDll)!;
        var pipeName = PipeName(session);
        var args = BuildArguments(session, pipeName, botDir);

        // sessions.json для MCBBridge — тот же контракт, что у живого бота (§3).
        BotAttacher.WriteSessionsFile(store, botDir);

        try
        {
            var (process, elevated) = Spawn(agentDll, args, botDir);
            var pid = process?.Id ?? 0;
            if (pid > 0)
            {
                session.BotProcessId = pid;
                McbLog.Info("Agent",
                    $"{session.AccountLogin}: MCB.Agent запущен (PID {pid}{(elevated ? ", UAC" : "")}), пайп '{pipeName}'");
            }

            return pid;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            LastAttachError = "старт отклонён (нет прав на аттач к WoW из не-elevated MCB)";
            McbLog.Warn("Agent", $"{session.AccountLogin}: {LastAttachError}");
            return 0;
        }
        catch (Exception ex)
        {
            LastAttachError = ex.GetType().Name + ": " + ex.Message;
            McbLog.Error("Agent", $"{session.AccountLogin}: старт агента упал: {LastAttachError}", ex);
            return 0;
        }
    }

    private static (Process? Process, bool Elevated) Spawn(string agentDll, string args, string workingDir)
    {
        var host = ResolveDotnetHost();

        if (WindowsJob.IsElevated)
        {
            var process = Process.Start(new ProcessStartInfo(host, $"\"{agentDll}\" {args}")
            {
                UseShellExecute = false,
                WorkingDirectory = workingDir,
            });
            if (process != null && !WindowsJob.Assign(process))
            {
                McbLog.Warn("Agent", $"PID {process.Id}: не добавлен в Job Object — умерший менеджер не убьёт агента");
            }

            return (process, false);
        }

        // Не-elevated MCB: runas → один UAC-запрос. PID может быть недоступен —
        // определяем по диффу хостов dotnet (как BotAttacher.StartElevated).
        var before = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(host))
            .Select(p => p.Id).ToHashSet();
        var elevated = Process.Start(new ProcessStartInfo(host, $"\"{agentDll}\" {args}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = workingDir,
        });

        var pid = 0;
        try
        {
            pid = elevated?.Id ?? 0;
        }
        catch
        {
            // elevated-процесс: Id недоступен
        }

        if (pid <= 0)
        {
            for (var attempt = 0; attempt < 10 && pid <= 0; attempt++)
            {
                Thread.Sleep(1000);
                pid = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(host))
                    .FirstOrDefault(p => !before.Contains(p.Id))?.Id ?? 0;
            }
        }

        return (pid > 0 ? Process.GetProcessById(pid) : null, true);
    }

    /// <summary>dotnet-хост той же битности, что и MCB (машина — x86 runtime 10.0.15).</summary>
    private static string ResolveDotnetHost()
    {
        try
        {
            return Environment.ProcessPath
                   ?? Process.GetCurrentProcess().MainModule?.FileName
                   ?? @"C:\Program Files (x86)\dotnet\dotnet.exe";
        }
        catch
        {
            return @"C:\Program Files (x86)\dotnet\dotnet.exe";
        }
    }

    private static string Quote(string value)
        => value.Contains(' ') ? $"\"{value}\"" : value;
}
