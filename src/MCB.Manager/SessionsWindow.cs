using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MCB.Core.Launch;
using MCB.Core.L10n;
using MCB.Core.Sessions;
using MCB.Core.Storage;

namespace MCB.Manager;

/// <summary>
/// Список сессий (Этап 5): статусы всех сессий (клиент/бот/рестарты),
/// кнопки Запустить/Остановить/Остановить всё, автообновление.
/// </summary>
public sealed class SessionsWindow : Window
{
    private readonly MultiAccountStore _store;
    private readonly ListView _list = new();
    private readonly TextBlock _status = new();
    private readonly DispatcherTimer _timer = new();
    private readonly SessionManager _manager;
    private readonly WowLauncher _launcher;

    public SessionsWindow(MultiAccountStore store)
    {
        _store = store;
        _launcher = new WowLauncher(store);
        _manager = new SessionManager(store, _launcher);

        Title = "MCB - " + Strings.Dashboard + " / " + Strings.Accounts;
        Width = 780;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var refreshButton = new Button { Content = Strings.Refresh, Padding = new Thickness(8, 4, 8, 4) };
        var launchButton = new Button { Content = Strings.Launch, Padding = new Thickness(8, 4, 8, 4) };
        var stopButton = new Button { Content = Strings.Stop, Padding = new Thickness(8, 4, 8, 4) };
        var stopAllButton = new Button { Content = Strings.StopAll, Padding = new Thickness(8, 4, 8, 4) };
        foreach (var button in new[] { refreshButton, launchButton, stopButton, stopAllButton })
        {
            button.Padding = new Thickness(8, 4, 8, 4);
        }

        refreshButton.Click += (_, _) => Refresh();
        launchButton.Click += (_, _) => LaunchSelected();
        stopButton.Click += (_, _) => StopSelected();
        stopAllButton.Click += (_, _) => StopEverything();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { refreshButton, launchButton, stopButton, stopAllButton })
        {
            buttons.Children.Add(button);
            buttons.Children.Add(new Border { Width = 8 });
        }

        var view = new GridView();
        view.Columns.Add(MakeColumn(Strings.Account, "Account", 110));
        view.Columns.Add(MakeColumn(Strings.Character, "Character", 110));
        view.Columns.Add(MakeColumn("State", "State", 90));
        view.Columns.Add(MakeColumn("PID", "WowPid", 70));
        view.Columns.Add(MakeColumn("Bot", "BotPid", 70));
        view.Columns.Add(MakeColumn("Restarts", "Restarts", 70));
        view.Columns.Add(MakeColumn("Bridge", "Bridge", 60));
        _list.View = view;

        _status.Margin = new Thickness(0, 8, 0, 0);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        root.Children.Add(new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            Child = _list,
        });
        Content = root;

        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    private static GridViewColumn MakeColumn(string header, string path, int width)
        => new()
        {
            Header = header,
            DisplayMemberBinding = new System.Windows.Data.Binding(path),
            Width = width,
        };

    private sealed class Row
    {
        public string Account { get; init; } = string.Empty;
        public string Character { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string WowPid { get; init; } = string.Empty;
        public string BotPid { get; init; } = string.Empty;
        public string Restarts { get; init; } = string.Empty;
        public string Bridge { get; init; } = string.Empty;
    }

    private void Refresh()
    {
        try
        {
            _store.Load();
        }
        catch
        {
            // конфиг мог быть занят другим процессом
        }

        _ = _manager.Pulse(); // обновляет статусы/автоперезапуски

        _list.ItemsSource = _store.Config.Sessions.Select(s => new Row
        {
            Account = s.AccountLogin,
            Character = s.CharacterName,
            State = s.State.ToString(),
            WowPid = s.WowProcessId > 0 ? s.WowProcessId.ToString() : "-",
            BotPid = s.BotProcessId > 0 ? s.BotProcessId.ToString() : "-",
            Restarts = s.RestartCount.ToString(),
            Bridge = s.BridgePort > 0 ? s.BridgePort.ToString() : "-",
        }).ToList();

        _status.Text = $"Сессий: {_store.Config.Sessions.Count}. {Strings.ConfigLabel}{_store.FilePath}";
    }

    private Core.Models.SessionModel? Selected => _list.SelectedItem as Core.Models.SessionModel;

    private void LaunchSelected()
    {
        var session = Selected;
        if (session is null)
        {
            return;
        }

        var account = _store.FindAccount(session.AccountLogin);
        if (account is null)
        {
            return;
        }

        var result = _launcher.LaunchCharacter(account, session.CharacterName, allowWhenRunning: true);
        _status.Text = result.Started
            ? $"PID {result.ProcessId}"
            : "ERR: " + result.Detail;
        Refresh();
    }

    private void StopSelected()
    {
        var session = Selected;
        if (session is null || session.WowProcessId <= 0)
        {
            return;
        }

        _ = _launcher.KillAliveClient(session.AccountLogin);
        Refresh();
    }

    private void StopEverything()
    {
        var stopped = _manager.StopAll();
        _status.Text = Strings.Ready + $" (stop-all: {stopped})";
        Refresh();
    }
}
