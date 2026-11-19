using MCB.Core.Models;
using MCB.Core.Storage;

namespace MCB.Core.Roster;

/// <summary>
/// Запись collect-аддона из SavedVariables (MCBuddyDB / WowManagerDB):
/// DB["&lt;реалм&gt;.&lt;имя&gt;"] = { name, realm, level, class, ..., gold (медь),
/// currencies, gs, ilvl, spec, updated ... } — поля 1:1 с Core.lua.
/// </summary>
public sealed class CharacterRecord
{
    public string Name { get; init; } = string.Empty;
    public string Realm { get; init; } = string.Empty;
    public string Class { get; init; } = string.Empty;
    public string Race { get; init; } = string.Empty;
    public string Sex { get; init; } = string.Empty;
    public string Faction { get; init; } = string.Empty;
    public string Guild { get; init; } = string.Empty;
    public string Zone { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public string Talents { get; init; } = string.Empty;
    public long Level { get; init; }
    public long GoldCopper { get; init; }
    public long Ilvl { get; init; }
    public long Gs { get; init; }
    public long Rested { get; init; }
    public long Xp { get; init; }
    public long XpMax { get; init; }
    public long AchPoints { get; init; }
    public long BagFree { get; init; }
    public long DailyDone { get; init; }
    public long DailyMax { get; init; }
    public long ArenaGames { get; init; }
    public long QuestsDone { get; init; }
    public long QuestsTotal { get; init; }
    public long Played { get; init; }
    public long Updated { get; init; }
    public Dictionary<string, long> Currencies { get; init; } = new();

    /// <summary>Золото строкой "г с м" (медь → монеты).</summary>
    public string GoldText
    {
        get
        {
            var gold = GoldCopper / 10000;
            var silver = GoldCopper / 100 % 100;
            var copper = GoldCopper % 100;
            return $"{gold}г {silver}с {copper}м";
        }
    }

    /// <summary>Виртуальная колонка cur:&lt;валюта&gt; (FR-6).</summary>
    public long Currency(string name)
        => Currencies.TryGetValue(name, out var value) ? value : 0;
}

/// <summary>Чтение рорстера из SavedVariables на диске — без запуска клиента (FR-6).</summary>
public static class RosterReader
{
    /// <summary>Имя DB-файла аддона MCBuddy и оригинального WowManager (для миграции данных).</summary>
    public static readonly string[] DbFileNames = { "MCBuddyDB.lua", "WowManagerDB.lua" };

    /// <summary>
    /// Разобрать DB-файл: словарь «ключ (реалм.имя) → запись». Служебные ключи
    /// (__relog, __social, ...) пропускаются. Возвращает также имя переменной
    /// (MCBuddyDB / WowManagerDB) — для диагностики.
    /// </summary>
    public static Dictionary<string, CharacterRecord> ReadDbFile(string path, out string variableName)
    {
        variableName = Path.GetFileNameWithoutExtension(path);
        var top = SavedVariablesParser.ParseFile(path);

        // Значение верхнего уровня может быть и `MCBuddyDB = {...}` (первое присваивание).
        var db = top.Values.OfType<Dictionary<string, object>>().FirstOrDefault()
                 ?? new Dictionary<string, object>();

        var records = new Dictionary<string, CharacterRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in db)
        {
            if (key.StartsWith("__") || value is not Dictionary<string, object> entry)
            {
                continue;
            }

            var record = ToRecord(key, entry);
            if (!string.IsNullOrWhiteSpace(record.Name))
            {
                records[key] = record;
            }
        }

        return records;
    }

    /// <summary>Отдельный файл per-character SavedVariables (MCBuddyCharDB) → поле played.</summary>
    public static long ReadPlayed(string path)
    {
        try
        {
            var top = SavedVariablesParser.ParseFile(path);
            var db = top.Values.OfType<Dictionary<string, object>>().FirstOrDefault();
            if (db is not null && db.TryGetValue("played", out var played) && played is double d)
            {
                return (long)d;
            }
        }
        catch
        {
        }

        return 0;
    }

    public static CharacterRecord ToRecord(string key, Dictionary<string, object> entry)
    {
        string? Str(string field) => entry.TryGetValue(field, out var v) && v is string s ? s : null;
        long Num(string field) => entry.TryGetValue(field, out var v) && v is double d ? (long)d : 0;

        var currencies = new Dictionary<string, long>(StringComparer.Ordinal);
        if (entry.TryGetValue("currencies", out var curRaw) && curRaw is Dictionary<string, object> cur)
        {
            foreach (var (currencyName, currencyValue) in cur)
            {
                if (currencyValue is double cnt)
                {
                    currencies[currencyName] = (long)cnt;
                }
            }
        }

        return new CharacterRecord
        {
            Name = Str("name") ?? key[(key.IndexOf('.') + 1)..],
            Realm = Str("realm") ?? key[..key.IndexOf('.')],
            Class = Str("class") ?? string.Empty,
            Race = Str("race") ?? string.Empty,
            Sex = Str("sex") ?? string.Empty,
            Faction = Str("faction") ?? string.Empty,
            Guild = Str("guild") ?? string.Empty,
            Zone = Str("zone") ?? string.Empty,
            Spec = Str("spec") ?? string.Empty,
            Talents = Str("talents") ?? string.Empty,
            Level = Num("level"),
            GoldCopper = Num("gold"),
            Ilvl = Num("ilvl"),
            Gs = Num("gs"),
            Rested = Num("rested"),
            Xp = Num("xp"),
            XpMax = Num("xpMax"),
            AchPoints = Num("achPoints"),
            BagFree = Num("bagFree"),
            DailyDone = Num("dailyDone"),
            DailyMax = Num("dailyMax"),
            ArenaGames = Num("arenaGames"),
            QuestsDone = Num("questsDone"),
            QuestsTotal = Num("questsTotal"),
            Played = Num("played"),
            Updated = Num("updated"),
            Currencies = currencies,
        };
    }

    /// <summary>Плоские колонки записи (для Data у CharacterModel и таблицы рорстера).</summary>
    public static Dictionary<string, string> ToColumns(CharacterRecord record)
    {
        var columns = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["level"] = record.Level.ToString(),
            ["gold"] = record.GoldText,
            ["gs"] = record.Gs > 0 ? record.Gs.ToString() : string.Empty,
            ["ilvl"] = record.Ilvl > 0 ? record.Ilvl.ToString() : string.Empty,
            ["spec"] = record.Spec,
            ["zone"] = record.Zone,
            ["guild"] = record.Guild,
            ["faction"] = record.Faction,
            ["race"] = record.Race,
            ["sex"] = record.Sex,
            ["bagFree"] = record.BagFree.ToString(),
            ["daily"] = $"{record.DailyDone}/{record.DailyMax}",
            ["arenaGames"] = record.ArenaGames.ToString(),
            ["quests"] = $"{record.QuestsDone}/{record.QuestsTotal}",
            ["played"] = TimeSpan.FromSeconds(record.Played).ToString(@"d\d\ hh\ч\ mm\м"),
            ["rested"] = record.Rested.ToString(),
            ["achPoints"] = record.AchPoints.ToString(),
            ["updated"] = record.Updated > 0
                ? DateTimeOffset.FromUnixTimeSeconds(record.Updated).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                : string.Empty,
        };
        foreach (var (currencyName, value) in record.Currencies)
        {
            columns["cur:" + currencyName] = value.ToString();
        }

        return columns;
    }

    /// <summary>
    /// Применить записи DB-файла к хранилищу: аккаунт ищется/создаётся по логину
    /// (имя папки WTF\Account, foldCase), персонажи — tombstone-aware (FR-1/FR-6).
    /// Возвращает число изменённых персонажей.
    /// </summary>
    public static int ApplyToStore(MultiAccountStore store, string accountFolderName,
        IReadOnlyDictionary<string, CharacterRecord> records)
    {
        var changed = 0;
        var account = store.FindAccount(accountFolderName);
        if (account is null)
        {
            account = new AccountModel
            {
                Login = accountFolderName.Trim(),
                Order = store.Config.Accounts.Count,
            };
            store.Config.Accounts.Add(account);
        }

        foreach (var record in records.Values)
        {
            var character = store.UpsertCharacter(account, record.Name, record.Realm, record.Class);
            if (character is null)
            {
                continue; // tombstone — данные не пишем
            }

            if (string.IsNullOrWhiteSpace(character.Class) && !string.IsNullOrWhiteSpace(record.Class))
            {
                character.Class = record.Class.ToUpperInvariant();
            }

            if (string.IsNullOrWhiteSpace(character.Race))
            {
                character.Race = record.Race;
            }

            if (string.IsNullOrWhiteSpace(character.Sex))
            {
                character.Sex = record.Sex;
            }

            character.Data = ToColumns(record);
            changed++;
        }

        return changed;
    }
}
