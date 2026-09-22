using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using VoiceKit.Core;
using VoiceKit.Remote;

namespace VoiceKit.Windows;

public partial class MainWindow
{
    private RemoteServer? _remote;
    private bool _shuttingDown, _shutdownComplete;
    private Task? _remoteStarting;
    private RemoteControlState? _remoteSnapshot;
    private long _remoteRevision;

    // Only UI owns settings. HTTP never touches WPF, the sound command queue or DSP state directly.
    private RemoteControlState ReadRemoteState()
    {
        var candidate = RemoteControlState.From(_settings.Audio, _engine is not null, _engine?.Graph.IsPanicked == true);
        if (_remoteSnapshot is null || candidate != (_remoteSnapshot with { Revision = 0 }))
            _remoteSnapshot = candidate with { Revision = ++_remoteRevision };
        return _remoteSnapshot;
    }
    private Task<RemoteControlState> ReadRemoteStateAsync(CancellationToken ct) =>
        Dispatcher.InvokeAsync(ReadRemoteState, DispatcherPriority.Background, ct).Task;
    private Task<RemoteControlState> ApplyRemoteEffectAsync(string effect, EffectPatch patch, CancellationToken ct) =>
        Dispatcher.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (_exit) throw new OperationCanceledException(ct);
            var next = patch.Apply(effect, _settings.Audio.Effects);
            _settings = _settings with { Audio = _settings.Audio with { Effects = next } };
            PopulateAudio();
            _engine?.Graph.Publish(_settings.Audio);
            QueueSave();
            return ReadRemoteState();
        }, DispatcherPriority.Background, ct).Task;

    private async void ToggleRemote(object sender, RoutedEventArgs e)
    {
        if (!_ready || _exit) return;
        RemoteToggleButton.IsEnabled = false;
        try
        {
            if (_remote is not null) await StopRemoteAsync();
            else
            {
                if (!int.TryParse(RemotePortBox.Text, out int port) || port is < 1024 or > 65535)
                    throw new ArgumentException("Укажи порт от 1024 до 65535, например 8765.");
                _remoteStarting = StartRemoteAsync(port);
                await _remoteStarting;
            }
        }
        catch (Exception ex) { ShowError("Не удалось изменить состояние веб-пульта", ex); }
        finally { _remoteStarting = null; if (!_exit) RemoteToggleButton.IsEnabled = true; }
    }
    private async Task StartRemoteAsync(int port)
    {
        var server = await RemoteServer.StartAsync(port, ReadRemoteStateAsync, ApplyRemoteEffectAsync);
        if (_exit) { await server.DisposeAsync(); return; }
        _remote = server;
        _settings = _settings with { RemotePort = port };
        QueueSave();
        RemoteAddressBox.ItemsSource = server.Addresses;
        RemoteAddressBox.SelectedIndex = 0;
        RemoteCodeLabel.Text = server.PairingCode.Insert(4, " ");
        RemoteStatusLabel.Text = "Веб-пульт доступен. Телефону понадобится код сопряжения.";
        RemotePortBox.IsEnabled = false;
        RemoteCopyButton.IsEnabled = RemoteOpenButton.IsEnabled = true;
        RemoteToggleButton.Content = "Выключить веб-пульт";
    }
    private async Task StopRemoteAsync()
    {
        if (_remoteStarting is not null) await _remoteStarting;
        var server = _remote;
        _remote = null;
        if (server is not null) await server.DisposeAsync();
        if (_exit) return;
        RemoteAddressBox.ItemsSource = null;
        RemoteCodeLabel.Text = "— — — — — — — —";
        RemoteStatusLabel.Text = "Веб-пульт выключен. Все сессии отключены.";
        RemotePortBox.IsEnabled = true;
        RemoteCopyButton.IsEnabled = RemoteOpenButton.IsEnabled = false;
        RemoteToggleButton.Content = "Включить веб-пульт";
    }
    private void CopyRemoteAddress(object sender, RoutedEventArgs e)
    {
        try { if (RemoteAddressBox.SelectedItem is string url) Clipboard.SetText(url); }
        catch (Exception ex) { ShowError("Не удалось скопировать адрес", ex); }
    }
    private void OpenRemoteAddress(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RemoteAddressBox.SelectedItem is string url)
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("Не удалось открыть браузер", ex); }
    }
}
