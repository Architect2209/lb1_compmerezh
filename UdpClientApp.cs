using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

/// <summary>UDP-клієнт: Time Sync + тест 100 датаграм із таймаутом 1 с та підрахунком Packet Loss Ratio.</summary>
static class UdpClientApp
{
    public static void Run(string host, int port)
    {
        using var udp = new UdpClient();
        udp.Connect(host, port);
        udp.Client.ReceiveTimeout = 1000;   // Socket Timeout = 1 секунда

        // ── 1. Time Sync: одна датаграма TIME_SYNC, оцінка зсуву годинника ──
        try
        {
            var sw = Stopwatch.StartNew();
            long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Send(udp, "TIME_SYNC");
            string reply = Receive(udp);
            sw.Stop();
            long serverMs = long.Parse(reply.Split(' ')[1]);
            long t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long offset = serverMs - (t0 + t1) / 2;
            Console.WriteLine($"[TIME_SYNC] Відповідь: '{reply}'");
            Console.WriteLine($"[TIME_SYNC] RTT = {sw.ElapsedMilliseconds} мс, зсув годинника клієнта відносно сервера ≈ {offset} мс");
            Console.WriteLine($"[TIME_SYNC] UNIX timestamp сервера: {serverMs / 1000} с ({DateTimeOffset.FromUnixTimeMilliseconds(serverMs):u})");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
        {
            Console.WriteLine("[TIME_SYNC] Таймаут — сервер не відповів.");
        }

        // ── 2. Тест втрат: 100 датаграм Packet #1 ... Packet #100 ──
        const int Total = 100;
        int received = 0, lost = 0;
        Console.WriteLine($"\nНадсилаємо {Total} датаграм (таймаут очікування відповіді — 1 с)...");

        for (int i = 1; i <= Total; i++)
        {
            string msg = $"Packet #{i}";
            Send(udp, msg);                                 // sendto()
            try
            {
                string reply = Receive(udp);                // recvfrom()
                received++;
                Console.WriteLine($"{msg}: OK  <- {reply}");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                lost++;
                Console.WriteLine($"{msg}: ТАЙМАУТ — пакет вважається втраченим");
            }
        }

        double ratio = lost * 100.0 / Total;
        Console.WriteLine("\n========== ПІДСУМОК ==========");
        Console.WriteLine($"Надіслано: {Total}, отримано відповідей: {received}, втрачено: {lost}");
        Console.WriteLine($"Packet Loss Ratio = {ratio:F1} %");
    }

    static void Send(UdpClient udp, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text);
        udp.Send(data, data.Length);
    }

    static string Receive(UdpClient udp)
    {
        System.Net.IPEndPoint? ep = null;
        byte[] data = udp.Receive(ref ep);
        return Encoding.UTF8.GetString(data);
    }
}
