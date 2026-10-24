using System.Net.Sockets;
using System.Text;

/// <summary>Консольний TCP-клієнт: читає команди з консолі, надсилає на сервер, виводить відповідь.</summary>
static class TcpClientApp
{
    public static async Task RunAsync(string host, int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port);            // connect()
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Не вдалося підключитися до {host}:{port}: {ex.Message}");
            return;
        }

        Console.WriteLine($"Підключено до {client.Client.RemoteEndPoint}, локальна адреса {client.Client.LocalEndPoint}");
        Console.WriteLine("Команди: GET_TIME Europe/Kyiv | HELP | EXIT");

        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        while (true)
        {
            Console.Write("> ");
            string? input = Console.ReadLine();
            if (input == null) break;
            if (string.IsNullOrWhiteSpace(input)) continue;

            await writer.WriteLineAsync(input);               // send()
            string? response = await reader.ReadLineAsync();  // recv()
            if (response == null)
            {
                Console.WriteLine("Сервер закрив з'єднання.");
                break;
            }
            Console.WriteLine(response);

            if (input.Trim().Equals("EXIT", StringComparison.OrdinalIgnoreCase)) break;
        }
        // close() — виконується при Dispose
        Console.WriteLine("З'єднання закрито.");
    }
}
