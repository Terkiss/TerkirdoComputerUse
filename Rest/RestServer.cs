using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TerkirdoComputer.Windows;

namespace TerkirdoComputer.Rest;

public static class RestServer
{
    public const int DiscoveryPort = 5005;

    public static async Task<int> RunAsync(
        AutomationService automationService,
        int port,
        string? overrideToken = null,
        string host = "127.0.0.1",
        bool allowRemote = false,
        CancellationToken cancellationToken = default)
    {
        IPAddress bindAddress;
        if (allowRemote || host == "0.0.0.0" || host == "*" || host.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            bindAddress = IPAddress.Any;
            allowRemote = true;
        }
        else if (IPAddress.TryParse(host, out var parsed))
        {
            bindAddress = parsed;
            if (!IPAddress.IsLoopback(bindAddress))
            {
                allowRemote = true;
            }
        }
        else
        {
            bindAddress = IPAddress.Loopback;
        }

        var tokenManager = new TokenManager(overrideToken, port);

        var builder = WebApplication.CreateBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        bool bindDiscoveryPort = false;

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(bindAddress, port);

            // If main port is different from DiscoveryPort, bind DiscoveryPort as handoff gateway if available
            if (port != DiscoveryPort)
            {
                if (IsPortAvailable(bindAddress, DiscoveryPort))
                {
                    try
                    {
                        options.Listen(bindAddress, DiscoveryPort);
                        bindDiscoveryPort = true;
                    }
                    catch
                    {
                        bindDiscoveryPort = false;
                    }
                }
            }
        });

        var app = builder.Build();

        RestEndpoints.MapEndpoints(app, automationService, tokenManager, port, allowRemote);

        string hostDisplay = allowRemote ? "0.0.0.0 (Remote Access Allowed)" : "127.0.0.1 (Loopback Only)";
        Console.WriteLine("==================================================================");
        Console.WriteLine("🚀 TerkirdoComputer v2.0.0 Pure Real-Time I/O Daemon Started");
        Console.WriteLine($"  - Bind Mode:      {hostDisplay}");
        Console.WriteLine($"  - Local CUA URL:  http://127.0.0.1:{port}");
        Console.WriteLine($"  - Full-Duplex WS: ws://127.0.0.1:{port}/ws/control");
        if (allowRemote)
        {
            var localIps = GetLocalIpAddresses();
            foreach (var ip in localIps)
            {
                Console.WriteLine($"  - Remote CUA URL: http://{ip}:{port}");
            }
        }
        if (port != DiscoveryPort)
        {
            if (bindDiscoveryPort)
            {
                Console.WriteLine($"  - Handoff Gateway:http://{(allowRemote ? "<host-ip>" : "127.0.0.1")}:{DiscoveryPort} (인도인접 게이트웨이 활성화)");
            }
            else
            {
                Console.WriteLine($"  - Handoff Note:   Port {DiscoveryPort} occupied; file handoff active via ~/.terkirdo/serve.url");
            }
        }
        Console.WriteLine($"  - Discovery URL:  http://{(allowRemote ? "<host-ip>" : "127.0.0.1")}:{(port != DiscoveryPort && bindDiscoveryPort ? DiscoveryPort : port)}/discover");
        Console.WriteLine($"  - Swagger UI:     http://127.0.0.1:{port}/swagger");
        Console.WriteLine($"  - Health Check:   http://127.0.0.1:{port}/health");
        Console.WriteLine($"  - OpenAPI JSON:   http://127.0.0.1:{port}/openapi.json");
        Console.WriteLine($"  - Token Saved To: {tokenManager.TokenFilePath}");
        Console.WriteLine($"  - Port Saved To:  {tokenManager.PortFilePath}");
        Console.WriteLine($"  - URL Saved To:   {tokenManager.UrlFilePath}");
        Console.WriteLine($"  - Active Token:   {tokenManager.Token}");
        Console.WriteLine("==================================================================");
        Console.WriteLine("💡 Tip: AI agents can discover active port via ~/.terkirdo/serve.url or GET /discover");
        Console.WriteLine("   Authenticate using 'Authorization: Bearer <token>' or 'X-Terkirdo-Token: <token>'.");
        Console.WriteLine();

        try
        {
            await app.StartAsync(cancellationToken);
            await app.WaitForShutdownAsync(cancellationToken);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[TerkirdoComputer Rest] Daemon stopped gracefully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TerkirdoComputer Rest] Fatal daemon error: {ex.Message}");
            return 1;
        }
    }

    public static bool IsPortAvailable(IPAddress address, int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPortAvailable(int port) => IsPortAvailable(IPAddress.Loopback, port);

    private static List<string> GetLocalIpAddresses()
    {
        var ips = new List<string>();
        try
        {
            foreach (var netInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (netInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var ipProps = netInterface.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                    {
                        ips.Add(addr.Address.ToString());
                    }
                }
            }
        }
        catch { }
        return ips;
    }
}
