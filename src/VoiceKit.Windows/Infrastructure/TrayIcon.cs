using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;

namespace VoiceKit.Windows.Infrastructure;

public sealed class TrayIcon : IDisposable
{
    private const int Message = 0x8000 + 42;
    private readonly HwndSource _source;
    private readonly Action _show;
    private readonly ContextMenu _menu;
    private NotifyData _data;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    public bool Available { get; private set; }
    public TrayIcon(IntPtr handle, Action show, Action exit)
    {
        _show = show;
        _source = HwndSource.FromHwnd(handle)!;
        _source.AddHook(Hook);
        _menu = new();
        var open = new MenuItem { Header = "Открыть VoiceKit" };
        open.Click += (_, _) => show();
        var quit = new MenuItem { Header = "Выход" };
        quit.Click += (_, _) => exit();
        _menu.Items.Add(open);
        _menu.Items.Add(quit);
        _data = new NotifyData
        {
            Size = Marshal.SizeOf<NotifyData>(), Window = handle, Id = 1, Flags = 1 | 2 | 4,
            Callback = Message, Icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)), Tip = "VoiceKit — управление голосом",
            Info = "", InfoTitle = ""
        };
        Available = ShellNotifyIcon(0, ref _data);
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == _taskbarCreated)
        {
            Available = ShellNotifyIcon(0, ref _data);
            if (!Available) _show();
            return IntPtr.Zero;
        }
        if (message != Message) return IntPtr.Zero;
        if (l.ToInt32() == 0x203) _show(); // left double click
        if (l.ToInt32() == 0x205)
        {
            SetForegroundWindow(hwnd);
            _menu.IsOpen = true;
        }
        handled = true;
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        ShellNotifyIcon(2, ref _data);
        _source.RemoveHook(Hook);
        _menu.IsOpen = false;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public int Size;
        public IntPtr Window;
        public uint Id, Flags;
        public int Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
