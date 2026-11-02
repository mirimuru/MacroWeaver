using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using MacroWeaver.Models;

namespace MacroWeaver.Services;

public sealed class MacroExecutionService
{
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private CancellationTokenSource? _runCts;

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }

    public void Pause()
    {
        if (!IsRunning || IsPaused) return;
        IsPaused = true;
        _pauseGate.Reset();
    }

    public void Resume()
    {
        if (!IsRunning || !IsPaused) return;
        IsPaused = false;
        _pauseGate.Set();
    }

    public void Stop()
    {
        _runCts?.Cancel();
        _pauseGate.Set();
    }

    public async Task RunAsync(IReadOnlyList<MacroStep> steps, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("マクロはすでに実行中です。");
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsRunning = true;
        IsPaused = false;
        _pauseGate.Set();

        try
        {
            await ExecuteRangeAsync(steps, 0, steps.Count, progress, _runCts.Token);
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _pauseGate.Set();
            _runCts.Dispose();
            _runCts = null;
        }
    }

    private async Task ExecuteRangeAsync(IReadOnlyList<MacroStep> steps, int start, int end, IProgress<string>? progress, CancellationToken token)
    {
        for (var i = start; i < end; i++)
        {
            token.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(token);
            var step = steps[i];
            progress?.Report($"実行中：{i + 1}/{end} — {step.Name}");

            if (step.Type == MacroStepType.Repeat)
            {
                // Repeat acts as a loop marker: repeat all steps after it through the end.
                var count = step.RepeatCount;
                var iteration = 0;
                while (count == 0 || iteration < count)
                {
                    token.ThrowIfCancellationRequested();
                    await WaitIfPausedAsync(token);
                    iteration++;
                    progress?.Report($"繰り返し {iteration}{(count == 0 ? " / ∞" : $" / {count}")}");
                    await ExecuteRangeAsync(steps, i + 1, end, progress, token);
                }
                return;
            }

            switch (step.Type)
            {
                case MacroStepType.Wait:
                    await DelayWithPauseAsync(TimeSpan.FromSeconds(Math.Clamp(step.Seconds, 0, 86400)), token);
                    break;
                case MacroStepType.KeyPress:
                    await PressKeyAsync(step.Key, step.Seconds, token);
                    break;
                case MacroStepType.MouseMove:
                    SetCursorPos(step.X, step.Y);
                    break;
                case MacroStepType.MouseClick:
                    for (var click = 0; click < Math.Clamp(step.RepeatCount, 1, 10000); click++)
                    {
                        token.ThrowIfCancellationRequested();
                        await WaitIfPausedAsync(token);
                        ClickMouse(step.MouseButton);
                        if (click + 1 < step.RepeatCount) await DelayWithPauseAsync(TimeSpan.FromMilliseconds(80), token);
                    }
                    break;
            }
        }
    }

    private async Task PressKeyAsync(string keyName, double seconds, CancellationToken token)
    {
        if (!Enum.TryParse<Key>(keyName, true, out var key) || key == Key.None)
            throw new InvalidOperationException($"キー名「{keyName}」を認識できません。例：A、Enter、Space、F5");
        var virtualKey = (ushort)KeyInterop.VirtualKeyFromKey(key);
        SendKey(virtualKey, false);
        try
        {
            await DelayWithPauseAsync(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 3600)), token);
        }
        finally
        {
            SendKey(virtualKey, true);
        }
    }

    private async Task DelayWithPauseAsync(TimeSpan duration, CancellationToken token)
    {
        var remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            token.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(token);
            var slice = remaining > TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : remaining;
            var before = System.Diagnostics.Stopwatch.GetTimestamp();
            await Task.Delay(slice, token);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(before);
            remaining -= elapsed;
        }
    }

    private Task WaitIfPausedAsync(CancellationToken token)
        => Task.Run(() => { _pauseGate.Wait(token); }, token);

    private static void ClickMouse(string button)
    {
        var down = button switch
        {
            "Right" => MouseEventFlags.RightDown,
            "Middle" => MouseEventFlags.MiddleDown,
            _ => MouseEventFlags.LeftDown
        };
        var up = button switch
        {
            "Right" => MouseEventFlags.RightUp,
            "Middle" => MouseEventFlags.MiddleUp,
            _ => MouseEventFlags.LeftUp
        };
        SendMouse(down);
        SendMouse(up);
    }

    private static void SendKey(ushort virtualKey, bool keyUp)
    {
        var input = new INPUT
        {
            type = 1,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = virtualKey, dwFlags = keyUp ? 0x0002u : 0u } }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void SendMouse(MouseEventFlags flags)
    {
        var input = new INPUT
        {
            type = 0,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = (uint)flags } }
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nuint dwExtraInfo;
    }

    [Flags]
    private enum MouseEventFlags : uint
    {
        LeftDown = 0x0002, LeftUp = 0x0004,
        RightDown = 0x0008, RightUp = 0x0010,
        MiddleDown = 0x0020, MiddleUp = 0x0040
    }
}