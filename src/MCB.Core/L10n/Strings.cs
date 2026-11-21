namespace MCB.Core.L10n;

/// <summary>
/// Локализация UI менеджера (Этап 4): словарь ru/en. CLI-вывод остаётся
/// русским (консоль), переводится GUI (MCB.Manager). Язык хранится в
/// MultiAccount.json → Ui.Language.
/// </summary>
public static class Strings
{
    public static string Language { get; set; } = "ru";

    private static bool En => string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);

    private static string T(string ru, string en) => En ? en : ru;

    public static string Dashboard => T("Дашборд", "Dashboard");
    public static string Accounts => T("Аккаунты", "Accounts");
    public static string Characters => T("Персонажи", "Characters");
    public static string Account => T("Аккаунт", "Account");
    public static string Character => T("Персонаж", "Character");
    public static string Class => T("Класс", "Class");
    public static string Level => T("Ур.", "Lvl");
    public static string Gold => T("Золото", "Gold");
    public static string Gearscore => T("ГС", "GS");
    public static string ItemLevel => T("ilvl", "ilvl");
    public static string Zone => T("Зона", "Zone");
    public static string Updated => T("Обновлено", "Updated");
    public static string Refresh => T("Обновить", "Refresh");
    public static string ScanRoster => T("Сканировать рорстер", "Scan roster");
    public static string Launch => T("Запустить", "Launch");
    public static string Stop => T("Остановить", "Stop");
    public static string HarvestRun => T("Собрать голд (harvest)", "Harvest gold");
    public static string LanguageToggle => T("English", "Русский");
    public static string Ready => T("Готово.", "Ready.");
    public static string Scanning => T("Сканирую SavedVariables…", "Scanning SavedVariables…");
    public static string HarvestRunning => T("Harvest идёт…", "Harvest running…");
    public static string HarvestDone => T("Harvest завершён: ", "Harvest finished: ");
    public static string HarvestConfirm => T(
        "Запустить harvest по всем аккаунтам?\nКлиенты будут запускаться по очереди, клиент соберёт голд и закроется сам.",
        "Start harvest for all accounts?\nClients will launch one by one; each collects gold and quits by itself.");
    public static string NoAccounts => T("Нет аккаунтов.", "No accounts.");
    public static string ConfigLabel => T("Конфиг: ", "Config: ");
    public static string AccountsCount => T("Аккаунтов: ", "Accounts: ");
    public static string AddAccount => T("Добавить аккаунт", "Add account");
    public static string SetPassword => T("Пароль…", "Password…");
    public static string AddCharacter => T("Добавить персонажа", "Add character");
    public static string HideChar => T("Скрыть (tombstone)", "Hide (tombstone)");
    public static string StopAll => T("Остановить всё", "Stop all");
    public static string Sessions => T("Сессии", "Sessions");
    public static string LaunchAllFirst => T("Запустить всё (лидер первым)", "Launch all (leader first)");
    public static string LaunchSelectedFirst => T("Запустить выбранные (лидер первым)", "Launch selected (leader first)");
    public static string RestartBots => T("Перезапустить боты", "Restart bots");
    public static string BotLauncher => T("Bot Launcher", "Bot Launcher");
    public static string MakeLeader => T("Сделать лидером", "Make leader");
    public static string LeaderRole => T("лидер", "leader");
    public static string Role => T("Роль", "Role");
}
