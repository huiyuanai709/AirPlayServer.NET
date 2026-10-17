using System.Globalization;
using System.Net;
using AirPlayServer.Core;
using AirPlayServer.Core.Protocol;

Args arguments;
try
{
    arguments = Args.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Args.HelpText);
    return 2;
}

if (arguments.Help)
{
    Console.WriteLine(Args.HelpText);
    return 0;
}

if (arguments.SelfCheck)
{
    ProtocolSelfCheck.Run();
    Console.WriteLine("self-check ok");
    return 0;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

AirPlayReceiver receiver;
try
{
    receiver = await AirPlayReceiver.StartAsync(arguments.Options, shutdown.Token).ConfigureAwait(false);
}
catch (Exception ex) when (ex is ArgumentException or IOException or System.Net.Sockets.SocketException)
{
    Console.Error.WriteLine("启动失败: " + ex.Message);
    return 1;
}

await using (receiver)
{
    Console.WriteLine("AirPlayServer.NET 接收端已启动");
    Console.WriteLine("名称: " + receiver.Name);
    Console.WriteLine("设备 ID: " + receiver.DeviceId);
    Console.WriteLine("AirPlay 控制: " + receiver.AirPlayEndPoint);
    Console.WriteLine("RAOP 控制: " + receiver.RaopEndPoint);
    Console.WriteLine(receiver.DiscoveryReceiving
        ? "mDNS: 正在广播并应答 _airplay._tcp / _raop._tcp"
        : arguments.Options.EnableDiscovery
            ? "mDNS: 查询端口不可用，仅尝试发送通告"
            : "mDNS: 已关闭");
    Console.WriteLine("本切片尚未实现：视频解码与渲染、音频、FairPlay、Windows 显示路径。");
    Console.WriteLine("按 Ctrl+C 停止。");
    try
    {
        await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
    }
}

Console.WriteLine("已停止。");
return 0;

internal sealed class Args
{
    public bool Help { get; private set; }

    public bool SelfCheck { get; private set; }

    public AirPlayReceiverOptions Options { get; private set; } = new();

    public static string HelpText => """
        AirPlayServer.NET

        用法:
          AirPlayServer.Host [--name 名称] [--listen 0.0.0.0] [--airplay-port 7001] [--raop-port 5001]
                             [--no-discovery] [--seed 64位十六进制]
          AirPlayServer.Host --self-check
          AirPlayServer.Host --help

        --self-check  在当前进程里核对 X25519、Ed25519 和一次 pair-verify。
        --seed        仅存在于内存的 32 字节身份种子，不会写入磁盘。
        媒体解密、音频、视频渲染和 Windows 显示路径不在本切片内。
        """;

    public static Args Parse(string[] args)
    {
        var parsed = new Args();
        var options = new AirPlayReceiverOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            switch (token)
            {
                case "--help":
                case "-h":
                    parsed.Help = true;
                    break;
                case "--self-check":
                    parsed.SelfCheck = true;
                    break;
                case "--no-discovery":
                    options.EnableDiscovery = false;
                    break;
                case "--name":
                    options.Name = Next(args, ref i, token);
                    break;
                case "--listen":
                    options.ListenAddress = IPAddress.Parse(Next(args, ref i, token));
                    break;
                case "--airplay-port":
                    options.AirPlayPort = ushort.Parse(Next(args, ref i, token), CultureInfo.InvariantCulture);
                    break;
                case "--raop-port":
                    options.RaopPort = ushort.Parse(Next(args, ref i, token), CultureInfo.InvariantCulture);
                    break;
                case "--seed":
                    options.PairingSeed = Convert.FromHexString(Next(args, ref i, token));
                    break;
                default:
                    throw new ArgumentException("Unknown argument: " + token);
            }
        }

        parsed.Options = options;
        return parsed;
    }

    private static string Next(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException("Missing value for " + name);
        }

        index++;
        return args[index];
    }
}
