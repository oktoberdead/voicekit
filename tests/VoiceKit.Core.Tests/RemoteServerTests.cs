using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using VoiceKit.Core;
using VoiceKit.Remote;
using Xunit;

namespace VoiceKit.Core.Tests;

public class RemoteServerTests
{
    [Fact]
    public void PairingIsRateLimitedAndSessionsAreScopedToServerLifetime()
    {
        var access = new RemoteAccess();
        Assert.False(access.Authorize(null)); Assert.False(access.Authorize(access.PairingCode));
        var (token, limited) = access.Pair(access.PairingCode);
        Assert.NotNull(token); Assert.False(limited); Assert.True(access.Authorize(token));
        Assert.False(new RemoteAccess().Authorize(token));
        for (int i = 0; i < 9; i++) Assert.Null(access.Pair("wrong").Token);
        Assert.True(access.Pair(access.PairingCode).Limited);
    }
    [Fact]
    public async Task HostRequiresPairingProtectsStateAndServesEmbeddedUi()
    {
        // Reserve an available unprivileged port (small bind race acceptable for a local integration test).
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var settings = new AudioSettings { Effects = new() { VocoderEnabled = true } };
        var gate = new object(); long revision = 1;
        RemoteControlState State() => RemoteControlState.From(settings, true, false) with { Revision = revision };
        await using var server = await RemoteServer.StartAsync(port,
            _ => { lock (gate) return Task.FromResult(State()); },
            (effect, patch, _) =>
            {
                lock (gate)
                {
                    settings = settings with { Effects = patch.Apply(effect, settings.Effects) }; revision++;
                    return Task.FromResult(State());
                }
            });
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var html = await http.GetStringAsync("/"); Assert.Contains("Цепочка эффектов", html);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/pair", new { code = "bad" })).StatusCode);
        var paired = await http.PostAsJsonAsync("/api/pair", new { code = server.PairingCode }); paired.EnsureSuccessStatusCode();
        var data = await paired.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Add("X-VoiceKit-Token", data.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/state")).StatusCode);
        var update = await http.PatchAsJsonAsync("/api/effects/echo", new { enabled = true, parameters = new { delayMs = 400 } });
        update.EnsureSuccessStatusCode();
        Assert.True(settings.Effects.EchoEnabled); Assert.Equal(400, settings.Effects.EchoMs);
        Assert.True(settings.Effects.VocoderEnabled);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsJsonAsync("/api/effects/vocoder", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsJsonAsync("/api/effects/echo", new { arbitrary = "invalid" })).StatusCode);
        http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/settings.json")).StatusCode);
    }
}
