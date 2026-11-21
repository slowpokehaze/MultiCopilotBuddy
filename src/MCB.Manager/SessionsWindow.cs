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
    private readonly TextBox _log = new();
    private readonly DispatcherTimer _timer = new();
    private readonly SessionManager _manager;
    private readonly WowLauncher _launcher;

    public SessionsWindow(MultiAccountStore store, SessionManager manager)
    {
        _store = store;
        _launcher = new WowLauncher(store);
        // Общий менеджер с главным окном: два SessionManager = два пульса =
        // задвоенные аттачи ботов и рестарты.
        _manager = manager;

        Title = "MCB - " + Strings.Sessions;
        Width = 780;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var refreshButton = new Button { Content = Strings.Refresh, Padding = new Thickness(8, 4, 8, 4) };
        var launchButton = new Button { Content = Strings.Launch, Padding = new Thickness(8, 4, 8, 4) };
        var launchAllButton = new Button { Content = Strings.LaunchAllFirst, Padding = new Thickness(8, 4, 8, 4) };
        var makeLeaderButton = new Button { Content = Strings.MakeLeader, Padding = new Thickness(8, 4, 8, 4) };
        var stopButton = new Button { Content = Strings.Stop, Padding = new Thickness(8, 4, 8, 4) };
        var stopAllButton = new Button { Content = Strings.StopAll, Padding = new Thickness(8, 4, 8, 4) };
        foreach (var button in new[] { refreshButton, launchButton, launchAllButton, makeLeaderButton, stopButton, stopAllButton })
        {
            button.Padding = new Thickness(8, 4, 8, 4);
        }

        refreshButton.Click += (_, _) => Refresh();
        launchButton.Click += (_, _) => LaunchSelected();
        launchAllButton.Click += (_, _) => _ = LaunchGroup();
        makeLeaderButton.Click += (_, _) => MakeSelectedLeader();
        stopButton.Click += (_, _) => StopSelected();
        stopAllButton.Click += (_, _) => StopEverything();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { refreshButton, launchButton, launchAllButton, makeLeaderButton, stopButton, stopAllButton })
        {
            buttons.Children.Add(button);
            buttons.Children.Add(new Border { Width = 8 });
        }

        var view = new GridView();
        view.Columns.Add(MakeColumn(Strings.Role, "Role", 60));
        view.Columns.Add(MakeColumn(Strings.Account, "Account", 110));
        view.Columns.Add(MakeColumn(Strings.Character, "Character", 110));
        view.Columns.Add(MakeColumn("State", "State", 90));
        view.Columns.Add(MakeColumn("PID", "WowPid", 70));
        view.Columns.Add(MakeColumn("Bot", "BotPid", 70));
        view.Columns.Add(MakeColumn("Restarts", "Restarts", 70));
        view.Columns.Add(MakeColumn("Bridge", "Bridge", 60));
        _list.View = view;

        _status.Margin = new Thickness(0, 8, 0, 0);

        // Лог как в Bot Launcher: сообщения кнопок не пропадают (статус-строку
        // таймер затирал каждые 5 с — бой 2026-11-21).
        _log.IsReadOnly = true;
        _log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _log.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _log.FontSize = 12;
        _log.Height = 110;
        _log.Margin = new Thickness(0, 8, 0, 0);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_log, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        root.Children.Add(_log);
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
        public string Id { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
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

        // Выделение надо сохранять: таймер перестраивает таблицу каждые 5 с,
        // и без этого выделение слетает раньше, чем пользователь нажмёт кнопку
        // (бой 2026-11-21: «выбираю аккаунт — ничего не происходит»).
        var selectedId = (_list.SelectedItem as Row)?.Id;

        _list.ItemsSource = _store.Config.Sessions.Select(s => new Row
        {
            Id = s.Id,
            Role = s.IsLeader ? "★ " + Strings.LeaderRole : string.Empty,
            Account = s.AccountLogin,
            Character = s.CharacterName,
            State = s.State.ToString(),
            WowPid = s.WowProcessId > 0 ? s.WowProcessId.ToString() : "-",
            BotPid = s.BotProcessId > 0 ? s.BotProcessId.ToString() : "-",
            Restarts = s.RestartCount.ToString(),
            Bridge = s.BridgePort > 0 ? s.BridgePort.ToString() : "-",
        }).ToList();

        if (selectedId is not null)
        {
            _list.SelectedItem = _list.Items.OfType<Row>().FirstOrDefault(r => r.Id == selectedId);
        }

        _status.Text = $"Сессий: {_store.Config.Sessions.Count}. {Strings.ConfigLabel}{_store.FilePath}";
    }

    /// <summary>Сессия выбранной строки таблицы. Строки — копии (Row), поэтому
    /// ищем по Id: раньше строку кастовали к SessionModel, всегда получали null
    /// и кнопки «Запустить/Остановить/Назначить лидером» молча не работали.</summary>
    private Core.Models.SessionModel? Selected
        => _list.SelectedItem is Row row
            ? _store.Config.Sessions.FirstOrDefault(s => s.Id == row.Id)
            : null;

    private void Log(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        _log.ScrollToEnd();
    }

    /// <summary>Идёт групповой запуск (кнопка «Запустить всё» заблокирована).</summary>
    private bool _groupLaunchRunning;

    /// <summary>Назначить выбранную сессию лидером группы (остальные — ведомые).</summary>
    private void MakeSelectedLeader()
    {
        var selected = Selected;
        if (selected is null)
        {
            Log("Выбери строку сессии в таблице.");
            return;
        }

        var session = _store.Config.Sessions.FirstOrDefault(s => s.Id == selected.Id);
        if (session is null)
        {
            return;
        }

        foreach (var s in _store.Config.Sessions)
        {
            s.IsLeader = ReferenceEquals(s, session);
        }

        _store.Save();
        Log(Strings.LeaderRole + ": " + session.AccountLogin + "/" + session.CharacterName);
        Refresh();
    }

    /// <summary>
    /// Групповой запуск «лидер первым»: бот лидера грузится первым, ведомые —
    /// по очереди после него (LeaderPlugin должен быть загружен раньше всех).
    /// </summary>
    private async Task LaunchGroup()
    {
        if (_groupLaunchRunning)
        {
            return;
        }

        _groupLaunchRunning = true;
        try
        {
            var summary = await Task.Run(() => _manager.LaunchGroupAsync(
                progress: message => Dispatcher.BeginInvoke(() => Log(message)))).ConfigureAwait(true);
            Log(summary);
        }
        catch (Exception ex)
        {
            Log("ERR: " + ex.Message);
        }
        finally
        {
            _groupLaunchRunning = false;
        }
    }

    private void LaunchSelected()
    {
        var session = Selected;
        if (session is null)
        {
            _status.Text = "Выбери строку сессии в таблице.";
            return;
        }

        var account = _store.FindAccount(session.AccountLogin);
        if (account is null)
        {
            Log($"ERR: аккаунт '{session.AccountLogin}' не найден в конфиге.");
            return;
        }

        // Та же схема, что в SessionManager.ResolveLaunchCharacterName:
        // строгая проверка лоунчера требует персонажа в рорстере, но рорстер
        // может быть пуст (не сканировался) — тогда передаём null, лоунчер
        // возьмёт DefaultCharacter, а DLL найдёт слот по имени на сервере.
        var name = session.CharacterName;
        var launchName = string.IsNullOrWhiteSpace(name)
            || account.Characters.Count == 0
            || string.Equals(name, account.DefaultCharacter, StringComparison.OrdinalIgnoreCase)
            ? null
            : name;

        // Один клиент на аккаунт: если клиент аккаунта уже жив — закрываем
        // его вместе с ботом и запускаем пару заново. Иначе открывался ВТОРОЙ
        // клиент, а старый бот оставался — итого два бота (бой 2026-11-21).
        _launcher.KillAliveClient(account.Login);

        var result = _launcher.LaunchCharacter(account, launchName, allowWhenRunning: true);
        Log(result.Started
            ? $"{session.AccountLogin}/{session.CharacterName}: клиент запущен, PID {result.ProcessId}"
            : $"ERR ({session.AccountLogin}): " + result.Detail);
        Refresh();
    }

    private void StopSelected()
    {
        var session = Selected;
        if (session is null)
        {
            Log("Выбери строку сессии в таблице.");
            return;
        }
        if (session.WowProcessId <= 0)
        {
            Log($"{session.AccountLogin}: клиент не запущен.");
            return;
        }

        _ = _launcher.KillAliveClient(session.AccountLogin);
        Log($"{session.AccountLogin}: остановка клиента (и его бота)…");
        Refresh();
    }

    private void StopEverything()
    {
        var stopped = _manager.StopAll();
        Log($"Остановлено клиентов: {stopped}.");
        Refresh();
    }
}
