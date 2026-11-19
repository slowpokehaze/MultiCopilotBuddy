namespace MCB.Core.Models;

/// <summary>Профиль внешнего лоадера (FR-2, порт orchestrate_loader из WowManager).</summary>
public sealed class LoaderProfile
{
    /// <summary>Заголовок окна лоадера для FindWindow.</summary>
    public string WindowTitle { get; set; } = string.Empty;

    /// <summary>Идентификатор поля пути (автоматизация: UIA AutomationId или ControlID).</summary>
    public string PathFieldId { get; set; } = string.Empty;

    /// <summary>Текст кнопки запуска.</summary>
    public string ButtonText { get; set; } = string.Empty;

    /// <summary>Путь к exe лоадера (если его надо запускать самому).</summary>
    public string LoaderExePath { get; set; } = string.Empty;
}

/// <summary>Настройки запуска клиентов (cooldown, сериализация, лоадер).</summary>
public sealed class LaunchOptions
{
    /// <summary>Порт LAUNCH_COOLDOWN_SEC из WowManager.</summary>
    public int CooldownSec { get; set; } = 20;

    /// <summary>Профиль внешнего лоадера; null = прямой запуск Wow.exe.</summary>
    public LoaderProfile? Loader { get; set; }

    /// <summary>
    /// Путь к CopilotBuddy.exe для аттача бота; пусто = автодетект (корень бота
    /// относительно сборки MCB).
    /// </summary>
    public string CopilotBuddyExePath { get; set; } = string.Empty;

    /// <summary>
    /// Этап 5: по копии бота на сессию (изоляция общих файлов — Logs, кэш
    /// компиляции плагинов). false = один и тот же exe на все сессии
    /// (статик-стейт бота изолирован процессами, Logs per-PID — этого обычно
    /// достаточно).
    /// </summary>
    public bool BotCopyPerSession { get; set; }
}

/// <summary>Настройки рорстера (колонки и ширины — FR-6).</summary>
public sealed class RosterOptions
{
    public List<string> Columns { get; set; } = new();

    public Dictionary<string, double> ColumnWidths { get; set; } = new();
}

/// <summary>Поиск группы (FR-8): флаг прокидывается в Config.lua аддона.</summary>
public sealed class LfgOptions
{
    public bool Enabled { get; set; }
}

/// <summary>Синхронизация друзей/игнора (FR-8).</summary>
public sealed class SocialOptions
{
    public bool SyncFriends { get; set; } = true;
}

/// <summary>Корневая модель Settings\MultiAccount.json (план §4.1).</summary>
public sealed class MultiAccountConfig
{
    public List<AccountModel> Accounts { get; set; } = new();

    /// <summary>Сессии ботов (состояния переживают перезапуск менеджера).</summary>
    public List<SessionModel> Sessions { get; set; } = new();

    public LaunchOptions Launch { get; set; } = new();

    public RosterOptions Roster { get; set; } = new();

    public LfgOptions Lfg { get; set; } = new();

    public SocialOptions Social { get; set; } = new();

    /// <summary>Настройки интерфейса (язык).</summary>
    public UiOptions Ui { get; set; } = new();

    /// <summary>Настройки harvest-режима.</summary>
    public HarvestOptions Harvest { get; set; } = new();
}
