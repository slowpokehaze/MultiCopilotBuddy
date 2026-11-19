namespace MCB.Core.Models;

/// <summary>
/// Персонаж, привязанный к аккаунту. <see cref="Hidden"/> = tombstone:
/// скрытый/удалённый пользователем персонаж никогда не возвращается
/// при автопополнении рорстера (FR-1).
/// </summary>
public sealed class CharacterModel
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Реалм персонажа (по умолчанию наследует аккаунт).</summary>
    public string Realm { get; set; } = string.Empty;

    /// <summary>Класс в верхнем регистре (PALADIN, DRUID, …) — для цвета в UI.</summary>
    public string Class { get; set; } = string.Empty;

    public string Race { get; set; } = string.Empty;

    public string Sex { get; set; } = string.Empty;

    /// <summary>Tombstone: пользователь скрыл/удалил персонажа — автопополнение его не трогает.</summary>
    public bool Hidden { get; set; }

    public int Order { get; set; }

    /// <summary>
    /// Данные рорстера из collect-аддона (MCBuddyDB / WowManagerDB):
    /// level, gold, gs, ilvl, spec, zone, guild, currencies... — ключи как в аддоне.
    /// </summary>
    public Dictionary<string, string> Data { get; set; } = new();
}
