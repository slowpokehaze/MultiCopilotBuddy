using System.Windows;
using System.Windows.Controls;
using MCB.Core.L10n;
using MCB.Core.Models;
using MCB.Core.Storage;

namespace MCB.Manager;

/// <summary>
/// Окно «Multi Account» (каркас Этапа 0): список аккаунтов и персонажей,
/// добавление/скрытие/пароли. Данные — MCB.Core (Settings\MultiAccount.json, DPAPI).
/// UI построен кодом без XAML (нет SDK на машине сборки).
/// </summary>
public sealed class MultiAccountWindow : Window
{
    private readonly MultiAccountStore _store;
    private readonly ListBox _accountsList = new();
    private readonly ListBox _charactersList = new();
    private readonly TextBlock _status = new();

    public MultiAccountWindow(string configPath)
    {
        _store = new MultiAccountStore(configPath);
        Strings.Language = _store.Config.Ui.Language;

        Title = "Multi Copilot Buddy — " + Strings.Accounts;
        Width = 720;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var addAccountButton = new Button { Content = Strings.AddAccount, Padding = new Thickness(8, 4, 8, 4) };
        var passwordButton = new Button { Content = Strings.SetPassword, Padding = new Thickness(8, 4, 8, 4) };
        var addCharButton = new Button { Content = Strings.AddCharacter, Padding = new Thickness(8, 4, 8, 4) };
        var hideCharButton = new Button { Content = Strings.HideChar, Padding = new Thickness(8, 4, 8, 4) };
        var dashboardButton = new Button { Content = Strings.Dashboard, Padding = new Thickness(8, 4, 8, 4) };
        var sessionsButton = new Button { Content = Strings.Sessions, Padding = new Thickness(8, 4, 8, 4) };

        addAccountButton.Click += (_, _) => OnAddAccount();
        passwordButton.Click += (_, _) => OnSetPassword();
        addCharButton.Click += (_, _) => OnAddCharacter();
        hideCharButton.Click += (_, _) => OnHideCharacter();
        dashboardButton.Click += (_, _) => new DashboardWindow(_store).Show();
        sessionsButton.Click += (_, _) => new SessionsWindow(_store).Show();

        _accountsList.SelectionChanged += (_, _) => RefreshCharacters();
        _status.Margin = new Thickness(0, 8, 0, 0);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
        };
        foreach (var button in new[] { addAccountButton, passwordButton, addCharButton, hideCharButton, dashboardButton, sessionsButton })
        {
            buttons.Children.Add(button);
            buttons.Children.Add(new Border { Width = 8 });
        }

        var grid = new Grid
        {
            Margin = new Thickness(12),
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() },
        };
        var accountsHost = new Border { BorderBrush = SystemColors.ControlDarkBrush, BorderThickness = new Thickness(1), Child = _accountsList };
        var charactersHost = new Border { BorderBrush = SystemColors.ControlDarkBrush, BorderThickness = new Thickness(1), Child = _charactersList };
        Grid.SetColumn(accountsHost, 0);
        Grid.SetColumn(charactersHost, 1);
        grid.Children.Add(accountsHost);
        grid.Children.Add(charactersHost);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        root.Children.Add(grid);
        Content = root;

        RefreshAll();
    }

    private AccountModel? SelectedAccount => _accountsList.SelectedItem as AccountModel;

    private CharacterModel? SelectedCharacter => _charactersList.SelectedItem as CharacterModel;

    private void RefreshAll()
    {
        _accountsList.ItemsSource = null;
        _accountsList.ItemsSource = _store.Config.Accounts.OrderBy(a => a.Order).ToList();
        RefreshCharacters();
    }

    private void RefreshCharacters()
    {
        var account = SelectedAccount;
        _charactersList.ItemsSource = account?.Characters.OrderBy(c => c.Order).ToList();
        _status.Text = $"Аккаунтов: {_store.Config.Accounts.Count}, конфиг: {_store.FilePath}";
    }

    private void OnAddAccount()
    {
        var login = Prompt("Логин аккаунта");
        if (string.IsNullOrWhiteSpace(login))
        {
            return;
        }

        var wowPath = Prompt("Путь к Wow.exe", @"C:\Games\WoW 3.3.5a\Wow.exe") ?? string.Empty;
        var realm = Prompt("Реалм", _store.Config.Accounts.Count > 0 ? _store.Config.Accounts[0].Realm : string.Empty) ?? string.Empty;
        var password = PromptPassword($"Пароль для '{login}'");

        try
        {
            _store.AddAccount(login, password, wowPath, realm);
        }
        catch (InvalidOperationException ex)
        {
            _ = MessageBox.Show(this, ex.Message, "MCB", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _store.Save();
        RefreshAll();
    }

    private void OnSetPassword()
    {
        var account = SelectedAccount;
        if (account is null)
        {
            return;
        }

        var password = PromptPassword($"Новый пароль для '{account.Login}'");
        account.SetPassword(password);
        _store.Save();
        RefreshAll();
    }

    private void OnAddCharacter()
    {
        var account = SelectedAccount;
        if (account is null)
        {
            _ = MessageBox.Show(this, "Выберите аккаунт.", "MCB", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = Prompt("Имя персонажа");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var charClass = Prompt("Класс (PALADIN/DRUID/…)", string.Empty) ?? string.Empty;
        _store.UpsertCharacter(account, name, string.Empty, charClass);
        _store.Save();
        RefreshAll();
    }

    private void OnHideCharacter()
    {
        var account = SelectedAccount;
        var character = SelectedCharacter;
        if (account is null || character is null)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"Скрыть '{character.Name}'? Скрытые персонажи не возвращаются при автопополнении.",
                "MCB", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _ = _store.HideCharacter(account, character.Name);
        _store.Save();
        RefreshAll();
    }

    // ---------- простые диалоги ввода ----------

    private static string? Prompt(string title, string? initial = null)
    {
        var input = new TextBox { Text = initial ?? string.Empty, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 2, 16, 2) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 2, 16, 2) };

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = new StackPanel { Margin = new Thickness(12), Children = { input, ok } },
        };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttonRow.Children.Add(ok);
        buttonRow.Children.Add(cancel);
        ((StackPanel)dialog.Content).Children.Add(buttonRow);

        bool confirmed = false;
        ok.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        input.Focus();

        bool? result = dialog.ShowDialog();
        return (result == true && confirmed) ? input.Text : null;
    }

    private static string PromptPassword(string title)
    {
        var result = Prompt(title);
        return result ?? string.Empty;
    }
}
