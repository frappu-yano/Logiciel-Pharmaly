using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pharmaly.Contracts;
using Pharmaly.Server.Transport;

namespace Pharmaly.Tests.Transport;

/// <summary>Une demande arrivée jusqu'au serveur : la preuve que quelque chose a été envoyé.</summary>
internal sealed record ReceivedRequest(string Path, bool WithPosteToken, bool WithEmployeeToken, bool Encrypted);

/// <summary>
/// Le vrai serveur (<c>Program</c>), démarré sur un vrai port avec Kestrel et son écoute
/// chiffrée, sur un port libre attribué par le système. Chaque demande reçue est notée.
/// </summary>
internal sealed class HttpsServer : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> settings;

    private HttpsServer(Dictionary<string, string?> settings) => this.settings = settings;

    public ConcurrentQueue<ReceivedRequest> Received { get; } = new();

    /// <summary>Avertissements et erreurs écrits dans le journal du serveur.</summary>
    public ConcurrentQueue<string> Warnings { get; } = new();

    /// <summary>Adresses réellement écoutées, telles que Kestrel les annonce.</summary>
    public IReadOnlyList<string> Addresses { get; private set; } = [];

    public int HttpsPort { get; private set; }

    public Uri Url => new($"https://127.0.0.1:{HttpsPort}/");

    public string CertificateFile => settings["Transport:CertificateFile"]!;

    /// <summary>Empreinte que l'installateur lirait avec <c>--empreinte</c>.</summary>
    public string Fingerprint
    {
        get
        {
            using var certificate = ServerCertificateStore.LoadOrCreate(CertificateFile);
            return CertificateFingerprint.Of(certificate);
        }
    }

    public static HttpsServer Start(
        string connectionString, string certificateFile, Dictionary<string, string?>? moreSettings = null)
    {
        var settings = new Dictionary<string, string?>(moreSettings ?? [])
        {
            ["ConnectionStrings:Pharmaly"] = connectionString,
            ["Transport:CertificateFile"] = certificateFile,
            ["Transport:HttpsPort"] = "0",
        };
        var server = new HttpsServer(settings);
        server.UseKestrel();
        server.StartServer();
        server.Addresses = [.. server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses];
        server.HttpsPort = new Uri(server.Addresses.Single(address => address.StartsWith("https://")).Replace("[::]", "127.0.0.1")).Port;
        return server;
    }

    /// <summary>Un port libre de cette machine (pour une écoute dont le port doit être connu d'avance).</summary>
    public static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new RecordingFilter(Received));
            services.AddSingleton<ILoggerProvider>(new WarningCollector(Warnings));
        });
    }

    private sealed class RecordingFilter(ConcurrentQueue<ReceivedRequest> received) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                received.Enqueue(new ReceivedRequest(
                    context.Request.Path,
                    context.Request.Headers.ContainsKey(ApiHeaders.PosteToken),
                    context.Request.Headers.ContainsKey(ApiHeaders.EmployeeToken),
                    context.Request.IsHttps));
                await following(context);
            });
            next(app);
        };
    }

    private sealed class WarningCollector(ConcurrentQueue<string> warnings) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                warnings.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
