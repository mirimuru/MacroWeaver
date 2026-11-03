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

        int[]? originalMouseSettings = null;
        var normalizedMouseSettingsApplied = false;

        try
        {
            // Windows pointer acceleration makes identical relative SendInput deltas
            // travel different screen distances depending on movement speed.
            // Temporarily use 1:1 pointer scaling with acceleration disabled.
            if (ContainsRelativeMove(steps))
            {
                originalMouseSettings = new int[3];
                if (!SystemParametersInfo(SpiGetMouse, 0, originalMouseSettings, 0))
                    throw new InvalidOperationException("Windowsのマウス設定を取得できませんでした。");

                if (!SystemParametersInfo(SpiSetMouse, 0, [0, 0, 10], 0))
                    throw new InvalidOperationException("マウス加速を一時的に無効化できませんでした。");

                normalizedMouseSettingsApplied = true;
            }

            await ExecuteStepsAsync(steps, progress, _runCts.Token, 0);
        }
        finally
        {
            if (normalizedMouseSettingsApplied && originalMouseSettings is not null)
            {
                // Restore the user's exact settings even when execution is stopped or fails.
                SystemParametersInfo(SpiSetMouse, 0, originalMouseSettings, 0);
            }

            IsRunning = false;
            IsPaused = false;
            _pauseGate.Set();
            _runCts.Dispose();
            _runCts = null;
        }
    }

    private async Task ExecuteStepsAsync(IReadOnlyList<MacroStep> steps, IProgress<string>? progress, CancellationToken token, int depth)
    {
        if (depth > 32) throw new InvalidOperationException("繰り返しの入れ子が深すぎます（最大32階層）。");

        for (var i = 0; i < steps.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(token);
            var step = steps[i];
            progress?.Report($"実行中：{step.Name}");

            if (step.Type == MacroStepType.Repeat)
            {
                if (step.Children.Count == 0)
                {
                    progress?.Report($"スキップ：{step.Name}（内部操作がありません）");
                    continue;
                }

                var count = step.RepeatCount;
                var iteration = 0;
                var durationMode = step.RepeatByDuration;
                var duration = TimeSpan.FromSeconds(Math.Clamp(step.RepeatDurationSeconds, 0, 86400));
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                while (durationMode ? stopwatch.Elapsed < duration : count == 0 || iteration < count)
                {
                    token.ThrowIfCancellationRequested();
                    await WaitIfPausedAsync(token);
                    if (durationMode && stopwatch.Elapsed >= duration) break;
                    iteration++;
                    progress?.Report(durationMode
                        ? $"{step.Name}：{stopwatch.Elapsed.TotalSeconds:0.0} / {duration.TotalSeconds:0.###} 秒"
                        : $"{step.Name}：{iteration}{(count == 0 ? " / ∞" : $" / {count}")}");
                    await ExecuteStepsAsync(step.Children, progress, token, depth + 1);
                }
                continue;
            }

            switch (step.Type)
            {
                case MacroStepType.Wait:
                    await DelayWithPauseAsync(TimeSpan.FromSeconds(Math.Clamp(step.Seconds, 0, 86400)), token);
                    break;
                case MacroStepType.RandomWait:
                    var minimum = Math.Clamp(step.Seconds, 0, 86400);
                    var maximum = Math.Clamp(step.RandomWaitMaxSeconds, minimum, 86400);
                    var randomSeconds = Random.Shared.NextDouble() * (maximum - minimum) + minimum;
                    await DelayWithPauseAsync(TimeSpan.FromSeconds(randomSeconds), token);
                    progress?.Report($"待機完了：{step.Name}（{randomSeconds:0.###} 秒）");
                    break;
                case MacroStepType.MouseScroll:
                    SendMouseWheel(step.ScrollDelta);
                    break;
                case MacroStepType.KeyPress:
                    await PressKeyAsync(step.Key, step.Seconds, token);
                    break;
                case MacroStepType.MouseMove:
                    if (!SetCursorPos(step.X, step.Y))
                        throw new InvalidOperationException($"マウスを座標 ({step.X}, {step.Y}) に移動できませんでした。");
                    break;
                case MacroStepType.MouseMoveRelative:
                    MoveMouseRelative(step.RelativeX, step.RelativeY);
                    break;
                case MacroStepType.MouseClick:
                    for (var click = 0; click < Math.Clamp(step.RepeatCount, 1, 10000); click++)
                    {
                        token.ThrowIfCancellationRequested();
                        await WaitIfPausedAsync(token);
                        ClickMouse(step.MouseButton);
                        if (click + 1 < step.RepeatCount)
                            await DelayWithPauseAsync(TimeSpan.FromMilliseconds(80), token);
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
            remaining -= System.Diagnostics.Stopwatch.GetElapsedTime(before);
        }
    }

    private Task WaitIfPausedAsync(CancellationToken token)
        => Task.Run(() => _pauseGate.Wait(token), token);

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

    private const uint SpiGetMouse = 0x0003;
    private const uint SpiSetMouse = 0x0004;

    private static bool ContainsRelativeMove(IEnumerable<MacroStep> steps)
        => steps.Any(step => step.Type == MacroStepType.MouseMoveRelative ||
                             ContainsRelativeMove(step.Children));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

    private static void MoveMouseRelative(int deltaX, int deltaY)
    {
        var input = new INPUT
        {
            type = 0,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = deltaX,
                    dy = deltaY,
                    dwFlags = (uint)MouseEventFlags.Move
                }
            }
        };

        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException("マウスの相対移動を送信できませんでした。");
    }

    private static void SendMouseWheel(int delta)
    {
        var input = new INPUT
        {
            type = 0,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = (uint)MouseEventFlags.Wheel
                }
            }
        };

        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException("マウスホイール入力を送信できませんでした。");
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
        Move = 0x0001,
        Wheel = 0x0800,
        LeftDown = 0x0002, LeftUp = 0x0004,
        RightDown = 0x0008, RightUp = 0x0010,
        MiddleDown = 0x0020, MiddleUp = 0x0040
    }
}