using System.Text.Json.Serialization;

namespace MCB.Core.Models;

/// <summary>Настройки интерфейса (Этап 4): язык UI.</summary>
public sealed class UiOptions
{
    /// <summary>Язык интерфейса: "ru" | "en".</summary>
    public string Language { get; set; } = "ru";

    [JsonIgnore]
    public bool IsEnglish => string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Настройки harvest-режима (Этап 4, FR-7).</summary>
public sealed class HarvestOptions
{
    /// <summary>
    /// Таймаут на аккаунт, минут (клиент должен сам выйти после harvest —
    /// аддон делает Quit() по завершении списка).
    /// </summary>
    public int PerAccountTimeoutMin { get; set; } = 15;

    /// <summary>Закрывать живого клиента аккаунта перед harvest-запуском.</summary>
    public bool KillRunning { get; set; } = true;
}
