using System.Threading;
using System.Windows;

namespace VoiceKit.Windows;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, @"Local\VoiceKit.Desktop", out bool first);
        if (!first)
        {
            MessageBox.Show("VoiceKit уже запущен. Открой его через значок в трее.", "VoiceKit");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
