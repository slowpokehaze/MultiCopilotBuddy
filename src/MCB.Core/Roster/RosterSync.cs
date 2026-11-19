using MCB.Core.Models;
using MCB.Core.Storage;

namespace MCB.Core.Roster;

/// <summary>Запись рорстера, пришедшая извне: репорт патч-DLL (FR-3) или парсер SavedVariables (FR-6).</summary>
public sealed record RosterEntry(string AccountLogin, string Name, string Realm, string CharClass = "");

/// <summary>
/// Синхронизация рорстера из внешних источников (FR-1 автопополнение):
/// порядок аккаунтов/персонажей = порядок первых появлений, tombstones не воскресают.
/// </summary>
public static class RosterSync
{
    /// <summary>
    /// Применяет пачку записей к хранилищу. Аккаунт с неизвестным логином
    /// создаётся (без пароля — его введёт пользователь). Возвращает,
    /// были ли изменения (чтобы знать, нужно ли Save).
    /// </summary>
    public static bool Apply(MultiAccountStore store, IEnumerable<RosterEntry> entries)
    {
        var changed = false;

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var account = store.FindAccount(entry.AccountLogin);
            if (account is null)
            {
                account = new AccountModel
                {
                    Login = entry.AccountLogin.Trim(),
                    Realm = entry.Realm,
                    Order = store.Config.Accounts.Count,
                };
                store.Config.Accounts.Add(account);
                changed = true;
            }

            var before = (account.Characters.Count, TotalKnownHash(account));
            store.UpsertCharacter(account, entry.Name, entry.Realm, entry.CharClass);
            if ((account.Characters.Count, TotalKnownHash(account)) != before)
            {
                changed = true;
            }
        }

        return changed;
    }

    private static int TotalKnownHash(AccountModel account)
        => account.Characters.Sum(c => (c.Class?.Length ?? 0) + (c.Realm?.Length ?? 0));
}
