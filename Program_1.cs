// Лабораторна робота №1, варіант 7: Сервер часу та часових поясів.
// Запуск:
//   dotnet run -- server [tcpPort=9000] [udpPort=9001] [lossEvery=10]
//   dotnet run -- tcp-client [host=127.0.0.1] [port=9000]
//   dotnet run -- udp-client [host=127.0.0.1] [port=9001]

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("Використання: dotnet run -- <server|tcp-client|udp-client> [параметри]");
    return;
}

string Arg(int i, string def) => args.Length > i ? args[i] : def;

switch (args[0].ToLowerInvariant())
{
    case "server":
        await TimeServer.RunAsync(int.Parse(Arg(1, "9000")), int.Parse(Arg(2, "9001")), int.Parse(Arg(3, "10")));
        break;
    case "tcp-client":
        await TcpClientApp.RunAsync(Arg(1, "127.0.0.1"), int.Parse(Arg(2, "9000")));
        break;
    case "udp-client":
        UdpClientApp.Run(Arg(1, "127.0.0.1"), int.Parse(Arg(2, "9001")));
        break;
    default:
        Console.WriteLine("Невідомий режим: " + args[0]);
        break;
}
