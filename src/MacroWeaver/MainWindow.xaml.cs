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

    public ObservableCollection<MacroStep> Steps { get; } = [];

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
        UpdateStepCount();
    }

    private void Steps_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshOrderLabels();
        UpdateStepCount();
    }

    private void RefreshOrderLabels()
    {
        for (var i = 0; i < Steps.Count; i++) Steps[i].OrderLabel = (i + 1).ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateStepCount() => StepCountText.Text = $"{Steps.Count} 操作";

    private void AddStep(MacroStepType type)
    {
        var step = new MacroStep { Type = type, Name = type switch
        {
            MacroStepType.KeyPress => "キー入力",
            MacroStepType.MouseClick => "マウスクリック",
            MacroStepType.MouseMove => "マウス移動",
            MacroStepType.Wait => "待機",
            MacroStepType.Repeat => "繰り返し",
            _ => "操作"
        }};
        Steps.Add(step);
        SelectedStep = step;
        StepsGrid.SelectedItem = step;
        StepsGrid.ScrollIntoView(step);
        StatusText.Text = $"「{step.Name}」を追加しました。右側で設定を編集できます。";
    }

    private void AddKey_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.KeyPress);
    private void AddClick_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.MouseClick);
    private void AddMove_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.MouseMove);
    private void AddWait_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.Wait);
    private void AddRepeat_Click(object sender, RoutedEventArgs e) => AddStep(MacroStepType.Repeat);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is null) return;
        var index = Steps.IndexOf(SelectedStep);
        Steps.Remove(SelectedStep);
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Clamp(index, 0, Steps.Count - 1)];
        StepsGrid.SelectedItem = SelectedStep;
        StatusText.Text = "選択した操作を削除しました。";
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStep is null) return;
        var original = SelectedStep;
        var copy = new MacroStep
        {
            Type = original.Type,
            Name = $"{original.Name} (コピー)",
            Key = original.Key,
            MouseButton = original.MouseButton,
            Seconds = original.Seconds,
            RepeatCount = original.RepeatCount,
            X = original.X,
            Y = original.Y
        };
        var index = Steps.IndexOf(original) + 1;
        Steps.Insert(index, copy);
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
        var oldIndex = Steps.IndexOf(SelectedStep);
        var newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= Steps.Count) return;
        Steps.Move(oldIndex, newIndex);
        StepsGrid.SelectedItem = SelectedStep;
        StepsGrid.ScrollIntoView(SelectedStep);
        StatusText.Text = "操作の順番を変更しました。";
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
            if (int.TryParse(ClickCountBox.Text, out var count)) step.RepeatCount = Math.Max(1, count);
        }
        else if (step.Type == MacroStepType.MouseMove)
        {
            if (int.TryParse(XBox.Text, out var x)) step.X = x;
            if (int.TryParse(YBox.Text, out var y)) step.Y = y;
        }
        else if (step.Type == MacroStepType.Wait)
        {
            if (double.TryParse(WaitSecondsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) step.Seconds = Math.Max(0, seconds);
        }
        else if (step.Type == MacroStepType.Repeat)
        {
            if (int.TryParse(RepeatCountBox.Text, out var count)) step.RepeatCount = Math.Max(0, count);
        }
        step.RefreshComputedProperties();
        StepsGrid.Items.Refresh();
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
        var dialog = new OpenFileDialog { Title = "マクロを読み込み", Filter = "MacroWeaver マクロ (*.json)|*.json|すべてのファイル (*.*)|*.*", DefaultExt = ".json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var document = await MacroDocumentService.LoadAsync(dialog.FileName);
            Steps.Clear();
            foreach (var step in document.Steps) Steps.Add(step);
            SelectedStep = Steps.FirstOrDefault();
            StepsGrid.SelectedItem = SelectedStep;
            StatusText.Text = $"読み込みました：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"読み込みに失敗しました。\n{ex.Message}", "読み込みエラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Run_Click(object sender, RoutedEventArgs e)
        => StatusText.Text = "実行エンジンは未実装です。次の段階で安全な停止制御とともに追加します。";

    private void Pause_Click(object sender, RoutedEventArgs e)
        => StatusText.Text = "一時停止は実行エンジンの実装後に利用できます。";

    private void Stop_Click(object sender, RoutedEventArgs e)
        => StatusText.Text = "停止しました（現在は編集画面のみ動作します）。";
}
