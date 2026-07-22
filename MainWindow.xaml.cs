using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TodoList;

public partial class MainWindow : Window
{
    private const string StartupValueName = "Ticky";
    private const string LegacyStartupValueName = "TodoListLight";
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TodoListLight");
    private static readonly string StatePath = Path.Combine(DataDirectory, "state.json");

    private readonly ObservableCollection<TodoItem> _items = [];
    private readonly DispatcherTimer _opacityCloseTimer;
    private Point _todoDragStartPoint;
    private TodoItem? _draggedTodoItem;
    private bool _isReorderingTodo;
    private bool _didReorderTodo;
    private bool _isShowingCurrentVersion;
    private UpdateCheckResult? _pendingUpdate;
    private AppState _state = new();
    private bool _isLoading = true;

    public MainWindow()
    {
        InitializeComponent();
        TodoItems.ItemsSource = _items;
        _opacityCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _opacityCloseTimer.Tick += OpacityCloseTimer_Tick;

        LoadState();
        ApplyState();
        EnsureStartupEnabled();

        _isLoading = false;
        _ = CheckForUpdatesOnStartupAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyPinMode(save: false);
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return;
            }

            var json = File.ReadAllText(StatePath);
            _state = JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
        }
        catch
        {
            _state = new AppState();
        }
    }

    private void ApplyState()
    {
        foreach (var item in _state.Items)
        {
            item.PropertyChanged += TodoItem_PropertyChanged;
            _items.Add(item);
        }

        PinToggle.IsChecked = _state.IsPinned;
        ApplyPinMode(save: false);

        OpacitySlider.Value = _state.OpacityPercent;
        ApplyOpacity(_state.OpacityPercent);

        if (_state.Width >= MinWidth)
        {
            Width = _state.Width;
        }

        if (_state.Height >= MinHeight)
        {
            Height = _state.Height;
        }

        if (_state.Left is not null && _state.Top is not null && IsOnScreen(_state.Left.Value, _state.Top.Value, Width, Height))
        {
            Left = _state.Left.Value;
            Top = _state.Top.Value;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void AddTodo()
    {
        var text = TodoInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var item = new TodoItem { Text = text };
        item.PropertyChanged += TodoItem_PropertyChanged;
        _items.Add(item);
        TodoInput.Clear();
        SaveState();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        AddTodo();
    }

    private void AddButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        AddTodo();
        e.Handled = true;
    }

    private void AddSeparatorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var targetItem = (sender as FrameworkElement)?.DataContext as TodoItem;
        var separator = new TodoItem { Kind = TodoItem.SeparatorKind };
        separator.PropertyChanged += TodoItem_PropertyChanged;

        var targetIndex = targetItem is null ? -1 : _items.IndexOf(targetItem);
        if (targetIndex < 0)
        {
            _items.Add(separator);
        }
        else
        {
            _items.Insert(targetIndex + 1, separator);
        }

        SaveState();
    }

    private void EditSeparatorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItem { IsSeparator: true } item)
        {
            return;
        }

        EditSeparatorTitle(item);
    }

    private void SeparatorRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItem item)
        {
            return;
        }

        EditSeparatorTitle(item);
        e.Handled = true;
    }

    private void EditSeparatorTitle(TodoItem item)
    {
        var title = ShowSeparatorTitleDialog(item.SeparatorTitle);
        if (title is null)
        {
            return;
        }

        item.SeparatorTitle = title.Trim();
        SaveState();
    }

    private void DeleteTodoButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteItemFromElement(sender as FrameworkElement);
    }

    private void DeleteTodoButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DeleteItemFromElement(sender as FrameworkElement);
        e.Handled = true;
    }

    private void DeleteItemFromElement(FrameworkElement? element)
    {
        if (element is null)
        {
            return;
        }

        var item = element.Tag as TodoItem ?? element.DataContext as TodoItem;
        if (item is null)
        {
            return;
        }

        item.PropertyChanged -= TodoItem_PropertyChanged;
        _items.Remove(item);
        SaveState();
    }

    private void TodoItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Button>(e.OriginalSource as DependencyObject) is not null ||
            FindParent<CheckBox>(e.OriginalSource as DependencyObject) is not null)
        {
            _draggedTodoItem = null;
            return;
        }

        _todoDragStartPoint = e.GetPosition(null);
        _draggedTodoItem = (sender as FrameworkElement)?.DataContext as TodoItem;
        _isReorderingTodo = false;
        _didReorderTodo = false;
    }

    private void TodoItem_MouseMove(object sender, MouseEventArgs e)
    {
        UpdateTodoDrag(e);
    }

    private void TodoCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItem item)
        {
            return;
        }

        item.IsDone = !item.IsDone;
        e.Handled = true;
    }

    private void TodoItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItem item)
        {
            return;
        }

        if (item.IsSeparator)
        {
            EditSeparatorTitle(item);
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu { DataContext = item };
        var addSeparatorItem = new MenuItem { Header = "구분선 추가", DataContext = item };
        addSeparatorItem.Click += AddSeparatorMenuItem_Click;
        menu.Items.Add(addSeparatorItem);
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (IsMouseOver)
        {
            TopControls.Opacity = 1;
            TopControls.IsHitTestVisible = true;
            UpdateCurrentVersionBannerVisibility();
        }

        UpdateTodoDrag(e);
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        TopControls.Opacity = 1;
        TopControls.IsHitTestVisible = true;
        UpdateCurrentVersionBannerVisibility();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        TopControls.Opacity = 0;
        TopControls.IsHitTestVisible = false;
        UpdateCurrentVersionBannerVisibility();

        if (OpacityPopup.IsOpen)
        {
            _opacityCloseTimer.Stop();
            _opacityCloseTimer.Start();
            return;
        }

        _opacityCloseTimer.Stop();
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isReorderingTodo && _didReorderTodo)
        {
            SaveState();
        }

        EndTodoDrag();
    }

    private void UpdateTodoDrag(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedTodoItem is null)
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        var movedEnough =
            Math.Abs(currentPosition.X - _todoDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(currentPosition.Y - _todoDragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance;

        if (!movedEnough)
        {
            return;
        }

        if (!_isReorderingTodo)
        {
            BeginTodoDrag();
        }

        var windowPosition = e.GetPosition(this);
        UpdateDragGhost(windowPosition);
        ReorderTodoUnderPointer(windowPosition);
        e.Handled = true;
    }

    private void BeginTodoDrag()
    {
        _isReorderingTodo = true;
        DragGhostText.Text = _draggedTodoItem?.DisplayText ?? string.Empty;
        DragGhost.Visibility = Visibility.Visible;
        Mouse.Capture(this, CaptureMode.SubTree);
    }

    private void UpdateDragGhost(Point position)
    {
        Canvas.SetLeft(DragGhost, Math.Clamp(position.X + 12, 8, Math.Max(8, ActualWidth - DragGhost.Width - 8)));
        Canvas.SetTop(DragGhost, Math.Clamp(position.Y + 10, 8, Math.Max(8, ActualHeight - 56)));
    }

    private void ReorderTodoUnderPointer(Point position)
    {
        var hit = VisualTreeHelper.HitTest(this, position);
        var targetItem = FindParentWithDataContext<TodoItem>(hit?.VisualHit) ??
                         FindNearestItemByY(position);
        if (_draggedTodoItem is null ||
            targetItem is null ||
            ReferenceEquals(_draggedTodoItem, targetItem))
        {
            return;
        }

        var oldIndex = _items.IndexOf(_draggedTodoItem);
        var newIndex = _items.IndexOf(targetItem);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
        {
            return;
        }

        _items.Move(oldIndex, newIndex);
        _didReorderTodo = true;
    }

    private TodoItem? FindNearestItemByY(Point position)
    {
        TodoItem? nearestItem = null;
        var nearestDistance = double.MaxValue;

        foreach (var item in _items)
        {
            if (TodoItems.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
            {
                continue;
            }

            var topLeft = container.TransformToAncestor(this).Transform(new Point(0, 0));
            var top = topLeft.Y - 10;
            var bottom = topLeft.Y + Math.Max(container.ActualHeight, 32) + 10;
            if (position.Y >= top && position.Y <= bottom)
            {
                return item;
            }

            var center = (top + bottom) / 2;
            var distance = Math.Abs(position.Y - center);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestItem = item;
            }
        }

        return nearestDistance <= 28 ? nearestItem : null;
    }

    private void EndTodoDrag()
    {
        _draggedTodoItem = null;
        _isReorderingTodo = false;
        _didReorderTodo = false;
        DragGhost.Visibility = Visibility.Collapsed;
        Mouse.Capture(null);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed ||
            FindParent<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        DragMove();
    }

    private void TopResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromTop(e.VerticalChange);
    }

    private void BottomResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromBottom(e.VerticalChange);
    }

    private void LeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromLeft(e.HorizontalChange);
    }

    private void RightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromRight(e.HorizontalChange);
    }

    private void TopLeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromTop(e.VerticalChange);
        ResizeFromLeft(e.HorizontalChange);
    }

    private void TopRightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromTop(e.VerticalChange);
        ResizeFromRight(e.HorizontalChange);
    }

    private void BottomLeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromBottom(e.VerticalChange);
        ResizeFromLeft(e.HorizontalChange);
    }

    private void BottomRightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromBottom(e.VerticalChange);
        ResizeFromRight(e.HorizontalChange);
    }

    private void ResizeFromTop(double delta)
    {
        var newHeight = Height - delta;
        if (newHeight < MinHeight)
        {
            delta = Height - MinHeight;
            newHeight = MinHeight;
        }

        Top += delta;
        Height = newHeight;
        SaveState();
    }

    private void ResizeFromBottom(double delta)
    {
        Height = Math.Max(MinHeight, Height + delta);
        SaveState();
    }

    private void ResizeFromLeft(double delta)
    {
        var newWidth = Width - delta;
        if (newWidth < MinWidth)
        {
            delta = Width - MinWidth;
            newWidth = MinWidth;
        }

        Left += delta;
        Width = newWidth;
        SaveState();
    }

    private void ResizeFromRight(double delta)
    {
        Width = Math.Max(MinWidth, Width + delta);
        SaveState();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MinimizeButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WindowState = WindowState.Minimized;
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CloseButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var result = await UpdateService.CheckLatestAsync();
            if (!result.HasUpdate)
            {
                ShowCurrentVersion(result.CurrentVersion);
                return;
            }

            _pendingUpdate = result;
            ShowUpdateAvailable();
            UpdateBannerText.Text = "업데이트 확인됨";
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch
        {
            // Startup update checks should never block the todo app.
        }
    }

    private void ShowCurrentVersion(string version)
    {
        _isShowingCurrentVersion = true;
        _pendingUpdate = null;
        UpdateBannerDot.Fill = (Brush)FindResource("Line");
        UpdateBannerText.Text = $"v{version}";
        UpdateBanner.Cursor = Cursors.Arrow;
        UpdateBanner.IsHitTestVisible = false;
        UpdateCurrentVersionBannerVisibility();
    }

    private void ShowUpdateAvailable()
    {
        _isShowingCurrentVersion = false;
        UpdateBannerDot.Fill = new SolidColorBrush(Color.FromRgb(49, 196, 141));
        UpdateBanner.Cursor = Cursors.Hand;
        UpdateBanner.IsHitTestVisible = true;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void UpdateCurrentVersionBannerVisibility()
    {
        if (!_isShowingCurrentVersion)
        {
            return;
        }

        UpdateBanner.Visibility = IsMouseOver ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UpdateBanner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        UpdateBannerText.Text = "업데이트 설치 중...";
        UpdateBanner.IsHitTestVisible = false;

        try
        {
            await UpdateService.InstallAsync(_pendingUpdate);
        }
        catch (Exception ex)
        {
            UpdateBannerText.Text = "업데이트 실패";
            UpdateBanner.IsHitTestVisible = true;
            MessageBox.Show(
                this,
                $"업데이트 중 오류가 발생했습니다.\n{ex.Message}",
                "Ticky Update",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpacityButton_MouseEnter(object sender, MouseEventArgs e)
    {
        _opacityCloseTimer.Stop();
        OpacityPopup.IsOpen = true;
    }

    private void OpacityButton_MouseLeave(object sender, MouseEventArgs e)
    {
        _opacityCloseTimer.Stop();
        _opacityCloseTimer.Start();
    }

    private void OpacityHoverArea_MouseEnter(object sender, MouseEventArgs e)
    {
        _opacityCloseTimer.Stop();
    }

    private void OpacityHoverArea_MouseLeave(object sender, MouseEventArgs e)
    {
        _opacityCloseTimer.Stop();
        _opacityCloseTimer.Start();
    }

    private void OpacityCloseTimer_Tick(object? sender, EventArgs e)
    {
        _opacityCloseTimer.Stop();

        if (OpacityButton.IsMouseOver || OpacityHoverArea.IsMouseOver || OpacitySlider.IsMouseOver)
        {
            _opacityCloseTimer.Start();
            return;
        }

        OpacityPopup.IsOpen = false;
    }

    private void TodoInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        AddTodo();
        e.Handled = true;
    }

    private void PinToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        PinToggle.IsChecked = PinToggle.IsChecked != true;
        ApplyPinMode(save: true);
        e.Handled = true;
    }

    private void PinToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        ApplyPinMode(save: true);
    }

    private void ApplyPinMode(bool save)
    {
        var isPinned = PinToggle.IsChecked == true;
        if (isPinned)
        {
            Topmost = false;
            Topmost = true;
            Activate();
            ApplyNativeTopmost(HwndTopmost);
        }
        else
        {
            Topmost = false;
            ApplyNativeTopmost(HwndNotTopmost);
        }

        if (save)
        {
            SaveState();
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyOpacity(e.NewValue);

        if (!_isLoading)
        {
            SaveState();
        }
    }

    private void ApplyOpacity(double percent)
    {
        var value = Math.Clamp(percent, 45, 100);
        Opacity = value / 100;

        if (OpacityValue is not null)
        {
            OpacityValue.Text = $"{value:0}%";
        }
    }

    private void TodoItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_isLoading)
        {
            SaveState();
        }
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (!_isLoading)
        {
            SaveState();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveState();
    }

    private void SaveState()
    {
        Directory.CreateDirectory(DataDirectory);

        _state = new AppState
        {
            Items = [.. _items],
            IsPinned = PinToggle.IsChecked == true,
            OpacityPercent = Math.Clamp(OpacitySlider.Value, 45, 100),
            Left = GetFiniteOrNull(RestoreBounds.Left),
            Top = GetFiniteOrNull(RestoreBounds.Top),
            Width = GetFiniteOrDefault(RestoreBounds.Width, Width),
            Height = GetFiniteOrDefault(RestoreBounds.Height, Height)
        };

        var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(StatePath, json);
    }

    private static double? GetFiniteOrNull(double value)
    {
        return double.IsFinite(value) ? value : null;
    }

    private static double GetFiniteOrDefault(double value, double fallback)
    {
        return double.IsFinite(value) ? value : fallback;
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            return false;
        }

        var right = left + width;
        var bottom = top + height;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        return right > screenLeft && left < screenRight && bottom > screenTop && top < screenBottom;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T typedParent)
            {
                return typedParent;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private static T? FindParentWithDataContext<T>(DependencyObject? child) where T : class
    {
        while (child is not null)
        {
            if (child is FrameworkElement { DataContext: T dataContext })
            {
                return dataContext;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void ApplyNativeTopmost(IntPtr insertAfter)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(handle, insertAfter, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private string? ShowSeparatorTitleDialog(string currentTitle)
    {
        var dialog = new Window
        {
            Title = "구분선 제목",
            Width = 260,
            Height = 130,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            FontFamily = FontFamily,
            Topmost = Topmost
        };

        var input = new TextBox
        {
            Text = currentTitle,
            Margin = new Thickness(14, 12, 14, 10),
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var okButton = new Button
        {
            Content = "확인",
            Width = 68,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var cancelButton = new Button
        {
            Content = "취소",
            Width = 68,
            Height = 28
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(14, 0, 14, 12)
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(input);

        dialog.Content = new Border
        {
            Background = (Brush)FindResource("Paper"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = panel
        };

        okButton.Click += (_, _) =>
        {
            dialog.DialogResult = true;
            dialog.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            dialog.DialogResult = false;
            dialog.Close();
        };

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                dialog.DialogResult = true;
                dialog.Close();
                e.Handled = true;
            }
        };

        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return dialog.ShowDialog() == true ? input.Text : null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    private static void EnsureStartupEnabled()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                writable: true);

            var processPath = Environment.ProcessPath;
            if (runKey is null || string.IsNullOrWhiteSpace(processPath))
            {
                return;
            }

            if (runKey.GetValue(LegacyStartupValueName) is not null)
            {
                runKey.DeleteValue(LegacyStartupValueName, throwOnMissingValue: false);
            }

            var existingValue = runKey.GetValue(StartupValueName) as string;
            var startupCommand = $"\"{processPath}\"";

            if (!string.Equals(existingValue, startupCommand, StringComparison.OrdinalIgnoreCase))
            {
                runKey.SetValue(StartupValueName, startupCommand);
            }
        }
        catch
        {
            Debug.WriteLine("Failed to register startup entry.");
        }
    }
}

public sealed class TodoItem : INotifyPropertyChanged
{
    public const string TodoKind = "Todo";
    public const string SeparatorKind = "Separator";

    private string _kind = TodoKind;
    private string _text = string.Empty;
    private string _separatorTitle = string.Empty;
    private bool _isDone;

    public string Kind
    {
        get => string.IsNullOrWhiteSpace(_kind) ? TodoKind : _kind;
        set
        {
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? TodoKind : value;
            if (_kind == normalizedValue)
            {
                return;
            }

            _kind = normalizedValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSeparator));
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    public string SeparatorTitle
    {
        get => _separatorTitle;
        set
        {
            if (_separatorTitle == value)
            {
                return;
            }

            _separatorTitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (_isDone == value)
            {
                return;
            }

            _isDone = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public bool IsSeparator => string.Equals(Kind, SeparatorKind, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string DisplayText => IsSeparator
        ? string.IsNullOrWhiteSpace(SeparatorTitle) ? "구분선" : SeparatorTitle
        : Text;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class AppState
{
    public List<TodoItem> Items { get; set; } = [];
    public bool IsPinned { get; set; } = true;
    public double OpacityPercent { get; set; } = 95;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 520;
}
