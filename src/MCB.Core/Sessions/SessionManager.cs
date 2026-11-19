using MCB.Core.Bridge;
using MCB.Core.Launch;
using MCB.Core.Models;
using MCB.Core.Storage;

namespace MCB.Core.Sessions;

/// <summary>
/// Жизненный цикл сессий бота (FR-4/FR-5): опрос процессов, детект краша/закрытия,
/// автоперезапуск. Pulse() вызывается таймером GUI или циклом `watch` в CLI.
/// </summary>
public sealed class SessionManager
{
    private readonly MultiAccountStore _store;
    private readonly WowLauncher _launcher;

    /// <summary>Лимит подряд идущих автоперезапусков, чтобы не молотить по мёртвому клиенту.</summary>
    public int MaxConsecutiveRestarts { get; set; } = 5;

    /// <summary>Минимальный интервал между попытками аттача бота, с.</summary>
    public int BotAttachRetrySec { get; set; } = 30;

    public SessionManager(MultiAccountStore store, WowLauncher launcher)
    {
        _store = store;
        _launcher = launcher;
    }

    /// <summary>Один проход по всем сессиям. Возвращает актуальный список.</summary>
    public IReadOnlyList<SessionModel> Pulse()
    {
        foreach (var session in _store.Config.Sessions.ToList())
        {
            if (session.State == SessionState.Disabled)
            {
                continue;
            }

            if (session.WowProcessId <= 0)
            {
                if (session.State != SessionState.Idle)
                {
                    session.State = SessionState.Idle;
                    session.LastUpdatedUtc = DateTime.UtcNow;
                }

                continue;
            }

            // Расписание (Этап 6): вне окна — глушим клиента и не трогаем до окна.
            var scheduleAccount = _store.FindAccount(session.AccountLogin);
            if (scheduleAccount is not null
                && scheduleAccount.Schedule.Enabled
                && !scheduleAccount.Schedule.IsActiveNow
                && WowLauncher.IsProcessAlive(session.WowProcessId, session.WowImageName))
            {
                _launcher.KillAliveClient(session.AccountLogin);
                session.WowProcessId = 0;
                session.BotProcessId = 0;
                session.State = SessionState.Idle;
                session.LastUpdatedUtc = DateTime.UtcNow;
                continue;
            }

            // Ротация персонажей (Этап 6): очередь по списку видимых, только
            // через мост (без перезапуска). Мост недоступен — ждём следующего пульса.
            if (session.RotationHours > 0
                && session.State is SessionState.InGame or SessionState.Botting
                && (DateTime.UtcNow - session.LastRotationUtc).TotalHours >= session.RotationHours
                && session.CharacterName is { Length: > 0 }
                && scheduleAccount is not null)
            {
                var next = NextRotationCharacter(scheduleAccount, session.CharacterName);
                if (next is not null && session.BridgePort > 0)
                {
                    var response = BotBridgeClient.SwitchCharacter(session.BridgePort, next);
                    if (response is not null && response.StartsWith("OK", StringComparison.Ordinal))
                    {
                        session.CharacterName = next;
                        session.LastRotationUtc = DateTime.UtcNow;
                        session.LastUpdatedUtc = DateTime.UtcNow;
                    }
                }
            }

            if (WowLauncher.IsProcessAlive(session.WowProcessId, session.WowImageName))
            {
                // Живой клиент: Launching → Running по истечении окна логина;
                // счётчик рестартов сбрасывается — клиент стабильно живёт.
                if (session.State == SessionState.Launching
                    && (DateTime.UtcNow - session.LastUpdatedUtc).TotalSeconds > _launcher.LaunchingGraceSec)
                {
                    session.State = SessionState.Running;
                }

                if (session.State == SessionState.Running || session.State == SessionState.InGame || session.State == SessionState.Botting)
                {
                    session.RestartCount = 0;
                }

                // Детект «в мире» (один netstat на пульс, только когда нужен):
                // Running → InGame по факту входа в мир.
                HashSet<int>? inWorld = null;
                if (session.State == SessionState.Running
                    || (session.AutoAttach
                        && session.State is SessionState.Running or SessionState.InGame or SessionState.Botting
                        && !WowLauncher.IsProcessAlive(session.BotProcessId, "CopilotBuddy")))
                {
                    inWorld = WowLauncher.GetInWorldPids();
                    if (session.State == SessionState.Running && inWorld.Contains(session.WowProcessId))
                    {
                        session.State = SessionState.InGame;
                    }
                }

                // Аттач/перезапуск бота: только когда клиент уже В МИРЕ (после
                // логина и входа — иначе бот стартует на логин-скрине и падает).
                // Повтор попытки не чаще раза в 30 с — чтобы не плодить процессы.
                if (session.AutoAttach
                    && session.State is SessionState.InGame or SessionState.Botting
                    && inWorld is not null
                    && inWorld.Contains(session.WowProcessId)
                    && !WowLauncher.IsProcessAlive(session.BotProcessId, "CopilotBuddy")
                    && (DateTime.UtcNow - session.BotLastAttachUtc).TotalSeconds >= BotAttachRetrySec)
                {
                    session.BotLastAttachUtc = DateTime.UtcNow;
                    _ = BotAttacher.AssignBridgePort(_store, session);
                    var botPid = BotAttacher.Attach(_store, session);
                    if (botPid > 0)
                    {
                        session.BotProcessId = botPid;
                        session.State = string.IsNullOrWhiteSpace(session.BotName)
                            ? SessionState.InGame
                            : SessionState.Botting;
                        session.LastUpdatedUtc = DateTime.UtcNow;
                    }
                }

                continue;
            }

            // Процесс мёртв.
            session.State = SessionState.Crashed;
            session.LastUpdatedUtc = DateTime.UtcNow;
            session.WowProcessId = 0;

            if (session.AutoRestart && session.RestartCount < MaxConsecutiveRestarts)
            {
                var account = _store.FindAccount(session.AccountLogin);
                if (account is not null)
                {
                    var result = _launcher.LaunchCharacter(account, session.CharacterName, allowWhenRunning: true);
                    if (result.Started)
                    {
                        session.RestartCount++;
                        session.WowProcessId = result.ProcessId;
                        session.State = SessionState.Launching;
                        session.LastUpdatedUtc = DateTime.UtcNow;
                    }
                    // Cooldown → сессия остаётся Crashed, повтор на следующем Pulse.
                }
            }
        }

        _store.Save();
        return _store.Config.Sessions;
    }

    /// <summary>Остановить все живые сессии (Этап 5).</summary>
    public int StopAll()
    {
        var stopped = 0;
        foreach (var session in _store.Config.Sessions.ToList())
        {
            if (session.WowProcessId > 0 && WowLauncher.IsProcessAlive(session.WowProcessId, session.WowImageName))
            {
                if (_launcher.KillAliveClient(session.AccountLogin))
                {
                    stopped++;
                }

                session.WowProcessId = 0;
                session.BotProcessId = 0;
                session.State = SessionState.Idle;
                session.LastUpdatedUtc = DateTime.UtcNow;
            }
        }

        _store.Save();
        return stopped;
    }

    /// <summary>Следующий видимый персонаж аккаунта по порядку (очередь, Этап 6).</summary>
    public static string? NextRotationCharacter(AccountModel account, string current)
    {
        var visible = account.Characters.Where(c => !c.Hidden).OrderBy(c => c.Order).ToList();
        if (visible.Count == 0)
        {
            return null;
        }

        var index = visible.FindIndex(c => string.Equals(c.Name, current, StringComparison.OrdinalIgnoreCase));
        return visible[(index + 1) % visible.Count].Name;
    }

    /// <summary>Компактный статус сессии для лога/UI.</summary>
    public static string FormatStatus(SessionModel session)
    {
        var character = string.IsNullOrEmpty(session.CharacterName) ? "-" : session.CharacterName;
        var pid = session.WowProcessId > 0 ? $" pid={session.WowProcessId}" : string.Empty;
        var bot = session.BotProcessId > 0 ? $" bot={session.BotProcessId}" : string.Empty;
        var restarts = session.RestartCount > 0 ? $" restarts={session.RestartCount}" : string.Empty;
        return $"{session.AccountLogin} | {character} | {session.State}{pid}{bot}{restarts}";
    }
}
