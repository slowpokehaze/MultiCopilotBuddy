using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MCB.Core.Bridge;

/// <summary>
/// Клиент MCBBridge-плагина (Этап 2): localhost TCP, одна строка-команда →
/// одна строка-ответ ("OK ..." / "ERR ..."). Команды: PING, SWITCH &lt;имя&gt;.
/// Выполняется в процессе CopilotBuddy на главном потоке бота (Lua безопасен).
/// </summary>
public static class BotBridgeClient
{
    /// <summary>Отправляет команду, возвращает ответ без завершающего \n. null = связи нет.</summary>
    public static string? Query(int port, string command, int timeoutMs = 5000)
    {
        if (port <= 0)
        {
            return null;
        }

        try
        {
            using var client = new TcpClient();
            var connect = client.BeginConnect(IPAddress.Loopback, port, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(timeoutMs) || !client.Connected)
            {
                return null;
            }

            using var stream = client.GetStream();
            stream.ReadTimeout = timeoutMs;
            stream.WriteTimeout = timeoutMs;

            var request = Encoding.UTF8.GetBytes(command + "\n");
            stream.Write(request, 0, request.Length);

            var sb = new StringBuilder();
            var buffer = new byte[512];
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var text = sb.ToString();
                var nl = text.IndexOf('\n');
                if (nl >= 0)
                {
                    return text.Substring(0, nl).TrimEnd('\r');
                }
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd('\r') : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Статус сессии через мост: имя|реалм|есть ли switch. null = мост недоступен.</summary>
    public static string? Ping(int port) => Query(port, "PING");

    /// <summary>Разбор ответа PING: (имя, реалм, есть ли WowManagerSwitchCharacter).</summary>
    public static (string Name, string Realm, bool SwitchAvailable)? ParsePing(string? response)
    {
        if (response is null || !response.StartsWith("PONG ", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = response.Substring(5).Split('|');
        return (
            parts.Length > 0 ? parts[0] : "?",
            parts.Length > 1 ? parts[1] : "?",
            parts.Length > 2 && parts[2] == "1");
    }

    /// <summary>Быстрая смена персонажа через мост. Возвращает ответ плагина (null = недоступен).</summary>
    public static string? SwitchCharacter(int port, string characterName)
        => Query(port, "SWITCH " + characterName.Trim());
}
