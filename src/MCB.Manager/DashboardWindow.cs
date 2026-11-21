using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MCB.Core.Harvest;
using MCB.Core.L10n;
using MCB.Core.Launch;
using MCB.Core.Models;
using MCB.Core.Roster;
using MCB.Core.Storage;

namespace MCB.Manager;

/// <summary>
/// Дашборд (Этап 4): таблица всех персонажей (голд/ГС/ilvl/зона/обновлено)
/// из конфига + кнопки сканирования SavedVariables и harvest по всем
/// аккаунтам одной кнопкой. Переключатель ru/en (Strings).
/// </summary>
public sealed class DashboardWindow : Window
{
    private readonly MultiAccountStore _store;
    private readonly ListView _roster = new();
    private readonly TextBlock _status = new();
    private readonly Button _scanButton = new();
    private readonly Button _harvestButton = new();
    private readonly Button _languageButton = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly List<GridViewColumn> _columns = new();

    public DashboardWindow(MultiAccountStore store)
    {
        _store = store;
        Strings.Language = store.Config.Ui.Language;

        Title = "MCB - " + Strings.Dashboard;
        Width = 1100;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var refreshButton = new Button { Content = Strings.Refresh, Padding = new Thickness(8, 4, 8, 4) };
        _scanButton.Content = Strings.ScanRoster;
        _harvestButton.Content = Strings.HarvestRun;
        _languageButton.Content = Strings.LanguageToggle;
        foreach (var button in new[] { refreshButton, _scanButton, _harvestButton, _languageButton })
        {
            button.Padding = new Thickness(8, 4, 8, 4);
        }

        refreshButton.Click += (_, _) => RefreshRoster();
        _scanButton.Click += (_, _) => RunBackground(ScanRoster);
        _harvestButton.Click += (_, _) => ConfirmAndHarvest();
        _languageButton.Click += (_, _) => ToggleLanguage();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var button in new[] { refreshButton, _scanButton, _harvestButton, _languageButton })
        {
            buttons.Children.Add(button);
            buttons.Children.Add(new Border { Width = 8 });
        }

        // GridView обязан существовать ДО BuildColumns: метод делает
        // (GridView)_roster.View — иначе NRE при открытии дашборда.
        _roster.View = new GridView();
        BuildColumns();
        _roster.ItemsSource = null;

        _status.Margin = new Thickness(0, 8, 0, 0);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        var host = new Border
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            Child = _roster,
        };
        root.Children.Add(host);
        Content = root;

        // Автообновление из конфига (store перечитываем — CLI/другой процесс мог дописать).
        _refreshTimer.Interval = TimeSpan.FromSeconds(10);
        _refreshTimer.Tick += (_, _) => RefreshRoster();
        _refreshTimer.Start();

        RefreshRoster();
    }

    // ---------- таблица ----------

    private void BuildColumns()
    {
        var view = (GridView)_roster.View;
        view.Columns.Clear();
        _columns.Clear();

        void AddColumn(string header, string bindingPath, int width)
        {
            var column = new GridViewColumn
            {
                Header = header,
                DisplayMemberBinding = new System.Windows.Data.Binding(bindingPath),
                Width = width,
            };
            view.Columns.Add(column);
            _columns.Add(column);
        }

        AddColumn(Strings.Account, "Account", 110);
        AddColumn(Strings.Character, "Name", 120);
        AddColumn(Strings.Class, "ClassName", 90);
        AddColumn(Strings.Level, "Level", 50);
        AddColumn(Strings.Gold, "Gold", 130);
        AddColumn(Strings.Gearscore, "Gs", 60);
        AddColumn(Strings.ItemLevel, "Ilvl", 55);
        AddColumn(Strings.Zone, "Zone", 170);
        AddColumn(Strings.Updated, "Updated", 130);
    }

    private sealed class Row
    {
        public string Account { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string ClassName { get; init; } = string.Empty;
        public string Level { get; init; } = string.Empty;
        public string Gold { get; init; } = string.Empty;
        public string Gs { get; init; } = string.Empty;
        public string Ilvl { get; init; } = string.Empty;
        public string Zone { get; init; } = string.Empty;
        public string Updated { get; init; } = string.Empty;
    }

    private void RefreshRoster()
    {
        try
        {
            _store.Load();
        }
        catch
        {
            // конфиг мог быть занят другим процессом — покажем прошлые данные
        }

        var rows = new List<Row>();
        foreach (var account in _store.Config.Accounts.OrderBy(a => a.Order))
        {
            foreach (var character in account.Characters.Where(c => !c.Hidden).OrderBy(c => c.Order))
            {
                string Get(string key) => character.Data.TryGetValue(key, out var value) ? value : string.Empty;
                rows.Add(new Row
                {
                    Account = account.Login,
                    Name = character.Name,
                    ClassName = character.Class,
                    Level = Get("level"),
                    Gold = Get("gold"),
                    Gs = Get("gs"),
                    Ilvl = Get("ilvl"),
                    Zone = Get("zone"),
                    Updated = Get("updated"),
                });
            }
        }

        _roster.ItemsSource = rows;
        _status.Text = $"{Strings.AccountsCount}{_store.Config.Accounts.Count}. {Strings.ConfigLabel}{_store.FilePath}";
    }

    // ---------- фоновые операции ----------

    private void SetBusy(bool busy, string message)
    {
        _status.Text = message;
        _scanButton.IsEnabled = !busy;
        _harvestButton.IsEnabled = !busy;
    }

    private void RunBackground(Action body)
    {
        SetBusy(true, Strings.Scanning);
        Task.Run(body);
    }

    private void ScanRoster()
    {
        try
        {
            var wtf = ResolveWtf();
            var (changed, files) = RosterScanner.Scan(_store, wtf);
            _store.Save();
            Dispatcher.Invoke(() =>
            {
                RefreshRoster();
                _status.Text = $"{Strings.Ready} (файлов {files.Count}, +{changed})";
                SetBusy(false, _status.Text);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => SetBusy(false, "ERR: " + ex.Message));
        }
    }

    private void ConfirmAndHarvest()
    {
        if (_store.Config.Accounts.Count == 0)
        {
            _ = MessageBox.Show(this, Strings.NoAccounts, "MCB", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, Strings.HarvestConfirm, "MCB - " + Strings.HarvestRun,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, Strings.HarvestRunning);
        Task.Run(RunHarvest);
    }

    private void RunHarvest()
    {
        try
        {
            var orchestrator = new HarvestOrchestrator(_store)
            {
                Progress = line => Dispatcher.Invoke(() => _status.Text = line),
            };
            var results = orchestrator.RunAll();
            _store.Save();

            var harvested = results.Count(r => r.Status == HarvestStatus.Harvested);
            var summary = string.Join("; ", results.Select(r => $"{r.AccountLogin}: {r.Status} ({r.Detail})"));
            Dispatcher.Invoke(() =>
            {
                RefreshRoster();
                SetBusy(false, Strings.HarvestDone + $"{harvested}/{results.Count}. " + summary);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => SetBusy(false, "ERR: " + ex.Message));
        }
    }

    private string ResolveWtf()
    {
        foreach (var account in _store.Config.Accounts)
        {
            var (_, dir) = WowLauncher.ResolveWowPath(account.WowPath);
            var wtf = Path.Combine(dir, "WTF");
            if (Directory.Exists(wtf))
            {
                return wtf;
            }
        }

        throw new InvalidOperationException("WTF не найден ни по одному WowPath аккаунтов.");
    }

    // ---------- язык ----------

    private void ToggleLanguage()
    {
        Strings.Language = Strings.Language == "ru" ? "en" : "ru";
        _store.Config.Ui.Language = Strings.Language;
        _store.Save();

        Title = "MCB - " + Strings.Dashboard;
        _scanButton.Content = Strings.ScanRoster;
        _harvestButton.Content = Strings.HarvestRun;
        _languageButton.Content = Strings.LanguageToggle;
        BuildColumns();
        RefreshRoster();
    }
}
