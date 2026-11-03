using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using MacroWeaver.Models;
using MacroWeaver.Services;

namespace MacroWeaver;

public sealed class MacroLibraryItem : INotifyPropertyChanged
{
    private string _name = "";

    public string Id { get; set; } = "";
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private MacroStep? _selectedStep;
    private bool _updatingProperties;
    private readonly MacroExecutionService _executionService = new();
    private MacroInputRecorder? _inputRecorder;
    private bool _stopRequested;
    private bool _loadingMacro;
    private bool _allowClose;
    private bool _isHandlingClose;
    private bool _isWindowClosed;
    private string? _savedMacroSnapshot;
    private HwndSource? _hotkeySource;
    private const int StopHotkeyId = 0x4D57;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const int WmHotkey = 0x0312;
    private string? _currentMacroId;
    private readonly Stack<string> _undoHistory = new();
    private readonly Stack<string> _redoHistory = new();
    private string? _clipboardStepsJson;
    private const int MaxHistoryEntries = 100;
    private IList<MacroStep>? _insertionList;
    private int _insertionIndex = -1;
    private DataGridRow? _insertionMarkerRow;
    private System.Windows.Media.Brush? _insertionMarkerOriginalBrush;
    private Thickness _insertionMarkerOriginalThickness;
    private readonly string _macroLibraryDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacroWeaver", "Macros");

    public ObservableCollection<MacroStep> Steps { get; } = [];
    public ObservableCollection<MacroStep> VisibleSteps { get; } = [];
    public ObservableCollection<MacroLibraryItem> MacroLibrary { get; } = [];

    public MacroStep? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (ReferenceEquals(_selectedStep, value)) return;
            _selectedStep = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedStep)));
            UpdatePropertyPanel();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Steps.CollectionChanged += Steps_CollectionChanged;
        PushUndoSnapshot();
        AddStep(MacroStepType.KeyPress);
        AddStep(MacroStepType.MouseClick);
        AddStep(MacroStepType.Wait);
        StepsGrid.SelectedIndex = 0;
        RefreshOrderLabels();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void Steps_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshOrderLabels();

    private void RefreshOrderLabels()
    {
        VisibleSteps.Clear();
        for (var i = 0; i < Steps.Count; i++)
            AddVisibleStep(Steps[i], (i + 1).ToString(CultureInfo.InvariantCulture), 0);
        UpdateStepCount();
        StepsGrid.Items.Refresh();
    }

    private void AddVisibleStep(MacroStep step, string label, int depth)
    {
        step.OrderLabel = depth == 0 ? label : $"{new string(' ', depth * 2)}↳ {label}";
        VisibleSteps.Add(step);
        if (step.Type == MacroStepType.Repeat)
        {
            for (var i = 0; i < step.Children.Count; i++)
                AddVisibleStep(step.Children[i], $"{label}.{i + 1}", depth + 1);
        }
        step.RefreshComputedProperties();
    }

    private int CountSteps(IEnumerable<MacroStep> steps) => steps.Sum(s => 1 + CountSteps(s.Children));

    private void UpdateStepCount() => StepCountText.Text = $"{CountSteps(Steps)} 操作";

    private IList<MacroStep> FindContainingList(MacroStep target, IList<MacroStep>? current = null)
    {
        current ??= Steps;
        if (current.Contains(target)) return current;
        foreach (var step in current)
        {
            var found = FindContainingList(target, step.Children);
            if (found.Count > 0 && found.Contains(target)) return found;
        }
        return [];
    }

    private void AddStep(MacroStepType type)
    {
        PushUndoSnapshot();
        var step = new MacroStep
        {
            Type = type,
            Seconds = type == MacroStepType.Wait ? 1 : 0.1,
            RepeatCount = type == MacroStepType.Repeat ? 3 : 1,
            Name = type switch
            {
                MacroStepType.KeyPress => "キー入力",
                MacroStepType.MouseClick => "マウスクリック",
                MacroStepType.MouseMove => "マウス移動（座標）",
                MacroStepType.MouseMoveRelative => "マウス相対移動",
                MacroStepType.MouseScroll => "マウスホイール",
                MacroStepType.Wait => "待機",
                MacroStepType.RandomWait => "ランダム待機",
                MacroStepType.Repeat => "繰り返し",
                _ => "操作"
            }
        };

        IList<MacroStep> destination;
        if (_insertionList is not null && _insertionIndex >= 0)
        {
            destination = _insertionList;
            destination.Insert(Math.Clamp(_insertionIndex, 0, destination.Count), step);
            ClearInsertionPoint();
        }
        else if (AddAtRootCheck.IsChecked == true)
        {
            destination = Steps;
            var rootIndex = SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
            if (rootIndex >= 0) destination.Insert(rootIndex + 1, step);
            else destination.Add(step);
        }
        else if (SelectedStep?.Type == MacroStepType.Repeat)
        {
            destination = SelectedStep.Children;
            destination.Add(step);
        }
        else if (SelectedStep is not null)
        {
            destination = FindContainingList(SelectedStep);
            var index = destination.IndexOf(SelectedStep);
            destination.Insert(index + 1, step);
        }
        else
        {
            destination = Steps;
            destination.Add(step);
        }

        RefreshOrderLabels();
        SelectedStep = step;
        StepsGrid.SelectedItem = step;
        StepsGrid.ScrollIntoView(step);
        StatusText.Text = SelectedStep is not null && !ReferenceEquals(FindContainingList(step), Steps)
            ? $"「{step.Name}」を追加しました。繰り返しの内部に追加するには、繰り返し行を選択してから操作を追加してください。"
            : $"「{step.Name}」を追加しました。";
    }

    private void StartRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_executionService.IsRunning)
        {
            MessageBox.Show(this, "マクロ実行中は操作を記録できません。先にマクロを停止してください。", "実行中", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _inputRecorder ??= new MacroInputRecorder(new WindowInteropHelper(this).Handle);
            _inputRecorder.StepRecorded -= InputRecorder_StepRecorded;
            _inputRecorder.StepRecorded += InputRecorder_StepRecorded;
            _inputRecorder.Start();
            StartRecordingButton.IsEnabled = false;
            StopRecordingButton.IsEnabled = true;
            StatusText.Text = "操作を記録中です。キーボード・マウスを操作し、「記録停止」を押してください。";
        }
        catch (Exception ex)
        {
            _inputRecorder?.Stop();
            MessageBox.Show(this, $"操作記録を開始できませんでした。\n{ex.Message}", "記録エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_inputRecorder is null || !_inputRecorder.IsRecording) return;
        _inputRecorder.Stop();
        StartRecordingButton.IsEnabled = true;
        StopRecordingButton.IsEnabled = false;
        RefreshOrderLabels();
        StatusText.Text = "操作記録を停止しました。記録した操作は現在のマクロ末尾に追加されています。内容を確認して保存してください。";
    }

    private void InputRecorder_StepRecorded(MacroStep step)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Ignore queued hook events once shutdown has started.
            if (_allowClose || _isWindowClosed || !IsLoaded) return;
            // Preserve the final event when recording merely stops.
            Steps.Add(step);
            RefreshOrderLabels();
            StepsGrid.SelectedItem = step;
            StepsGrid.ScrollIntoView(step);
        }));
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            UnregisterStopHotkey();
            _inputRecorder?.Dispose();
            _inputRecorder = null;
            return;
        }

        // Closing is asynchronous because saving the current macro uses async file I/O.
        e.Cancel = true;
        if (_isHandlingClose) return;
        _isHandlingClose = true;

        try
        {
            if (HasUnsavedChanges())
            {
                var result = MessageBox.Show(
                    this,
                    $"マクロ「{MacroLibrary.FirstOrDefault(m => m.Id == _currentMacroId)?.Name ?? "現在のマクロ"}」に保存していない変更があります。\n保存してから終了しますか？",
                    "未保存の変更",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel)
                    return;

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        await SaveCurrentMacroAsync();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"マクロの保存に失敗しました。\n{ex.Message}\n終了せずに編集画面に戻ります。",
                            "保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
            }

            // Defer Close until this async Closing handler has unwound.
            _allowClose = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isWindowClosed && IsVisible)
                    Close();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        finally
        {
            _isHandlingClose = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isWindowClosed = true;
        base.OnClosed(e);
    }

    private void AddKey_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.KeyPress);
    private void AddClick_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.MouseClick);
    private void AddMove_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.MouseMove);
    private void AddRelativeMove_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.MouseMoveRelative);
    private void AddScroll_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.MouseScroll);
    private void AddWait_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.Wait);
    private void AddRandomWait_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.RandomWait);
    private void AddRepeat_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.Repeat);

    private List<MacroStep> GetSelectedSteps() => StepsGrid.SelectedItems.Cast<MacroStep>().ToList();

    private List<MacroStep> GetTopLevelSelectedSteps()
    {
        var selected = GetSelectedSteps();
        var set = selected.ToHashSet();
        return selected.Where(step => !set.Any(parent => !ReferenceEquals(parent, step) &&
            DescendantsAndSelf(parent).Skip(1).Contains(step))).ToList();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetTopLevelSelectedSteps();
        if (selected.Count == 0 && SelectedStep is not null) selected.Add(SelectedStep);
        if (selected.Count == 0) return;
        PushUndoSnapshot();
        foreach (var step in selected) FindContainingList(step).Remove(step);
        RefreshOrderLabels();
        SelectedStep = VisibleSteps.FirstOrDefault();
        StepsGrid.SelectedItem = SelectedStep;
        StatusText.Text = $"{selected.Count} 件の操作を削除しました。";
    }

    private static IEnumerable<MacroStep> DescendantsAndSelf(MacroStep step)
        => new[] { step }.Concat(step.Children.SelectMany(DescendantsAndSelf));

    private void MoveIntoRepeat_Click(object sender, RoutedEventArgs e)
    {
        var movingSteps = GetTopLevelSelectedSteps();
        if (movingSteps.Count == 0 && SelectedStep is not null) movingSteps.Add(SelectedStep);
        if (movingSteps.Count == 0) return;

        var forbidden = movingSteps.SelectMany(DescendantsAndSelf).ToHashSet();
        var repeatTargets = AllSteps(Steps).Where(step => step.Type == MacroStepType.Repeat && !forbidden.Contains(step)).ToList();
        if (repeatTargets.Count == 0)
        {
            MessageBox.Show(this, "移動先にできる繰り返し操作がありません。", "移動先がありません", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selector = new ComboBox { MinWidth = 360, MaxDropDownHeight = 300, Margin = new Thickness(0, 10, 0, 14), Padding = new Thickness(6), ItemsSource = repeatTargets, DisplayMemberPath = nameof(MacroStep.Name) };
        selector.SelectionChanged += (_, _) => { if (selector.SelectedItem is MacroStep selected) selector.ToolTip = $"{selected.OrderLabel}：{selected.Name}"; };
        selector.SelectedIndex = 0;
        var confirm = new Button { Content = "移動", Width = 84, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "キャンセル", Width = 92, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { confirm, cancel } };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"選択した {movingSteps.Count} 件の操作を入れる繰り返し操作を選択してください。" });
        panel.Children.Add(selector);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "繰り返しに移動", Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#FF20232B")!,
            Foreground = System.Windows.Media.Brushes.White
        };
        confirm.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true || selector.SelectedItem is not MacroStep target) return;
        if (forbidden.Contains(target) || target.Type != MacroStepType.Repeat) return;

        var ordered = movingSteps.OrderBy(step => VisibleSteps.IndexOf(step)).ToList();
        PushUndoSnapshot();
        foreach (var step in ordered) FindContainingList(step).Remove(step);
        target.Children.AddRange(ordered);
        RefreshOrderLabels();
        StepsGrid.SelectedItems.Clear();
        foreach (var step in ordered) StepsGrid.SelectedItems.Add(step);
        SelectedStep = ordered.LastOrDefault();
        StatusText.Text = $"{ordered.Count} 件の操作を「{target.Name}」の繰り返し内部に移動しました。";
    }

    private static MacroStep CloneStep(MacroStep original) => new()
    {
        Id = Guid.NewGuid(),
        Type = original.Type,
        Name = $"{original.Name} (コピー)",
        Key = original.Key,
        MouseButton = original.MouseButton,
        Seconds = original.Seconds,
        RepeatCount = original.RepeatCount,
        RepeatByDuration = original.RepeatByDuration,
        RepeatDurationSeconds = original.RepeatDurationSeconds,
        X = original.X,
        Y = original.Y,
        RelativeX = original.RelativeX,
        RelativeY = original.RelativeY,
        ScrollDelta = original.ScrollDelta,
        RandomWaitMaxSeconds = original.RandomWaitMaxSeconds,
        Children = original.Children.Select(CloneStep).ToList()
    };

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var originals = GetTopLevelSelectedSteps();
        if (originals.Count == 0 && SelectedStep is not null) originals.Add(SelectedStep);
        if (originals.Count == 0) return;

        var ordered = originals.OrderBy(step => VisibleSteps.IndexOf(step)).ToList();
        PushUndoSnapshot();
        var copies = new List<MacroStep>();
        foreach (var original in ordered)
        {
            var parent = FindContainingList(original);
            var copy = CloneStep(original);
            parent.Insert(parent.IndexOf(original) + 1, copy);
            copies.Add(copy);
        }

        RefreshOrderLabels();
        StepsGrid.SelectedItems.Clear();
        foreach (var copy in copies) StepsGrid.SelectedItems.Add(copy);
        SelectedStep = copies.LastOrDefault();
        if (SelectedStep is not null) StepsGrid.ScrollIntoView(SelectedStep);
        StatusText.Text = $"{copies.Count} 件の操作を複製しました。";
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int offset)
    {
        // RefreshOrderLabels clears and rebuilds VisibleSteps. The DataGrid can
        // temporarily clear its selection during that refresh, so preserve the
        // selected item and restore it only after the collection has been rebuilt.
        var item = SelectedStep;
        if (item is null) return;

        var parent = FindContainingList(item);
        var oldIndex = parent.IndexOf(item);
        if (oldIndex < 0) return;

        var newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= parent.Count) return;

        PushUndoSnapshot();
        parent.RemoveAt(oldIndex);
        parent.Insert(newIndex, item);

        RefreshOrderLabels();
        SelectedStep = item;
        StepsGrid.SelectedItem = item;
        StepsGrid.ScrollIntoView(item);
        StatusText.Text = "同じ階層内で操作の順番を変更しました。";
    }

    private void StepsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepsGrid.CurrentItem is MacroStep current) SelectedStep = current;
        else if (StepsGrid.SelectedItem is MacroStep selected) SelectedStep = selected;
    }

    private void StepsGridRow_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow row || row.Item is not MacroStep step) return;

        // Treat the narrow strip at either edge of a row as the insertion gap.
        // This keeps ordinary clicks in the row available for selection/editing.
        var position = e.GetPosition(row);
        // Make the insertion zones large enough to hit reliably. The row
        // itself remains the only visible selection; this only records the
        // insertion index used by Paste and AddStep.
        const double edgeSize = 12.0;
        var rowHeight = row.ActualHeight;
        if (rowHeight <= 0) return;

        bool insertBefore;
        if (position.Y <= edgeSize) insertBefore = true;
        else if (position.Y >= rowHeight - edgeSize) insertBefore = false;
        else
        {
            // A normal row click cancels the pending insertion-gap selection.
            ClearInsertionPoint();
            return;
        }

        var list = FindContainingList(step);
        var index = list.IndexOf(step) + (insertBefore ? 0 : 1);
        SetInsertionPoint(list, index, row, insertBefore);
        e.Handled = true;
    }

    private void SetInsertionPoint(IList<MacroStep> list, int index, DataGridRow row, bool before)
    {
        ClearInsertionPoint();
        _insertionList = list;
        _insertionIndex = index;

        // Keep the normal row selection, and additionally draw a clear line
        // on the exact edge that represents the insertion gap.
        _insertionMarkerRow = row;
        _insertionMarkerOriginalBrush = row.BorderBrush;
        _insertionMarkerOriginalThickness = row.BorderThickness;
        row.BorderBrush = System.Windows.Media.Brushes.DeepSkyBlue;
        row.BorderThickness = before
            ? new Thickness(0, 3, 0, 0)
            : new Thickness(0, 0, 0, 3);

        StepsGrid.SelectedItems.Clear();
        row.IsSelected = true;
        StepsGrid.CurrentItem = row.Item;
        SelectedStep = row.Item as MacroStep;
        StatusText.Text = $"青い線が挿入位置です（{(before ? "この操作の前" : "この操作の後")}）。貼り付けまたは操作追加でここに挿入します。";
    }

    private void ClearInsertionPoint()
    {
        if (_insertionMarkerRow is not null)
        {
            _insertionMarkerRow.BorderBrush = _insertionMarkerOriginalBrush;
            _insertionMarkerRow.BorderThickness = _insertionMarkerOriginalThickness;
        }
        _insertionMarkerRow = null;
        _insertionMarkerOriginalBrush = null;
        _insertionMarkerOriginalThickness = default;
        _insertionList = null;
        _insertionIndex = -1;
    }

    private void StepsGridRow_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row && row.Item is MacroStep step)
        {
            // Keep a multi-selection when opening the context menu on one of its selected rows.
            if (!row.IsSelected)
            {
                StepsGrid.SelectedItems.Clear();
                row.IsSelected = true;
            }
            SelectedStep = step;
            StepsGrid.CurrentItem = step;
        }
    }

    private void UpdatePropertyPanel()
    {
        _updatingProperties = true;
        var step = SelectedStep;
        var hasStep = step is not null;
        NameBox.IsEnabled = hasStep;
        KeyPanel.Visibility = hasStep && step!.Type == MacroStepType.KeyPress ? Visibility.Visible : Visibility.Collapsed;
        ClickPanel.Visibility = hasStep && step!.Type == MacroStepType.MouseClick ? Visibility.Visible : Visibility.Collapsed;
        MovePanel.Visibility = hasStep && step!.Type == MacroStepType.MouseMove ? Visibility.Visible : Visibility.Collapsed;
        RelativeMovePanel.Visibility = hasStep && step!.Type == MacroStepType.MouseMoveRelative ? Visibility.Visible : Visibility.Collapsed;
        ScrollPanel.Visibility = hasStep && step!.Type == MacroStepType.MouseScroll ? Visibility.Visible : Visibility.Collapsed;
        WaitPanel.Visibility = hasStep && step!.Type == MacroStepType.Wait ? Visibility.Visible : Visibility.Collapsed;
        RandomWaitPanel.Visibility = hasStep && step!.Type == MacroStepType.RandomWait ? Visibility.Visible : Visibility.Collapsed;
        RepeatPanel.Visibility = hasStep && step!.Type == MacroStepType.Repeat ? Visibility.Visible : Visibility.Collapsed;

        NameBox.Text = step?.Name ?? string.Empty;
        KeyBox.Text = step?.Key ?? "A";
        KeySecondsBox.Text = (step?.Seconds ?? 0.1).ToString(CultureInfo.InvariantCulture);
        ClickCountBox.Text = (step?.RepeatCount ?? 1).ToString(CultureInfo.InvariantCulture);
        XBox.Text = (step?.X ?? 500).ToString(CultureInfo.InvariantCulture);
        YBox.Text = (step?.Y ?? 300).ToString(CultureInfo.InvariantCulture);
        RelativeXBox.Text = (step?.RelativeX ?? 10).ToString(CultureInfo.InvariantCulture);
        RelativeYBox.Text = (step?.RelativeY ?? 0).ToString(CultureInfo.InvariantCulture);
        ScrollDeltaBox.Text = (step?.ScrollDelta ?? 120).ToString(CultureInfo.InvariantCulture);
        RandomWaitMinBox.Text = (step?.Seconds ?? 1).ToString(CultureInfo.InvariantCulture);
        RandomWaitMaxBox.Text = (step?.RandomWaitMaxSeconds ?? 3).ToString(CultureInfo.InvariantCulture);
        WaitSecondsBox.Text = (step?.Seconds ?? 1).ToString(CultureInfo.InvariantCulture);
        RepeatCountBox.Text = (step?.RepeatCount ?? 3).ToString(CultureInfo.InvariantCulture);
        RepeatDurationCheck.IsChecked = step?.RepeatByDuration ?? false;
        RepeatDurationBox.Text = (step?.RepeatDurationSeconds ?? 10).ToString(CultureInfo.InvariantCulture);
        RepeatCountSettings.Visibility = step?.RepeatByDuration == true ? Visibility.Collapsed : Visibility.Visible;
        RepeatDurationSettings.Visibility = step?.RepeatByDuration == true ? Visibility.Visible : Visibility.Collapsed;
        MouseButtonBox.SelectedIndex = step?.MouseButton switch { "Right" => 1, "Middle" => 2, _ => 0 };
        _updatingProperties = false;
    }

    private void Property_Changed(object sender, TextChangedEventArgs e) => ApplyPropertyChanges();

    private void RepeatDurationCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingProperties) return;
        if (SelectedStep is { Type: MacroStepType.Repeat } step)
        {
            PushUndoSnapshot();
            step.RepeatByDuration = RepeatDurationCheck.IsChecked == true;
            RepeatCountSettings.Visibility = step.RepeatByDuration ? Visibility.Collapsed : Visibility.Visible;
            RepeatDurationSettings.Visibility = step.RepeatByDuration ? Visibility.Visible : Visibility.Collapsed;
            step.RefreshComputedProperties();
            StepsGrid.Items.Refresh();
        }
    }

    private void Property_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingProperties) ApplyPropertyChanges();
    }

    private void ApplyPropertyChanges()
    {
        if (_updatingProperties || SelectedStep is not { } step) return;
        PushUndoSnapshot();
        step.Name = NameBox.Text;
        if (step.Type == MacroStepType.KeyPress)
        {
            step.Key = KeyBox.Text;
            if (double.TryParse(KeySecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) step.Seconds = Math.Max(0, seconds);
        }
        else if (step.Type == MacroStepType.MouseClick)
        {
            if (MouseButtonBox.SelectedItem is ComboBoxItem item) step.MouseButton = item.Content?.ToString() ?? "Left";
            if (int.TryParse(ClickCountBox.Text, out var count)) step.RepeatCount = Math.Clamp(count, 1, 10000);
        }
        else if (step.Type == MacroStepType.MouseMove)
        {
            if (int.TryParse(XBox.Text, out var x)) step.X = x;
            if (int.TryParse(YBox.Text, out var y)) step.Y = y;
        }
        else if (step.Type == MacroStepType.MouseMoveRelative)
        {
            if (int.TryParse(RelativeXBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var relativeX)) step.RelativeX = relativeX;
            if (int.TryParse(RelativeYBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var relativeY)) step.RelativeY = relativeY;
        }
        else if (step.Type == MacroStepType.MouseScroll)
        {
            if (int.TryParse(ScrollDeltaBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta)) step.ScrollDelta = delta;
        }
        else if (step.Type == MacroStepType.Wait)
        {
            if (double.TryParse(WaitSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) step.Seconds = Math.Clamp(seconds, 0, 86400);
        }
        else if (step.Type == MacroStepType.RandomWait)
        {
            if (double.TryParse(RandomWaitMinBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minimum)) step.Seconds = Math.Clamp(minimum, 0, 86400);
            if (double.TryParse(RandomWaitMaxBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var maximum)) step.RandomWaitMaxSeconds = Math.Clamp(maximum, step.Seconds, 86400);
        }
        else if (step.Type == MacroStepType.Repeat)
        {
            if (int.TryParse(RepeatCountBox.Text, out var count)) step.RepeatCount = Math.Max(0, count);
            step.RepeatByDuration = RepeatDurationCheck.IsChecked == true;
            if (double.TryParse(RepeatDurationBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                step.RepeatDurationSeconds = Math.Clamp(duration, 0, 86400);
        }
        step.RefreshComputedProperties();
        // Property edits do not change the tree structure. Refreshing VisibleSteps here
        // clears and repopulates the bound collection on every keystroke, which drops
        // the DataGrid selection. Refresh only the displayed cell values instead.
        StepsGrid.Items.Refresh();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RegisterStopHotkey();
        _loadingMacro = true;
        try
        {
            Directory.CreateDirectory(_macroLibraryDirectory);
            foreach (var path in Directory.GetFiles(_macroLibraryDirectory, "*.json"))
            {
                try
                {
                    var document = await MacroDocumentService.LoadAsync(path);
                    MacroLibrary.Add(new MacroLibraryItem
                    {
                        Id = System.IO.Path.GetFileNameWithoutExtension(path),
                        Name = document.Name
                    });
                }
                catch
                {
                    // Ignore damaged entries so one bad macro does not prevent startup.
                }
            }

            if (MacroLibrary.Count == 0)
            {
                var initial = new MacroLibraryItem { Id = Guid.NewGuid().ToString("N"), Name = "マクロ 1" };
                MacroLibrary.Add(initial);
                _currentMacroId = initial.Id;
                await SaveCurrentMacroAsync();
                MacroSelector.SelectedItem = initial;
                StatusText.Text = "初期マクロを作成しました。編集後は「保存」を押してください。";
            }
            else
            {
                var first = MacroLibrary[0];
                MacroSelector.SelectedItem = first;
                _currentMacroId = first.Id;
                var document = await MacroDocumentService.LoadAsync(GetMacroPath(first.Id));
                ReplaceCurrentSteps(document.Steps);
                UpdateSavedMacroSnapshot();
                StatusText.Text = $"「{first.Name}」を読み込みました。";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"マクロ一覧の読み込みに失敗しました。\n{ex.Message}", "読み込みエラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _loadingMacro = false;
        }
    }

    private string GetMacroPath(string id) => System.IO.Path.Combine(_macroLibraryDirectory, $"{id}.json");

    private void ReplaceCurrentSteps(IEnumerable<MacroStep> steps)
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        Steps.Clear();
        foreach (var step in steps) Steps.Add(step);
        RefreshOrderLabels();
        SelectedStep = VisibleSteps.FirstOrDefault();
        StepsGrid.SelectedItem = SelectedStep;
    }

    private string? CreateCurrentMacroSnapshot()
    {
        var item = MacroLibrary.FirstOrDefault(m => m.Id == _currentMacroId);
        if (item is null) return null;
        var document = new MacroDocument { Name = item.Name, Steps = Steps.ToList() };
        return JsonSerializer.Serialize(document);
    }

    private void UpdateSavedMacroSnapshot() => _savedMacroSnapshot = CreateCurrentMacroSnapshot();

    private bool HasUnsavedChanges()
        => _currentMacroId is not null &&
           !string.Equals(CreateCurrentMacroSnapshot(), _savedMacroSnapshot, StringComparison.Ordinal);

    private async Task SaveCurrentMacroAsync()
    {
        Directory.CreateDirectory(_macroLibraryDirectory);
        var item = MacroLibrary.FirstOrDefault(m => m.Id == _currentMacroId);
        if (item is null)
        {
            item = new MacroLibraryItem { Id = Guid.NewGuid().ToString("N"), Name = $"マクロ {MacroLibrary.Count + 1}" };
            MacroLibrary.Add(item);
            _currentMacroId = item.Id;
        }

        var document = new MacroDocument { Name = item.Name, Steps = Steps.ToList() };
        await MacroDocumentService.SaveAsync(GetMacroPath(item.Id), document);
        UpdateSavedMacroSnapshot();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveCurrentMacroAsync();
            StatusText.Text = $"「{MacroLibrary.First(m => m.Id == _currentMacroId).Name}」を保存しました。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存に失敗しました。\n{ex.Message}", "保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MacroSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingMacro || MacroSelector.SelectedItem is not MacroLibraryItem selected) return;
        if (_executionService.IsRunning)
        {
            MessageBox.Show(this, "実行中はマクロを切り替えられません。先に停止してください。", "実行中", MessageBoxButton.OK, MessageBoxImage.Information);
            _loadingMacro = true;
            MacroSelector.SelectedItem = MacroLibrary.FirstOrDefault(m => m.Id == _currentMacroId);
            _loadingMacro = false;
            return;
        }

        try
        {
            if (_currentMacroId is not null)
                await SaveCurrentMacroAsync();

            var document = await MacroDocumentService.LoadAsync(GetMacroPath(selected.Id));
            _loadingMacro = true;
            _currentMacroId = selected.Id;
            ReplaceCurrentSteps(document.Steps);
            UpdateSavedMacroSnapshot();
            _loadingMacro = false;
            StatusText.Text = $"「{selected.Name}」に切り替えました。";
        }
        catch (Exception ex)
        {
            _loadingMacro = false;
            MessageBox.Show(this, $"マクロの切り替えに失敗しました。\n{ex.Message}", "切り替えエラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RenameMacro_Click(object sender, RoutedEventArgs e)
    {
        if (MacroSelector.SelectedItem is not MacroLibraryItem selected) return;
        if (_executionService.IsRunning)
        {
            MessageBox.Show(this, "実行中はマクロ名を変更できません。先に停止してください。", "実行中", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var input = new TextBox
        {
            Text = selected.Name,
            MinWidth = 300,
            Margin = new Thickness(0, 8, 0, 12),
            Padding = new Thickness(8, 6, 8, 6)
        };
        input.SelectAll();

        var okButton = new Button { Content = "変更", Width = 84, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancelButton = new Button { Content = "キャンセル", Width = 92, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { okButton, cancelButton }
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "新しいマクロ名を入力してください。" });
        panel.Children.Add(input);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "マクロ名の変更",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#FF20232B")!,
            Foreground = System.Windows.Media.Brushes.White
        };
        okButton.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => input.Focus();

        if (dialog.ShowDialog() != true) return;

        var newName = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            MessageBox.Show(this, "マクロ名を空欄にはできません。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MacroLibrary.Any(m => m.Id != selected.Id && string.Equals(m.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "同じ名前のマクロが既に存在します。別の名前を指定してください。", "名前の重複", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            selected.Name = newName;
            await SaveCurrentMacroAsync();
            StatusText.Text = $"マクロ名を「{newName}」に変更しました。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"マクロ名の保存に失敗しました。\n{ex.Message}", "名前変更エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void NewMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_executionService.IsRunning)
        {
            MessageBox.Show(this, "実行中は新しいマクロを作成できません。", "実行中", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await SaveCurrentMacroAsync();
            var number = 1;
            while (MacroLibrary.Any(m => m.Name == $"マクロ {number}")) number++;
            var item = new MacroLibraryItem { Id = Guid.NewGuid().ToString("N"), Name = $"マクロ {number}" };
            MacroLibrary.Add(item);
            _loadingMacro = true;
            _currentMacroId = item.Id;
            ReplaceCurrentSteps([]);
            MacroSelector.SelectedItem = item;
            _loadingMacro = false;
            await SaveCurrentMacroAsync();
            StatusText.Text = $"新しいマクロ「{item.Name}」を作成しました。";
        }
        catch (Exception ex)
        {
            _loadingMacro = false;
            MessageBox.Show(this, $"新規作成に失敗しました。\n{ex.Message}", "作成エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteMacro_Click(object sender, RoutedEventArgs e)
    {
        if (MacroSelector.SelectedItem is not MacroLibraryItem selected) return;
        if (_executionService.IsRunning)
        {
            MessageBox.Show(this, "実行中はマクロを削除できません。", "実行中", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"マクロ「{selected.Name}」を削除しますか？", "マクロの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            // Suppress SelectionChanged while the selected entry is removed and
            // the replacement entry is selected below.
            _loadingMacro = true;
            File.Delete(GetMacroPath(selected.Id));
            MacroLibrary.Remove(selected);
            if (MacroLibrary.Count == 0)
            {
                var item = new MacroLibraryItem { Id = Guid.NewGuid().ToString("N"), Name = "マクロ 1" };
                MacroLibrary.Add(item);
                _loadingMacro = true;
                _currentMacroId = item.Id;
                ReplaceCurrentSteps([]);
                MacroSelector.SelectedItem = item;
                _loadingMacro = false;
                await SaveCurrentMacroAsync();
            }
            else
            {
                var next = MacroLibrary[0];
                _loadingMacro = true;
                _currentMacroId = next.Id;
                MacroSelector.SelectedItem = next;
                var document = await MacroDocumentService.LoadAsync(GetMacroPath(next.Id));
                ReplaceCurrentSteps(document.Steps);
                _loadingMacro = false;
            }
            _loadingMacro = false;
            StatusText.Text = $"「{selected.Name}」を削除しました。";
        }
        catch (Exception ex)
        {
            _loadingMacro = false;
            MessageBox.Show(this, $"削除に失敗しました。\n{ex.Message}", "削除エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private string CaptureStepsSnapshot()
        => JsonSerializer.Serialize(Steps.ToList());

    private void PushUndoSnapshot()
    {
        if (_loadingMacro || _isWindowClosed) return;
        var snapshot = CaptureStepsSnapshot();
        if (_undoHistory.Count == 0 || _undoHistory.Peek() != snapshot)
        {
            _undoHistory.Push(snapshot);
            if (_undoHistory.Count > MaxHistoryEntries)
            {
                var retained = _undoHistory.Take(MaxHistoryEntries).Reverse().ToList();
                _undoHistory.Clear();
                foreach (var entry in retained) _undoHistory.Push(entry);
            }
        }
        _redoHistory.Clear();
    }

    private void RestoreHistorySnapshot(string snapshot)
    {
        var restored = JsonSerializer.Deserialize<List<MacroStep>>(snapshot) ?? [];
        _loadingMacro = true;
        Steps.Clear();
        foreach (var step in restored) Steps.Add(step);
        _loadingMacro = false;
        RefreshOrderLabels();
        SelectedStep = VisibleSteps.FirstOrDefault();
        StepsGrid.SelectedItems.Clear();
        if (SelectedStep is not null) StepsGrid.SelectedItem = SelectedStep;
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undoHistory.Count == 0) return;
        _redoHistory.Push(CaptureStepsSnapshot());
        var previous = _undoHistory.Pop();
        RestoreHistorySnapshot(previous);
        StatusText.Text = "直前の編集を取り消しました。";
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redoHistory.Count == 0) return;
        _undoHistory.Push(CaptureStepsSnapshot());
        var next = _redoHistory.Pop();
        RestoreHistorySnapshot(next);
        StatusText.Text = "編集をやり直しました。";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetTopLevelSelectedSteps();
        if (selected.Count == 0 && SelectedStep is not null) selected.Add(SelectedStep);
        if (selected.Count == 0) return;
        var ordered = selected.OrderBy(step => VisibleSteps.IndexOf(step)).ToList();
        // Store originals in the clipboard payload; cloning (including fresh IDs
        // and the copy suffix) happens once, when the user pastes.
        _clipboardStepsJson = JsonSerializer.Serialize(ordered);
        try { Clipboard.SetText(_clipboardStepsJson); } catch { /* Keep the in-app clipboard available. */ }
        StatusText.Text = $"{ordered.Count} 件の操作をコピーしました。";
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        var json = _clipboardStepsJson;
        if (Clipboard.ContainsText())
        {
            try { json = Clipboard.GetText(); } catch { /* Use the in-app clipboard. */ }
        }
        if (string.IsNullOrWhiteSpace(json)) return;

        List<MacroStep>? copies;
        try { copies = JsonSerializer.Deserialize<List<MacroStep>>(json); }
        catch { StatusText.Text = "クリップボードにMacroWeaverの操作データがありません。"; return; }
        if (copies is null || copies.Count == 0) return;

        // Give every pasted node a fresh identity while preserving the complete repeat hierarchy.
        copies = copies.Select(CloneStep).ToList();
        IList<MacroStep> destination;
        int insertAt;
        if (_insertionList is not null && _insertionIndex >= 0)
        {
            destination = _insertionList;
            insertAt = Math.Clamp(_insertionIndex, 0, destination.Count);
        }
        else
        {
            destination = SelectedStep is null ? Steps : FindContainingList(SelectedStep);
            insertAt = SelectedStep is null ? destination.Count : destination.IndexOf(SelectedStep) + 1;
            if (SelectedStep?.Type == MacroStepType.Repeat)
            {
                destination = SelectedStep.Children;
                insertAt = destination.Count;
            }
        }

        PushUndoSnapshot();
        for (var i = 0; i < copies.Count; i++) destination.Insert(insertAt + i, copies[i]);
        ClearInsertionPoint();
        RefreshOrderLabels();
        StepsGrid.SelectedItems.Clear();
        foreach (var copy in copies) StepsGrid.SelectedItems.Add(copy);
        SelectedStep = copies.LastOrDefault();
        if (SelectedStep is not null) StepsGrid.ScrollIntoView(SelectedStep);
        StatusText.Text = $"{copies.Count} 件の操作を貼り付けました。";
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
        // Preserve the normal text-editing shortcuts while a text field is focused.
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        switch (e.Key)
        {
            case System.Windows.Input.Key.C:
                Copy_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case System.Windows.Input.Key.V:
                Paste_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Z:
                Undo_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Y:
                Redo_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private static MacroStep CloneForExecution(MacroStep s) => new()
    {
        Type = s.Type, Name = s.Name, Key = s.Key, MouseButton = s.MouseButton,
        Seconds = s.Seconds, RepeatCount = s.RepeatCount,
        RepeatByDuration = s.RepeatByDuration, RepeatDurationSeconds = s.RepeatDurationSeconds,
        X = s.X, Y = s.Y,
        RelativeX = s.RelativeX, RelativeY = s.RelativeY,
        ScrollDelta = s.ScrollDelta,
        RandomWaitMaxSeconds = s.RandomWaitMaxSeconds,
        Children = s.Children.Select(CloneForExecution).ToList()
    };

    private static IEnumerable<MacroStep> AllSteps(IEnumerable<MacroStep> steps)
        => steps.SelectMany(s => new[] { s }.Concat(AllSteps(s.Children)));

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_executionService.IsRunning) return;
        if (Steps.Count == 0)
        {
            StatusText.Text = "実行する操作がありません。";
            return;
        }

        var allSteps = AllSteps(Steps).ToList();
        if (allSteps.Any(s => s.Type == MacroStepType.Repeat && !s.RepeatByDuration && s.RepeatCount == 0 && s.Children.Count > 0))
        {
            var result = MessageBox.Show(this, "回数が 0 の繰り返しは停止するまで無限に実行されます。続行しますか？", "無限ループの確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        var snapshot = Steps.Select(CloneForExecution).ToList();
        _stopRequested = false;
        var progress = new Progress<string>(message => StatusText.Text = message);
        StatusText.Text = "マクロを開始します。実行中はマウス・キーボードが実際に操作されます。";
        try
        {
            await _executionService.RunAsync(snapshot, progress);
            StatusText.Text = _stopRequested ? "マクロを停止しました。" : "マクロが完了しました。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "マクロを停止しました。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "マクロ実行中にエラーが発生しました。";
            MessageBox.Show(this, $"マクロの実行に失敗しました。\n{ex.Message}", "実行エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (!_executionService.IsRunning) return;
        if (_executionService.IsPaused)
        {
            _executionService.Resume();
            StatusText.Text = "マクロを再開しました。";
        }
        else
        {
            _executionService.Pause();
            StatusText.Text = "マクロを一時停止しました。";
        }
    }

    private void RegisterStopHotkey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hotkeySource = HwndSource.FromHwnd(handle);
        _hotkeySource?.AddHook(HotkeyWindowProc);

        // Ctrl + Alt + S is registered globally, so it also works while another
        // application (including a game) is in the foreground.
        if (!RegisterHotKey(handle, StopHotkeyId, ModControl | ModAlt, 0x53))
            StatusText.Text = "停止ホットキー（Ctrl + Alt + S）を登録できませんでした。別のアプリとの競合を確認してください。";
    }

    private void UnregisterStopHotkey()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            UnregisterHotKey(handle, StopHotkeyId);
        _hotkeySource?.RemoveHook(HotkeyWindowProc);
        _hotkeySource = null;
    }

    private IntPtr HotkeyWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == StopHotkeyId)
        {
            if (_executionService.IsRunning)
                Stop_Click(this, new RoutedEventArgs());
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!_executionService.IsRunning) return;
        _stopRequested = true;
        _executionService.Stop();
        StatusText.Text = "停止要求を送信しました…";
    }
}