namespace MCB.Core.Models;

/// <summary>Состояния сессии бота (FR-5).</summary>
public enum SessionState
{
    Idle,
    Launching,

    /// <summary>Процесс жив, но статус «в игре» ещё не подтверждён (уточняется на Этапе 5).</summary>
    Running,
    CharSelect,
    InGame,
    Botting,
    Crashed,
    Disabled,
}
