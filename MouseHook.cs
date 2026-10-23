using System.Runtime.InteropServices;
using System.Windows.Threading;
using static Radii.Native;

namespace Radii;

/// <summary>
/// Low-level mouse and keyboard hooks on their own thread.
/// A right click on the item view of File Explorer or the desktop is swallowed completely (press and release):
/// Explorer then never starts a drag or rubber band, and the native menu never opens. The UI thread is told
/// about both halves of the click. While the menu is open, the wheel and a few keys are swallowed too.
/// Shift + right click asks for the converter ring and Ctrl + right click for the editors ring (each can be turned off;
/// with Shift switched off the click goes to the native menu).
/// </summary>
internal sealed class InputHook : IDisposable
{
    private static readonly HashSet<string> HostClasses = new(StringComparer.Ordinal) { "CabinetWClass", "Progman", "WorkerW" };

    private readonly Dispatcher _dispatcher;
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private readonly HashSet<uint> _swallowedKeys = [];
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

    /// <summary>Which ring was asked for: Shift = converter, Ctrl = editors, neither = the usual ring.</summary>
    public event Action<POINT, IntPtr, RingMode>? Pressed;
    public event Action<POINT>? Released;
    public event Action? DismissRequested;
    public event Action<int>? Wheel;
    /// <summary>Key code and, for keys that type something, the character (null otherwise).</summary>
    public event Action<uint, string?>? KeyPressed;

    public volatile bool Enabled = true;
    public volatile bool MenuVisible;
    /// <summary>Shift + right click is the converter instead of the native-menu bypass.</summary>
    public volatile bool ShiftConvert = true;
    /// <summary>Ctrl + right click is the ring of editors (off: Ctrl changes nothing).</summary>
    public volatile bool CtrlEdit = true;
    /// <summary>The visible ring can be searched: letters and digits are taken by the menu while it is open.</summary>
    public volatile bool SearchEnabled;
    /// <summary>A search is under way: Space and Backspace belong to it too.</summary>
    public volatile bool SearchActive;

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
            Install();
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                // Hook callbacks are delivered while this thread waits for messages.
                if (msg.message == WM_REINSTALL)
                {
                    UnhookWindowsHookEx(_mouseHook);
                    UnhookWindowsHookEx(_keyboardHook);
                    Install();
                }
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
        // Windows silently drops a low-level hook that was too slow once (and after sleep / lock): put it back then, and now and then anyway.
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
                Reinstall();
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                Reinstall();
        };
        _watchdog = new Timer(_ => Reinstall(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    private const uint WM_REINSTALL = 0x8000 + 1;
    private Timer? _watchdog;

    private void Install()
    {
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
    }

    /// <summary>Asks the hook thread to drop and set the hooks again (a no-op for the user, a cure when Windows removed them).</summary>
    private void Reinstall()
    {
        if (_thread is not null && !MenuVisible)
            PostThreadMessage(_threadId, WM_REINSTALL, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_thread is null)
            return;
        _watchdog?.Dispose();
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
                var ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                var mode = shift ? RingMode.Convert : ctrl && CtrlEdit ? RingMode.Edit : RingMode.Normal;
                if (Enabled && (!shift || ShiftConvert) && ItemViewRoot(pt) is var root && root != IntPtr.Zero && !BlockedByElevation(root))
                {
                    _swallowUp = true;
                    _root = root;
                    _dispatcher.BeginInvoke(() => Pressed?.Invoke(pt, root, mode));
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

    private static readonly bool SelfElevated = Updates.Updater.IsElevated();

    /// <summary>An elevated Explorer ignores the keys we send (UIPI), so the ring would do nothing: leave that window to its own menu.</summary>
    private static bool BlockedByElevation(IntPtr root) => !SelfElevated && IsElevatedWindow(root);

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
                if ((data.flags & LLKHF_INJECTED) == 0 && MenuVisible && HandleKey((int)wParam, data.vkCode))
                    return (IntPtr)1;
            }
            catch
            {
                // Never let an exception escape into the system hook chain.
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>Esc, Enter and arrows belong to the menu while it is open; everything else goes to Explorer.</summary>
    private bool HandleKey(int message, uint vk)
    {
        var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        if (!down)
            return _swallowedKeys.Remove(vk);
        // A and D only without Ctrl / Alt, so that real shortcuts still work.
        var plain = (GetAsyncKeyState(0x11) & 0x8000) == 0 && (GetAsyncKeyState(0x12) & 0x8000) == 0;
        // Typing: letters, digits, punctuation (and Space / Backspace once a search runs). Ctrl / Alt combinations stay with Explorer.
        var typing = SearchEnabled && plain
            && (vk is (>= 0x30 and <= 0x5A) or (>= 0x60 and <= 0x69) or (>= 0xBA and <= 0xC0) or (>= 0xDB and <= 0xDF)
                || (SearchActive && vk is 0x20 or 0x08));
        var ours = vk is 0x1B or 0x0D or (>= 0x25 and <= 0x28) || vk is 0x41 or 0x44 && plain || typing;
        if (!ours)
            return false;
        _swallowedKeys.Add(vk);
        var text = typing || vk is 0x41 or 0x44 ? TypedChar(vk) : null;
        _dispatcher.BeginInvoke(() => KeyPressed?.Invoke(vk, text));
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
