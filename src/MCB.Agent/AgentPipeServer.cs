using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MCB.Agent;

/// <summary>
/// Named-pipe сервер агента (Этап C): JSON-строки по \n, реестр из
/// integration-cb-into-mcb.md §4. Агент шлёт LOG/STATE, менеджер шлёт
/// PING/STOP. Смерть пайпа ≠ смерть агента (агент может жить без клиента —
/// «клиент мёртв, агент жив» определяется пульсом менеджера по WoW-PID).
/// </summary>
public sealed class AgentPipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly ConcurrentQueue<string> _outbox = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _writeLock = new();

    private NamedPipeServerStream? _server;
    private StreamWriter? _writer;

    /// <summary>Внешняя команда STOP (менеджер просит мягко завершиться).</summary>
    public event Action? StopRequested;

    public AgentPipeServer(string pipeName)
    {
        _pipeName = pipeName;
    }

    public string PipeName => _pipeName;

    /// <summary>Запускает поток accept-loop (одновременно один клиент; реконнект разрешён).</summary>
    public void Start()
    {
        var thread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "MCBAgent.Pipe",
        };
        thread.Start();
    }

    /// <summary>Ставит строку в исходящую очередь (без клиента — копится и теряется при переполнении).</summary>
    public void Send(string line)
    {
        if (_outbox.Count > 4096)
        {
            _outbox.TryDequeue(out _); // дропаем старое, живой LOG-поток не должен расти бесконечно
        }

        _outbox.Enqueue(line);

        // Если клиент уже подключён — выталкиваем сразу.
        lock (_writeLock)
        {
            if (_writer is not null)
            {
                DrainTo(_writer);
            }
        }
    }

    /// <summary>STATE {"status":...,"detail":...} — реестр §4.</summary>
    public void SendState(string status, string detail = "")
        => Send(JsonSerializer.Serialize(new StateMessage(status, detail)));

    /// <summary>LOG {"level":...,"msg":...}.</summary>
    public void SendLog(string level, string msg)
        => Send(JsonSerializer.Serialize(new LogMessage(level, msg)));

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                // WaitForConnection с отменой через асинхронную обёртку.
                var connectTask = _server.WaitForConnectionAsync(_cts.Token);
                try
                {
                    connectTask.Wait(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using (_server)
                {
                    // Строгий request-response (как BotBridgeClient): клиент первый
                    // пишет (PING/POLL/STOP), сервер только отвечает. Сервер не
                    // пишет без запроса — иначе обе стороны могут залипнуть в
                    // Write одновременно (найдено в selftest).
                    // leaveOpen: dispose читателя не должен рвать пайп под писателем.
                    using var reader = new StreamReader(_server, Encoding.UTF8, false, 1024, leaveOpen: true);
                    _writer = new StreamWriter(_server, Encoding.UTF8)
                    {
                        AutoFlush = true,
                        NewLine = "\n",
                    };

                    ReadLoop(reader);

                    lock (_writeLock)
                    {
                        _writer.Dispose();
                        _writer = null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine("MCBAgent pipe error: " + ex.Message); } catch { }
            }
            finally
            {
                try { _server?.Dispose(); } catch { }
                _server = null;
            }

            if (!_cts.IsCancellationRequested)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// Блокирующее чтение команд текущего клиента (PING / POLL / STOP / …).
    /// Клиент всегда пишет первым, сервер отвечает — см. AgentClient.
    /// </summary>
    private void ReadLoop(StreamReader reader)
    {
        while (!_cts.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = reader.ReadLine();
            }
            catch
            {
                break; // клиент отвалился
            }

            if (line is null)
            {
                break;
            }

            var command = line.Trim();
            if (command.Length == 0)
            {
                continue;
            }

            if (string.Equals(command, "PING", StringComparison.OrdinalIgnoreCase))
            {
                lock (_writeLock)
                {
                    _writer?.Write("PONG\n");
                }
                continue;
            }

            if (string.Equals(command, "POLL", StringComparison.OrdinalIgnoreCase))
            {
                // Вычитать всё накопленное (STATE/LOG) в текущее подключение.
                lock (_writeLock)
                {
                    DrainTo(_writer);
                }
                continue;
            }

            if (string.Equals(command, "STOP", StringComparison.OrdinalIgnoreCase))
            {
                StopRequested?.Invoke();
                continue;
            }

            // Неизвестные команды отвечаем ERR — но пайп не рвём.
            lock (_writeLock)
            {
                _writer?.Write("ERR unknown command\n");
            }
        }
    }

    private void DrainTo(StreamWriter writer)
    {
        while (_outbox.TryDequeue(out var line))
        {
            writer.Write(line);
            writer.Write('\n');
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _server?.Dispose(); } catch { }
    }

    public sealed record StateMessage(string Status, string Detail);

    public sealed record LogMessage(string Level, string Msg);
}
