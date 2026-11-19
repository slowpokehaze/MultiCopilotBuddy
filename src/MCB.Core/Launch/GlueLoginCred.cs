using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MCB.Core.Launch;

/// <summary>
/// Режим SlowEnter (бой 2026-11-19): DLL делает только логин, а вход в мир
/// выполняет аддон MCBGlueLogin с человеческими паузами. Найдено: при
/// DLL-автоматизации (select+enter ~1.5 с после авторизации) сервер WoW Circle
/// принудительно логаутит персонажа через ~0.5 с после входа в мир (ручной вход
/// тем же клиентом — чист; сервер видит только пакеты, разница лишь в скорости).
///
/// Аддон грузится уже на экране выбора персонажа (после аутентификации — список
/// включённых аддонов хранится в WTF аккаунта), так что DLL доходит до экрана
/// выбора и останавливается, дальше работает аддон.
///
/// Cred.lua больше не нужен. Файл экспорта рорстера (&lt;login&gt;__&lt;hash&gt;.json,
/// пишет DLL при получении списка персонажей) служит сигналом «клиент
/// залогинился» — им сериализуются мульти-запуски (WaitLoggedIn).
/// </summary>
public static class GlueLoginCred
{
    private static readonly Regex CharListExportPattern =
        new(@"^.+__[0-9a-fA-F]{8}\.json$", RegexOptions.Compiled);

    public static string AddonDirectory(string wowDirectory)
        => Path.Combine(wowDirectory, "Interface", "AddOns", "MCBGlueLogin");

    /// <summary>Создаёт/освежает аддон MCBGlueLogin (TOC + логика).</summary>
    public static void EnsureAddon(string wowDirectory)
    {
        var dir = AddonDirectory(wowDirectory);
        Directory.CreateDirectory(dir);

        // Остаток старой схемы (пароль в Cred.lua) — больше не нужен.
        var staleCred = Path.Combine(dir, "Cred.lua");
        if (File.Exists(staleCred))
        {
            File.Delete(staleCred);
        }

        var tocPath = Path.Combine(dir, "MCBGlueLogin.toc");
        var luaPath = Path.Combine(dir, "GlueLogin.lua");
        var toc =
            "## Interface: 30300\n" +
            "## Title: MCB Glue Login\n" +
            "## Author: Multi Copilot Buddy\n" +
            "## Notes: Slow character entry (server force-logouts too-fast entries)\n" +
            "GlueLogin.lua\n";
        File.WriteAllText(tocPath, toc, new UTF8Encoding(false));

        var lua = """
            -- Slow character entry for Multi Copilot Buddy (battle 2026-11-19):
            -- the DLL's automated select+enter (1.5s after auth) makes WoW Circle
            -- force-logout the character right after the world loads; human pacing
            -- is fine. The DLL now stops at the character select; this addon picks
            -- the character and enters the world with human-like pauses.
            --
            -- Addons load at the CHARACTER SELECT (the account is authenticated by
            -- then), so this runs exactly where the DLL stops.
            -- The WowManagerSwitchCharacter("") call at load writes
            -- "[switch] requested ''" to autologin.log - the manager uses it as a
            -- "the addon is alive" marker.

            local tried = false
            local selected = false
            local idle = 0

            local function cfgValue(key)
                local cfg = MCBuddyConfig
                return cfg and cfg[key] or nil
            end

            local function findIndex(name)
                local n = GetNumCharacters and GetNumCharacters() or 0
                for i = 1, n do
                    local nm = GetCharacterInfo and GetCharacterInfo(i)
                    if nm and strlower(tostring(nm)) == strlower(name) then
                        return i
                    end
                end
                return nil
            end

            if type(WowManagerSwitchCharacter) == "function" then
                WowManagerSwitchCharacter("")
            end

            local f = CreateFrame("Frame")
            f:SetScript("OnUpdate", function(_, e)
                if tried then
                    f:SetScript("OnUpdate", nil)
                    return
                end

                idle = idle + (e or 0)

                -- Char list not arrived yet (or the login screen is still up).
                local n = GetNumCharacters and GetNumCharacters() or 0
                if n <= 0 then return end

                local delaySelect = tonumber(cfgValue("enterDelaySelect")) or 8
                local delayWorld = tonumber(cfgValue("enterDelayWorld")) or 3

                if not selected then
                    if idle < delaySelect then return end

                    local idx
                    if n == 1 then
                        idx = 1                        -- единственный персонаж
                    else
                        local target = cfgValue("enterCharacter")
                        if target and target ~= "" then
                            idx = findIndex(target)
                        end
                    end

                    if not idx then
                        tried = true                   -- not our queue - don't interfere
                        f:SetScript("OnUpdate", nil)
                        return
                    end

                    selected = true
                    idle = 0
                    if CharacterSelect_SelectCharacter then
                        CharacterSelect_SelectCharacter(idx)
                    end
                    return
                end

                if idle < delayWorld then return end

                tried = true
                f:SetScript("OnUpdate", nil)
                if EnterWorld then
                    EnterWorld()
                end
            end)
            """;
        File.WriteAllText(luaPath, lua, new UTF8Encoding(false));
    }

    /// <summary>Удаляет файлы экспорта рорстера (&lt;login&gt;__&lt;hash&gt;.json) у Wow.exe.</summary>
    public static void DeleteCharListExports(string wowDirectory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(wowDirectory, "*__*.json"))
            {
                if (CharListExportPattern.IsMatch(Path.GetFileName(file)))
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
            // экспортные файлы некритичны
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Ждёт файл экспорта рорстера — он появляется, когда DLL получает список
    /// персонажей, т.е. клиент залогинился и стоит на выборе персонажа. После
    /// этого можно запускать следующего клиента (его аддон прочитает уже свой
    /// Config.lua — предыдущий клиент свой прочитал при загрузке аддонов).
    /// </summary>
    public static bool WaitLoggedIn(string wowDirectory, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (Directory.EnumerateFiles(wowDirectory, "*__*.json")
                    .Any(f => CharListExportPattern.IsMatch(Path.GetFileName(f))))
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }

            Thread.Sleep(1000);
        }

        return false;
    }
}
