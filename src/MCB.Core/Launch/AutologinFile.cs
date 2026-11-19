using System.Text.Json;

namespace MCB.Core.Launch;

/// <summary>Параметры логина для AwesomeWotlkLib.dll (протокол wow-autologin, проверен по исходникам).</summary>

/// <summary>Параметры логина для AwesomeWotlkLib.dll (протокол wow-autologin, проверен по исходникам).</summary>
public sealed class AutologinRequest
{
    public string Login { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Realmlist сервера, DLL выставляет его клиенту.</summary>
    public string Realmlist { get; set; } = string.Empty;

    /// <summary>Имя реалма для выбора на экране выбора реалма.</summary>
    public string RealmName { get; set; } = string.Empty;

    /// <summary>Имя персонажа для входа; пусто = только логин (экран выбора персонажа).</summary>
    public string Character { get; set; } = string.Empty;

    /// <summary>TOTP-секрет 2FA; DLL пересчитывает код в момент появления диалога.</summary>
    public string TotpSecret { get; set; } = string.Empty;
}

/// <summary>
/// autologin.json рядом с Wow.exe: DLL читает его при старте и сразу удаляет
/// (внутри пароль открытым текстом — так работает протокол). Переживший файл
/// без запущенного клиента = запуск умер рано, его надо удалять (порт
/// drop_stale_autologin_json из launcher.py).
/// </summary>
public static class AutologinFile
{
    public static string PathFor(string wowDirectory) => System.IO.Path.Combine(wowDirectory, "autologin.json");

    public static bool Exists(string wowDirectory) => File.Exists(PathFor(wowDirectory));

    /// <summary>Пишет autologin.json (UTF-8 без BOM — парсер DLL ждёт '{' первым байтом).</summary>
    public static void Write(string wowDirectory, AutologinRequest request)
    {
        var data = new Dictionary<string, string>
        {
            ["login"] = request.Login,
            ["password"] = request.Password,
            ["realmlist"] = request.Realmlist,
            ["realmname"] = request.RealmName,
            ["character"] = request.Character,
        };
        if (!string.IsNullOrEmpty(request.TotpSecret))
        {
            data["totp_secret"] = request.TotpSecret;
        }

        var json = JsonSerializer.Serialize(data);
        File.WriteAllBytes(PathFor(wowDirectory), System.Text.Encoding.UTF8.GetBytes(json));
    }

    public static void Delete(string wowDirectory)
    {
        var path = PathFor(wowDirectory);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
