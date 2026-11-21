using MCB.Core.Bridge;
using MCB.Core.Launch;
using MCB.Core.Logging;
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

    /// <summary>
    /// Перечитывать конфиг с диска в начале каждого пульса. Включает CLI watch:
    /// launch/stop в других процессах пишут конфиг между пульсами, и без
    /// перечитывания watch затирает их изменения своей устаревшей копией
    /// (бой 2026-11-19: сессии живых клиентов откатились в Idle).
    /// </summary>
    public bool ReloadOnPulse { get; set; }

    /// <summary>
    /// Идёт групповой запуск «лидер первым»: авто-рестарт Pulse глушится,
    /// иначе Pulse в порядке списка мог запустить ведомых раньше лидера.
    /// </summary>
    public bool GroupLaunchActive { get; private set; }

    /// <summary>
    /// Ручной режим: false = пульс не авторестартит клиентов и не аттачит
    /// ботов — можно спокойно убивать/перезапускать что угодно руками
    /// (бой 2026-11-21: убитый руками клиент воскресал через 5 секунд).
    /// </summary>
    public bool AutomationEnabled { get; set; } = true;

    public SessionManager(MultiAccountStore store, WowLauncher launcher)
    {
        _store = store;
        _launcher = launcher;
    }

    /// <summary>
    /// Pulse вызывается и из UI-таймера, и из группового запуска — сериализуем,
    /// чтобы два Pulse не мутировали и не сохраняли конфиг вперегонки.
    /// </summary>
    private readonly object _pulseLock = new();

    /// <summary>
    /// Сессия, которую сейчас запускает групповой запуск. Пока группа идёт,
    /// Pulse аттачит бота ТОЛЬКО этой сессии — иначе он пачкой аттачит ботов
    /// всех клиентов, вошедших в мир, раньше загрузки бота лидера
    /// (бой 2026-11-21).
    /// </summary>
    private string? _groupCurrentSessionId;

    /// <summary>Аттач бота сессии под общим локом пульса. Отдельный метод для
    /// UI-кнопки «Перезапустить боты»: раньше она звала BotAttacher.Attach
    /// напрямую, вне лока, и гонялась с Pulse — на одну сессию открывались
    /// два бота за 14 мс (бой 2026-11-21, Крылатыйлом).</summary>
    public int AttachBotLocked(SessionModel session)
    {
        lock (_pulseLock)
        {
            // Пока ждали лок, бот мог прицепиться другим пульсом — не дублируем.
            if (WowLauncher.IsProcessAlive(session.BotProcessId, "CopilotBuddy"))
            {
                return session.BotProcessId;
            }

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
                McbLog.Info("Attach",
                    $"{session.AccountLogin}: CopilotBuddy запущен (PID {botPid}), бот '{session.BotName}', состояние {session.State}");
            }

            _store.Save();
            return botPid;
        }
    }

    /// <summary>Один проход по всем сессиям. Возвращает актуальный список.</summary>
    public IReadOnlyList<SessionModel> Pulse()
    {
        lock (_pulseLock)
        {
            return PulseCore();
        }
    }

    private IReadOnlyList<SessionModel> PulseCore()
    {
        if (ReloadOnPulse)
        {
            try
            {
                _store.Load();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException)
            {
                McbLog.Warn("Session",
                    $"конфиг занят/битый, работаем по предыдущей копии: {ex.Message}");
            }
        }

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
                McbLog.Info("Session",
                    $"{session.AccountLogin}: вне окна расписания " +
                    $"({scheduleAccount.Schedule.Start}-{scheduleAccount.Schedule.End}, {scheduleAccount.Schedule.Days}) — глушу клиента (PID {session.WowProcessId})");
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
                    McbLog.Info("Bridge",
                        $"{session.AccountLogin}: ротация каждые {session.RotationHours} ч — переключаю на '{next}' (порт {session.BridgePort})");
                    var response = BotBridgeClient.SwitchCharacter(session.BridgePort, next);
                    if (response is not null && response.StartsWith("OK", StringComparison.Ordinal))
                    {
                        session.CharacterName = next;
                        session.LastRotationUtc = DateTime.UtcNow;
                        session.LastUpdatedUtc = DateTime.UtcNow;
                        McbLog.Info("Bridge", $"{session.AccountLogin}: ротация на '{next}' выполнена");
                    }
                    else
                    {
                        McbLog.Warn("Bridge",
                            $"{session.AccountLogin}: мост не ответил на смену персонажа '{next}': {response ?? "нет ответа"}");
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
                    McbLog.Info("Session",
                        $"{session.AccountLogin}: клиент жив (PID {session.WowProcessId}), состояние Running");
                }

                if (session.State == SessionState.Running || session.State == SessionState.InGame || session.State == SessionState.Botting)
                {
                    session.RestartCount = 0;
                }

                // Детект «в мире» (один netstat на пульс, только когда нужен):
                // Running/Launching → InGame по факту входа в мир (бой 2026-11-21:
                // убрали 90-секундную страховку — аттач бота должен начинаться
                // сразу, как только соединение с world-сервером установлено).
                HashSet<int>? inWorld = null;
                if (session.State is SessionState.Running or SessionState.Launching
                    || (session.AutoAttach
                        && session.State is SessionState.Running or SessionState.Launching or SessionState.InGame or SessionState.Botting
                        && !WowLauncher.IsProcessAlive(session.BotProcessId, "CopilotBuddy")))
                {
                    inWorld = WowLauncher.GetInWorldPids();
                    if (session.State is SessionState.Running or SessionState.Launching
                        && inWorld.Contains(session.WowProcessId))
                    {
                        session.State = SessionState.InGame;
                        // Отметка «в мире с этого момента»: netstat видит соединение
                        // уже на экране загрузки (бой 2026-11-21), поэтому аттач бота
                        // ждёт InWorldSettleSec — персонаж должен УСПЕШНО загрузиться.
                        session.LastUpdatedUtc = DateTime.UtcNow;
                        McbLog.Info("Session", $"{session.AccountLogin}: клиент в мире (PID {session.WowProcessId}), состояние InGame");
                    }
                }

                // Бот жив дольше 5 минут — серия неудач закрыта, backoff сбрасываем.
                var sinceAttachSec = (DateTime.UtcNow - session.BotLastAttachUtc).TotalSeconds;
                if (WowLauncher.IsProcessAlive(session.BotProcessId, "CopilotBuddy") && sinceAttachSec > 300)
                {
                    if (session.AttachFailCount > 0)
                    {
                        session.AttachFailCount = 0;
                    }
                }

                // Аттач/перезапуск бота: только когда клиент уже В МИРЕ (после
                // логина и входа — иначе бот стартует на логин-скрине и падает).
                // Бот, умерший быстро после старта (клиент на экране выбора
                // персонажа → «No available WoW»), растит backoff: 30 с → 1 мин →
                // 2 мин → … до 10 мин, чтобы не молотить UAC-запросы.
                // Лидер первый — глобально: бот ведомого ждёт, пока бот лидера
                // не загрузится (жив и продержался 10 с после аттача). Правило
                // действует только когда клиент лидера вообще в игре — если
                // лидер офлайн, ведомые работают сами по себе.
                // Бой 2026-11-21: после массового падения ботов ведомые
                // доаттачились пачкой раньше лидерского.
                if (!session.IsLeader)
                {
                    var leaderSession = _store.Config.Sessions.FirstOrDefault(s => s.IsLeader);
                    if (leaderSession is not null
                        && leaderSession.WowProcessId > 0
                        && WowLauncher.IsProcessAlive(leaderSession.WowProcessId, leaderSession.WowImageName)
                        && (!WowLauncher.IsProcessAlive(leaderSession.BotProcessId, "CopilotBuddy")
                            || (DateTime.UtcNow - leaderSession.BotLastAttachUtc).TotalSeconds < 10))
                    {
                        continue;
                    }
                }

                if (AutomationEnabled
                    && session.AutoAttach
                    && session.State is SessionState.InGame or SessionState.Botting
                    && inWorld is not null
                    && inWorld.Contains(session.WowProcessId)
                    && (DateTime.UtcNow - session.LastUpdatedUtc).TotalSeconds >= InWorldSettleSec
                    && (!GroupLaunchActive || session.Id == _groupCurrentSessionId)
                    && !WowLauncher.IsProcessAlive(session.BotProcessId, "CopilotBuddy"))
                {
                    if (session.BotProcessId > 0 && sinceAttachSec < 300)
                    {
                        session.AttachFailCount++;
                        McbLog.Warn("Attach",
                            $"{session.AccountLogin}: CopilotBuddy (PID {session.BotProcessId}) продержался {sinceAttachSec:F0} с — " +
                            $"неудача #{session.AttachFailCount} (клиент не в мире? бот не нашёл процесс?)");
                    }

                    var backoffSec = Math.Min(BotAttachRetrySec << Math.Min(session.AttachFailCount, 5), 600);
                    if (sinceAttachSec >= backoffSec)
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
                            McbLog.Info("Attach",
                                $"{session.AccountLogin}: CopilotBuddy запущен (PID {botPid}), бот '{session.BotName}', состояние {session.State}");
                        }
                        else
                        {
                            McbLog.Warn("Attach",
                                $"{session.AccountLogin}: аттач не удался: {BotAttacher.LastAttachError ?? "причина неизвестна"} " +
                                $"(следующий повтор через {backoffSec} с)");
                        }
                    }
                }

                continue;
            }

            // Процесс мёртв.
            if (session.State != SessionState.Crashed)
            {
                McbLog.Warn("Session",
                    $"{session.AccountLogin}: клиент мёртв (PID {session.WowProcessId}), состояние Crashed, рестартов уже {session.RestartCount}");
            }
            session.State = SessionState.Crashed;
            session.LastUpdatedUtc = DateTime.UtcNow;

            // Старый бот остался прицепленным к мёртвому клиенту — закрываем.
            // CopilotBuddy выбирает процесс один раз при старте (/pid=) и не
            // умеет переключаться на новый: живой бот-зомби блокировал аттач
            // нового бота к перезапущенному клиенту (бой 2026-11-21).
            if (WowLauncher.IsProcessAlive(session.BotProcessId, "CopilotBuddy"))
            {
                var oldBotPid = session.BotProcessId;
                WowLauncher.KillProcess(oldBotPid);
                McbLog.Info("Session", $"{session.AccountLogin}: старый бот (PID {oldBotPid}) закрыт вместе с клиентом — после рестарта запустится новый");
            }
            session.BotProcessId = 0;

            session.WowProcessId = 0;

            if (session.AutoRestart && AutomationEnabled && !GroupLaunchActive && session.RestartCount < MaxConsecutiveRestarts)
            {
                var account = _store.FindAccount(session.AccountLogin);
                if (account is not null)
                {
                    var result = _launcher.LaunchCharacter(account, ResolveLaunchCharacterName(account, session), allowWhenRunning: true);
                    if (result.Started)
                    {
                        session.RestartCount++;
                        session.WowProcessId = result.ProcessId;
                        session.State = SessionState.Launching;
                        session.LastUpdatedUtc = DateTime.UtcNow;
                        McbLog.Info("Session",
                            $"{session.AccountLogin}: авторестарт ({session.RestartCount}/{MaxConsecutiveRestarts}), новый PID {result.ProcessId}, персонаж '{session.CharacterName}'");
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
        McbLog.Info("Session", "StopAll: остановка всех живых сессий");
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

    // ---------- Групповой запуск «лидер первым» ----------

    /// <summary>Сколько секунд ждать загрузки бота одной сессии при групповом запуске.</summary>
    public int GroupLaunchBotTimeoutSec { get; set; } = 420;

    /// <summary>
    /// Сколько секунд после первого появления соединения с world-сервером ждать
    /// перед аттачем бота: netstat видит соединение уже на экране загрузки,
    /// персонаж должен успеть реально войти в мир (бой 2026-11-21).
    /// </summary>
    public int InWorldSettleSec { get; set; } = 10;

    /// <summary>
    /// Порядок группового запуска: сессия-лидер первой (бот лидера должен
    /// загрузиться раньше ведомых — LeaderPlugin PartyBot приглашает группу),
    /// остальные — в порядке списка.
    /// </summary>
    public IReadOnlyList<SessionModel> GetGroupLaunchOrder()
        => _store.Config.Sessions.OrderByDescending(s => s.IsLeader).ToList();

    private static string Label(SessionModel session)
        => $"{session.AccountLogin}/{session.CharacterName}";

    /// <summary>
    /// Групповой запуск: сначала лидер, ждём полной загрузки его бота, затем
    /// ведомые по очереди — каждый следующий стартует только после загрузки
    /// бота предыдущего (или таймаута). Прогресс — через progress (вызывается
    /// из фонового потока). sessionIds — запустить только эти сессии
    /// (чек-лист Bot Launcher); null = все. Возвращает итоговую сводку.
    /// </summary>
    public async Task<string> LaunchGroupAsync(Action<string>? progress = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? sessionIds = null)
    {
        GroupLaunchActive = true;
        try
        {
            return await LaunchGroupCoreAsync(progress, ct, sessionIds).ConfigureAwait(false);
        }
        finally
        {
            GroupLaunchActive = false;
            _groupCurrentSessionId = null;
        }
    }

    private async Task<string> LaunchGroupCoreAsync(Action<string>? progress, CancellationToken ct,
        IReadOnlyCollection<string>? sessionIds)
    {
        var order = GetGroupLaunchOrder();
        if (sessionIds is not null)
        {
            var wanted = new HashSet<string>(sessionIds, StringComparer.OrdinalIgnoreCase);
            order = order.Where(s => wanted.Contains(s.Id)).ToList();
        }

        if (order.Count == 0)
        {
            return "Сессий нет — запускать нечего.";
        }

        var report = new List<string>();
        for (var i = 0; i < order.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var isLeader = order[i].IsLeader;
            if (isLeader)
            {
                progress?.Invoke("=== Лидер: " + Label(order[i]) + " ===");
            }

            _groupCurrentSessionId = order[i].Id;
            var ok = await LaunchSessionAndWaitAsync(order[i], progress, ct).ConfigureAwait(false);
            _groupCurrentSessionId = null;
            report.Add($"{Label(order[i])}{(isLeader ? " (лидер)" : "")}: " +
                       (ok ? "бот загружен" : "без подтверждения (таймаут/ошибка)"));

            // Лидер не загрузился — ведомых не запускаем: LeaderPlugin не
            // заработает, группа соберётся криво.
            if (isLeader && !ok)
            {
                progress?.Invoke("Бот лидера не загрузился — ведомых не запускаю.");
                return "Групповой запуск прерван: бот лидера не загрузился. " + string.Join("; ", report);
            }
        }

        return "Групповой запуск завершён. " + string.Join("; ", report);
    }

    /// <summary>
    /// Имя персонажа для LaunchCharacter. Строгая проверка лоунчера требует,
    /// чтобы явно переданный персонаж был в рорстере аккаунта — но рорстер
    /// может быть пуст (не сканировался), а имя сессии создано из
    /// DefaultCharacter. В этих случаях передаём null: лоунчер сам возьмёт
    /// дефолтного персонажа аккаунта.
    /// </summary>
    private static string? ResolveLaunchCharacterName(AccountModel account, SessionModel session)
    {
        var name = session.CharacterName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (account.Characters.Count == 0
            || string.Equals(name, account.DefaultCharacter, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name;
    }

    private async Task<bool> LaunchSessionAndWaitAsync(SessionModel current, Action<string>? progress, CancellationToken ct)
    {
        // Ссылки на модели могут устареть (UI-таймер перечитывает конфиг) — работаем по Id.
        var session = _store.Config.Sessions.FirstOrDefault(s => s.Id == current.Id);
        if (session is null)
        {
            progress?.Invoke($"{Label(current)}: сессия удалена из конфига — пропуск");
            return false;
        }

        var account = _store.FindAccount(session.AccountLogin);
        if (account is null)
        {
            progress?.Invoke($"{Label(session)}: аккаунт '{session.AccountLogin}' не найден — пропуск");
            return false;
        }

        // 1. Клиент: живой не трогаем, мёртвый — запускаем.
        int launchedPid = 0;
        if (!WowLauncher.IsProcessAlive(session.WowProcessId, session.WowImageName))
        {
            progress?.Invoke($"{Label(session)}: запускаю клиента…");
            var result = _launcher.LaunchCharacter(account, ResolveLaunchCharacterName(account, session), allowWhenRunning: false);
            if (!result.Started)
            {
                progress?.Invoke($"{Label(session)}: клиент не запущен — {result.Detail}");
                return false;
            }

            launchedPid = result.ProcessId;
            session.WowProcessId = result.ProcessId;
            session.WowImageName = "Wow";
            session.State = SessionState.Launching;
            session.LastUpdatedUtc = DateTime.UtcNow;
            _store.Save();
            McbLog.Info("Group", $"{Label(session)}: клиент запущен (PID {result.ProcessId})");
        }

        // 2. Ждём загрузки бота: Pulse аттачит CopilotBuddy сам, когда клиент
        // войдёт в мир. Готово = бот жив и продержался &gt; 10 с после аттача.
        progress?.Invoke($"{Label(session)}: жду загрузки бота…");
        var deadline = DateTime.UtcNow.AddSeconds(GroupLaunchBotTimeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct).ConfigureAwait(false);

            session = _store.Config.Sessions.FirstOrDefault(s => s.Id == session.Id);
            if (session is null)
            {
                return false;
            }

            // Страховка от гонки с перечитыванием конфига в UI: если записанный
            // нами PID потерялся, а процесс жив — возвращаем его на место.
            if (session.WowProcessId <= 0 && launchedPid > 0
                && WowLauncher.IsProcessAlive(launchedPid, "Wow"))
            {
                session.WowProcessId = launchedPid;
                session.WowImageName = "Wow";
                _store.Save();
            }

            Pulse();

            if (IsBotLoaded(session))
            {
                progress?.Invoke($"{Label(session)}: бот загружен ✓");
                return true;
            }
        }

        progress?.Invoke($"{Label(session)}: таймаут ожидания бота ({GroupLaunchBotTimeoutSec} с)");
        return false;
    }

    /// <summary>Бот сессии загружен: CopilotBuddy жив и продержался &gt; 10 с после аттача.</summary>
    private static bool IsBotLoaded(SessionModel session)
        => WowLauncher.IsProcessAlive(session.BotProcessId, "CopilotBuddy")
           && session.BotLastAttachUtc != DateTime.MinValue
           && (DateTime.UtcNow - session.BotLastAttachUtc).TotalSeconds > 10;

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
