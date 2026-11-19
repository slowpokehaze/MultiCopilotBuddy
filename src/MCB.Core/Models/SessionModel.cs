namespace MCB.Core.Models;

/// <summary>Сессия бота: процесс WoW + персонаж + выбор бота/профиля/рутины (FR-5).</summary>
public sealed class SessionModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Логин аккаунта сессии (владелец процесса WoW).</summary>
    public string AccountLogin { get; set; } = string.Empty;

    public string CharacterName { get; set; } = string.Empty;

    /// <summary>Имя бота (GrindBuddy / PartyBot / Questing …).</summary>
    public string BotName { get; set; } = string.Empty;

    /// <summary>Имя combat-рутины (например «Singular wotlk»).</summary>
    public string RoutineName { get; set; } = string.Empty;

    /// <summary>Путь к профилю бота; настройки per-character переиспользуют CharacterSettings_&lt;имя&gt;.xml.</summary>
    public string ProfilePath { get; set; } = string.Empty;

    /// <summary>PID процесса WoW, к которому привязана сессия (0 = не привязан).</summary>
    public int WowProcessId { get; set; }

    /// <summary>Имя образа процесса без расширения ("Wow") — защита от переиспользования PID.</summary>
    public string WowImageName { get; set; } = string.Empty;

    /// <summary>Перезапускать клиента автоматически при краше/закрытии (FR-4).</summary>
    public bool AutoRestart { get; set; } = true;

    /// <summary>Автоматически запускать CopilotBuddy для сессии (когда задан BotName).</summary>
    public bool AutoAttach { get; set; } = true;

    /// <summary>
    /// Сколько раз подряд бот не продержался после запуска (клиент на экране
    /// выбора персонажа и т.п.). Растит паузу между повторами аттача —
    /// иначе UAC-запрос сыпется каждые 30 секунд.
    /// </summary>
    public int AttachFailCount { get; set; }

    /// <summary>Сколько раз подряд перезапускали безуспешно (сбрасывается при живом клиенте).</summary>
    public int RestartCount { get; set; }

    /// <summary>PID процесса CopilotBuddy, приаттаченного к этой сессии (0 = бот не запущен).</summary>
    public int BotProcessId { get; set; }

    /// <summary>
    /// Порт MCBBridge-плагина на этом клиенте (127.0.0.1) — быстрая смена
    /// персонажа и пинг статуса (Этап 2). 0 = не назначен.
    /// </summary>
    public int BridgePort { get; set; }

    /// <summary>Момент последней попытки аттача бота (UTC) — чтобы не плодить процессы.</summary>
    public DateTime BotLastAttachUtc { get; set; } = DateTime.MinValue;

    public SessionState State { get; set; } = SessionState.Idle;

    /// <summary>
    /// Очередь персонажей (Этап 6): авто-ротация на следующего видимого
    /// персонажа аккаунта каждые N часов (только через мост, без перезапуска).
    /// 0 = выключено.
    /// </summary>
    public int RotationHours { get; set; }

    /// <summary>Последняя ротация персонажа (UTC).</summary>
    public DateTime LastRotationUtc { get; set; } = DateTime.MinValue;

    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
}
