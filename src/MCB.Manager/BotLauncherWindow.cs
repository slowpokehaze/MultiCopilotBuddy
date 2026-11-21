using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MCB.Core.L10n;
using MCB.Core.Launch;
using MCB.Core.Models;
using MCB.Core.Sessions;
using MCB.Core.Storage;

namespace MCB.Manager;

/// <summary>
/// Окно «Bot Launcher» — перенос возможностей BotLauncher.ps1 в MCB.Manager:
/// чек-лист аккаунтов (лидер первым), «Запустить выбранные» (групповой запуск
/// SessionManager.LaunchGroupAsync: бот лидера грузится раньше ведомых),
/// «Остановить всё» (клиенты + CopilotBuddy через UAC), «Перезапустить боты»,
/// счётчики процессов и лог. Аккаунты — те же, что у BotLauncher (WowManager
/// characters.json импортирован в Settings\MultiAccount.json).
/// </summary>
public sealed class BotLauncherWindow : Window
{
    private readonly MultiAccountStore _store;
    private readonly SessionManager _manager;
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new();
    private readonly TextBox _log = new();
    private readonly DispatcherTimer _timer = new();
    private bool _busy;

    public BotLauncherWindow(MultiAccountStore store, SessionManager manager)
    {
        _store = store;
        _manager = manager;

        Title = "MCB — " + Strings.BotLauncher;
        Width = 640;
        Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var launchButton = new Button { Content = Strings.LaunchSelectedFirst, Padding = new Thickness(8, 4, 8, 4) };
        var stopButton = new Button { Content = Strings.StopAll, Padding = new Thickness(8, 4, 8, 4) };
        var restartButton = new Button { Content = Strings.RestartBots, Padding = new Thickness(8, 4, 8, 4) };
        var refreshButton = new Button { Content = Strings.Refresh, Padding = new Thickness(8, 4, 8, 4) };
        var autoBox = new CheckBox
        {
            Content = "Автоматика",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Вкл: авторестарт клиентов и автозааттачивание ботов. Выкл: ручной режим — менеджер ничего не перезапускает сам.",
        };
        autoBox.Click += (_, _) =>
        {
            _manager.AutomationEnabled = autoBox.IsChecked == true;
            Log(_manager.AutomationEnabled
                ? "Автоматика включена (авторестарт + автозааттачивание)."
                : "РУЧНОЙ РЕЖИМ: менеджер ничего не перезапускает сам.");
        };

        launchButton.Click += (_, _) => _ = LaunchChecked();
        stopButton.Click += (_, _) => StopEverything();
        restartButton.Click += (_, _) => RestartBots();
        refreshButton.Click += (_, _) => { _manager.Pulse(); RebuildList(); UpdateCounts(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { launchButton, stopButton, restartButton, refreshButton })
        {
            buttons.Children.Add(button);
            buttons.Children.Add(new Border { Width = 8 });
        }
        buttons.Children.Add(autoBox);

        _log.IsReadOnly = true;
        _log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _log.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _log.FontSize = 12;

        _status.Margin = new Thickness(0, 8, 0, 0);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_status);

        var listBorder = new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            Child = _list,
        };
        DockPanel.SetDock(listBorder, Dock.Top);
        var split = new DockPanel();
        split.Children.Add(listBorder);
        split.Children.Add(new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 0),
            Child = _log,
        });
        root.Children.Add(split);
        Content = root;

        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += (_, _) =>
        {
            _ = _manager.Pulse(); // статусы/автоперезапуски, пока окно открыто
            UpdateCounts();
        };
        _timer.Start();

        RebuildList();
        UpdateCounts();
        Log(Strings.Ready);
    }

    // ---------- список аккаунтов ----------

    /// <summary>Перестроить чек-лист: лидер сверху, сохраняя проставленные галочки.</summary>
    private void RebuildList()
    {
        var wasChecked = CheckedIds();
        _list.Items.Clear();

        foreach (var session in _manager.GetGroupLaunchOrder())
        {
            var role = session.IsLeader ? " | LEADER" : string.Empty;
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var box = new CheckBox
            {
                IsChecked = wasChecked.Count == 0 || wasChecked.Contains(session.Id),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Tag = session.Id,
            };
            panel.Children.Add(box);
            panel.Children.Add(new TextBlock
            {
                Text = $"{session.AccountLogin}  |  {session.CharacterName}{role}",
                VerticalAlignment = VerticalAlignment.Center,
            });
            _list.Items.Add(panel);
        }
    }

    /// <summary>Id отмеченных сессий. Пустой список = все (дефолт BotLauncher).</summary>
    private List<string> CheckedIds()
    {
        var ids = new List<string>();
        foreach (var item in _list.Items)
        {
            if (item is StackPanel panel
                && panel.Children.OfType<CheckBox>().FirstOrDefault() is { } box
                && box.IsChecked == true
                && box.Tag is string id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    // ---------- кнопки ----------

    /// <summary>«Запустить выбранные»: групповой запуск, лидер первым (перезаписывает галочки порядка).</summary>
    private async Task LaunchChecked()
    {
        if (_busy)
        {
            return;
        }

        var ids = CheckedIds();
        if (ids.Count == 0)
        {
            Log("Ничего не выбрано.");
            return;
        }

        _busy = true;
        try
        {
            var summary = await Task.Run(() => _manager.LaunchGroupAsync(
                progress: message => Dispatcher.BeginInvoke(() => Log(message)),
                ct: default,
                sessionIds: ids)).ConfigureAwait(true);
            Log(summary);
        }
        catch (Exception ex)
        {
            Log("ERR: " + ex.Message);
        }
        finally
        {
            _busy = false;
            RebuildList();
            UpdateCounts();
        }
    }

    /// <summary>«Остановить всё»: клиенты (StopAll) + CopilotBuddy (taskkill через UAC).</summary>
    private void StopEverything()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var stopped = _manager.StopAll();
            Log($"Клиентов остановлено: {stopped}.");
            KillBotsElevated();
            UpdateCounts();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>«Перезапустить боты»: kill CopilotBuddy (UAC) + аттач заново к живым клиентам.</summary>
    private void RestartBots()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var alive = _store.Config.Sessions
                .Where(s => WowLauncher.IsProcessAlive(s.WowProcessId, s.WowImageName))
                .ToList();
            if (alive.Count == 0)
            {
                Log("Живых клиентов нет — перезапускать боты не к кому.");
                return;
            }

            KillBotsElevated();

            foreach (var session in alive)
            {
                var botPid = _manager.AttachBotLocked(session);
                if (botPid > 0)
                {
                    Log($"CopilotBuddy запущен (PID {botPid}) → {session.AccountLogin}/{session.CharacterName}");
                }
                else
                {
                    Log($"Аттач не удался ({session.AccountLogin}): {BotAttacher.LastAttachError ?? "причина неизвестна"} — Pulse повторит");
                }

                Thread.Sleep(3000);
            }

            _store.Save();
            UpdateCounts();
        }
        finally
        {
            _busy = false;
        }
    }

    private void KillBotsElevated()
    {
        var anyBot = _store.Config.Sessions
            .Any(s => WowLauncher.IsProcessAlive(s.BotProcessId, "CopilotBuddy"));
        if (!anyBot)
        {
            Log("Живых CopilotBuddy нет.");
            return;
        }

        Log("Останавливаю CopilotBuddy (taskkill, подтверди UAC)…");
        try
        {
            Process.Start(new ProcessStartInfo("taskkill.exe", "/F /IM CopilotBuddy.exe")
            {
                Verb = "runas",
                UseShellExecute = true,
            });
            Thread.Sleep(3000);
            Log("CopilotBuddy остановлены.");
        }
        catch (Exception ex)
        {
            Log("UAC отклонён или ошибка taskkill: " + ex.Message);
        }
    }

    // ---------- статус и лог ----------

    private void UpdateCounts()
    {
        var wow = Process.GetProcessesByName("Wow").Length;
        var bots = Process.GetProcessesByName("CopilotBuddy").Length;
        var leader = _store.Config.Sessions.FirstOrDefault(s => s.IsLeader);
        var leaderInfo = leader is null
            ? "лидер не назначен"
            : $"лидер: {leader.AccountLogin}/{leader.CharacterName}";
        _status.Text = $"WoW: {wow}   CopilotBuddy: {bots}   ({leaderInfo})";
    }

    private void Log(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        _log.ScrollToEnd();
    }
}
