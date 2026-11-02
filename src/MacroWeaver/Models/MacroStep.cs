using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MacroWeaver.Models;

public enum MacroStepType
{
    KeyPress,
    MouseClick,
    MouseMove,
    Wait,
    Repeat
}

public sealed class MacroStep : INotifyPropertyChanged
{
    private string _name = "新しい操作";
    private string _key = "A";
    private string _mouseButton = "Left";
    private double _seconds = 0.1;
    private int _repeatCount = 1;
    private bool _repeatByDuration;
    private double _repeatDurationSeconds = 10;
    private int _x = 500;
    private int _y = 300;

    public Guid Id { get; set; } = Guid.NewGuid();
    public MacroStepType Type { get; set; }
    public string OrderLabel { get; set; } = "1";
    public List<MacroStep> Children { get; set; } = [];

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Key
    {
        get => _key;
        set => SetField(ref _key, value);
    }

    public string MouseButton
    {
        get => _mouseButton;
        set => SetField(ref _mouseButton, value);
    }

    public double Seconds
    {
        get => _seconds;
        set => SetField(ref _seconds, Math.Max(0, value));
    }

    public int RepeatCount
    {
        get => _repeatCount;
        set => SetField(ref _repeatCount, Math.Max(0, value));
    }

    public bool RepeatByDuration
    {
        get => _repeatByDuration;
        set => SetField(ref _repeatByDuration, value);
    }

    public double RepeatDurationSeconds
    {
        get => _repeatDurationSeconds;
        set => SetField(ref _repeatDurationSeconds, Math.Clamp(value, 0, 86400));
    }

    public int X
    {
        get => _x;
        set => SetField(ref _x, value);
    }

    public int Y
    {
        get => _y;
        set => SetField(ref _y, value);
    }

    public string TypeLabel => Type switch
    {
        MacroStepType.KeyPress => "⌨ キー入力",
        MacroStepType.MouseClick => "🖱 マウスクリック",
        MacroStepType.MouseMove => "↗ マウス移動",
        MacroStepType.Wait => "⏱ 待機",
        MacroStepType.Repeat => "🔁 繰り返し",
        _ => Type.ToString()
    };

    public string Summary => Type switch
    {
        MacroStepType.KeyPress => $"{Key} キー / {Seconds:0.###} 秒",
        MacroStepType.MouseClick => $"{MouseButton} ボタン / {RepeatCount} 回",
        MacroStepType.MouseMove => $"X={X}, Y={Y}",
        MacroStepType.Wait => $"{Seconds:0.###} 秒待機",
        MacroStepType.Repeat => RepeatByDuration
            ? $"{RepeatDurationSeconds:0.###} 秒間 / 内部操作 {Children.Count} 件"
            : $"{(RepeatCount == 0 ? "無限" : RepeatCount.ToString())} 回 / 内部操作 {Children.Count} 件",
        _ => string.Empty
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshComputedProperties()
    {
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(Summary));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(Summary));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}