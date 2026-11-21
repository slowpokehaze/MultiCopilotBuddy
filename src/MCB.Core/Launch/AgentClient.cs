using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MCB.Core.Models;

namespace MCB.Core.Launch;

/// <summary>
/// Клиент пайпа MCB.Agent (integration-cb-into-mcb.md §4). Один сеанс = короткое
/// подключение: вычитать накопленные STATE/LOG, при необходимости отправить
/// команду. Агент держит одновременно одного клиента — Pulse опрашивает сессии
/// по очереди, конфликтов нет.
/// </summary>
public static class AgentClient
{
    public sealed record AgentState(string Status, string Detail, DateTime Utc);

    /// <summary>
    /// Вычитывает все доступные сообщения агента. Обновляет Agent*-поля сессии.
    /// Возвращает true, если агент ответил (пайп подключился).
    /// Строгий request-response (как BotBridgeClient): клиент шлёт POLL, сервер
    /// отвечает накопленными STATE/LOG-строками. Чтение по PeekNamedPipe —
    /// незавершённый async-read при dispose НЕ закрывает соединение (selftest).
    /// </summary>
    public static bool Poll(SessionModel session, int connectTimeoutMs = 400)
    {
        var pipeName = AgentLauncher.PipeName(session);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            if (!WaitConnected(pipe, connectTimeoutMs))
            {
                return false;
            }

            var request = Encoding.UTF8.GetBytes("POLL\n");
            pipe.Write(request, 0, request.Length);
            pipe.Flush();

            var sb = new StringBuilder();
            var buffer = new byte[4096];
            var quietDeadline = Environment.TickCount64 + 300; // пауза без данных = вычитали всё
            while (true)
            {
                if (pipe.IsConnected && Available(pipe) > 0)
                {
                    var read = pipe.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    quietDeadline = Environment.TickCount64 + 300;
                    continue;
                }

                if (Environment.TickCount64 >= quietDeadline || !pipe.IsConnected)
                {
                    break;
                }

                Thread.Sleep(20);
            }

            foreach (var line in sb.ToString().Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                {
                    HandleLine(session, trimmed);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Живой и отвечающий агент (PING → PONG). true заодно подтверждает, что пайп поднят.</summary>
    public static bool PingAlive(SessionModel session, int connectTimeoutMs = 400)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AgentLauncher.PipeName(session), PipeDirection.InOut);
            if (!WaitConnected(pipe, connectTimeoutMs))
            {
                return false;
            }

            var request = Encoding.UTF8.GetBytes("PING\n");
            pipe.Write(request, 0, request.Length);
            pipe.Flush();

            var sb = new StringBuilder();
            var buffer = new byte[256];
            var deadline = Environment.TickCount64 + 1500;
            while (Environment.TickCount64 < deadline && pipe.IsConnected)
            {
                if (Available(pipe) > 0)
                {
                    var read = pipe.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    if (sb.ToString().Contains('\n', StringComparison.Ordinal))
                    {
                        break;
                    }

                    continue;
                }

                Thread.Sleep(20);
            }

            return sb.ToString()
                .Split('\n')
                .Any(line => line.Trim().Equals("PONG", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Мягкая остановка: агент стопит бота и выходит. false = не ответил (убивать процессом).</summary>
    public static bool SendStop(SessionModel session, int connectTimeoutMs = 1000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AgentLauncher.PipeName(session), PipeDirection.InOut);
            if (!WaitConnected(pipe, connectTimeoutMs))
            {
                return false;
            }

            var request = Encoding.UTF8.GetBytes("STOP\n");
            pipe.Write(request, 0, request.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Статус агента → состояние сессии (STATE-машина Этапа C).</summary>
    public static SessionState? MapStatus(string? status) => status switch
    {
        "ATTACHED" => SessionState.Running,
        "IN_WORLD" => SessionState.InGame,
        "BOT_RUNNING" => SessionState.Botting,
        "BOT_STOPPED" => SessionState.InGame, // бот остановился сам — клиент жив, Pulse перезапустит
        "CLIENT_DEAD" => SessionState.Crashed,
        "ERROR" => null, // обрабатывает вызывающий (SendStop + перезапуск с backoff)
        _ => null,
    };

    private static void HandleLine(SessionModel session, string line)
    {
        try
        {
            var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("status", out var statusEl))
            {
                session.AgentStatus = statusEl.GetString();
                session.AgentDetail = doc.RootElement.TryGetProperty("detail", out var detailEl)
                    ? detailEl.GetString()
                    : null;
                session.AgentStatusUtc = DateTime.UtcNow;
            }
            else if (doc.RootElement.TryGetProperty("msg", out var msgEl))
            {
                session.AgentLastLog = msgEl.GetString();
            }
        }
        catch
        {
            // мусорная строка — пропускаем
        }
    }

    private static bool WaitConnected(NamedPipeClientStream pipe, int timeoutMs)
    {
        try
        {
            // Connect(timeout) поллит до дедлайна (агент между подключениями
            // спит ~200 мс — одной попытки ConnectAsync мало).
            pipe.Connect(timeoutMs);
            return pipe.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(IntPtr handle, IntPtr buffer, int bufferSize, IntPtr bytesRead,
        out int totalBytesAvail, IntPtr bytesLeftThisMessage);

    /// <summary>
    /// Сколько байт доступно в пайпе (PeekNamedPipe). У PipeStream нет
    /// DataAvailable, а незавершённый async-read при dispose НЕ закрывает
    /// соединение — поэтому только peek + синхронные чтения.
    /// </summary>
    private static int Available(NamedPipeClientStream pipe)
    {
        try
        {
            if (!PeekNamedPipe(pipe.SafePipeHandle.DangerousGetHandle(), IntPtr.Zero, 0, IntPtr.Zero,
                    out var available, IntPtr.Zero))
            {
                return 0;
            }

            return available;
        }
        catch
        {
            return 0;
        }
    }
}
