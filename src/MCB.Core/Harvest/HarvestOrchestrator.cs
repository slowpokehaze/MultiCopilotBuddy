using System.Diagnostics;
using MCB.Core.Launch;
using MCB.Core.Models;
using MCB.Core.Roster;
using MCB.Core.Storage;

namespace MCB.Core.Harvest;

/// <summary>Итог harvest по одному аккаунту.</summary>
public sealed class HarvestAccountResult
{
    public string AccountLogin { get; init; } = string.Empty;
    public HarvestStatus Status { get; init; }
    public List<string> Characters { get; init; } = new();
    public string Detail { get; init; } = string.Empty;
}

public enum HarvestStatus
{
    Harvested,
    Timeout,
    Failed,
    Skipped,
}

/// <summary>
/// Harvest-режим (Этап 4, FR-7): «собрать голд со всех аккаунтов одной кнопкой».
/// Семантика wow-autologin: аддон MCBuddy в клиенте сам обходит список
/// harvestChars (collect → смена без перезапуска через DLL-глобал) и по
/// завершении списка закрывает игру (Quit()). Оркестратор последовательно:
/// для каждого аккаунта пишет Config.lua (harvest=true, harvestChars),
/// запускает клиента, ждёт выхода процесса, сканирует SavedVariables —
/// рорстер обновлён. Очередь — порядок аккаунтов в конфиге.
/// </summary>
public sealed class HarvestOrchestrator
{
    private readonly MultiAccountStore _store;

    public HarvestOrchestrator(MultiAccountStore store) => _store = store;

    /// <summary>Прогресс: человекочитаемая строка на шаге.</summary>
    public Action<string>? Progress { get; set; }

    /// <summary>
    /// Прогнать harvest по аккаунтам в порядке конфига (или по заданным логинам).
    /// Блокирующий вызов — из UI запускать в фоне.
    /// </summary>
    public List<HarvestAccountResult> RunAll(
        IEnumerable<string>? accountLogins = null,
        IReadOnlyList<string>? explicitChars = null,
        int? perAccountTimeoutMin = null,
        bool? killRunning = null)
    {
        var logins = accountLogins?.ToList();
        var accounts = logins is null or { Count: 0 }
            ? _store.Config.Accounts.OrderBy(a => a.Order).ToList()
            : logins.Select(login => _store.FindAccount(login)
                    ?? throw new InvalidOperationException($"Аккаунт '{login}' не найден."))
                .ToList();

        var results = new List<HarvestAccountResult>();
        var launcher = new WowLauncher(_store);
        var timeoutMin = perAccountTimeoutMin ?? _store.Config.Harvest.PerAccountTimeoutMin;
        var kill = killRunning ?? _store.Config.Harvest.KillRunning;

        foreach (var account in accounts)
        {
            var chars = ResolveHarvestChars(account, explicitChars);
            if (chars.Count == 0)
            {
                results.Add(new HarvestAccountResult
                {
                    AccountLogin = account.Login,
                    Status = HarvestStatus.Skipped,
                    Detail = "Нет видимых персонажей (или список пуст).",
                });
                continue;
            }

            results.Add(RunAccount(launcher, account, chars, timeoutMin, kill));
            _store.Save();
        }

        return results;
    }

    /// <summary>
    /// Список персонажей harvest: явно заданные, иначе все видимые в порядке
    /// конфига (первый в списке — тот, на кого пишется autologin.json).
    /// </summary>
    public static List<string> ResolveHarvestChars(AccountModel account, IReadOnlyList<string>? explicitChars)
    {
        if (explicitChars is { Count: > 0 })
        {
            return explicitChars.ToList();
        }

        return account.Characters
            .Where(c => !c.Hidden)
            .OrderBy(c => c.Order)
            .Select(c => c.Name)
            .ToList();
    }

    private HarvestAccountResult RunAccount(WowLauncher launcher, AccountModel account,
        List<string> chars, int timeoutMin, bool killRunning)
    {
        Progress?.Invoke($"[{account.Login}] harvest: {string.Join(", ", chars)}");

        // Живой клиент не harvest-ится (конфиг с harvest пишется на запуске) —
        // по настройке закрываем и перезапускаем.
        var alive = launcher.FindAliveSession(account.Login);
        if (alive is not null)
        {
            if (!killRunning)
            {
                return new HarvestAccountResult
                {
                    AccountLogin = account.Login,
                    Status = HarvestStatus.Skipped,
                    Characters = chars,
                    Detail = $"Клиент уже запущен (PID {alive.WowProcessId}), KillRunning=false.",
                };
            }

            Progress?.Invoke($"[{account.Login}] закрываю живого клиента (PID {alive.WowProcessId})…");
            launcher.KillAliveClient(account.Login);
        }

        var launch = launcher.LaunchCharacter(account, chars[0], allowWhenRunning: true, harvestChars: chars);
        if (!launch.Started)
        {
            return new HarvestAccountResult
            {
                AccountLogin = account.Login,
                Status = HarvestStatus.Failed,
                Characters = chars,
                Detail = launch.Detail,
            };
        }

        Progress?.Invoke($"[{account.Login}] клиент запущен (PID {launch.ProcessId}), жду завершения harvest…");

        // Аддон сам обходит список и делает Quit(); ждём выхода процесса.
        var deadline = DateTime.UtcNow.AddMinutes(Math.Max(1, timeoutMin));
        while (DateTime.UtcNow < deadline)
        {
            if (!WowLauncher.IsProcessAlive(launch.ProcessId, "Wow"))
            {
                break;
            }

            System.Threading.Thread.Sleep(2000);
        }

        if (WowLauncher.IsProcessAlive(launch.ProcessId, "Wow"))
        {
            Progress?.Invoke($"[{account.Login}] таймаут {timeoutMin} мин — закрываю принудительно.");
            launcher.KillAliveClient(account.Login);
            return new HarvestAccountResult
            {
                AccountLogin = account.Login,
                Status = HarvestStatus.Timeout,
                Characters = chars,
                Detail = $"Клиент не вышел за {timeoutMin} мин.",
            };
        }

        // Клиент вышел — данные в SavedVariables, сканируем.
        var wtf = Path.Combine(WowLauncher.ResolveWowPath(account.WowPath).Directory, "WTF");
        var (changed, files) = RosterScanner.Scan(_store, wtf);
        Progress?.Invoke($"[{account.Login}] готово: файлов {files.Count}, персонажей обновлено {changed}.");

        return new HarvestAccountResult
        {
            AccountLogin = account.Login,
            Status = HarvestStatus.Harvested,
            Characters = chars,
            Detail = $"Файлов {files.Count}, обновлено {changed}.",
        };
    }
}
