using System.Text.Json;
using System.Text.Json.Serialization;
using MCB.Core.Models;
using MCB.Core.Security;

namespace MCB.Core.Storage;

/// <summary>
/// JSON-хранилище Settings\MultiAccount.json (план §4.1): camelCase, UTF-8,
/// пароли — только DPAPI-блобы. Потокобезопасность не нужна на Этапе 0
/// (один поток UI), но Save атомарный (temp + replace).
/// </summary>
public sealed class MultiAccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string FilePath { get; }

    public MultiAccountConfig Config { get; private set; }

    /// <summary>Путь по умолчанию: Settings\MultiAccount.json в папке CopilotBuddy.</summary>
    public static string DefaultPath(string copilotBuddyDirectory)
        => Path.Combine(copilotBuddyDirectory, "Settings", "MultiAccount.json");

    public MultiAccountStore(string filePath)
    {
        FilePath = filePath;
        Config = new MultiAccountConfig();
        if (File.Exists(filePath))
        {
            Load();
        }
    }

    public void Load()
    {
        var json = File.ReadAllText(FilePath);
        Config = JsonSerializer.Deserialize<MultiAccountConfig>(json, JsonOptions) ?? new MultiAccountConfig();
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(Config, JsonOptions);

        // Атомарная запись: temp-файл + File.Replace, чтобы краш не оставил битый JSON.
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        if (File.Exists(FilePath))
        {
            File.Replace(tempPath, FilePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, FilePath);
        }
    }

    // ---------- Аккаунты ----------

    /// <summary>Добавляет аккаунт (логин — ключ, без регистра). Кидаёт при дубле.</summary>
    public AccountModel AddAccount(string login, string password, string wowPath, string realm)
    {
        if (FindAccount(login) is not null)
        {
            throw new InvalidOperationException($"Аккаунт '{login}' уже существует.");
        }

        var account = new AccountModel
        {
            Login = login.Trim(),
            WowPath = wowPath,
            Realm = realm,
            Order = Config.Accounts.Count,
        };
        account.SetPassword(password);
        Config.Accounts.Add(account);
        return account;
    }

    public AccountModel? FindAccount(string login)
        => Config.Accounts.FirstOrDefault(a =>
            string.Equals(a.Login, login.Trim(), StringComparison.OrdinalIgnoreCase));

    // ---------- Персонажи (FR-1) ----------

    /// <summary>
    /// Автопополнение рорстера: добавляет персонажа или дозаполняет существующего.
    /// Tombstone-персонажи (Hidden) не возвращаются и данные о них не теряются.
    /// Возвращает null, если персонаж — tombstone (добавлять было нечего).
    /// </summary>
    public CharacterModel? UpsertCharacter(AccountModel account, string name, string realm, string charClass = "")
    {
        var existing = FindCharacter(account, name);
        if (existing is not null)
        {
            // Tombstone не воскресает; новые данные в него не пишем, чтобы
            // «удалённый» персонаж не мутировал под ногами пользователя.
            if (existing.Hidden)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(realm) && string.IsNullOrWhiteSpace(existing.Realm))
            {
                existing.Realm = realm;
            }

            if (!string.IsNullOrWhiteSpace(charClass) && string.IsNullOrWhiteSpace(existing.Class))
            {
                existing.Class = charClass.ToUpperInvariant();
            }

            return existing;
        }

        var character = new CharacterModel
        {
            Name = name.Trim(),
            Realm = string.IsNullOrWhiteSpace(realm) ? account.Realm : realm,
            Class = string.IsNullOrWhiteSpace(charClass) ? string.Empty : charClass.ToUpperInvariant(),
            Order = account.Characters.Count,
        };
        account.Characters.Add(character);
        return character;
    }

    public CharacterModel? FindCharacter(AccountModel account, string name)
        => account.Characters.FirstOrDefault(c =>
            string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Скрывает персонажа (tombstone): он исчезает из рорстера и больше
    /// никогда не возвращается через автопополнение (FR-1).
    /// </summary>
    public bool HideCharacter(AccountModel account, string name)
    {
        var character = FindCharacter(account, name);
        if (character is null)
        {
            return false;
        }

        character.Hidden = true;
        return true;
    }

    /// <summary>Перенос персонажа на другой аккаунт (FR-1 «перенести»).</summary>
    public CharacterModel MoveCharacter(AccountModel from, AccountModel to, string name)
    {
        var character = FindCharacter(from, name) ?? throw new InvalidOperationException(
            $"Персонаж '{name}' не найден у аккаунта '{from.Login}'.");
        from.Characters.Remove(character);

        var existing = FindCharacter(to, character.Name);
        if (existing is not null)
        {
            // У целевого аккаунта уже есть такой персонаж — оставляем его.
            return existing;
        }

        character.Order = to.Characters.Count;
        to.Characters.Add(character);
        return character;
    }

    /// <summary>Переупорядочивание персонажей внутри аккаунта (drag-n-drop, FR-1).</summary>
    public void ReorderCharacters(AccountModel account, IList<string> namesInOrder)
    {
        var index = 0;
        foreach (var name in namesInOrder)
        {
            var character = FindCharacter(account, name);
            if (character is not null)
            {
                character.Order = index++;
            }
        }

        account.Characters.Sort((a, b) => a.Order.CompareTo(b.Order));
    }
}
