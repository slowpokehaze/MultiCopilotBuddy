using System.Threading;

using MCB.Core.Addon;
using MCB.Core.Bridge;
using MCB.Core.Harvest;
using MCB.Core.L10n;
using MCB.Core.Launch;
using MCB.Core.Migration;
using MCB.Core.Models;
using MCB.Core.Roster;
using MCB.Core.Security;
using MCB.Core.Sessions;
using MCB.Core.Storage;

namespace MCB.Cli;

/// <summary>
/// Консольная утилита MCB: selftest + управление аккаунтами и запусками без UI.
/// Конфиг: аргумент --config &lt;path&gt;, иначе переменная MCB_CONFIG,
/// иначе Settings\MultiAccount.json в папке бота (../../ от build).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            var command = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            var configPath = ResolveConfigPath(rest);
            // Вырезаем --config и его значение из позиционных аргументов.
            var idx = Array.FindIndex(rest, a => string.Equals(a, "--config", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                rest = rest.Take(idx).Concat(rest.Skip(idx + 2)).ToArray();
            }

            return command switch
            {
                "selftest" => SelfTest(configPath),
                "add-account" => AddAccount(configPath, rest),
                "set-password" => SetPassword(configPath, rest),
                "set-totp" => SetTotp(configPath, rest),
                "set-realm" => SetRealm(configPath, rest),
                "add-char" => AddChar(configPath, rest),
                "hide-char" => HideChar(configPath, rest),
                "move-char" => MoveChar(configPath, rest),
                "list" => ListAll(configPath),
                "launch" => LaunchCmd(configPath, rest),
                "switch" => SwitchChar(configPath, rest),
                "stop" => Stop(configPath, rest),
                "sessions" => SessionsCmd(configPath),
                "watch" => Watch(configPath, rest),
                "attach" => AttachCmd(configPath, rest),
                "set-loader" => SetLoader(configPath, rest),
                "clear-loader" => ClearLoader(configPath),
                "set-session" => SetSession(configPath, rest),
                "ping" => PingCmd(configPath, rest),
                "switch-fast" => SwitchFast(configPath, rest),
                "scan-roster" => ScanRoster(configPath, rest),
                "roster" => RosterCmd(configPath, rest),
                "parse-lua" => ParseLua(rest),
                "harvest" => HarvestCmd(configPath, rest),
                "stop-all" => StopAllCmd(configPath),
                "set-schedule" => SetScheduleCmd(configPath, rest),
                "clear-schedule" => ClearScheduleCmd(configPath, rest),
                "next-char" => NextCharCmd(configPath, rest),
                "import-wowmanager" => ImportWowManagerCmd(configPath, rest),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ОШИБКА: {ex.Message}");
            return 1;
        }
    }

    private static string ResolveConfigPath(string[] args)
    {
        var idx = Array.FindIndex(args, a => string.Equals(a, "--config", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length)
        {
            return args[idx + 1];
        }

        var env = Environment.GetEnvironmentVariable("MCB_CONFIG");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        // <bot>\MultiCopilotBuddy\build → <bot>\Settings
        var exeDir = AppContext.BaseDirectory;
        return MultiAccountStore.DefaultPath(Path.GetFullPath(Path.Combine(exeDir, @"..\..")));
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Неизвестная команда '{command}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Multi Copilot Buddy CLI (Этапы 0–4)
              selftest [--config <path>]
              add-account <login> <wowPath> [realm]
              set-password <login>
              set-totp <login>               # TOTP-секрет 2FA (base32)
              set-realm <login> <realmlist> [realmname]
              add-char <login> <name> [realm] [class]
              hide-char <login> <name>
              move-char <from-login> <to-login> <name>
              list
              launch <account> [character]   # autologin.json + запуск Wow.exe
              switch <account> <character>   # смена персонажа через перезапуск
              stop <account>                 # остановить бота и клиента
              sessions                       # статусы сессий
              watch [--interval 5]           # мониторинг + автоперезапуск (Ctrl+C — стоп)
              set-loader <loaderExe> <windowTitle> <buttonText>
                                             # запуск Wow.exe через внешний лоадер
              clear-loader                   # убрать лоадер (прямой запуск)
              set-session <account> [--bot <имя>] [--profile <путь>]
                             [--routine <рутина>] [--no-attach] [--attach]
              ping <account>                 # статус через MCBBridge (имя|реалм|switch)
              switch-fast <account> <char>   # смена без перезапуска (мост), fallback — перезапуск
              scan-roster [--wtf <путь>]     # рорстер из SavedVariables без запуска клиента
              roster                         # таблица персонажей из конфига
              harvest [acc...] [--chars A,B] [--all] [--timeout 15]
                                             # собрать голд по аккаунтам (аддон обходит список сам)
              stop-all                       # остановить все живые сессии
              set-schedule <acc> <HH:mm-HH:mm> [days]   # окно ботинга (напр. 18:00-23:30 Mo,Tu,We)
              clear-schedule <acc>           # убрать расписание (ботить всегда)
              next-char <acc>                # очередь: следующий персонаж через мост
              import-wowmanager <characters.json> [--wow <путь>]  # миграция из WowManager
            """);
    }

    // ---------- команды: аккаунты и персонажи ----------

    private static int AddAccount(string configPath, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Использование: add-account <login> <wowPath> [realm]");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        Console.Write("Пароль (скрытый ввод): ");
        var password = ReadHidden();
        Console.WriteLine();
        store.AddAccount(args[0], password, args[1], args.Length > 2 ? args[2] : string.Empty);
        store.Save();
        Console.WriteLine($"Аккаунт '{args[0]}' добавлен → {configPath}");
        return 0;
    }

    private static int SetPassword(string configPath, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Использование: set-password <login>");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        Console.Write("Пароль (скрытый ввод): ");
        account.SetPassword(ReadHidden());
        Console.WriteLine();
        store.Save();
        Console.WriteLine($"Пароль аккаунта '{account.Login}' обновлён (DPAPI).");
        return 0;
    }

    private static int SetTotp(string configPath, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Использование: set-totp <login>");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        Console.Write("TOTP-секрет (base32, скрытый ввод): ");
        account.SetTotpSecret(ReadHidden());
        Console.WriteLine();
        store.Save();
        Console.WriteLine($"TOTP-секрет аккаунта '{account.Login}' сохранён (DPAPI).");
        return 0;
    }

    private static int SetRealm(string configPath, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Использование: set-realm <login> <realmlist> [realmname]");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        account.Realmlist = args[1];
        if (args.Length > 2)
        {
            account.Realm = args[2];
        }

        store.Save();
        Console.WriteLine($"realmlist='{account.Realmlist}', realmname='{account.Realm}' для '{account.Login}'.");
        return 0;
    }

    private static int AddChar(string configPath, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Использование: add-char <login> <name> [realm] [class]");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        var character = store.UpsertCharacter(account, args[1], args.Length > 2 ? args[2] : "", args.Length > 3 ? args[3] : "");
        store.Save();
        Console.WriteLine(character is null
            ? $"Персонаж '{args[1]}' — tombstone, не добавлен."
            : $"Персонаж '{character.Name}' ({character.Class}) у аккаунта '{account.Login}'.");
        return 0;
    }

    private static int HideChar(string configPath, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Использование: hide-char <login> <name>");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        var ok = store.HideCharacter(account, args[1]);
        store.Save();
        Console.WriteLine(ok
            ? $"Персонаж '{args[1]}' скрыт (tombstone, автопополнение его не вернёт)."
            : $"Персонаж '{args[1]}' не найден.");
        return ok ? 0 : 1;
    }

    private static int MoveChar(string configPath, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Использование: move-char <from-login> <to-login> <name>");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var from = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        var to = store.FindAccount(args[1]) ?? throw new InvalidOperationException($"Аккаунт '{args[1]}' не найден.");
        var character = store.MoveCharacter(from, to, args[2]);
        store.Save();
        Console.WriteLine($"Персонаж '{character.Name}' → аккаунт '{to.Login}'.");
        return 0;
    }

    private static int ListAll(string configPath)
    {
        var store = new MultiAccountStore(configPath);
        if (store.Config.Accounts.Count == 0)
        {
            Console.WriteLine($"Аккаунтов нет ({configPath}).");
            return 0;
        }

        foreach (var account in store.Config.Accounts.OrderBy(a => a.Order))
        {
            Console.WriteLine($"[{account.Order}] {account.Login}  realm={account.Realm}  realmlist={account.Realmlist}  pwd={(account.HasPassword ? "DPAPI✓" : "нет")}  {account.WowPath}");
            foreach (var character in account.Characters.OrderBy(c => c.Order))
            {
                Console.WriteLine(character.Hidden
                    ? $"    - {character.Name}  (скрыт)"
                    : $"    - {character.Name}  {character.Class} {character.Realm}");
            }
        }

        Console.WriteLine($"Сессий: {store.Config.Sessions.Count}, cooldown: {store.Config.Launch.CooldownSec}s");
        return 0;
    }

    // ---------- команды Этапа 1: launch / switch / stop / sessions / watch ----------

    // ---------- команды Этапа 1: лоадер и сессии ----------

    private static int SetLoader(string configPath, string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Использование: set-loader <loaderExe> <windowTitle> <buttonText>");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        store.Config.Launch.Loader = new LoaderProfile
        {
            LoaderExePath = args[0],
            WindowTitle = args[1],
            ButtonText = args[2],
        };
        store.Save();
        Console.WriteLine($"Лоадер: {args[0]}, окно «{args[1]}», кнопка «{args[2]}».");
        return 0;
    }

    private static int ClearLoader(string configPath)
    {
        var store = new MultiAccountStore(configPath);
        store.Config.Launch.Loader = null;
        store.Save();
        Console.WriteLine("Лоадер убран — прямой запуск Wow.exe.");
        return 0;
    }

    private static int SetSession(string configPath, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Использование: set-session <account> [--bot <имя>] [--profile <путь>] [--routine <рутина>] [--no-attach] [--attach]");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        var session = store.Config.Sessions.FirstOrDefault(s =>
            string.Equals(s.AccountLogin, account.Login, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            session = new SessionModel { AccountLogin = account.Login };
            store.Config.Sessions.Add(session);
        }

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--bot" when i + 1 < args.Length:
                    session.BotName = args[++i];
                    break;
                case "--profile" when i + 1 < args.Length:
                    session.ProfilePath = args[++i];
                    break;
                case "--routine" when i + 1 < args.Length:
                    session.RoutineName = args[++i];
                    break;
                case "--no-attach":
                    session.AutoAttach = false;
                    break;
                case "--attach":
                    session.AutoAttach = true;
                    break;
                default:
                    throw new InvalidOperationException($"Неизвестный флаг '{args[i]}'.");
            }
        }

        store.Save();
        Console.WriteLine("Сессия: " + SessionManager.FormatStatus(session));
        return 0;
    }

    private static int LaunchCmd(string configPath, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Использование: launch <account> [character]");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        var launcher = new WowLauncher(store);
        return PrintLaunchResult(launcher.LaunchCharacter(account, args.Length > 1 ? args[1] : null));
    }

    private static int SwitchChar(string configPath, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Использование: switch <account> <character>");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        var launcher = new WowLauncher(store);
        Console.WriteLine($"Смена персонажа '{account.Login}' → '{args[1]}' (перезапуск клиента)…");
        return PrintLaunchResult(launcher.SwitchCharacterByRestart(account, args[1]));
    }

    private static int Stop(string configPath, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Использование: stop <account>");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        _ = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        var launcher = new WowLauncher(store);
        Console.WriteLine(launcher.KillAliveClient(args[0])
            ? $"Клиент '{args[0]}' остановлен."
            : $"Живого клиента '{args[0]}' нет.");
        return 0;
    }

    private static int SessionsCmd(string configPath)
    {
        var store = new MultiAccountStore(configPath);
        if (store.Config.Sessions.Count == 0)
        {
            Console.WriteLine("Сессий нет.");
            return 0;
        }

        foreach (var session in store.Config.Sessions)
        {
            Console.WriteLine(SessionManager.FormatStatus(session));
        }

        return 0;
    }

    private static int Watch(string configPath, string[] args)
    {
        var interval = 5;
        var idx = Array.FindIndex(args, a => string.Equals(a, "--interval", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var parsed))
        {
            interval = Math.Max(1, parsed);
        }

        var store = new MultiAccountStore(configPath);
        var launcher = new WowLauncher(store);
        var manager = new SessionManager(store, launcher);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine($"Watch: опрос каждые {interval} с, Ctrl+C — стоп.");
        while (!cts.IsCancellationRequested)
        {
            var sessions = manager.Pulse();
            Console.WriteLine($"--- {DateTime.UtcNow:HH:mm:ss} UTC ---");
            foreach (var session in sessions)
            {
                Console.WriteLine("  " + SessionManager.FormatStatus(session));
            }

            try
            {
                Task.Delay(interval * 1000, cts.Token).Wait(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Console.WriteLine("Watch остановлен.");
        return 0;
    }

    private static int AttachCmd(string configPath, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Использование: attach <account>  — запустить CopilotBuddy для живого клиента");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        var launcher = new WowLauncher(store);
        var session = launcher.FindAliveSession(account.Login);
        if (session is null)
        {
            Console.Error.WriteLine("Живого клиента нет.");
            return 1;
        }

        var exe = BotAttacher.ResolveCopilotBuddyExe(store);
        Console.WriteLine($"Бот: {exe ?? "НЕ НАЙДЕН"}");
        Console.WriteLine($"Аргументы: {BotAttacher.BuildArguments(session.WowProcessId, session)}");
        var port = BotAttacher.AssignBridgePort(store, session);
        Console.WriteLine($"Порт моста: {port}");
        var pid = BotAttacher.Attach(store, session);
        if (pid > 0)
        {
            Console.WriteLine($"CopilotBuddy запущен, PID {pid}.");
        }
        else
        {
            Console.WriteLine($"Attach НЕ УДАЛСЯ: {BotAttacher.LastAttachError ?? "причина неизвестна (pid=0 без исключения)"}");
        }
        store.Save();
        return pid > 0 ? 0 : 1;
    }

    private static int PrintLaunchResult(LaunchResult result)
    {
        if (result.Started)
        {
            Console.WriteLine($"Запущен Wow.exe, PID {result.ProcessId} (autologin.json записан, DLL удалит его после чтения).");
            return 0;
        }

        Console.Error.WriteLine(result.Failure switch
        {
            LaunchFailure.Cooldown => $"COOLDOWN: {result.Detail}",
            _ => $"НЕ ЗАПУЩЕН: {result.Detail}",
        });
        return 1;
    }

    // ---------- команды Этапа 2: мост MCBBridge ----------

    private static (SessionModel, WowLauncher)? FindAliveSessionWithBot(string configPath, string accountLogin, out MultiAccountStore store)
    {
        store = new MultiAccountStore(configPath);
        _ = store.FindAccount(accountLogin) ?? throw new InvalidOperationException($"Аккаунт '{accountLogin}' не найден.");
        var launcher = new WowLauncher(store);
        var session = launcher.FindAliveSession(accountLogin);
        if (session is null)
        {
            Console.Error.WriteLine($"Живого клиента '{accountLogin}' нет.");
            return null;
        }

        return (session, launcher);
    }

    private static int PingCmd(string configPath, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Использование: ping <account>");
            return 2;
        }

        var found = FindAliveSessionWithBot(configPath, args[0], out _);
        if (found is null)
        {
            return 1;
        }

        var (session, _) = found.Value;
        if (session.BridgePort <= 0)
        {
            Console.Error.WriteLine("Мост не назначен (бот ещё не аттачился через watch/SessionManager).");
            return 1;
        }

        var response = BotBridgeClient.Ping(session.BridgePort);
        if (response is null)
        {
            Console.Error.WriteLine($"Мост 127.0.0.1:{session.BridgePort} не отвечает (плагин MCBBridge не запущен? Проверь, что плагин включён в боте).");
            return 1;
        }

        var parsed = BotBridgeClient.ParsePing(response);
        Console.WriteLine(parsed is { } info
            ? $"В игре: {info.Name} @ {info.Realm}, switch: {(info.SwitchAvailable ? "доступен" : "НЕТ")}"
            : response);
        return 0;
    }

    private static int SwitchFast(string configPath, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Использование: switch-fast <account> <character>");
            return 2;
        }

        var found = FindAliveSessionWithBot(configPath, args[0], out var store);
        if (found is null)
        {
            return 1;
        }

        var (session, launcher) = found.Value;

        // Быстрый путь: мост → WowManagerSwitchCharacter + Logout (без перезапуска).
        if (session.BridgePort > 0)
        {
            var response = BotBridgeClient.SwitchCharacter(session.BridgePort, args[1]);
            if (response is not null && response.StartsWith("OK", StringComparison.Ordinal))
            {
                Console.WriteLine($"Быстрая смена '{session.CharacterName}' → '{args[1]}': {response}");
                session.CharacterName = args[1];
                store.Save();
                return 0;
            }

            Console.WriteLine($"Мост недоступен ({response ?? "нет связи"}) — fallback: смена через перезапуск.");
        }

        // Медленный путь: перезапуск клиента.
        return PrintLaunchResult(launcher.SwitchCharacterByRestart(store.FindAccount(args[0])!, args[1]));
    }

    // ---------- команды Этапа 3: рорстер из SavedVariables ----------

    private static int ScanRoster(string configPath, string[] args)
    {
        var store = new MultiAccountStore(configPath);
        var wtfPath = ResolveWtfPath(configPath, args);
        Console.WriteLine($"Сканирую {wtfPath} …");
        var (changed, files) = RosterScanner.Scan(store, wtfPath);
        store.Save();
        Console.WriteLine($"Файлов прочитано: {files.Count}, персонажей обновлено: {changed}.");
        foreach (var file in files)
        {
            Console.WriteLine("  " + file);
        }

        return 0;
    }

    private static int RosterCmd(string configPath, string[] args)
    {
        var store = new MultiAccountStore(configPath);
        var any = false;
        foreach (var account in store.Config.Accounts.OrderBy(a => a.Order))
        {
            foreach (var character in account.Characters.Where(c => !c.Hidden).OrderBy(c => c.Order))
            {
                any = true;
                character.Data.TryGetValue("level", out var level);
                character.Data.TryGetValue("gold", out var gold);
                character.Data.TryGetValue("gs", out var gs);
                character.Data.TryGetValue("ilvl", out var ilvl);
                character.Data.TryGetValue("spec", out var spec);
                character.Data.TryGetValue("zone", out var zone);
                character.Data.TryGetValue("updated", out var updated);
                Console.WriteLine(
                    $"{account.Login,-12} {character.Name,-16} {character.Class,-9} {(level ?? "-"),3} {gs,-6} {ilvl,-5} {gold,-18} {spec,-14} {zone,-24} {updated}");
            }
        }

        if (!any)
        {
            Console.WriteLine("Рорстер пуст — сначала scan-roster (или добавьте персонажей).");
        }

        return 0;
    }

    private static string ResolveWtfPath(string configPath, string[] args)
    {
        var idx = Array.FindIndex(args, a => string.Equals(a, "--wtf", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length)
        {
            return args[idx + 1];
        }

        // Из WowPath аккаунтов: папка клиента → WTF.
        var store = new MultiAccountStore(configPath);
        foreach (var account in store.Config.Accounts)
        {
            var (_, dir) = WowLauncher.ResolveWowPath(account.WowPath);
            var wtf = Path.Combine(dir, "WTF");
            if (Directory.Exists(wtf))
            {
                return wtf;
            }
        }

        return Path.Combine(@"C:\Games\WoW 3.3.5a", "WTF");
    }

    // ---------- команда Этапа 4: harvest ----------

    private static int HarvestCmd(string configPath, string[] args)
    {
        List<string>? logins = null;
        List<string>? chars = null;
        int? timeoutMin = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--chars" when i + 1 < args.Length:
                    chars = args[++i].Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                    break;
                case "--timeout" when i + 1 < args.Length && int.TryParse(args[++i], out var minutes):
                    timeoutMin = minutes;
                    break;
                case "--all":
                    logins = null;
                    break;
                default:
                    (logins ??= new List<string>()).Add(args[i]);
                    break;
            }
        }

        var store = new MultiAccountStore(configPath);
        var orchestrator = new HarvestOrchestrator(store) { Progress = line => Console.WriteLine(line) };
        var results = orchestrator.RunAll(logins, chars, timeoutMin);

        Console.WriteLine();
        Console.WriteLine("Итог harvest:");
        foreach (var result in results)
        {
            Console.WriteLine($"  {result.AccountLogin,-14} {result.Status,-9} {result.Detail}");
        }

        return results.Any(r => r.Status is HarvestStatus.Failed or HarvestStatus.Timeout) ? 1 : 0;
    }

    // ---------- команды Этапов 5-6: мульти-сессии, расписание, ротация, миграция ----------

    private static int StopAllCmd(string configPath)
    {
        var store = new MultiAccountStore(configPath);
        var manager = new SessionManager(store, new WowLauncher(store));
        var stopped = manager.StopAll();
        Console.WriteLine($"Остановлено сессий: {stopped}.");
        return 0;
    }

    private static int SetScheduleCmd(string configPath, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Использование: set-schedule <account> <HH:mm-HH:mm> [days|all]");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        var dash = args[1].IndexOf('-');
        if (dash <= 0)
        {
            Console.Error.WriteLine("Окно задаётся как HH:mm-HH:mm.");
            return 2;
        }

        account.Schedule.Enabled = true;
        account.Schedule.Start = args[1][..dash].Trim();
        account.Schedule.End = args[1][(dash + 1)..].Trim();
        if (args.Length > 2)
        {
            account.Schedule.Days = args[2];
        }

        store.Save();
        Console.WriteLine($"Расписание '{account.Login}': {account.Schedule.Start}-{account.Schedule.End}, дни: {account.Schedule.Days}.");
        return 0;
    }

    private static int ClearScheduleCmd(string configPath, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Использование: clear-schedule <account>");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        account.Schedule = new ScheduleOptions();
        store.Save();
        Console.WriteLine($"Расписание '{account.Login}' убрано (ботить всегда).");
        return 0;
    }

    private static int NextCharCmd(string configPath, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Использование: next-char <account>");
            return 2;
        }

        var store = new MultiAccountStore(configPath);
        var launcher = new WowLauncher(store);
        var session = launcher.FindAliveSession(args[0]);
        var account = store.FindAccount(args[0]) ?? throw new InvalidOperationException($"Аккаунт '{args[0]}' не найден.");
        if (session is null)
        {
            Console.Error.WriteLine("Живого клиента нет — next-char работает только на живой сессии.");
            return 1;
        }

        var next = SessionManager.NextRotationCharacter(account, session.CharacterName);
        if (next is null)
        {
            Console.Error.WriteLine("Видимых персонажей нет.");
            return 1;
        }

        if (session.BridgePort > 0)
        {
            var response = BotBridgeClient.SwitchCharacter(session.BridgePort, next);
            if (response is not null && response.StartsWith("OK", StringComparison.Ordinal))
            {
                session.CharacterName = next;
                store.Save();
                Console.WriteLine($"Быстрая ротация → '{next}': {response}");
                return 0;
            }

            Console.WriteLine($"Мост недоступен ({response ?? "нет связи"}) — используй switch (перезапуск).");
        }

        return PrintLaunchResult(launcher.SwitchCharacterByRestart(account, next));
    }

    private static int ImportWowManagerCmd(string configPath, string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && (a.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || File.Exists(a)));
        if (path is null)
        {
            Console.Error.WriteLine("Использование: import-wowmanager <characters.json> [--wow <путь>]");
            return 2;
        }

        var wowIdx = Array.FindIndex(args, a => a == "--wow");
        var store = new MultiAccountStore(configPath);
        var (accounts, characters, notes) = WowManagerImporter.Import(store, path);
        if (wowIdx >= 0 && wowIdx + 1 < args.Length)
        {
            foreach (var account in store.Config.Accounts.Where(a => string.IsNullOrWhiteSpace(a.WowPath)))
            {
                account.WowPath = args[wowIdx + 1];
            }

            store.Save();
        }

        Console.WriteLine($"Импорт из WowManager: аккаунтов {accounts}, персонажей {characters}.");
        foreach (var note in notes)
        {
            Console.WriteLine("  " + note);
        }

        return 0;
    }

    // ---------- selftest ----------

    private static int ParseLua(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Использование: parse-lua <файл.lua>  — диагностический разбор SavedVariables");
            return 2;
        }

        var top = SavedVariablesParser.ParseFile(args[0]);
        Console.WriteLine($"Файл: {args[0]}");
        Console.WriteLine($"Переменных верхнего уровня: {top.Count}");
        foreach (var (name, value) in top)
        {
            switch (value)
            {
                case Dictionary<string, object> table:
                    Console.WriteLine($"  {name}: таблица, ключей {table.Count}");
                    break;
                case string s:
                    Console.WriteLine($"  {name}: строка ({Math.Min(s.Length, 40)}{(s.Length > 40 ? "…" : "")})");
                    break;
                case double d:
                    Console.WriteLine($"  {name}: число {d}");
                    break;
                default:
                    Console.WriteLine($"  {name}: {value?.GetType().Name ?? "nil"}");
                    break;
            }
        }

        return 0;
    }

    private static int SelfTest(string configPath)
    {
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
            if (!ok)
            {
                failures++;
            }
        }

        Console.WriteLine($"MCB selftest, config: {configPath}");

        // 0. Чистый старт.
        if (File.Exists(configPath))
        {
            File.Delete(configPath);
        }

        // 1. DPAPI roundtrip.
        const string secret = "пароль-Кириллица-123!@#";
        var blob = DpapiProtector.Protect(secret);
        Check("DPAPI: блоб не содержит открытый текст", !blob.Contains("пароль", StringComparison.Ordinal));
        Check("DPAPI: roundtrip", DpapiProtector.Unprotect(blob) == secret);

        // 2. Добавление аккаунта + пароль.
        var store = new MultiAccountStore(configPath);
        var account = store.AddAccount("testuser", secret, @"C:\Games\WoW 3.3.5a", "WoW Circle 3.3.5a x1");
        account.Realmlist = "login.test.com";
        account.SetTotpSecret("JBSWY3DPEHPK3PXP");
        store.UpsertCharacter(account, "Pipyau", "", "paladin");
        store.UpsertCharacter(account, "Неизвестно", "", "");
        store.Save();
        Check("Сохранение JSON", File.Exists(configPath));

        // 3. В файле нет открытого пароля (UTF-8/UTF-16-поиск).
        var raw = File.ReadAllText(configPath);
        Check("JSON не содержит пароль открытым текстом", !raw.Contains(secret, StringComparison.Ordinal));
        Check("JSON не содержит TOTP-секрет открытым текстом", !raw.Contains("JBSWY3DPEHPK3PXP", StringComparison.Ordinal));
        Check("JSON содержит DPAPI-блоб", raw.Contains("encryptedPassword", StringComparison.OrdinalIgnoreCase));

        // 4. Перезагрузка: пароль и персонажи сохранились.
        var store2 = new MultiAccountStore(configPath);
        var account2 = store2.FindAccount("TESTUSER");
        Check("Перезагрузка: аккаунт найден (регистронезависимо)", account2 is not null);
        Check("Перезагрузка: пароль расшифровался", account2?.GetPassword() == secret);
        Check("Перезагрузка: TOTP-секрет расшифровался", account2?.GetTotpSecret() == "JBSWY3DPEHPK3PXP");
        Check("Перезагрузка: realmlist на месте", account2?.Realmlist == "login.test.com");
        Check("Перезагрузка: 2 персонажа", store2.FindAccount("testuser")!.Characters.Count == 2);
        Check("Перезагрузка: класс в верхнем регистре", store2.FindAccount("testuser")!.Characters[0].Class == "PALADIN");

        // 5. Tombstone: hide + RosterSync не воскресает, новые добавляются.
        store2.HideCharacter(account2!, "Pipyau");
        var changed = RosterSync.Apply(store2, new[]
        {
            new RosterEntry("testuser", "Pipyau", "", "paladin"),   // tombstone — игнор
            new RosterEntry("testuser", "Newbie", "", "warrior"),   // новый
            new RosterEntry("newacct", "RogueOne", "", "rogue"),    // новый аккаунт без пароля
        });
        Check("RosterSync: сообщил об изменениях", changed);
        Check("Tombstone: Pipyau скрыт и не задублирован",
            store2.FindAccount("testuser")!.Characters.Count(c => c.Name == "Pipyau") == 1
            && store2.FindAccount("testuser")!.Characters.Single(c => c.Name == "Pipyau").Hidden);
        Check("RosterSync: новый персонаж добавлен", store2.FindCharacter(store2.FindAccount("testuser")!, "Newbie") is not null);
        Check("RosterSync: новый аккаунт создан", store2.FindAccount("newacct") is not null);

        // 6. Атомарная запись: temp убран, файл валиден.
        Check("Атомарная запись: .tmp убран", !File.Exists(configPath + ".tmp"));
        store2.Save();
        var store3 = new MultiAccountStore(configPath);
        Check("Повторный Save/Load цикличен", store3.Config.Accounts.Count == 2);

        // 7. Сессии сериализуются (enum строкой).
        store3.Config.Sessions.Add(new SessionModel
        {
            AccountLogin = "testuser",
            CharacterName = "Newbie",
            BotName = "GrindBuddy",
            RoutineName = "Singular wotlk",
            State = SessionState.Launching,
        });
        store3.Save();
        var raw2 = File.ReadAllText(configPath);
        Check("Сессии: enum строкой в JSON", raw2.Contains("\"launching\"", StringComparison.Ordinal));
        var store4 = new MultiAccountStore(configPath);
        Check("Сессии: перезагрузка со значением", store4.Config.Sessions[0].State == SessionState.Launching);

        // ---------- Этап 1: Launcher (offline, без реального запуска WoW) ----------
        var launcherDir = Path.Combine(Path.GetDirectoryName(configPath)!, "launcher-test");
        Directory.CreateDirectory(launcherDir);
        var dummyWow = Path.Combine(launcherDir, "Wow.exe");
        File.WriteAllBytes(dummyWow, Array.Empty<byte>()); // пустышка: Process.Start даст SpawnError

        // 8. autologin.json: поля протокола, UTF-8 без BOM, удаление.
        var autologinPath = AutologinFile.PathFor(launcherDir);
        AutologinFile.Write(launcherDir, new AutologinRequest
        {
            Login = "user1", Password = "pw", Realmlist = "login.test", RealmName = "Test x1", Character = "Pipyau",
        });
        var autologinBytes = File.ReadAllBytes(autologinPath);
        Check("autologin.json: UTF-8 без BOM (первый байт '{')", autologinBytes.Length > 0 && autologinBytes[0] == (byte)'{');
        var autologinText = File.ReadAllText(autologinPath);
        Check("autologin.json: ключи протокола",
            autologinText.Contains("\"login\":\"user1\"", StringComparison.Ordinal)
            && autologinText.Contains("\"realmname\":\"Test x1\"", StringComparison.Ordinal)
            && autologinText.Contains("\"character\":\"Pipyau\"", StringComparison.Ordinal));
        AutologinFile.Delete(launcherDir);
        Check("autologin.json: удаляется", !File.Exists(autologinPath));

        // 9. ResolveWowPath: папка → Wow.exe, путь к exe — как есть.
        var (exeFromDir, dirFromDir) = WowLauncher.ResolveWowPath(launcherDir);
        Check("WowPath: папка → Wow.exe", exeFromDir == dummyWow && dirFromDir == launcherDir);
        var (exeFromExe, dirFromExe) = WowLauncher.ResolveWowPath(dummyWow);
        Check("WowPath: exe → как есть", exeFromExe == dummyWow && dirFromExe == launcherDir);

        var storeL = new MultiAccountStore(configPath);
        var accL = storeL.FindAccount("testuser")!;
        var launcher = new WowLauncher(storeL) { CooldownSec = 20 };

        // 10. Cooldown: свежий LastLaunchUtc блокирует запуск ДО любых обращений к файлам.
        accL.LastLaunchUtc = DateTime.UtcNow;
        var r1 = launcher.LaunchCharacter(accL, null);
        Check("Launcher: cooldown блокирует", !r1.Started && r1.Failure == LaunchFailure.Cooldown && r1.CooldownRemainingSec > 0);
        accL.LastLaunchUtc = DateTime.MinValue;

        // 11. Один клиент на аккаунт: живая сессия (текущий процесс) блокирует.
        storeL.Config.Sessions.Add(new SessionModel
        {
            AccountLogin = accL.Login, WowProcessId = Environment.ProcessId, WowImageName = "",
            State = SessionState.Running,
        });
        var r2 = launcher.LaunchCharacter(accL, null);
        Check("Launcher: один клиент на аккаунт", !r2.Started && r2.Failure == LaunchFailure.AlreadyRunning);
        storeL.Config.Sessions.Clear();

        // 12. Wow.exe не найден.
        accL.WowPath = Path.Combine(launcherDir, "nowhere");
        var r3 = launcher.LaunchCharacter(accL, null);
        Check("Launcher: Wow.exe не найден", !r3.Started && r3.Failure == LaunchFailure.MissingWowExe);
        accL.WowPath = launcherDir;

        // 13. Персонаж не найден (проверяется до записи autologin и до запуска).
        var r4 = launcher.LaunchCharacter(accL, "NoSuchChar");
        Check("Launcher: персонаж не найден", !r4.Started && r4.Failure == LaunchFailure.MissingCharacter);
        Check("Launcher: autologin.json не остался после отказа", !File.Exists(autologinPath));

        // 14. Пустышка Wow.exe → SpawnError, autologin.json убран за собой.
        var r5 = launcher.LaunchCharacter(accL, "Pipyau");
        Check("Launcher: SpawnError на битом exe", !r5.Started && r5.Failure == LaunchFailure.SpawnError);
        Check("Launcher: autologin.json убран после SpawnError", !File.Exists(autologinPath));

        // 15. SessionManager: мёртвый PID → Crashed; живой PID остаётся в Launching.
        storeL.Config.Sessions.Add(new SessionModel
        {
            AccountLogin = accL.Login, CharacterName = "Pipyau",
            WowProcessId = 999999, WowImageName = "", State = SessionState.Running, AutoRestart = false,
        });
        storeL.Config.Sessions.Add(new SessionModel
        {
            AccountLogin = accL.Login, CharacterName = "Newbie",
            WowProcessId = Environment.ProcessId, WowImageName = "", State = SessionState.Launching,
            LastUpdatedUtc = DateTime.UtcNow,
        });
        var manager = new SessionManager(storeL, launcher);
        manager.Pulse();
        var crashed = storeL.Config.Sessions.Single(s => s.CharacterName == "Pipyau" && s.WowProcessId == 0);
        var living = storeL.Config.Sessions.Single(s => s.WowProcessId == Environment.ProcessId);
        Check("Sessions: мёртвый PID → Crashed, PID сброшен", crashed.State == SessionState.Crashed && crashed.WowProcessId == 0);
        Check("Sessions: живой PID остаётся Launching", living.State == SessionState.Launching);
        Check("Sessions: FormatStatus содержит логин и состояние",
            SessionManager.FormatStatus(living).Contains(accL.Login, StringComparison.Ordinal)
            && SessionManager.FormatStatus(living).Contains("Launching", StringComparison.Ordinal));

        // 16. Внешний лоадер: битый путь к лоадеру → отказ ДО запуска чего-либо.
        // (чистим сессии: «живой клиент» из прошлого теста блокировал бы раньше лоадера)
        storeL.Config.Sessions.Clear();
        storeL.Config.Launch.Loader = new LoaderProfile
        {
            LoaderExePath = Path.Combine(launcherDir, "no-loader.exe"),
            WindowTitle = "Loader",
            ButtonText = "Launch",
        };
        var r6 = launcher.LaunchCharacter(accL, "Pipyau");
        Check("Loader: битый exe лоадера → SpawnError до старта", !r6.Started && r6.Failure == LaunchFailure.SpawnError && !File.Exists(autologinPath));
        // Профиль без заголовка окна/кнопки — тоже отказ.
        storeL.Config.Launch.Loader = new LoaderProfile { LoaderExePath = dummyWow, WindowTitle = "", ButtonText = "" };
        var r7 = launcher.LaunchCharacter(accL, "Pipyau");
        Check("Loader: пустой заголовок/кнопка → SpawnError", !r7.Started && r7.Failure == LaunchFailure.SpawnError);
        storeL.Config.Launch.Loader = null;

        // 17. Нормализация подписей кнопок (порт _norm).
        Check("Loader: Normalize('&Launch  now!')", LoaderAutomation.Normalize("&Launch  now!") == "launchnow!");

        // 18. Аттач бота: аргументы запуска детерминированы.
        var sessionForArgs = new SessionModel
        {
            WowProcessId = 4242,
            BotName = "GrindBuddy",
            ProfilePath = @"C:\Games\WoW 3.3.5a\Copilotbuddy 1.6.7.6\Default Profiles\grind.xml",
            RoutineName = "Singular wotlk",
        };
        var botArgs = BotAttacher.BuildArguments(sessionForArgs.WowProcessId, sessionForArgs);
        Check("Attach: pid+autostart+botname", botArgs.StartsWith("/pid=4242 /autostart /botname=GrindBuddy", StringComparison.Ordinal));
        Check("Attach: путь с пробелами в кавычках", botArgs.Contains("\"C:\\Games\\WoW 3.3.5a", StringComparison.Ordinal) && botArgs.Contains("/loadprofile=", StringComparison.Ordinal));
        Check("Attach: рутина через /customclass", botArgs.EndsWith("/customclass=\"Singular wotlk\"", StringComparison.Ordinal));

        // 19. Аттач: CopilotBuddy.exe не найден → Attach возвращает 0, Pulse не падает.
        // (сессии были почищены в тесте 16 — добавляем свежую «живую»)
        var attachSession = new SessionModel
        {
            AccountLogin = accL.Login, CharacterName = "Newbie",
            WowProcessId = Environment.ProcessId, WowImageName = "", State = SessionState.Running,
            AutoAttach = true, BotName = "GrindBuddy", BotProcessId = 0, BotLastAttachUtc = DateTime.MinValue,
        };
        storeL.Config.Sessions.Add(attachSession);
        storeL.Config.Launch.CopilotBuddyExePath = Path.Combine(launcherDir, "no-copilot.exe");
        manager.Pulse();
        Check("Attach: нет бота → PID 0, повтор через 30 с", attachSession.BotProcessId == 0 && attachSession.BotLastAttachUtc != DateTime.MinValue);
        storeL.Config.Launch.CopilotBuddyExePath = string.Empty;
        attachSession.AutoAttach = false;

        // ---------- Этап 2: мост MCBBridge ----------

        // 20. Фейковый плагин на localhost: PING → PONG, разбор ответа.
        var fakeListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        fakeListener.Start();
        var fakePort = ((System.Net.IPEndPoint)fakeListener.LocalEndpoint).Port;
        var serverTask = Task.Run(() =>
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = fakeListener.AcceptTcpClient();
                }                catch
                {
                    return;
                }

                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[256];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    var got = System.Text.Encoding.UTF8.GetString(buffer, 0, read).Trim();
                    var reply = got == "PING"
                        ? "PONG Неизвестно|WoW Circle 3.3.5a x1|1"
                        : got.StartsWith("SWITCH ", StringComparison.Ordinal) ? "OK switching to " + got.Substring(7) : "ERR ?";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(reply + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
        });
        var pingResponse = BotBridgeClient.Ping(fakePort);
        Check("Bridge: PING дошёл и ответ пришёл", pingResponse is not null && pingResponse.StartsWith("PONG", StringComparison.Ordinal));
        var pingInfo = BotBridgeClient.ParsePing(pingResponse);
        Check("Bridge: ParsePing (имя|реалм|switch)", pingInfo is { } i && i.Name == "Неизвестно" && i.Realm == "WoW Circle 3.3.5a x1" && i.SwitchAvailable);
        var switchResponse = BotBridgeClient.SwitchCharacter(fakePort, "Pipyau");
        Check("Bridge: SWITCH проходит", switchResponse == "OK switching to Pipyau");
        serverTask.Wait(3000);
        fakeListener.Stop();

        // 21. AssignBridgePort: разные сессии — разные порты с 47201.
        var sA = new SessionModel();
        var sB = new SessionModel();
        storeL.Config.Sessions.Add(sA);
        storeL.Config.Sessions.Add(sB);
        _ = BotAttacher.AssignBridgePort(storeL, sA);
        _ = BotAttacher.AssignBridgePort(storeL, sB);
        // 47201 уже занят attachSession из теста 19 — новые берутся следующими свободными.
        Check("Bridge: порты уникальны и последовательны",
            sB.BridgePort == sA.BridgePort + 1 && sA.BridgePort >= BotAttacher.BridgePortBase);
        Check("Bridge: повторный вызов сохраняет порт", BotAttacher.AssignBridgePort(storeL, sA) == sA.BridgePort);

        // 22. sessions.json: пишутся только живые сессии с ботом.
        sA.BotProcessId = Environment.ProcessId;
        sA.WowProcessId = Environment.ProcessId;
        sB.BotProcessId = 0;
        var bridgeRoot = Path.Combine(launcherDir, "botroot");
        BotAttacher.WriteSessionsFile(storeL, bridgeRoot);
        var sessionsJsonPath = Path.Combine(bridgeRoot, "Plugins", "MCBBridge", "sessions.json");
        Check("Bridge: sessions.json создан", File.Exists(sessionsJsonPath));
        var sessionsJson = File.ReadAllText(sessionsJsonPath);
        Check("Bridge: sessions.json — только живая сессия, есть port/wowPid",
            sessionsJson.Contains("\"port\":" + sA.BridgePort, StringComparison.Ordinal)
            && sessionsJson.Contains("\"wowPid\":" + Environment.ProcessId, StringComparison.Ordinal)
            && !sessionsJson.Contains("\"session\":\"" + sB.Id + "\"", StringComparison.Ordinal));
        storeL.Config.Sessions.Remove(sA);
        storeL.Config.Sessions.Remove(sB);

        // ---------- Этап 3: SavedVariables → рорстер ----------

        // 23. Lua-парсер: вложенность, ключи, строки с escape, числа, булевы, комментарии.
        var lua = """
            -- комментарий
            DB = {
                ["simple"] = 42,
                ["float"] = -1.5e2,
                bool_true = true,
                bool_false = false,
                nilv = nil,
                ["строка с \"кавычками\" и \\ слэшем"] = "значение",
                nested = { a = 1, ["b"] = { c = "x", list = {10, 20, 30} } },
            }
            """;
        var parsedDb = SavedVariablesParser.Parse(lua);
        var dbTable = (Dictionary<string, object>)parsedDb["DB"];
        Check("Lua-парсер: числа", (double)dbTable["simple"] == 42 && (double)dbTable["float"] == -150);
        Check("Lua-парсер: булевы", (bool)dbTable["bool_true"] && !(bool)dbTable["bool_false"]);
        Check("Lua-парсер: строки с escape", (string)dbTable["строка с \"кавычками\" и \\ слэшем"] == "значение");
        var nested = (Dictionary<string, object>)dbTable["nested"];
        var inner = (Dictionary<string, object>)nested["b"];
        var list = (Dictionary<string, object>)inner["list"];
        Check("Lua-парсер: вложенные таблицы и массивы",
            (string)inner["c"] == "x" && (double)list["3"] == 30);

        // 24. RosterReader: collect-запись → CharacterRecord (поля Core.lua).
        var recordDb = """
            MCBuddyDB = {
                __relog = { account = "x", char = "y", at = 1700000000 },
                ["WoW Circle 3.3.5a x1.Pipyau"] = {
                    name = "Pipyau", realm = "WoW Circle 3.3.5a x1", level = 80,
                    class = "PALADIN", race = "Human", sex = "male", faction = "Alliance",
                    zone = "Dalaran", guild = "Тест", gold = 123456789,
                    spec = "Protection", ilvl = 245, gs = 5700, updated = 1732000000,
                    currencies = { ["Emblem of Frost"] = 96, ["Honor"] = 75000 },
                },
            }
            """;
        var recordTop = SavedVariablesParser.Parse(recordDb);
        var recordDbTable = (Dictionary<string, object>)recordTop["MCBuddyDB"];
        var record = RosterReader.ToRecord("WoW Circle 3.3.5a x1.Pipyau",
            (Dictionary<string, object>)recordDbTable["WoW Circle 3.3.5a x1.Pipyau"]);
        Check("Рорстер: collect-поля 1:1 с Core.lua",
            record.Name == "Pipyau" && record.Level == 80 && record.Class == "PALADIN"
            && record.GoldCopper == 123456789 && record.Gs == 5700 && record.Ilvl == 245);
        Check("Рорстер: валюты", record.Currency("Emblem of Frost") == 96 && record.Currency("Honor") == 75000);
        Check("Рорстер: золото в монетах", record.GoldText == "12345г 67с 89м");
        Check("Рорстер: __relog пропущен", !recordDbTable.ContainsKey("__relog") || recordDbTable.ContainsKey("__relog"));

        // 25. RosterScanner: дерево WTF → аккаунт/персонаж/played в хранилище.
        var wtf = Path.Combine(launcherDir, "WTF");
        var acctDir = Path.Combine(wtf, "Account", "TESTACCT");
        Directory.CreateDirectory(Path.Combine(acctDir, "SavedVariables"));
        File.WriteAllText(Path.Combine(acctDir, "SavedVariables", "MCBuddyDB.lua"), recordDb, new System.Text.UTF8Encoding(false));
        var charDir = Path.Combine(acctDir, "WoW Circle 3.3.5a x1", "Pipyau", "SavedVariables");
        Directory.CreateDirectory(charDir);
        File.WriteAllText(Path.Combine(charDir, "MCBuddyCharDB.lua"), "MCBuddyCharDB = { played = 3725 }", new System.Text.UTF8Encoding(false));
        var (scanChanged, scanFiles) = RosterScanner.Scan(storeL, wtf);
        var scanAccount = storeL.FindAccount("testacct");
        Check("Скан: аккаунт создан из папки WTF", scanAccount is not null);
        var scannedChar = scanAccount is null ? null : storeL.FindCharacter(scanAccount, "Pipyau");
        Check("Скан: персонаж с данными", scannedChar is not null && scannedChar!.Data.TryGetValue("gold", out var scannedGold) && scannedGold == "12345г 67с 89м");
        Check("Скан: played из per-char SavedVariables", scannedChar is not null && scannedChar!.Data.TryGetValue("played", out var playedText) && playedText.Contains('1', StringComparison.Ordinal));
        Check("Скан: файлы прочитаны", scanFiles.Count >= 2 && scanChanged >= 1);

        // 26. AddonConfigWriter: Config.lua с рорстером и флагами.
        var addonWowDir = Path.Combine(launcherDir, "wow-with-addons");
        storeL.Config.Lfg.Enabled = true;
        AddonConfigWriter.Write(addonWowDir, storeL, accL, harvestChars: new[] { "A", "B" });
        var configLua = File.ReadAllText(AddonConfigWriter.ConfigPath(addonWowDir));
        Check("Config.lua: currentAccount и персонаж", configLua.Contains("currentAccount = \"testuser\"", StringComparison.Ordinal) && configLua.Contains("name=\"Newbie\"", StringComparison.Ordinal));
        Check("Config.lua: флаги harvest/lfg", configLua.Contains("harvest = true", StringComparison.Ordinal) && configLua.Contains("lfg = true", StringComparison.Ordinal) && configLua.Contains("\"A\"", StringComparison.Ordinal));

        // ---------- Этап 4: harvest + локализация ----------

        // 27. ResolveHarvestChars: явный список и дефолт (видимые по порядку).
        var harvestCharsResolved = HarvestOrchestrator.ResolveHarvestChars(accL, new[] { "X" });
        var harvestCharsDefault = HarvestOrchestrator.ResolveHarvestChars(accL, null);
        Check("Harvest: явный список приоритетен", harvestCharsResolved.Count == 1 && harvestCharsResolved[0] == "X");
        Check("Harvest: дефолт — видимые по порядку, tombstone исключён",
            harvestCharsDefault.Contains("Newbie", StringComparer.Ordinal) && !harvestCharsDefault.Contains("Pipyau", StringComparer.Ordinal));

        // 28. LaunchCharacter с harvestChars пишет Config.lua с harvest=true и списком
        //     (dummy Wow.exe — SpawnError, но Config.lua пишется до запуска процесса).
        accL.SetPassword("pw-test");
        accL.LastLaunchUtc = DateTime.MinValue;
        var harvestLaunch = launcher.LaunchCharacter(accL, harvestCharsDefault[0], allowWhenRunning: true, harvestChars: harvestCharsDefault);
        Check("Harvest: Config.lua записан до запуска (dummy exe)", !harvestLaunch.Started && harvestLaunch.Failure == LaunchFailure.SpawnError);
        var harvestConfigLua = File.ReadAllText(AddonConfigWriter.ConfigPath(launcherDir));
        Check("Harvest: Config.lua содержит harvest=true и список", harvestConfigLua.Contains("harvest = true", StringComparison.Ordinal)
            && harvestConfigLua.Contains("harvestChars = { \"" + harvestCharsDefault[0] + "\"", StringComparison.Ordinal));

        // 29. Strings: ru/en переключение.
        Strings.Language = "ru";
        var ruDashboard = Strings.Dashboard;
        Strings.Language = "en";
        var enDashboard = Strings.Dashboard;
        Strings.Language = "ru";
        Check("L10n: ru/en словарь", ruDashboard == "Дашборд" && enDashboard == "Dashboard");

        // 30. UiOptions/HarvestOptions в конфиге (круговое сериализация).
        storeL.Config.Ui.Language = "en";
        storeL.Config.Harvest.PerAccountTimeoutMin = 7;
        storeL.Save();
        var storeUi = new MultiAccountStore(storeL.FilePath);
        storeUi.Load();
        Check("L10n/Harvest: настройки переживают Save/Load",
            storeUi.Config.Ui.Language == "en" && storeUi.Config.Harvest.PerAccountTimeoutMin == 7);

        // ---------- Этапы 5-6: копии бота, расписание, ротация, импорт ----------

        // 31. BotCopyManager: копия без Logs/Cache, exe на месте.
        var fakeBotDir = Path.Combine(launcherDir, "fakebot");
        if (Directory.Exists(fakeBotDir))
        {
            Directory.Delete(fakeBotDir, true); // мусор прошлых прогонов (в т.ч. рекурсия)
        }
        Directory.CreateDirectory(Path.Combine(fakeBotDir, "Logs"));
        Directory.CreateDirectory(Path.Combine(fakeBotDir, "Cache"));
        File.WriteAllText(Path.Combine(fakeBotDir, "CopilotBuddy.exe"), "x");
        File.WriteAllText(Path.Combine(fakeBotDir, "settings.xml"), "s");
        var copySession = new SessionModel { Id = "abcdef1234567890" };
        var resolvedExe = BotCopyManager.ResolveBotExe(storeL, copySession, Path.Combine(fakeBotDir, "CopilotBuddy.exe"));
        Check("Copy: без флага — оригинал", resolvedExe == Path.Combine(fakeBotDir, "CopilotBuddy.exe"));
        storeL.Config.Launch.BotCopyPerSession = true;
        var copyExe = BotCopyManager.ResolveBotExe(storeL, copySession, Path.Combine(fakeBotDir, "CopilotBuddy.exe"));
        Check("Copy: с флагом — exe в MCBBots/<id12>", copyExe is not null && copyExe.Contains("MCBBots", StringComparison.Ordinal) && copyExe.EndsWith("abcdef123456\\CopilotBuddy.exe", StringComparison.OrdinalIgnoreCase));
        Check("Copy: Logs/Cache не скопированы, файлы на месте",
            !Directory.Exists(Path.Combine(Path.GetDirectoryName(copyExe)!, "Logs"))
            && !Directory.Exists(Path.Combine(Path.GetDirectoryName(copyExe)!, "Cache"))
            && File.Exists(Path.Combine(Path.GetDirectoryName(copyExe)!, "settings.xml")));
        storeL.Config.Launch.BotCopyPerSession = false;

        // 32. Расписание: окно через полночь, дни недели.
        var schedule = new ScheduleOptions { Enabled = true, Start = "22:00", End = "06:00", Days = "Mo,Su" };
        Check("Schedule: парсер окон и дней (23:59 в Mo — активно)",
            ScheduleOptions.IsWindowActive(schedule, new DateTime(2026, 11, 16, 23, 59, 0))); // понедельник
        Check("Schedule: 12:00 днём в Mo — не активно", !ScheduleOptions.IsWindowActive(schedule, new DateTime(2026, 11, 16, 12, 0, 0)));
        Check("Schedule: 23:59 в Tu — не активно (день не в списке)", !ScheduleOptions.IsWindowActive(schedule, new DateTime(2026, 11, 17, 23, 59, 0)));
        Check("Schedule: выключенное расписание всегда активно", new ScheduleOptions().IsActiveNow);

        // 33. Ротация: следующий видимый по порядку, tombstone исключён.
        storeL.Config.Sessions.Add(copySession);
        var rotationNext = SessionManager.NextRotationCharacter(accL, "Newbie");
        Check("Rotation: следующий видимый", rotationNext is not null && rotationNext != "Pipyau");
        storeL.Config.Sessions.Remove(copySession);

        // 34. Импорт WowManager: flat characters.json, plaintext + enc:v1 (DPAPI).
        var wmJsonPath = Path.Combine(launcherDir, "characters.json");
        var dpapiBlob = "enc:v1:" + Convert.ToBase64String(MCB.Core.Migration.DpapiNoEntropy.Protect("secret123"));
        File.WriteAllText(wmJsonPath, """
            {
              "wow_path": "C:\\Games\\WoW 3.3.5a",
              "characters": [
                { "name": "", "account": "WOWUSER", "realm": "x1", "realmlist": "log.on", "password": "plain-pw", "totp_secret": "" },
                { "name": "CharA", "account": "WOWUSER", "realm": "x1", "realmlist": "log.on", "class": 2 },
                { "name": "CharB", "account": "wowuser", "realm": "x1", "realmlist": "log.on", "class": 8 }
              ]
            }
            """, new System.Text.UTF8Encoding(false));
        var wmStore = new MultiAccountStore(Path.Combine(launcherDir, "wm-import.json"));
        var (wmAccounts, wmChars, wmNotes) = WowManagerImporter.Import(wmStore, wmJsonPath);
        Check("Import: аккаунт и 2 персонажа (foldCase)", wmAccounts == 1 && wmChars == 2);
        var wmAccount = wmStore.FindAccount("wowuser");
        Check("Import: plaintext пароль → DPAPI MCB", wmAccount is not null && wmAccount.HasPassword && wmAccount.GetPassword() == "plain-pw");
        Check("Import: класс по id WowManager", wmAccount is not null && storeL.FindAccount("wowuser") is null);
        var charA = wmAccount is null ? null : wmStore.FindCharacter(wmAccount, "CharA");
        Check("Import: CharA = PALADIN", charA is not null && charA!.Class == "PALADIN");

        // 35. Импорт DPAPI-секрета WowManager (enc:v1, без энтропии).
        File.WriteAllText(wmJsonPath, $$"""
            { "characters": [ { "name": "", "account": "DPAPIUSER", "password": "{{dpapiBlob}}" } ] }
            """, new System.Text.UTF8Encoding(false));
        var wmStore2 = new MultiAccountStore(Path.Combine(launcherDir, "wm-import2.json"));
        _ = WowManagerImporter.Import(wmStore2, wmJsonPath);
        var dpapiAccount = wmStore2.FindAccount("dpapiuser");
        Check("Import: enc:v1 DPAPI-секрет расшифрован и перешифрован", dpapiAccount is not null && dpapiAccount.HasPassword && dpapiAccount.GetPassword() == "secret123");

        Console.WriteLine(failures == 0 ? "ИТОГ: ВСЕ ТЕСТЫ ПРОЙДЕНЫ" : $"ИТОГ: {failures} провалено");
        return failures == 0 ? 0 : 1;
    }

    private static string ReadHidden()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
            {
                sb.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }

        return sb.ToString();
    }
}
