using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Threading;
using MacroWeaver.Models;

namespace MacroWeaver.Services;

/// <summary>
/// Records global keyboard presses, mouse clicks and wheel input without suppressing them.
/// Mouse coordinates are recorded when a click occurs; free mouse movement is intentionally ignored.
/// </summary>
public sealed class MacroInputRecorder : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmMouseWheel = 0x020A;
    private const uint LlkhfInjected = 0x10;
    private const uint LlMhfInjected = 0x01;

    private readonly IntPtr _ignoredWindow;
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private readonly Dictionary<uint, long> _pressedKeys = [];
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private long _lastRecordedTimestamp;
    private bool _disposed;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private readonly ManualResetEventSlim _hookReady = new(false);
    private Exception? _hookStartError;
    private volatile bool _stopRequested;

    public bool IsRecording { get; private set; }
    public event Action<MacroStep>? StepRecorded;
    public event Action<string>? RecordingError;

    public MacroInputRecorder(IntPtr ignoredWindow)
    {
        _ignoredWindow = ignoredWindow;
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRecording) return;
        _pressedKeys.Clear();
        _lastRecordedTimestamp = Stopwatch.GetTimestamp();
        _hookReady.Reset();
        _hookStartError = null;
        _stopRequested = false;
        _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "MacroWeaver input hook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        if (!_hookReady.Wait(TimeSpan.FromSeconds(5)))
        {
            _stopRequested = true;
            throw new TimeoutException("入力記録用スレッドの起動がタイムアウトしました。");
        }
        if (_hookStartError is not null)
        {
            _hookThread.Join(1000);
            _hookThread = null;
            throw new InvalidOperationException("入力フックを開始できませんでした。", _hookStartError);
        }
        IsRecording = true;
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            _hookStartError = new InvalidOperationException($"キーボードフックの登録に失敗しました（Win32 エラー {Marshal.GetLastWin32Error()}）。");
            _hookReady.Set();
            return;
        }
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
        {
            _hookStartError = new InvalidOperationException($"マウスフックの登録に失敗しました（Win32 エラー {Marshal.GetLastWin32Error()}）。");
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            _hookReady.Set();
            return;
        }
        _hookReady.Set();
        while (!_stopRequested && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        _hookThreadId = 0;
    }

    public void Stop()
    {
        IsRecording = false;
        _stopRequested = true;
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        if (_hookThread is not null && Thread.CurrentThread != _hookThread)
        {
            _hookThread.Join(2000);
            _hookThread = null;
        }
        _pressedKeys.Clear();
    }
    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording)
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((data.Flags & LlkhfInjected) == 0)
            {
                var message = unchecked((int)wParam.ToInt64());
                var isKeyDown = message is WmKeyDown or WmSysKeyDown;
                var isKeyUp = message is WmKeyUp or WmSysKeyUp;
                var foregroundIsTarget = GetForegroundWindow() != _ignoredWindow;

                // Only begin recording keys while another application is active, so
                // typing into MacroWeaver's own editor does not become macro input.
                if (isKeyDown && foregroundIsTarget)
                {
                    if (!_pressedKeys.ContainsKey(data.VirtualKey))
                        _pressedKeys[data.VirtualKey] = Stopwatch.GetTimestamp();
                }
                // Always accept key-up for a key we started tracking. The foreground
                // can change before release (for example, when returning to click Stop).
                else if (isKeyUp && _pressedKeys.Remove(data.VirtualKey, out var started))
                {
                    var key = KeyInterop.KeyFromVirtualKey((int)data.VirtualKey);
                    if (key != Key.None && key != Key.ImeProcessed && key != Key.DeadCharProcessed)
                    {
                        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
                        RecordStep(new MacroStep
                        {
                            Type = MacroStepType.KeyPress,
                            Name = $"キー入力：{key}",
                            Key = key.ToString(),
                            Seconds = Math.Clamp(elapsed, 0.01, 30)
                        });
                    }
                }
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsRecording)
        {
            var message = unchecked((int)wParam.ToInt64());
            var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            if ((data.Flags & LlMhfInjected) == 0 && !IsPointInsideIgnoredWindow(data.Point.X, data.Point.Y))
            {
                switch (message)
                {
                    case WmLButtonDown:
                        RecordClick(data.Point.X, data.Point.Y, "Left");
                        break;
                    case WmRButtonDown:
                        RecordClick(data.Point.X, data.Point.Y, "Right");
                        break;
                    case WmMButtonDown:
                        RecordClick(data.Point.X, data.Point.Y, "Middle");
                        break;
                    case WmMouseWheel:
                        var delta = unchecked((short)((data.MouseData >> 16) & 0xFFFF));
                        if (delta != 0)
                            RecordStep(new MacroStep
                            {
                                Type = MacroStepType.MouseScroll,
                                Name = "マウスホイール",
                                ScrollDelta = delta
                            });
                        break;
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool IsPointInsideIgnoredWindow(int x, int y)
    {
        if (_ignoredWindow == IntPtr.Zero || !GetWindowRect(_ignoredWindow, out var rect)) return false;
        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    private void RecordClick(int x, int y, string button)
    {
        RecordStep(new MacroStep
        {
            Type = MacroStepType.MouseMove,
            Name = "マウス移動（記録）",
            X = x,
            Y = y
        });
        RecordStep(new MacroStep
        {
            Type = MacroStepType.MouseClick,
            Name = $"マウスクリック：{button}",
            MouseButton = button,
            RepeatCount = 1
        });
    }

    private void RecordStep(MacroStep step)
    {
        var now = Stopwatch.GetTimestamp();
        var gap = Stopwatch.GetElapsedTime(_lastRecordedTimestamp, now).TotalSeconds;
        if (gap >= 0.08)
        {
            StepRecorded?.Invoke(new MacroStep
            {
                Type = MacroStepType.Wait,
                Name = "記録した待機",
                Seconds = Math.Clamp(gap, 0, 86400)
            });
        }

        _lastRecordedTimestamp = now;
        StepRecorded?.Invoke(step);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr HWnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
}
