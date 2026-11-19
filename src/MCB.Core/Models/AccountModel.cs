using MCB.Core.Security;

namespace MCB.Core.Models;

/// <summary>
/// Аккаунт игрового сервера. Пароль хранится только как DPAPI-блоб (base64),
/// открытый текст никогда не попадает в Settings\MultiAccount.json.
/// </summary>
public sealed class AccountModel
{
    /// <summary>Логин (уникальный ключ аккаунта, сравнение без регистра).</summary>
    public string Login { get; set; } = string.Empty;

    /// <summary>Base64 DPAPI-блоб (CryptProtectData, CurrentUser).</summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    /// <summary>Путь к Wow.exe этого аккаунта (серверы с разными папками клиента).</summary>
    public string WowPath { get; set; } = string.Empty;

    /// <summary>Реалм/сервер по умолчанию (передаётся в autologin.json как realmname).</summary>
    public string Realm { get; set; } = string.Empty;

    /// <summary>Realmlist сервера (например "login.wowcircle.com") — DLL выставляет его клиенту.</summary>
    public string Realmlist { get; set; } = string.Empty;

    /// <summary>TOTP-секрет 2FA, DPAPI-блоб (base64). Пусто = 2FA не используется.</summary>
    public string EncryptedTotp { get; set; } = string.Empty;

/// <summary>
/// Персонаж по умолчанию: launch без явного имени входит в мир этим
/// персонажем (DLL выбирает слот по имени из autologin.json). Пусто —
/// только логин, стоп на выборе персонажа.
/// </summary>
public string DefaultCharacter { get; set; } = string.Empty;

    /// <summary>Момент последнего запуска клиента (UTC) — для cooldown запусков.</summary>
    public DateTime LastLaunchUtc { get; set; } = DateTime.MinValue;

    public string GetTotpSecret() => string.IsNullOrEmpty(EncryptedTotp)
        ? string.Empty
        : DpapiProtector.Unprotect(EncryptedTotp);

    public void SetTotpSecret(string secret) => EncryptedTotp =
        string.IsNullOrEmpty(secret) ? string.Empty : DpapiProtector.Protect(secret);

    /// <summary>Порядок в UI (порядок первых появлений, drag-n-drop).</summary>
    public int Order { get; set; }

    /// <summary>Расписание ботинга (Этап 6). null = круглосуточно.</summary>
    public ScheduleOptions Schedule { get; set; } = new();

    public List<CharacterModel> Characters { get; set; } = new();

    public string PasswordEncrypted => EncryptedPassword;

    /// <summary>Пароль открытым текстом (расшифровка DPAPI). Кидаёт, если пароль не задан.</summary>
    public string GetPassword() => DpapiProtector.Unprotect(EncryptedPassword);

    public void SetPassword(string plainText) => EncryptedPassword = DpapiProtector.Protect(plainText);

    public bool HasPassword => !string.IsNullOrEmpty(EncryptedPassword);
}
