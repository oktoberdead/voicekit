using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using VoiceKit.Core;

namespace VoiceKit.Windows.Infrastructure;

/// <summary>UI-thread keyboard hook; handled keys (including repeats/up) are suppressed.
/// Does not claim exclusivity against earlier hooks, elevated apps or anti-cheat.</summary>
public sealed class HotkeyService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<HotkeyBinding, bool> _dispatch;
    private readonly HookProc _callback;
    private readonly Dictionary<int, HotkeyBinding> _held = [];
    private List<(int Key, int Modifiers, HotkeyBinding Binding)> _bindings = [];
    private IntPtr _hook;
    private int _generation;
    public HotkeyService(Dispatcher dispatcher, Action<HotkeyBinding, bool> dispatch)
    {
        _dispatcher = dispatcher;
        _dispatch = dispatch;
        _callback = Callback;
    }
    public void Configure(IEnumerable<HotkeyBinding> bindings)
    {
        var parsed = new List<(int Key, int Modifiers, HotkeyBinding Binding)>();
        foreach (var b in bindings)
        {
            if (string.IsNullOrWhiteSpace(b.Gesture)) continue;
            var (key, modifiers) = Parse(b.Gesture);
            if (parsed.Any(x => x.Key == key && x.Modifiers == modifiers))
                throw new ArgumentException($"Хоткей {b.Gesture} назначен несколько раз.");
            parsed.Add((key, modifiers, b));
        }
        // Parse first: a bad edit leaves the previous bindings intact.
        if (_hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        _generation++;
        foreach (var held in _held.Values) _dispatch(held, false);
        _held.Clear();
        _bindings = parsed;
    }
    private static (int Key, int Modifiers) Parse(string gesture)
    {
        string[] parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Пустой хоткей.");
        int mods = 0;
        for (int i = 0; i < parts.Length - 1; i++)
            mods |= parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => 2, "ALT" => 1, "SHIFT" => 4, "WIN" => 8,
                _ => throw new ArgumentException($"Неизвестный модификатор: {parts[i]}")
            };
        string name = parts[^1];
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) name = "D" + name;
        if (!Enum.TryParse<Key>(name, true, out var key) || key == Key.None ||
            key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            throw new ArgumentException($"Неизвестная клавиша: {parts[^1]}. Пример: Ctrl+Alt+F6");
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) throw new ArgumentException("Эта клавиша не поддерживается.");
        return (vk, mods);
    }
    private void Queue(HotkeyBinding binding, bool pressed)
    {
        int generation = _generation;
        _dispatcher.BeginInvoke(() =>
        {
            if (generation == _generation) _dispatch(binding, pressed);
        }, DispatcherPriority.Input);
    }
    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var data = Marshal.PtrToStructure<KeyboardData>(lParam);
                if ((data.Flags & 0x10) != 0) return CallNextHookEx(_hook, code, wParam, lParam); // injected
                int key = (int)data.Key, message = wParam.ToInt32();
                bool down = message is 0x100 or 0x104, up = message is 0x101 or 0x105;
                if (up && _held.Remove(key, out var released))
                {
                    Queue(released, false);
                    return new IntPtr(1);
                }
                if (down)
                {
                    if (_held.ContainsKey(key)) return new IntPtr(1);
                    int mods = (Down(0x11) ? 2 : 0) | (Down(0x12) ? 1 : 0) | (Down(0x10) ? 4 : 0) |
                        (Down(0x5B) || Down(0x5C) ? 8 : 0);
                    foreach (var b in _bindings)
                    {
                        if (b.Key != key || b.Modifiers != mods) continue;
                        _held.Add(key, b.Binding);
                        Queue(b.Binding, true);
                        return new IntPtr(1);
                    }
                }
            }
        }
        catch (Exception e) { Debug.WriteLine(e); } // never unwind into user32
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    public void Dispose()
    {
        _generation++;
        foreach (var held in _held.Values) _dispatch(held, false);
        _held.Clear();
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData
    {
        public uint Key, ScanCode, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
