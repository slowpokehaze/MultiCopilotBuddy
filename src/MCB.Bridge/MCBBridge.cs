using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Styx;
using Styx.Helpers;
using Styx.Plugins.PluginClass;
using Styx.WoWInternals;

namespace MCB.Bridge
{
    /// <summary>
    /// MCBBridge — мост между MCB-менеджером и CopilotBuddy (Этап 2 плана).
    /// Слушает 127.0.0.1:&lt;порт сессии&gt;, команды MCB ставит в очередь,
    /// исполняет Lua на пульсе плагина (главный поток бота — безопасно).
    ///
    /// Команды:
    ///   PING                → PONG &lt;имя&gt;|&lt;реалм&gt;|&lt;есть ли WowManagerSwitchCharacter&gt;
    ///   SWITCH &lt;имя&gt;        → быстрая смена персонажа: WowManagerSwitchCharacter('имя') + Logout()
    ///                         (DLL AwesomeWotlk после логаута сама заводит нового персонажа
    ///                          без перезапуска клиента; TTL запроса — 60 с)
    /// Ответы: "OK ..." / "ERR ..." — одна строка UTF-8.
    ///
    /// Конфиг: Plugins/MCBBridge/sessions.json — [{"port":47201,"wowPid":1234,...}];
    /// запись выбирается по PID приаттаченного процесса WoW.
    /// </summary>
    public class MCBBridge : HBPlugin
    {
        private sealed class Request
        {
            public TcpClient Client;
            public string Command;
            public string Argument;
        }

        private readonly object _sync = new object();
        private readonly Queue<Request> _queue = new Queue<Request>();
        private Thread _serverThread;
        private volatile bool _stopped;
        private int _port;

        public override string Name { get { return "MCBBridge"; } }
        public override string Author { get { return "Multi Copilot Buddy"; } }
        public override Version Version { get { return new Version(1, 0, 0); } }

        public override void Initialize()
        {
            _port = ReadPortForThisProcess();
            if (_port <= 0)
            {
                Logging.Write("MCBBridge: подходящая запись не найдена в sessions.json — слушатель не запущен.");
                return;
            }

            _stopped = false;
            _serverThread = new Thread(ServerLoop) { IsBackground = true, Name = "MCBBridge" };
            _serverThread.Start();
            Logging.Write("MCBBridge v{0}: слушаю 127.0.0.1:{1}", Version, _port);
        }

        public override void Dispose()
        {
            _stopped = true;
            lock (_sync)
            {
                // Разбудить и закрыть ожидающих.
                foreach (var request in _queue)
                {
                    TryRespond(request, "ERR plugin stopped");
                }

                _queue.Clear();
            }
        }

        public override void Pulse()
        {
            Request[] batch;
            lock (_sync)
            {
                if (_queue.Count == 0)
                {
                    return;
                }

                batch = _queue.ToArray();
                _queue.Clear();
            }

            foreach (var request in batch)
            {
                Execute(request);
            }
        }

        // ---------- сервер ----------

        private void ServerLoop()
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, _port);
                listener.Start();
            }
            catch (Exception ex)
            {
                Logging.Write("MCBBridge: не удалось занять порт {0}: {1}", _port, ex.Message);
                return;
            }

            while (!_stopped)
            {
                TcpClient client = null;
                try
                {
                    if (!listener.Pending())
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    break; // listener остановлен
                }

                try
                {
                    ReadAndEnqueue(client);
                }
                catch
                {
                    TryDispose(client);
                }
            }

            try
            {
                listener.Stop();
            }
            catch
            {
            }
        }

        private void ReadAndEnqueue(TcpClient client)
        {
            string line;
            try
            {
                client.ReceiveTimeout = 3000;
                var stream = client.GetStream();
                var buffer = new byte[512];
                var sb = new StringBuilder();
                while (true)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        TryDispose(client);
                        return;
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    var text = sb.ToString();
                    var nl = text.IndexOf('\n');
                    if (nl >= 0)
                    {
                        line = text.Substring(0, nl).Trim();
                        break;
                    }

                    if (sb.Length > 400)
                    {
                        TryDispose(client);
                        return;
                    }
                }
            }
            catch
            {
                TryDispose(client);
                return;
            }

            if (line.Length == 0)
            {
                TryDispose(client);
                return;
            }

            var space = line.IndexOf(' ');
            var command = space > 0 ? line.Substring(0, space).ToUpperInvariant() : line.ToUpperInvariant();
            var argument = space > 0 ? line.Substring(space + 1).Trim() : string.Empty;

            lock (_sync)
            {
                _queue.Enqueue(new Request { Client = client, Command = command, Argument = argument });
            }
        }

        // ---------- исполнение на главном потоке ----------

        private void Execute(Request request)
        {
            try
            {
                switch (request.Command)
                {
                    case "PING":
                        TryRespond(request, string.Format(
                            "PONG {0}|{1}|{2}",
                            StyxWoW.Me != null ? StyxWoW.Me.Name : "?",
                            GetRealmName(),
                            HasSwitchGlobal() ? "1" : "0"));
                        break;

                    case "SWITCH":
                        HandleSwitch(request);
                        break;

                    default:
                        TryRespond(request, "ERR unknown command: " + request.Command);
                        break;
                }
            }
            catch (Exception ex)
            {
                TryRespond(request, "ERR " + ex.Message);
            }
        }

        private void HandleSwitch(Request request)
        {
            var name = request.Argument;

            if (!ObjectManager.IsInGame || StyxWoW.Me == null)
            {
                TryRespond(request, "ERR not in game");
                return;
            }

            if (!HasSwitchGlobal())
            {
                TryRespond(request, "ERR WowManagerSwitchCharacter недоступен — Wow.exe не пропатчен или DLL старая");
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                TryRespond(request, "ERR SWITCH требует имя персонажа");
                return;
            }

            // Семантика wow-autologin: запрос действует только на ближайший логаут
            // (TTL 60 с), после Logout() DLL сама заводит запрошенного персонажа.
            var lua = string.Format(
                "WowManagerSwitchCharacter('{0}') Logout()",
                name.Replace("\\", "\\\\").Replace("'", "\\'"));
            Lua.GetReturnValues(lua);
            Logging.Write("MCBBridge: SWITCH → '{0}' (логаут и вход без перезапуска клиента)", name);
            TryRespond(request, "OK switching to " + name);
        }

        private static bool HasSwitchGlobal()
        {
            try
            {
                var values = Lua.GetReturnValues("return type(WowManagerSwitchCharacter) == 'function' and '1' or '0'");
                return values != null && values.Count > 0 && values[0] == "1";
            }
            catch
            {
                return false;
            }
        }

        private static string GetRealmName()
        {
            try
            {
                var values = Lua.GetReturnValues("return GetRealmName()");
                return values != null && values.Count > 0 ? values[0] : "?";
            }
            catch
            {
                return "?";
            }
        }

        // ---------- ответы и конфиг ----------

        private static void TryRespond(Request request, string message)
        {
            try
            {
                var stream = request.Client.GetStream();
                var bytes = Encoding.UTF8.GetBytes(message + "\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch
            {
            }
            finally
            {
                TryDispose(request.Client);
            }
        }

        private static void TryDispose(TcpClient client)
        {
            try
            {
                if (client != null)
                {
                    client.Close();
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// sessions.json: [{"port":47201,"wowPid":1234,"account":"...","session":"..."}].
        /// Выбираем запись по PID приаттаченного WoW; единственную — без проверки.
        /// </summary>
        private int ReadPortForThisProcess()
        {
            try
            {
                var path = Path.Combine(Logging.ApplicationPath, "Plugins", "MCBBridge", "sessions.json");
                if (!File.Exists(path))
                {
                    return 0;
                }

                var wowPid = 0;
                try
                {
                    if (ObjectManager.WoWProcess != null)
                    {
                        wowPid = ObjectManager.WoWProcess.Id;
                    }
                }
                catch
                {
                }

                var json = File.ReadAllText(path);
                var portByPid = new Dictionary<int, int>();
                var firstPort = 0;
                foreach (Match entry in Regex.Matches(json, "\\{[^{}]*\\}"))
                {
                    var port = MatchInt(entry.Value, "port");
                    var pid = MatchInt(entry.Value, "wowPid");
                    if (port <= 0)
                    {
                        continue;
                    }

                    if (firstPort == 0)
                    {
                        firstPort = port;
                    }

                    if (pid > 0 && !portByPid.ContainsKey(pid))
                    {
                        portByPid[pid] = port;
                    }
                }

                if (wowPid > 0 && portByPid.ContainsKey(wowPid))
                {
                    return portByPid[wowPid];
                }

                // PID ещё неизвестен (раннее чтение) или один процесс — берём единственную запись.
                return portByPid.Count == 1 ? portByPid.Values.First() : firstPort;
            }
            catch
            {
                return 0;
            }
        }

        private static int MatchInt(string text, string field)
        {
            var match = Regex.Match(text, "\"" + field + "\"\\s*:\\s*(\\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value) : 0;
        }
    }
}
