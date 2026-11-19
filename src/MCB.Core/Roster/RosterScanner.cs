using MCB.Core.Storage;

namespace MCB.Core.Roster;

/// <summary>
/// Обход WTF клиента без запуса игры (FR-6): WTF\Account\&lt;акк&gt;\SavedVariables\
/// {MCBuddyDB,WowManagerDB}.lua → записи рорстера; per-char SavedVariables
/// (&lt;реалм&gt;\&lt;имя&gt;\SavedVariables\MCBuddyCharDB.lua) дополняют played.
/// </summary>
public static class RosterScanner
{
    /// <summary>
    /// Просканировать WTF-папку клиента. Применяет найденное к хранилищу.
    /// Возвращает (персонажей обновлено, какие файлы прочитаны).
    /// </summary>
    public static (int Changed, List<string> Files) Scan(MultiAccountStore store, string wtfPath)
    {
        var files = new List<string>();
        var changed = 0;
        var accountsDir = Path.Combine(wtfPath, "Account");
        if (!Directory.Exists(accountsDir))
        {
            return (0, files);
        }

        foreach (var accountDir in Directory.GetDirectories(accountsDir))
        {
            var accountName = Path.GetFileName(accountDir);

            // DB-файлы аккаунта.
            var savedVariables = Path.Combine(accountDir, "SavedVariables");
            foreach (var dbName in RosterReader.DbFileNames)
            {
                var dbPath = Path.Combine(savedVariables, dbName);
                if (!File.Exists(dbPath))
                {
                    continue;
                }

                try
                {
                    var records = RosterReader.ReadDbFile(dbPath, out _);
                    files.Add(dbPath);
                    changed += RosterReader.ApplyToStore(store, accountName, records);
                }
                catch (FormatException)
                {
                    // битый/чужой lua — пропускаем файл, не роняя скан
                }
            }

            // per-char played (MCBuddyCharDB / WowManagerCharDB).
            changed += MergePlayed(store, accountDir, "MCBuddyCharDB.lua", files);
            changed += MergePlayed(store, accountDir, "WowManagerCharDB.lua", files);
        }

        return (changed, files);
    }

    private static int MergePlayed(MultiAccountStore store, string accountDir, string fileName, List<string> files)
    {
        var merged = 0;
        foreach (var realmDir in SafeDirectories(accountDir))
        {
            foreach (var charDir in SafeDirectories(realmDir))
            {
                var charDbPath = Path.Combine(charDir, "SavedVariables", fileName);
                if (!File.Exists(charDbPath))
                {
                    continue;
                }

                var charName = Path.GetFileName(charDir);
                var account = store.FindAccount(Path.GetFileName(accountDir));
                if (account is null)
                {
                    continue;
                }

                var character = store.FindCharacter(account, charName);
                if (character is null || character.Hidden)
                {
                    continue;
                }

                var played = RosterReader.ReadPlayed(charDbPath);
                if (played > 0)
                {
                    files.Add(charDbPath);
                    character.Data["played"] = TimeSpan.FromSeconds(played).ToString(@"d\d\ hh\ч\ mm\м");
                    merged++;
                }
            }
        }

        return merged;
    }

    private static IEnumerable<string> SafeDirectories(string path)
        => Directory.Exists(path) ? Directory.GetDirectories(path) : Enumerable.Empty<string>();
}
