namespace MCB.Agent;

/// <summary>
/// Аргументы агента. Соглашения — как у живого бота (HBRelog-стиль):
/// /pid= /botname= /loadprofile= /customclass=, плюс агентские
/// --pipe= &lt;имя&gt;, --botdir=&lt;путь&gt;, --bridge-port=&lt;n&gt;.
/// Пример: MCB.Agent.dll /pid=1234 /botname=Party Bot /loadprofile=C:\...\a.xml
///         /customclass=Singular wotlk --pipe=MCBAgent_ab12cd34 --bridge-port=47201
/// </summary>
public sealed class AgentArgs
{
    public int WowPid { get; set; }

    /// <summary>Имя (фрагмент) BotBase — substring match, как в MainWindow.ApplyAutoStartConfig.</summary>
    public string BotName { get; set; } = string.Empty;

    /// <summary>Абсолютный путь к профилю для ProfileManager.LoadNew.</summary>
    public string ProfilePath { get; set; } = string.Empty;

    /// <summary>Имя combat-рутины — уходит в /customclass (обход RoutineSelectionForm, stageA §2).</summary>
    public string RoutineName { get; set; } = string.Empty;

    /// <summary>Имя named pipe. Пусто = MCBAgent_&lt;pid&gt;.</summary>
    public string PipeName { get; set; } = string.Empty;

    /// <summary>Папка бота (оригинал или MCBBots\&lt;id&gt;). Пусто = папка агента.</summary>
    public string BotDir { get; set; } = string.Empty;

    /// <summary>Порт MCBBridge-плагина на этом клиенте (пишется менеджером в sessions.json).</summary>
    public int BridgePort { get; set; }

    public static AgentArgs Parse(string[] args)
    {
        var result = new AgentArgs();

        foreach (var raw in args)
        {
            var arg = raw.Trim();
            if (arg.Length == 0)
            {
                continue;
            }

            var (key, value) = Split(arg);
            switch (key.ToLowerInvariant())
            {
                case "pid":
                    _ = int.TryParse(value, out var pid);
                    result.WowPid = pid;
                    break;
                case "botname":
                case "bot":
                    result.BotName = value;
                    break;
                case "loadprofile":
                case "profile":
                    result.ProfilePath = value;
                    break;
                case "customclass":
                case "routine":
                    result.RoutineName = value;
                    break;
                case "pipe":
                    result.PipeName = value;
                    break;
                case "botdir":
                    result.BotDir = value;
                    break;
                case "bridge-port":
                case "bridgeport":
                    _ = int.TryParse(value, out var port);
                    result.BridgePort = port;
                    break;
            }
        }

        return result;
    }

    private static (string Key, string Value) Split(string arg)
    {
        var s = arg.TrimStart('/', '-');
        var eq = s.IndexOf('=');
        if (eq < 0)
        {
            return (s, string.Empty);
        }

        return (s[..eq].Trim(), s[(eq + 1)..].Trim().Trim('"'));
    }
}
