using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using GreenMagic;
using Styx;
using Styx.Helpers;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Combat;
using Styx.Logic.Profiles;
using Styx.Offsets;
using Styx.Plugins;
using Styx.WoWInternals;

namespace MCB.Agent;

/// <summary>
/// MCB.Agent — headless-хост движка CopilotBuddy (integration-cb-into-mcb.md,
/// вариант A). Крутит Styx-движок без UI: аттач к WoW по PID, старт бота,
/// named pipe для менеджера, self-exit при смерти клиента (зомби-фикс №2).
///
/// Init-последовательность — 1:1 с MainWindow.Window_Loaded живого бота
/// (см. stageA-init-recon.md §1). Заглушки headless — stageA-init-recon.md §5:
///   - BuddyControlPanel вырезается из EnabledPlugins (StyxWoW.Overlay → NRE);
///   - /customclass= всегда пробрасывается (обход RoutineSelectionForm);
///   - UISettings/HotkeysManager/окна — пропущены.
/// </summary>
internal static class Program
{
    /// <summary>Плагины, несовместимые с headless (трогают StyxWoW.Overlay).</summary>
    private const string DefaultDisabledPlugins = "BuddyControlPanel";

    private static readonly CancellationTokenSource Cts = new();
    private static AgentPipeServer? _pipe;
    private static Mutex? _wowMutex;
    private static Memory? _memory;
    private static Process? _wowProcess;
    private static AgentArgs _opts = new();

    private static int Main(string[] args)
    {
        _opts = AgentArgs.Parse(args);

        var botDir = ResolveBotDir();

        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch { /* redirected console */ }

        Console.WriteLine($"MCB.Agent: botDir={botDir}, wowPid={_opts.WowPid}, pipe={_opts.PipeName ?? "(auto)"}");

        // Папка бота = база всех относительных путей движка
        // (Logging.ApplicationPath, Routines/Bots/Plugins/mmaps).
        Directory.SetCurrentDirectory(botDir);
        SetPerAgentTemp(botDir);

        PreloadFrameworkAssemblies();

        InitLogging(botDir);

        _pipe = new AgentPipeServer(
            string.IsNullOrWhiteSpace(_opts.PipeName) ? $"MCBAgent_{Environment.ProcessId}" : _opts.PipeName);
        _pipe.StopRequested += OnStopRequested;
        _pipe.Start();
        Console.WriteLine($"MCB.Agent: pipe '{_pipe.PipeName}' слушает");

        // Аргументов нет — ждём AGENT-команду по пайпу? Для простоты: агент всегда
        // стартует менеджером с аргументами; без /pid= работать не можем.
        if (_opts.WowPid <= 0)
        {
            Console.Error.WriteLine("MCB.Agent: не задан /pid= — выход");
            return 3;
        }

        var exitCode = 0;
        try
        {
            Startup();
            MonitorLoop();
        }
        catch (Exception ex)
        {
            Logging.WriteException(ex);
            Console.Error.WriteLine("MCB.Agent: FATAL " + ex);
            _pipe.SendState("ERROR", ex.Message);
            exitCode = 2;
        }
        finally
        {
            Shutdown();
        }

        return exitCode;
    }

    /// <summary>
    /// Предзагрузка framework-сборок в AppDomain до компиляции плагинов.
    /// SourceCompiler берёт референсы из AppDomain.CurrentDomain.GetAssemblies();
    /// в legacy WPF-процессе всё нужное уже загружено, а в headless агента — нет,
    /// и плагины (MCBBridge: IPAddress; AutoEquip2/BCP: Font/Icon) не компилились.
    /// Сборки лежат в WindowsDesktop.App (Drawing/Forms) и NETCore.App (остальные),
    /// обе фреймворка объявлены в MCB.Agent.runtimeconfig.json.
    /// </summary>
    private static void PreloadFrameworkAssemblies()
    {
        var names = new[]
        {
            "System.Drawing.Common",      // Font, Icon, GraphicsUnit
            "System.Drawing.Primitives",
            "System.Windows.Forms",       // WinForms-части плагинов (без UI-запуска)
            "System.Net.Primitives",      // IPAddress — MCBBridge
            "System.Net.WebClient",       // WebClient — BCP и др.
            "System.Windows.Extensions",  // System.Media — BCP
            "System.ServiceModel",        // WCF (HBRelogHelper и т.п.)
            "System.ServiceModel.Primitives",
        };
        foreach (var name in names)
        {
            try
            {
                Assembly.Load(new AssemblyName(name));
            }
            catch
            {
                // Нет такой сборки в runtime — плагин, которому она нужна, не соберётся
                // и будет пропущен (как и раньше).
            }
        }
    }

    // ---------- init (stageA §1, шаги 2.1–9) ----------
    private static void Startup()
    {
        var wowPid = _opts.WowPid;
        _wowProcess = Process.GetProcessById(wowPid);
        if (_wowProcess.HasExited)
        {
            throw new InvalidOperationException($"WoW PID {wowPid} уже мёртв");
        }

        // Шаг 2.3: мьютекс владения WoW-PID (свой, per-PID; живой CopilotBuddy.exe
        // держит свой — двойной аттач закрыт с обеих сторон).
        _wowMutex = new Mutex(true, $@"Local\MCBAgent_WoW_{wowPid}", out var createdNew);
        if (!createdNew)
        {
            throw new InvalidOperationException($"WoW PID {wowPid} уже занят другим агентом");
        }

        // Шаги 2.4–2.6: память + EndScene-хук + первый Update.
        Logging.Write("Please wait a few seconds while MCB.Agent initializes.");
        Logging.WriteDebug("Using WoW with process ID {0}", wowPid);
        Logging.WriteDebug("Platform: {0}", Environment.Is64BitProcess ? "x64" : "x86");
        Logging.WriteDebug("Executable Path: {0}", AppContext.BaseDirectory);

        _memory = new Memory(wowPid);
        if (_memory.Read<byte>(GlobalOffsets.InGame) == 0)
        {
            throw new InvalidOperationException($"WoW PID {wowPid} не в мире (экран логина?)");
        }

        ObjectManager.Initialize(_memory);
        _pipe!.SendState("ATTACHED", $"wowPid={wowPid}");
        Logging.Write("Attached to WoW with ID {0}", wowPid);

        // Шаг 3: minimize-guard (движок не умеет тикать свёрнутый клиент).
        // TreeRoot.Initialize — internal (живой бот зовёт из своей сборки) — рефлексия.
        typeof(TreeRoot)
            .GetMethod("Initialize", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, null);

        // Шаг 4: лог персонажа.
        if (ObjectManager.Me != null)
        {
            Logging.Write("Character is a level {0} {1} {2}",
                ObjectManager.Me.Level, ObjectManager.Me.Race, ObjectManager.Me.Class);
            Logging.Write("Current zone is {0}", ObjectManager.Me.RealZoneText);
        }

        // Шаг 5: настройки per-character (UISettings — UI-only, пропущен).
        CharacterSettings.Initialize();
        StyxSettings.Instance.Load();
        LevelbotSettings.Instance.Load();
        Logging.WriteDebug("[Settings] Loaded settings for character: {0}", StyxWoW.Me?.Name ?? "Unknown");

        // Шаг 6: рутины (/customclass= в собственных аргументах — RoutineManager
        // сам парсит Environment.GetCommandLineArgs, форма выбора не показывается).
        RoutineManager.Init();
        if (!string.IsNullOrWhiteSpace(_opts.RoutineName)
            && !string.Equals(RoutineManager.Current.Name, _opts.RoutineName, StringComparison.OrdinalIgnoreCase))
        {
            Logging.Write("WARNING: requested routine '{0}', current is '{1}'",
                _opts.RoutineName, RoutineManager.Current.Name);
        }

        // Шаг 7: внешние боты из Bots\ (built-in уже в BotManager.Instance).
        try
        {
            var botsPath = Path.Combine(AppContext.BaseDirectory, "Bots");
            if (Directory.Exists(botsPath))
            {
                BotManager.Instance.LoadBots(botsPath);
            }
        }
        catch (Exception ex)
        {
            Logging.WriteException(ex);
        }

        // Шаг 8: плагины (EnabledPlugins минус headless-несовместимые).
        try
        {
            var disabled = (Environment.GetEnvironmentVariable("MCB_AGENT_DISABLE_PLUGINS")
                            ?? DefaultDisabledPlugins)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.ToLowerInvariant())
                .ToHashSet();

            var enabled = (CharacterSettings.Instance.EnabledPlugins ?? Array.Empty<string>())
                .Where(p => !disabled.Contains(p.ToLowerInvariant()))
                .ToArray();
            PluginManager.Initialize(enabled);
        }
        catch (Exception ex)
        {
            Logging.Write("Failed to initialize plugins: {0}", ex.Message);
            Logging.WriteException(ex);
        }

        // Шаг 9: профиль → бот → старт.
        if (!string.IsNullOrWhiteSpace(_opts.ProfilePath))
        {
            ProfileManager.LoadNew(_opts.ProfilePath);
            Logging.Write("Profile loaded: {0}", Path.GetFileName(_opts.ProfilePath));
        }

        if (!string.IsNullOrWhiteSpace(_opts.BotName))
        {
            var needle = _opts.BotName.ToLowerInvariant();
            var match = BotManager.Instance.Bots
                .FirstOrDefault(b => b.Key != null && b.Key.ToLowerInvariant().Contains(needle));
            if (match.Value != null)
            {
                Logging.Write("Auto-select botbase from /botname: {0}", match.Key);
                BotManager.Instance.SetCurrent(match.Value);
            }
            else
            {
                throw new InvalidOperationException(
                    $"/botname='{_opts.BotName}' не совпал ни с одним ботом (доступны: {string.Join(", ", BotManager.Instance.Bots.Keys)})");
            }
        }

        if (BotManager.Current == null)
        {
            throw new InvalidOperationException("Бот не выбран (нет /botname= и SelectedBotIndex пуст)");
        }

        _pipe.SendState("IN_WORLD", StyxWoW.IsInGame ? "in world" : "not confirmed");

        Logging.Write("Starting the bot.");
        Logging.Write("Currently Using BotBase : {0}", BotManager.Current.Name);
        TreeRoot.Start();
        _pipe.SendState("BOT_RUNNING", BotManager.Current.Name);
        Logging.Write("Currently Using BotBase : {0} — running", BotManager.Current.Name);
    }

    // ---------- monitor (зомби-фикс №2) ----------

    /// <summary>
    /// Раз в секунду: смерть клиента → STOP бота → self-exit; уход из мира
    /// (логаут/switch через мост) → STATE ATTACHED; возврат в мир → IN_WORLD.
    /// </summary>
    private static void MonitorLoop()
    {
        var lastInWorld = true;
        while (!Cts.IsCancellationRequested)
        {
            Thread.Sleep(1000);

            if (Cts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                // Клиент умер — агент уходит вместе с ним (двойная защита против
                // зомби: Job Object от менеджера + self-exit отсюда).
                if (_wowProcess == null || _wowProcess.HasExited)
                {
                    Logging.Write("WoW process has exited. Stopping bot and exiting.");
                    _pipe!.SendState("CLIENT_DEAD", $"wowPid={_opts.WowPid}");
                    StopBotSafe("WoW process exited");
                    Environment.Exit(0);
                }

                // Жив ли сам бот: TreeRoot погас сам (профиль кончился, стоп изнутри).
                var inWorld = StyxWoW.IsInGame;
                if (inWorld != lastInWorld)
                {
                    lastInWorld = inWorld;
                    if (inWorld)
                    {
                        _pipe!.SendState("IN_WORLD", "back in world");
                    }
                    else
                    {
                        _pipe!.SendState("ATTACHED", "left world (logout/switch)");
                    }
                }

                var running = TreeRoot.IsRunning;
                if (!running && _wasRunning)
                {
                    _pipe!.SendState("BOT_STOPPED", "TreeRoot stopped");
                    Logging.Write("Bot stopped (TreeRoot.State={0})", TreeRoot.State);
                }
                else if (running && !_wasRunning)
                {
                    _pipe!.SendState("BOT_RUNNING", BotManager.Current?.Name ?? "?");
                }
                _wasRunning = running;

                // Заголовок окна лога: персонаж/бот/уровень/HP/MP — видно, кто
                // ботит и в каком он состоянии (запрос 2026-11-21).
                UpdateLogWindowTitle();
            }
            catch (Exception ex)
            {
                Logging.WriteException(ex);
            }
        }
    }

    private static bool _wasRunning;

    /// <summary>Раз в ~2 с обновляем заголовок окна лога: персонаж/бот/уровень/HP/MP.</summary>
    private static void UpdateLogWindowTitle()
    {
        _titleUpdateCounter++;
        if (_titleUpdateCounter % 2 != 0)
        {
            return; // монитор тикает раз в секунду — заголовок обновляем раз в 2 с
        }

        try
        {
            var botName = BotManager.Current?.Name ?? _opts.BotName ?? "";
            string title;
            if (StyxWoW.IsInGame && StyxWoW.Me != null)
            {
                var me = StyxWoW.Me;
                title = $"{me.Name} [{botName}] lvl {me.Level} | HP {me.HealthPercent:F0}% | MP {me.ManaPercent:F0}%";
            }
            else
            {
                title = $"[{botName}] не в мире";
            }

            _logWindow?.UpdateTitle(title);
        }
        catch
        {
            // ObjectManager ещё не готов — попробуем в следующий тик.
        }
    }

    private static int _titleUpdateCounter;

    private static void OnStopRequested()
    {
        Logging.Write("STOP received from pipe — stopping bot and exiting.");
        StopBotSafe("STOP from manager");
        Environment.Exit(0);
    }

    private static void StopBotSafe(string reason)
    {
        try
        {
            if (TreeRoot.State != TreeRootState.Stopped)
            {
                TreeRoot.Stop(reason);
                TreeRoot.WaitForStop(5000);
            }
        }
        catch (Exception ex)
        {
            Logging.WriteException(ex);
        }
    }

    // ---------- teardown ----------

    private static void Shutdown()
    {
        StopBotSafe("agent shutdown");

        try
        {
            // Как MainWindow.SaveSettings: per-character настройки перед выходом.
            if (ObjectManager.Wow != null && StyxWoW.IsInGame)
            {
                CharacterSettings.Instance?.Save();
                StyxSettings.Instance.Save();
                LevelbotSettings.Instance.Save();
            }
        }
        catch { }

        try { _memory?.Dispose(); } catch { }
        try { _wowMutex?.Dispose(); } catch { }
        _pipe?.Dispose();
        Cts.Cancel();
    }

    // ---------- helpers ----------

    private static string ResolveBotDir()
    {
        if (!string.IsNullOrWhiteSpace(_opts.BotDir))
        {
            return Path.GetFullPath(_opts.BotDir);
        }

        // Агент лежит рядом с CopilotBuddy.dll (stageA §4) — папка агента и есть папка бота.
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Свой TMP на процесс агента: Roslyn-компиляции рутин/ботов/плагинов пишут
    /// в %TEMP% (stageA §5.4, stage5-audit §1) — изолируем параллельные агенты.
    /// </summary>
    private static void SetPerAgentTemp(string botDir)
    {
        try
        {
            var temp = Path.Combine(botDir, "Temp", $"agent_{Environment.ProcessId}");
            Directory.CreateDirectory(temp);
            Environment.SetEnvironmentVariable("TMP", temp);
            Environment.SetEnvironmentVariable("TEMP", temp);
        }
        catch
        {
            // Не критично: имена временных сборок и так с GUID.
        }
    }

    private static void InitLogging(string botDir)
    {
        var logDir = Path.Combine(botDir, "Logs");
        Directory.CreateDirectory(logDir);
        var timestamp = DateTime.Now;
        Logging.LogFilePath = Path.Combine(logDir,
            $"MCBAgent_{timestamp.Year}-{timestamp.Month:D2}-{timestamp.Day:D2}_{timestamp.Hour:D2}{timestamp.Minute:D2}_{Environment.ProcessId}.log");

        Logging.OnLogMessage += OnLogMessage;
        Logging.Write("MCB.Agent v1 headless host started (pid {0}, bot dir {1})",
            Environment.ProcessId, botDir);

        StartLogWindow();
    }

    /// <summary>
    /// Окно лога бота (запрос 2026-11-21): STA-поток + Form, строки приходят из
    /// OnLogMessage, заголовок обновляет монитор-цикл (персонаж/бот/HP/MP).
    /// Фоновый поток: агент без окна закрывается как раньше.
    /// </summary>
    private static void StartLogWindow()
    {
        var uiThread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                _logWindow = new LogWindow();
                System.Windows.Forms.Application.Run(_logWindow);
            }
            catch (Exception ex)
            {
                Logging.Write("Log window failed: {0}", ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "MCB.Agent.LogWindow",
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
    }

    private static LogWindow? _logWindow;
    private static void OnLogMessage(ReadOnlyCollection<Logging.LogMessage> messages)
    {
        foreach (var message in messages)
        {
            // Зеркало лога бота в пайп (LOG, реестр §4) + окно лога.
            var level = message.Level.ToString().ToLowerInvariant();
            _pipe?.SendLog(level, message.Message);
            _logWindow?.AppendLine($"[{DateTime.Now:HH:mm:ss}] [{message.Level}] {message.Message}");

            var color = Console.ForegroundColor;
            try
            {
                if (message.Level == LogLevel.Quiet)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                }
                else if (message.Level == LogLevel.Diagnostic)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                }

                Console.WriteLine(message);
            }
            finally
            {
                Console.ForegroundColor = color;
            }
        }
    }
}
