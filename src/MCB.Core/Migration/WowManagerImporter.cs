using System.Text;
using System.Text.Json;
using MCB.Core.Models;
using MCB.Core.Storage;

namespace MCB.Core.Migration;

/// <summary>
/// Миграция из WowManager (Этап 6): чтение characters.json (плоский список
/// «аккаунт → персонажи», пароли «enc:v1:» = DPAPI без энтропии либо plain),
/// расшифровка и перешифровка в DPAPI MCB (со своей энтропией).
/// WowManager не трогаем — данные копируются, исходный файл остаётся на месте.
/// </summary>
public static class WowManagerImporter
{
    /// <summary>Класс-токены WoW по id из CLASS_BY_ID wow-autologin.</summary>
    private static readonly Dictionary<long, string> ClassByWowManagerId = new()
    {
        [1] = "WARRIOR", [2] = "PALADIN", [3] = "HUNTER", [4] = "ROGUE",
        [5] = "PRIEST", [6] = "DEATHKNIGHT", [7] = "SHAMAN", [8] = "MAGE",
        [9] = "WARLOCK", [11] = "DRUID",
    };

    /// <summary>
    /// Импортировать characters.json. Возвращает (аккаунтов, персонажей, заметки).
    /// Аккаунт с тем же логином в MCB — обновляется (пароль только если в MCB пусто).
    /// </summary>
    public static (int Accounts, int Characters, List<string> Notes) Import(MultiAccountStore store, string charactersJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(charactersJsonPath));
        var root = doc.RootElement;
        if (!root.TryGetProperty("characters", out var characters) || characters.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("В файле нет массива 'characters' — это не characters.json WowManager?");
        }

        var notes = new List<string>();
        var accounts = new Dictionary<string, AccountModel>(StringComparer.OrdinalIgnoreCase);
        var characterCount = 0;

        foreach (var entry in characters.EnumerateArray())
        {
            var login = Str(entry, "account");
            if (string.IsNullOrWhiteSpace(login))
            {
                continue; // мусорная запись без аккаунта
            }

            var name = Str(entry, "name");
            var isAccountEntry = string.IsNullOrWhiteSpace(name);

            if (!accounts.TryGetValue(login, out var account))
            {
                account = store.FindAccount(login);
                if (account is null)
                {
                    account = new AccountModel
                    {
                        Login = login,
                        WowPath = Str(root, "wow_path") ?? string.Empty,
                        Realm = Str(entry, "realm") ?? string.Empty,
                        Realmlist = Str(entry, "realmlist") ?? string.Empty,
                        Order = store.Config.Accounts.Count,
                    };
                    store.Config.Accounts.Add(account);
                    notes.Add($"Аккаунт '{login}' добавлен.");
                }

                accounts[login] = account;
            }

            if (string.IsNullOrWhiteSpace(account.Realmlist) && Str(entry, "realmlist") is { } realmlist)
            {
                account.Realmlist = realmlist;
            }

            if (string.IsNullOrWhiteSpace(account.Realm) && Str(entry, "realm") is { } realm)
            {
                account.Realm = realm;
            }

            if (string.IsNullOrWhiteSpace(account.WowPath) && Str(root, "wow_path") is { } wowPath)
            {
                account.WowPath = wowPath;
            }

            // Секреты лежат на аккаунтной записи (name == "").
            if (isAccountEntry)
            {
                ImportSecret(account, entry, "password", plain => account.SetPassword(plain),
                    account.HasPassword, "пароль", notes, login);
                ImportSecret(account, entry, "totp_secret", plain => account.SetTotpSecret(plain),
                    !string.IsNullOrEmpty(account.EncryptedTotp), "TOTP", notes, login);
                continue;
            }

            var classId = entry.TryGetProperty("class", out var classRaw) && classRaw.ValueKind == JsonValueKind.Number
                ? classRaw.GetInt64()
                : 0;
            var classToken = ClassByWowManagerId.TryGetValue(classId, out var token) ? token : string.Empty;

            if (store.UpsertCharacter(account, name ?? string.Empty,
                                      Str(entry, "realm") ?? account.Realm,
                                      classToken) is not null)
            {
                characterCount++;
            }
        }

        store.Save();
        return (accounts.Count, characterCount, notes);
    }

    private static void ImportSecret(AccountModel account, JsonElement entry, string field,
        Action<string> setter, bool alreadySet, string label, List<string> notes, string login)
    {
        if (alreadySet)
        {
            return; // не перетираем то, что уже есть в MCB
        }

        var stored = Str(entry, field);
        if (string.IsNullOrEmpty(stored))
        {
            return;
        }

        // "enc:v1:" + base64(DPAPI blob, без энтропии) — расшифровка тем же
        // пользователем на той же машине; иначе ждём plaintext.
        const string prefix = "enc:v1:";
        if (stored.StartsWith(prefix, StringComparison.Ordinal))
        {
            try
            {
                var plain = DpapiNoEntropy.Unprotect(Convert.FromBase64String(stored[prefix.Length..]));
                if (!string.IsNullOrEmpty(plain))
                {
                    setter(plain);
                    notes.Add($"{label} '{login}' перешифрован в DPAPI MCB.");
                }
            }
            catch (Exception ex)
            {
                notes.Add($"{label} '{login}' не расшифрован ({ex.Message}) — задай вручную.");
            }
        }
        else
        {
            setter(stored);
            notes.Add($"{label} '{login}' импортирован (plaintext в источнике).");
        }
    }

    private static string? Str(JsonElement element, string field)
        => element.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}

/// <summary>DPAPI без энтропии — формат секретов WowManager (CryptUnprotectData/CryptProtectData).</summary>
public static class DpapiNoEntropy
{
    public static string Unprotect(byte[] blob) => Encoding.UTF8.GetString(UnprotectRaw(blob));

    public static byte[] Protect(string plain) => ProtectRaw(Encoding.UTF8.GetBytes(plain));

    [System.Runtime.InteropServices.DllImport("crypt32", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [System.Runtime.InteropServices.DllImport("crypt32", SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob input, IntPtr description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [System.Runtime.InteropServices.DllImport("kernel32")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private const int UiForbidden = 0x1;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    public static byte[] UnprotectRaw(byte[] blob)
    {
        var input = ToBlob(blob);
        CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output);
        return FromBlob(output);
    }

    public static byte[] ProtectRaw(byte[] plain)
    {
        var input = ToBlob(plain);
        CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output);
        return FromBlob(output);
    }

    private static DataBlob ToBlob(byte[] data)
    {
        var handle = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length);
        System.Runtime.InteropServices.Marshal.Copy(data, 0, handle, data.Length);
        return new DataBlob { Size = data.Length, Data = handle };
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        try
        {
            var result = new byte[blob.Size];
            System.Runtime.InteropServices.Marshal.Copy(blob.Data, result, 0, blob.Size);
            return result;
        }
        finally
        {
            _ = LocalFree(blob.Data);
        }
    }
}
