using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MacroWeaver.Models;
using MacroWeaver.Services;

namespace MacroWeaver;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private MacroStep? _selectedStep;
    private bool _updatingProperties;
    private readonly MacroExecutionService _executionService = new();
    private bool _stopRequested;

    public ObservableCollection<MacroStep> Steps { get; } = [];
    public ObservableCollection<MacroStep> VisibleSteps { get; } = [];

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
        AddStep(MacroStepType.KeyPress);
        AddStep(MacroStepType.MouseClick);
        AddStep(MacroStepType.Wait);
        StepsGrid.SelectedIndex = 0;
        RefreshOrderLabels();
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
        var step = new MacroStep
        {
            Type = type,
            Seconds = type == MacroStepType.Wait ? 1 : 0.1,
            RepeatCount = type == MacroStepType.Repeat ? 3 : 1,
            Name = type switch
            {
                MacroStepType.KeyPress => "キー入力",
                MacroStepType.MouseClick => "マウスクリック",
                MacroStepType.MouseMove => "マウス移動",
                MacroStepType.Wait => "待機",
                MacroStepType.Repeat => "繰り返し",
                _ => "操作"
            }
        };

        IList<MacroStep> destination;
        if (AddAtRootCheck.IsChecked == true)
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

    private void AddKey_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.KeyPress);
    private void AddClick_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.MouseClick);
    private void AddMove_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.MouseMove);
    private void AddWait_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.Wait);
    private void AddRepeat_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.Repeat);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is null) return;
        var parent = FindContainingList(SelectedStep);
        var index = parent.IndexOf(SelectedStep);
        parent.Remove(SelectedStep);
        RefreshOrderLabels();
        var nextIndex = Math.Clamp(index, 0, Math.Max(0, VisibleSteps.Count - 1));
        SelectedStep = VisibleSteps.Count == 0 ? null : VisibleSteps[nextIndex];
        StepsGrid.SelectedItem = SelectedStep;
        StatusText.Text = "選択した操作を削除しました。";
    }

    private static MacroStep CloneStep(MacroStep original) => new()
    {
        Type = original.Type,
        Name = $"{original.Name} (コピー)",
        Key = original.Key,
        MouseButton = original.MouseButton,
        Seconds = original.Seconds,
        RepeatCount = original.RepeatCount,
        X = original.X,
        Y = original.Y,
        Children = original.Children.Select(CloneStep).ToList()
    };

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is null) return;
        var original = SelectedStep;
        var parent = FindContainingList(original);
        var copy = CloneStep(original);
        var index = parent.IndexOf(original) + 1;
        parent.Insert(index, copy);
        RefreshOrderLabels();
        SelectedStep = copy;
        StepsGrid.SelectedItem = copy;
        StepsGrid.ScrollIntoView(copy);
        StatusText.Text = "操作を複製しました。";
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int offset)
    {
        if (SelectedStep is null) return;
        var parent = FindContainingList(SelectedStep);
        var oldIndex = parent.IndexOf(SelectedStep);
        var newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= parent.Count) return;
        var item = parent[oldIndex];
        parent.RemoveAt(oldIndex);
        parent.Insert(newIndex, item);
        RefreshOrderLabels();
        StepsGrid.SelectedItem = SelectedStep;
        StepsGrid.ScrollIntoView(SelectedStep);
        StatusText.Text = "同じ階層内で操作の順番を変更しました。";
    }

    private void StepsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepsGrid.SelectedItem is MacroStep step) SelectedStep = step;
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
        WaitPanel.Visibility = hasStep && step!.Type == MacroStepType.Wait ? Visibility.Visible : Visibility.Collapsed;
        RepeatPanel.Visibility = hasStep && step!.Type == MacroStepType.Repeat ? Visibility.Visible : Visibility.Collapsed;

        NameBox.Text = step?.Name ?? string.Empty;
        KeyBox.Text = step?.Key ?? "A";
        KeySecondsBox.Text = (step?.Seconds ?? 0.1).ToString(CultureInfo.InvariantCulture);
        ClickCountBox.Text = (step?.RepeatCount ?? 1).ToString(CultureInfo.InvariantCulture);
        XBox.Text = (step?.X ?? 500).ToString(CultureInfo.InvariantCulture);
        YBox.Text = (step?.Y ?? 300).ToString(CultureInfo.InvariantCulture);
        WaitSecondsBox.Text = (step?.Seconds ?? 1).ToString(CultureInfo.InvariantCulture);
        RepeatCountBox.Text = (step?.RepeatCount ?? 3).ToString(CultureInfo.InvariantCulture);
        MouseButtonBox.SelectedIndex = step?.MouseButton switch { "Right" => 1, "Middle" => 2, _ => 0 };
        _updatingProperties = false;
    }

    private void Property_Changed(object sender, TextChangedEventArgs e) => ApplyPropertyChanges();
    private void Property_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingProperties) ApplyPropertyChanges();
    }

    private void ApplyPropertyChanges()
    {
        if (_updatingProperties || SelectedStep is not { } step) return;
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
        else if (step.Type == MacroStepType.Wait)
        {
            if (double.TryParse(WaitSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) step.Seconds = Math.Clamp(seconds, 0, 86400);
        }
        else if (step.Type == MacroStepType.Repeat)
        {
            if (int.TryParse(RepeatCountBox.Text, out var count)) step.RepeatCount = Math.Max(0, count);
        }
        step.RefreshComputedProperties();
        RefreshOrderLabels();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "マクロを保存", Filter = "MacroWeaver マクロ (*.json)|*.json", FileName = "macro.json", DefaultExt = ".json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var document = new MacroDocument { Name = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName), Steps = Steps.ToList() };
            await MacroDocumentService.SaveAsync(dialog.FileName, document);
            StatusText.Text = $"保存しました：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存に失敗しました。\n{ex.Message}", "保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (_executionService.IsRunning)
        {
            MessageBox.Show(this, "実行中はマクロを読み込めません。先に停止してください。", "実行中", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new OpenFileDialog { Title = "マクロを読み込み", Filter = "MacroWeaver マクロ (*.json)|*.json|すべてのファイル (*.*)|*.*", DefaultExt = ".json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var document = await MacroDocumentService.LoadAsync(dialog.FileName);
            Steps.Clear();
            foreach (var step in document.Steps) Steps.Add(step);
            RefreshOrderLabels();
            SelectedStep = VisibleSteps.FirstOrDefault();
            StepsGrid.SelectedItem = SelectedStep;
            StatusText.Text = $"読み込みました：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"読み込みに失敗しました。\n{ex.Message}", "読み込みエラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static MacroStep CloneForExecution(MacroStep s) => new()
    {
        Type = s.Type, Name = s.Name, Key = s.Key, MouseButton = s.MouseButton,
        Seconds = s.Seconds, RepeatCount = s.RepeatCount, X = s.X, Y = s.Y,
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
        if (allSteps.Any(s => s.Type == MacroStepType.Repeat && s.RepeatCount == 0 && s.Children.Count > 0))
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

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!_executionService.IsRunning) return;
        _stopRequested = true;
        _executionService.Stop();
        StatusText.Text = "停止要求を送信しました…";
    }
}