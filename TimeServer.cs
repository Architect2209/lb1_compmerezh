using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// Сервер часу: TCP (GET_TIME) + UDP (Time Sync).
///
/// Прикладний протокол (текстовий, кожне повідомлення — один рядок, що закінчується '\n'):
///   TCP  запит:   GET_TIME Europe/Kyiv | HELP | EXIT
///   TCP  відповідь: OK 2026-10-24 14:21:00 Europe/Kyiv (UTC+03:00)
///                   ERR 404 UNKNOWN_TIMEZONE | ERR 400 UNKNOWN_COMMAND | BYE
///   UDP  запит:   TIME_SYNC  або  Packet #N
///   UDP  відповідь: TIME &lt;unix_ms&gt;   або   ACK Packet #N TIME &lt;unix_ms&gt;
/// </summary>
static class TimeServer
{
    static readonly Stopwatch Uptime = Stopwatch.StartNew();
    static readonly object LogLock = new();
    static int _activeClients;

    static void Log(string msg)
    {
        lock (LogLock) Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }

    public static async Task RunAsync(int tcpPort, int udpPort, int lossEvery)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Log($"Сервер запущено. TCP:{tcpPort}, UDP:{udpPort}, відкидання кожного {lossEvery}-го UDP-пакета (0 = вимкнено). Ctrl+C — зупинка.");
        await Task.WhenAll(RunTcpAsync(tcpPort, cts.Token), RunUdpAsync(udpPort, lossEvery, cts.Token));
        Log("Сервер зупинено.");
    }

    // ───────────────────────── TCP ─────────────────────────

    static async Task RunTcpAsync(int port, CancellationToken ct)
    {
        // socket() + bind() + listen() виконуються всередині TcpListener
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // accept(): повертає НОВИЙ сокет для конкретного клієнта
                TcpClient client = await listener.AcceptTcpClientAsync(ct);
                // Кожен клієнт обробляється в окремій задачі (Task) -> паралельна робота
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var ep = (IPEndPoint)client.Client.RemoteEndPoint!;
        int n = Interlocked.Increment(ref _activeClients);
        Log($"[TCP] Підключився клієнт {ep.Address}, ефемерний порт {ep.Port}, час {DateTime.Now:yyyy-MM-dd HH:mm:ss} (активних: {n})");

        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                string? line;
                // Межі повідомлень (framing) задаються символом '\n' -> ReadLine
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    Log($"[TCP] {ep.Port} -> '{line}'");
                    if (line.Trim().Equals("EXIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("BYE");
                        break;
                    }
                    string reply = ProcessCommand(line);
                    await writer.WriteLineAsync(reply);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { Log($"[TCP] З'єднання з {ep.Port} обірвано клієнтом."); }
        finally
        {
            int left = Interlocked.Decrement(ref _activeClients);
            Log($"[TCP] Клієнт {ep.Address}:{ep.Port} відключився (активних: {left})");
        }
    }

    static string ProcessCommand(string line)
    {
        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "ERR 400 EMPTY_COMMAND";

        switch (parts[0].ToUpperInvariant())
        {
            case "GET_TIME":
                string zoneId = parts.Length > 1 ? parts[1].Trim() : "UTC";
                var tz = FindZone(zoneId);
                if (tz == null) return "ERR 404 UNKNOWN_TIMEZONE";
                DateTime utc = DateTime.UtcNow;
                DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
                TimeSpan off = tz.GetUtcOffset(utc);
                string sign = off < TimeSpan.Zero ? "-" : "+";
                return $"OK {local:yyyy-MM-dd HH:mm:ss} {zoneId} (UTC{sign}{off.Duration():hh\\:mm})";

            case "HELP":
                return "OK Commands: GET_TIME <Zone> (e.g. Europe/Kyiv), HELP, EXIT";

            default:
                return "ERR 400 UNKNOWN_COMMAND";
        }
    }

    static TimeZoneInfo? FindZone(string id)
    {
        // Europe/Kyiv у старих базах tzdata називається Europe/Kiev — пробуємо обидва
        string[] candidates = id.Equals("Europe/Kyiv", StringComparison.OrdinalIgnoreCase)
            ? new[] { "Europe/Kyiv", "Europe/Kiev", "FLE Standard Time" }
            : new[] { id };
        foreach (var c in candidates)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(c); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return null;
    }

    // ───────────────────────── UDP ─────────────────────────

    static async Task RunUdpAsync(int port, int lossEvery, CancellationToken ct)
    {
        // socket(SOCK_DGRAM) + bind(); listen()/accept() для UDP не потрібні (немає з'єднання)
        using var udp = new UdpClient(port);
        int counter = 0;
        int dropped = 0;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }          // recvfrom()
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            string msg = Encoding.UTF8.GetString(r.Buffer).Trim();
            long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string? reply = null;

            if (msg.StartsWith("Packet #", StringComparison.OrdinalIgnoreCase))
            {
                counter++;
                // Моделювання втрати пакетів: відкидаємо кожен N-й (відповіді не буде)
                if (lossEvery > 0 && counter % lossEvery == 0)
                {
                    dropped++;
                    Log($"[UDP] {r.RemoteEndPoint} '{msg}' -> ВІДКИНУТО (імітація втрати, всього відкинуто: {dropped})");
                    continue;
                }
                reply = $"ACK {msg} TIME {unixMs}";
            }
            else if (msg.Equals("TIME_SYNC", StringComparison.OrdinalIgnoreCase))
            {
                reply = $"TIME {unixMs}";
            }
            else
            {
                reply = "ERR 400 UNKNOWN_COMMAND";
            }

            Log($"[UDP] {r.RemoteEndPoint} '{msg}' -> '{reply}'");
            byte[] data = Encoding.UTF8.GetBytes(reply);
            await udp.SendAsync(data, data.Length, r.RemoteEndPoint);   // sendto()
        }
    }
}
