using System.Runtime.InteropServices;
using System.Windows.Threading;
using static Radii.Native;

namespace Radii;

/// <summary>
/// Low-level mouse and keyboard hooks on their own thread.
/// A right click on the item view of File Explorer or the desktop is swallowed completely (press and release):
/// Explorer then never starts a drag or rubber band, and the native menu never opens. The UI thread is told
/// about both halves of the click. While the menu is open, the wheel and a few keys are swallowed too.
/// Shift + right click bypasses all of this and opens the native menu.
/// </summary>
internal sealed class InputHook : IDisposable
{
    private static readonly HashSet<string> HostClasses = new(StringComparer.Ordinal) { "CabinetWClass", "Progman", "WorkerW" };

    private readonly Dispatcher _dispatcher;
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private readonly HashSet<uint> _swallowedKeys = [];
    private readonly HashSet<uint> _swallowedHotkeys = [];
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private bool _swallowUp;
    private IntPtr _root;

    public InputHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _mouseProc = MouseProc;
        _keyboardProc = KeyboardProc;
    }

    /// <summary>The bool is true for Shift + right click, which opens the converter ring.</summary>
    public event Action<POINT, IntPtr, bool>? Pressed;
    /// <summary>The converter hotkey was pressed over a file view.</summary>
    public event Action<POINT, IntPtr>? ConvertRequested;
    public event Action<POINT>? Released;
    public event Action? DismissRequested;
    public event Action<int>? Wheel;
    public event Action<uint>? KeyPressed;

    public volatile bool Enabled = true;
    public volatile bool MenuVisible;
    /// <summary>Shift + right click is the converter instead of the native-menu bypass.</summary>
    public volatile bool ShiftConvert = true;

    private (uint Vk, bool Ctrl, bool Alt, bool Shift, bool Win)? _hotkey;

    /// <summary>Parses "F9" or "Ctrl+Alt+C"; null or empty disables the hotkey.</summary>
    public void SetHotkey(string? text)
    {
        _hotkey = ParseHotkey(text);
    }

    private static (uint, bool, bool, bool, bool)? ParseHotkey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        bool ctrl = false, alt = false, shift = false, win = false;
        uint vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0]))
                        vk = char.ToUpperInvariant(part[0]);
                    else if (part.Length is 2 or 3 && (part[0] is 'F' or 'f') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24)
                        vk = (uint)(0x6F + f);
                    else
                        return null;
                    break;
            }
        }
        return vk == 0 ? null : (vk, ctrl, alt, shift, win);
    }

    private bool HotkeyMatches(uint vk)
    {
        if (_hotkey is not { } h || vk != h.Vk)
            return false;
        static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
        return Down(0x11) == h.Ctrl && Down(0x12) == h.Alt && Down(VK_SHIFT) == h.Shift && (Down(0x5B) || Down(0x5C)) == h.Win;
    }

    /// <summary>Evaluated on the hook thread: is this physical point inside the ring window?</summary>
    public Func<POINT, bool>? IsInsideRing { get; set; }

    /// <summary>Evaluated on the hook thread: is this physical point inside the ring or any flyout?</summary>
    public Func<POINT, bool>? IsInsideMenu { get; set; }

    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var module = GetModuleHandle(null);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
                // Hook callbacks are delivered while this thread waits for messages.
            }
            UnhookWindowsHookEx(_mouseHook);
            UnhookWindowsHookEx(_keyboardHook);
        })
        {
            IsBackground = true,
            Name = "Radii input hooks",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait();
        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
            throw new InvalidOperationException("Could not install the global input hooks.");
    }

    public void Dispose()
    {
        if (_thread is null)
            return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((data.flags & LLMHF_INJECTED) == 0 && Handle((int)wParam, data.pt, data.mouseData))
                    return (IntPtr)1;
            }
            catch
            {
                // Never let an exception escape into the system hook chain.
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <returns>True to swallow the event.</returns>
    private bool Handle(int message, POINT pt, uint mouseData)
    {
        switch (message)
        {
            case WM_LBUTTONDOWN:
            case WM_MBUTTONDOWN:
                DismissIfOutside(pt);
                break;
            case WM_MOUSEWHEEL when MenuVisible:
                if (IsInsideRing?.Invoke(pt) == true)
                {
                    var delta = (short)(mouseData >> 16);
                    _dispatcher.BeginInvoke(() => Wheel?.Invoke(delta));
                    return true;
                }
                DismissIfOutside(pt);
                break;
            case WM_RBUTTONDOWN:
                DismissIfOutside(pt);
                _swallowUp = false;
                var shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                if (Enabled && (!shift || ShiftConvert) && ItemViewRoot(pt) is var root && root != IntPtr.Zero)
                {
                    _swallowUp = true;
                    _root = root;
                    _dispatcher.BeginInvoke(() => Pressed?.Invoke(pt, root, shift));
                    return true;
                }
                break;
            case WM_RBUTTONUP when _swallowUp:
                _swallowUp = false;
                _dispatcher.BeginInvoke(() => Released?.Invoke(pt));
                return true;
        }
        return false;
    }

    private void DismissIfOutside(POINT pt)
    {
        if (MenuVisible && IsInsideMenu?.Invoke(pt) != true)
            _dispatcher.BeginInvoke(() => DismissRequested?.Invoke());
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((data.flags & LLKHF_INJECTED) == 0 && (HandleHotkey((int)wParam, data.vkCode) || (MenuVisible && HandleKey((int)wParam, data.vkCode))))
                    return (IntPtr)1;
            }
            catch
            {
                // Never let an exception escape into the system hook chain.
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>The converter hotkey only counts over a file view of Explorer or the desktop, so typing elsewhere is never affected.</summary>
    private bool HandleHotkey(int message, uint vk)
    {
        var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        if (!down)
            return _swallowedHotkeys.Remove(vk);
        if (!Enabled || !HotkeyMatches(vk))
            return false;
        if (_swallowedHotkeys.Contains(vk))
            return true; // auto-repeat of the press that already opened the ring
        GetCursorPos(out var pt);
        var root = ItemViewRoot(pt);
        if (root == IntPtr.Zero)
            return false;
        _swallowedHotkeys.Add(vk);
        _dispatcher.BeginInvoke(() => ConvertRequested?.Invoke(pt, root));
        return true;
    }

    /// <summary>Esc, Enter, arrows and digits belong to the menu while it is open; everything else goes to Explorer.</summary>
    private bool HandleKey(int message, uint vk)
    {
        var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        if (!down)
            return _swallowedKeys.Remove(vk);
        var ours = vk is 0x1B or 0x0D or (>= 0x25 and <= 0x28) or (>= 0x31 and <= 0x39) or (>= 0x61 and <= 0x69);
        if (!ours)
            return false;
        _swallowedKeys.Add(vk);
        _dispatcher.BeginInvoke(() => KeyPressed?.Invoke(vk));
        return true;
    }

    /// <summary>Root window (Explorer frame or desktop) if the point is over a shell item view, else zero.</summary>
    private static IntPtr ItemViewRoot(POINT pt)
    {
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero)
            return IntPtr.Zero;
        var root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero || !HostClasses.Contains(ClassName(root)))
            return IntPtr.Zero;
        var window = hwnd;
        for (var depth = 0; window != IntPtr.Zero && depth < 10; depth++, window = GetParent(window))
        {
            if (ClassName(window) == "SHELLDLL_DefView")
                return root;
        }
        return IntPtr.Zero;
    }
}
