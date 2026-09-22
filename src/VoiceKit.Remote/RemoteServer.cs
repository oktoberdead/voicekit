using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VoiceKit.Core;

namespace VoiceKit.Remote;

/// <summary>HTTP only handles control messages. All audio and device access stay in the desktop app.</summary>
public sealed class RemoteServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stopping = new();
    private readonly RemoteAccess _access = new();
    public string PairingCode => _access.PairingCode;
    public int Port { get; }
    public IReadOnlyList<string> Addresses { get; }
    private RemoteServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
        Addresses = LocalAddresses(port);
    }
    public static async Task<RemoteServer> StartAsync(int port,
        Func<CancellationToken, Task<RemoteControlState>> read,
        Func<string, EffectPatch, CancellationToken, Task<RemoteControlState>> apply)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Порт: 1024–65535.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ApplicationName = typeof(RemoteServer).Assembly.GetName().Name
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.ListenAnyIP(port);
            options.Limits.MaxRequestBodySize = 4096;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
        var app = builder.Build();
        var server = new RemoteServer(app, port);
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                var origin = context.Request.Headers.Origin.ToString();
                if ((origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                    !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))) ||
                    context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                if (context.Request.Path != "/api/pair" && !server._access.Authorize(context.Request.Headers["X-VoiceKit-Token"].ToString()))
                {
                    context.Response.StatusCode = 401;
                    return;
                }
            }
            try { await next(context); }
            catch (OperationCanceledException)
            {
                if (!context.Response.HasStarted) context.Response.StatusCode = 503;
            }
        });
        // Exact static allowlist: no user files, directory traversal, config files or library access.
        foreach (var (path, file, mime) in new[]
        {
            ("/", "index.html", "text/html; charset=utf-8"),
            ("/app.js", "app.js", "text/javascript; charset=utf-8"),
            ("/control.js", "control.js", "text/javascript; charset=utf-8"),
            ("/style.css", "style.css", "text/css; charset=utf-8"),
            ("/icon.svg", "icon.svg", "image/svg+xml")
        })
        {
            using var stream = typeof(RemoteServer).Assembly.GetManifestResourceStream("VoiceKit.Remote.Web." + file)
                ?? throw new InvalidOperationException("В сборке отсутствует файл пульта: " + file);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            byte[] content = memory.ToArray();
            app.MapGet(path, () => Results.Bytes(content, mime));
        }
        app.MapPost("/api/pair", async (HttpContext context) =>
        {
            // Consume rate-limit budget even for malformed requests.
            PairRequest? request = null;
            try { if (context.Request.HasJsonContentType()) request = await context.Request.ReadFromJsonAsync<PairRequest>(context.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or NotSupportedException) { }
            var (token, limited) = server._access.Pair(request?.Code);
            if (limited) { context.Response.Headers["Retry-After"] = "60"; return Results.StatusCode(429); }
            return token is null ? Results.Unauthorized() : Results.Ok(new { token });
        });
        app.MapGet("/api/state", async (HttpContext context) =>
        {
            using var ct = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, server._stopping.Token);
            ct.CancelAfter(TimeSpan.FromSeconds(5));
            return Results.Ok(await read(ct.Token));
        });
        app.MapPatch("/api/effects/{effect}", async (string effect, HttpContext context) =>
        {
            using var ct = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, server._stopping.Token);
            ct.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                if (!context.Request.HasJsonContentType()) return Results.BadRequest(new { error = "Ожидается application/json." });
                var patch = await context.Request.ReadFromJsonAsync<EffectPatch>(ct.Token);
                if (patch is null) return Results.BadRequest(new { error = "Пустое изменение." });
                return Results.Ok(await apply(effect, patch, ct.Token));
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or BadHttpRequestException or NotSupportedException)
            {
                return Results.BadRequest(new { error = "Недопустимое изменение эффекта. Проверь параметр и диапазон." });
            }
        });
        try { await app.StartAsync(); return server; }
        catch { await server.DisposeAsync(); throw; }
    }
    private static IReadOnlyList<string> LocalAddresses(int port)
    {
        var ips = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(x => x.GetIPProperties().UnicastAddresses)
            .Select(x => x.Address)
            .Where(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x) && !x.ToString().StartsWith("169.254."))
            .Distinct().Select(x => $"http://{x}:{port}/").ToList();
        if (ips.Count == 0) ips.Add($"http://127.0.0.1:{port}/");
        return ips;
    }
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await _app.StopAsync(timeout.Token); }
        finally { await _app.DisposeAsync(); _stopping.Dispose(); }
    }
    private sealed record PairRequest(string? Code);
}
