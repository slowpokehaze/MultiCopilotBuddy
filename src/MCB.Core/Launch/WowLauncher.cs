using System.Diagnostics;
using System.Threading;
using MCB.Core.Addon;
using MCB.Core.Models;
using MCB.Core.Storage;

namespace MCB.Core.Launch;

public enum LaunchFailure
{
    None,
    MissingWowExe,
    MissingCharacter,
    NoPassword,
    AlreadyRunning,
    Cooldown,
    SpawnError,
}

/// <summary>Результат попытки запуска клиента аккаунта.</summary>
public sealed class LaunchResult
{
    public bool Started { get; init; }
    public int ProcessId { get; init; }
    public LaunchFailure Failure { get; init; }
    public string Detail { get; init; } = string.Empty;

    /// <summary>Остаток cooldown в секундах (для Failure == Cooldown).</summary>
    public int CooldownRemainingSec { get; init; }

    public static LaunchResult Ok(int pid) => new() { Started = true, ProcessId = pid };

    public static LaunchResult Fail(LaunchFailure failure, string detail, int cooldownRemainingSec = 0)
        => new() { Failure = failure, Detail = detail, CooldownRemainingSec = cooldownRemainingSec };
}

/// <summary>
/// Запуск клиента WoW аккаунта (Этап 1): запись autologin.json (логин/пароль/
/// реалм/персонаж делает AwesomeWotlkLib.dll), cooldown, «один клиент на аккаунт»,
/// сериализация запусков. Смена персонажа — через перезапуск (базовый путь без IPC).
/// </summary>
public sealed class WowLauncher
{
    private readonly MultiAccountStore _store;
    private readonly object _sync = new();

    public WowLauncher(MultiAccountStore store)
    {
        _store = store;
        CooldownSec = store.Config.Launch.CooldownSec;
    }

    /// <summary>Cooldown запусков (порт LAUNCH_COOLDOWN_SEC; по плану §4.1 — 20 с).</summary>
    public int CooldownSec { get; set; }

    /// <summary>Сколько секунд после старта считаем сессию Launching, потом Running.</summary>
    public int LaunchingGraceSec { get; set; } = 90;

    /// <summary>Wow.exe запущен и жив? imageName (без расширения) защищает от переиспользования PID.</summary>
    public static bool IsProcessAlive(int pid, string? imageName = null)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return string.IsNullOrEmpty(imageName)
                || string.Equals(process.ProcessName, imageName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Набор PID живых WoW-клиентов, которые уже в мире (не на логин-скрине
    /// и не на выборе персонажа). Признак: на логине/выборе персонажа у клиента
    /// единственный ESTABLISHED-коннект к auth-серверу (порт 3724); в мире —
    /// коннект к world-серверу с другим портом. Один вызов netstat на всех.
    /// </summary>
    public static HashSet<int> GetInWorldPids()
    {
        var result = new HashSet<int>();
        var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        try
        {
            using var netstat = Process.Start(psi);
            if (netstat is null)
            {
                return result;
            }

            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(5000);

            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5
                    || !string.Equals(parts[0], "TCP", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(parts[3], "ESTABLISHED", StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(parts[^1], out var pid))
                {
                    continue;
                }

                var foreign = parts[2];
                var portPos = foreign.LastIndexOf(':');
                if (portPos < 0 || !int.TryParse(foreign[(portPos + 1)..], out var remotePort))
                {
                    continue;
                }

                if (remotePort != AuthServerPort)
                {
                    result.Add(pid);
                }
            }
        }
        catch
        {
            // netstat недоступен — считаем, что данных нет (пустой набор).
        }

        return result;
    }

    /// <summary>Порт auth-сервера (логин/выбор персонажа) для определения «в мире».</summary>
    public const int AuthServerPort = 3724;

    /// <summary>Разбирает WowPath аккаунта: путь к exe ИЛИ к папке клиента.</summary>
    public static (string ExePath, string Directory) ResolveWowPath(string wowPath)
    {
        if (string.IsNullOrWhiteSpace(wowPath))
        {
            return (string.Empty, string.Empty);
        }

        var full = Path.GetFullPath(wowPath.Trim());
        if (full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return (full, Path.GetDirectoryName(full) ?? string.Empty);
        }

        // Папка клиента.
        var candidate = Path.Combine(full, "Wow.exe");
        return (candidate, full);
    }

    /// <summary>
    /// Живая сессия клиента аккаунта (по списку сессий хранилища).
    /// Возвращает сессию или null, если клиента нет.
    /// </summary>
    public SessionModel? FindAliveSession(string accountLogin)
    {
        foreach (var session in _store.Config.Sessions)
        {
            if (!string.Equals(session.AccountLogin, accountLogin, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsProcessAlive(session.WowProcessId, session.WowImageName))
            {
                return session;
            }
        }

        return null;
    }

    /// <summary>
    /// «Запустить персонажа»: пишет autologin.json и стартует Wow.exe.
    /// characterName пустой → логин без выбора персонажа.
    /// </summary>
    public LaunchResult LaunchCharacter(AccountModel account, string? characterName, bool allowWhenRunning = false,
        IReadOnlyList<string>? harvestChars = null)
    {
        lock (_sync)
        {
            // 1. Один клиент на аккаунт.
            var alive = FindAliveSession(account.Login);
            if (alive is not null && !allowWhenRunning)
            {
                return LaunchResult.Fail(LaunchFailure.AlreadyRunning,
                    $"Клиент аккаунта '{account.Login}' уже жив (PID {alive.WowProcessId}, сессия {alive.Id}).");
            }

            // 2. Cooldown запусков.
            if (account.LastLaunchUtc != DateTime.MinValue)
            {
                var elapsed = DateTime.UtcNow - account.LastLaunchUtc;
                var remaining = CooldownSec - (int)elapsed.TotalSeconds;
                if (remaining > 0)
                {
                    return LaunchResult.Fail(LaunchFailure.Cooldown,
                        $"Cooldown аккаунта '{account.Login}': ещё {remaining} с.", remaining);
                }
            }

            // 3. Wow.exe.
            var (exePath, wowDir) = ResolveWowPath(account.WowPath);
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return LaunchResult.Fail(LaunchFailure.MissingWowExe,
                    $"Wow.exe не найден по WowPath аккаунта '{account.Login}': '{account.WowPath}'.");
            }

            // 4. Персонаж (для autologin character). Пустое имя → берём
            // персонажа по умолчанию (DefaultCharacter, иначе первый видимый
            // из рорстера) — клиент сам входит в мир. Дефолтное имя может
            // отсутствовать в рорстере (взято из WTF) — это не ошибка,
            // DLL выбирает слот по имени из списка сервера. Явно заданное
            // имя проверяется строго.
            var charName = characterName;
            if (string.IsNullOrWhiteSpace(charName))
            {
                charName = account.DefaultCharacter;
                if (string.IsNullOrWhiteSpace(charName))
                {
                    charName = account.Characters.FirstOrDefault(c => !c.Hidden)?.Name;
                }
            }

            var known = string.IsNullOrWhiteSpace(charName)
                ? null
                : _store.FindCharacter(account, charName);
            if (!string.IsNullOrWhiteSpace(characterName) && known is null)
            {
                return LaunchResult.Fail(LaunchFailure.MissingCharacter,
                    $"Персонаж '{characterName}' не найден у аккаунта '{account.Login}' (или скрыт).");
            }

            var character = known ?? (string.IsNullOrWhiteSpace(charName)
                ? null
                : new CharacterModel { Name = charName.Trim(), Realm = account.Realm });

            // 5. Пароль.
            if (!account.HasPassword)
            {
                return LaunchResult.Fail(LaunchFailure.NoPassword,
                    $"У аккаунта '{account.Login}' не задан пароль (set-password).");
            }

            string password;
            try
            {
                password = account.GetPassword();
            }
            catch (InvalidOperationException ex)
            {
                return LaunchResult.Fail(LaunchFailure.NoPassword, ex.Message);
            }

            // 6. autologin.json: убираем переживший файл предыдущего запуска, пишем новый.
            try
            {
                AutologinFile.Delete(wowDir);
                AutologinFile.Write(wowDir, new AutologinRequest
                {
                    Login = account.Login,
                    Password = password,
                    Realmlist = account.Realmlist,
                    RealmName = account.Realm,
                    Character = character?.Name ?? string.Empty,
                    TotpSecret = account.GetTotpSecret(),
                });

                // Config.lua для MCBuddy-аддона (рорстер в игре, harvest, флаги).
                AddonConfigWriter.Write(wowDir, _store, account, harvestChars: harvestChars);
            }
            catch (Exception ex)
            {
                return LaunchResult.Fail(LaunchFailure.SpawnError, $"Не удалось записать autologin.json: {ex.Message}");
            }

            // 7. Старт процесса: через внешний лоадер или напрямую.
            int pid;
            string imageName;
            if (IsLoaderConfigured(out var loaderProblem))
            {
                if (loaderProblem is not null)
                {
                    AutologinFile.Delete(wowDir);
                    return LaunchResult.Fail(LaunchFailure.SpawnError, loaderProblem);
                }

                var viaLoader = LaunchViaLoader(wowDir);
                if (!viaLoader.started)
                {
                    AutologinFile.Delete(wowDir);
                    return LaunchResult.Fail(LaunchFailure.SpawnError, viaLoader.error);
                }

                pid = viaLoader.pid;
                imageName = "Wow";
            }
            else
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(exePath)
                    {
                        WorkingDirectory = wowDir,
                        UseShellExecute = true,
                    });
                    pid = process?.Id ?? 0;
                    imageName = Path.GetFileNameWithoutExtension(exePath);
                }
                catch (Exception ex)
                {
                    AutologinFile.Delete(wowDir);
                    return LaunchResult.Fail(LaunchFailure.SpawnError, $"Запуск {exePath} не удался: {ex.Message}");
                }
            }

            // 8. Ждём, пока DLL клиента заберёт autologin.json (удаляет после
            // чтения). Иначе следующий запуск перезапишет файл и этот клиент
            // войдёт без кредов или с чужими (гонка при мульти-запуске).
            AutologinFile.WaitConsumed(wowDir, TimeSpan.FromSeconds(60));

            // 9. Учёт: cooldown и сессия.
            account.LastLaunchUtc = DateTime.UtcNow;

            var session = alive ?? ReuseDeadSession(account.Login) ?? CreateSession(account.Login);
            session.WowProcessId = pid;
            session.WowImageName = imageName;
            session.CharacterName = character?.Name ?? string.Empty;
            session.State = SessionState.Launching;
            session.LastUpdatedUtc = DateTime.UtcNow;

            _store.Save();
            return LaunchResult.Ok(pid);
        }
    }

    private SessionModel? ReuseDeadSession(string accountLogin)
        => _store.Config.Sessions.FirstOrDefault(s =>
            string.Equals(s.AccountLogin, accountLogin, StringComparison.OrdinalIgnoreCase)
            && s.State != SessionState.Disabled
            && !IsProcessAlive(s.WowProcessId, s.WowImageName));

    // ---------- внешний лоадер ----------

    private bool IsLoaderConfigured(out string? problem)
    {
        var loader = _store.Config.Launch.Loader;
        problem = null;
        if (loader is null || string.IsNullOrWhiteSpace(loader.LoaderExePath))
        {
            return false;
        }

        if (!File.Exists(loader.LoaderExePath))
        {
            problem = $"Лоадер не найден: '{loader.LoaderExePath}'.";
        }
        else if (string.IsNullOrWhiteSpace(loader.WindowTitle) || string.IsNullOrWhiteSpace(loader.ButtonText))
        {
            problem = "В профиле лоадера должны быть заданы и заголовок окна, и текст кнопки запуска.";
        }

        return true;
    }

    /// <summary>
    /// Запуск через лоадер (порт _launch_via_loader): поток-оркестратор стартует
    /// ДО запуска лоадера, ждём окно/клик, затем до 30 с ловим появившийся
    /// процесс Wow.exe в папке клиента.
    /// </summary>
    private (bool started, int pid, string error) LaunchViaLoader(string wowDir)
    {
        var loader = _store.Config.Launch.Loader!;
        var wowExe = Path.Combine(wowDir, "Wow.exe");
        var before = LiveWowPids(wowDir);

        string report = string.Empty;
        var orchestration = Task.Run(() =>
        {
            _ = LoaderAutomation.Orchestrate(wowExe, loader.WindowTitle, loader.ButtonText, out var r);
            report = r;
        });

        try
        {
            using var loaderProcess = Process.Start(new ProcessStartInfo(loader.LoaderExePath)
            {
                WorkingDirectory = Path.GetDirectoryName(loader.LoaderExePath) ?? string.Empty,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            return (false, 0, $"Не удалось запустить лоадер '{loader.LoaderExePath}': {ex.Message}");
        }

        // Ор оркестрация может занять до ~40 с; даём ей закончиться.
        _ = orchestration.Wait(45_000);

        // Ждём, пока лоадер породит Wow.exe.
        var deadline = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < deadline)
        {
            var fresh = LiveWowPids(wowDir).Except(before).FirstOrDefault();
            if (fresh > 0)
            {
                return (true, fresh, string.Empty);
            }

            Thread.Sleep(500);
        }

        return (false, 0, $"Лоадер не запустил Wow.exe за 30 с. {report}");
    }

    /// <summary>PID всех живых Wow.exe из папки клиента (по пути главного модуля).</summary>
    private static List<int> LiveWowPids(string wowDir)
    {
        var pids = new List<int>();
        foreach (var process in Process.GetProcessesByName("Wow"))
        {
            using (process)
            {
                try
                {
                    var module = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(module)
                        && string.Equals(Path.GetDirectoryName(module), wowDir, StringComparison.OrdinalIgnoreCase))
                    {
                        pids.Add(process.Id);
                    }
                }
                catch
                {
                    // нет доступа к модулю — не наш клиент
                }
            }
        }

        return pids;
    }

    private SessionModel CreateSession(string accountLogin)
    {
        var session = new SessionModel { AccountLogin = accountLogin };
        _store.Config.Sessions.Add(session);
        return session;
    }

    /// <summary>
    /// Смена персонажа через перезапуск (базовый путь Этапа 1): убивает живого
    /// клиента аккаунта, ждёт закрытия и запускает нового. Возвращает null,
    /// если клиента не было (можно было запускать сразу).
    /// </summary>
    public LaunchResult SwitchCharacterByRestart(AccountModel account, string characterName)
    {
        lock (_sync)
        {
            var alive = FindAliveSession(account.Login);
            if (alive is null)
            {
                return LaunchResult.Fail(LaunchFailure.None, "клиент не был запущен");
            }
        }

        KillAliveClient(account.Login, waitTimeoutMs: 15000);
        return LaunchCharacter(account, characterName, allowWhenRunning: true);
    }

    /// <summary>Останавливает клиента аккаунта (если жив) и помечает сессию остановленной. Бот убивается первым.</summary>
    public bool KillAliveClient(string accountLogin, int waitTimeoutMs = 15000)
    {
        lock (_sync)
        {
            var session = FindAliveSession(accountLogin);
            if (session is null)
            {
                return false;
            }

            KillProcess(session.BotProcessId);

            try
            {
                using var process = Process.GetProcessById(session.WowProcessId);
                process.Kill();
                process.WaitForExit(waitTimeoutMs);
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            session.BotProcessId = 0;
            session.State = SessionState.Idle;
            session.WowProcessId = 0;
            session.LastUpdatedUtc = DateTime.UtcNow;
            _store.Save();
            return true;
        }
    }

    private static void KillProcess(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
