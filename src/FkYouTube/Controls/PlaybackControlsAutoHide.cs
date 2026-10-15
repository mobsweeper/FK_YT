using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FkYouTube.Controls;

/// <summary>Slides a playback tray out of its layout row, keeping native video airspace clear.</summary>
public sealed class PlaybackControlsAutoHide : IDisposable
{
    private const double IdleSeconds = 3;
    private const double WakeDistance = 64;
    private static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(180));
    private readonly Window _owner;
    private readonly FrameworkElement _pointerArea;
    private readonly Border _tray;
    private readonly FrameworkElement _wakeZone;
    private readonly FrameworkElement _content;
    private readonly Func<bool>? _interactionBusy;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Transform _originalTransform;
    private readonly TranslateTransform _slide = new();
    private readonly Visibility _originalVisibility;
    private readonly bool _originalHitTestVisible;
    private readonly HashSet<Key> _pressedKeys = [];
    private Point? _lastPointer;
    private double _lastInteractionAt;
    private double _shownHeight;
    private int _animationVersion;
    private bool _enabled, _hidden, _disposed;

    public PlaybackControlsAutoHide(Window owner, FrameworkElement pointerArea, Border tray,
        FrameworkElement wakeZone, Func<bool>? interactionBusy = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(pointerArea);
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(wakeZone);
        owner.Dispatcher.VerifyAccess();
        _owner = owner; _pointerArea = pointerArea; _tray = tray; _wakeZone = wakeZone;
        _content = tray.Child as FrameworkElement ??
            throw new ArgumentException("The tray must contain the complete playback controls.", nameof(tray));
        _interactionBusy = interactionBusy;
        _originalTransform = _content.RenderTransform;
        _originalVisibility = _content.Visibility;
        _originalHitTestVisible = _content.IsHitTestVisible;
        var transform = new TransformGroup();
        transform.Children.Add(_originalTransform);
        transform.Children.Add(_slide);
        _content.RenderTransform = transform;
        _tray.ClipToBounds = true;
        _wakeZone.Visibility = Visibility.Collapsed;
        _timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += Tick;
        _pointerArea.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(PointerMove), true);
        _pointerArea.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PointerButton), true);
        _pointerArea.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(PointerButton), true);
        _pointerArea.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(PointerWheel), true);
        _owner.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDown), true);
        _owner.AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(KeyUp), true);
        _tray.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(KeyboardFocus), true);
        _owner.Deactivated += Deactivated;
        _owner.Closed += Closed;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _owner.Dispatcher.VerifyAccess();
            if (_disposed || _enabled == value) return;
            _enabled = value;
            _lastPointer = _pointerArea.IsMouseOver ? Mouse.GetPosition(_pointerArea) : null;
            _pressedKeys.Clear();
            if (value)
            {
                _wakeZone.Visibility = Visibility.Visible;
                Reveal();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                _wakeZone.Visibility = Visibility.Collapsed;
                ShowImmediately();
            }
        }
    }

    /// <summary>Shows controls for another idle interval without retaining keyboard focus as a hold.</summary>
    public void Reveal()
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _lastInteractionAt = _clock.Elapsed.TotalSeconds;
        if (!_hidden) return;
        _hidden = false;
        _content.Visibility = _originalVisibility;
        _content.IsHitTestVisible = _originalHitTestVisible;
        MeasureShownHeight();
        Animate(shown: true);
    }

    private void Tick(object? sender, EventArgs args)
    {
        if (!_enabled || _disposed) return;
        // Poll only this WPF area. A disabled HwndHost lets its parent receive pointer input.
        if (_pointerArea.IsMouseOver) ObservePointer(Mouse.GetPosition(_pointerArea));
        else _lastPointer = null;
        if (InteractionBusy())
        {
            Reveal();
            return;
        }
        if (!_hidden && _clock.Elapsed.TotalSeconds - _lastInteractionAt >= IdleSeconds &&
            _tray.IsVisible && _tray.ActualHeight > 0)
            Hide();
    }

    private bool InteractionBusy()
    {
        if (Mouse.Captured is not null || Mouse.LeftButton == MouseButtonState.Pressed ||
            Mouse.RightButton == MouseButtonState.Pressed || Mouse.MiddleButton == MouseButtonState.Pressed ||
            Mouse.XButton1 == MouseButtonState.Pressed || Mouse.XButton2 == MouseButtonState.Pressed ||
            _interactionBusy?.Invoke() == true)
            return true;
        _pressedKeys.RemoveWhere(key => !Keyboard.IsKeyDown(key));
        return _pressedKeys.Count > 0 || HasOpenMenu(_content);
    }

    private static bool HasOpenMenu(DependencyObject element)
    {
        if (element is FrameworkElement { ContextMenu.IsOpen: true } ||
            element is ComboBox { IsDropDownOpen: true } ||
            element is MenuItem { IsSubmenuOpen: true } || element is Popup { IsOpen: true })
            return true;
        if (element is not Visual) return false;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (HasOpenMenu(VisualTreeHelper.GetChild(element, index))) return true;
        return false;
    }

    private void PointerMove(object sender, MouseEventArgs args)
    {
        if (_enabled) ObservePointer(args.GetPosition(_pointerArea));
    }

    private void ObservePointer(Point position)
    {
        if (!_enabled || _disposed || _lastPointer == position) return;
        _lastPointer = position;
        if (position.X < 0 || position.X > _pointerArea.ActualWidth || position.Y < 0 ||
            position.Y > _pointerArea.ActualHeight) return;
        if (_hidden)
        {
            if (position.Y >= Math.Max(0, _pointerArea.ActualHeight - WakeDistance)) Reveal();
            return;
        }
        var inTray = _pointerArea.TranslatePoint(position, _tray);
        if (inTray.X >= 0 && inTray.X <= _tray.ActualWidth && inTray.Y >= 0 && inTray.Y <= _tray.ActualHeight)
            _lastInteractionAt = _clock.Elapsed.TotalSeconds;
    }

    private void PointerButton(object sender, MouseButtonEventArgs args) { if (_enabled) Reveal(); }
    private void PointerWheel(object sender, MouseWheelEventArgs args) { if (_enabled) Reveal(); }
    private void KeyDown(object sender, KeyEventArgs args)
    {
        if (!_enabled) return;
        _pressedKeys.Add(args.Key == Key.System ? args.SystemKey : args.Key);
        Reveal(); // Reveal before Tab navigates into a tray that was hidden.
    }
    private void KeyUp(object sender, KeyEventArgs args)
    {
        if (!_enabled) return;
        _pressedKeys.Remove(args.Key == Key.System ? args.SystemKey : args.Key);
        Reveal();
    }
    private void KeyboardFocus(object sender, KeyboardFocusChangedEventArgs args) { if (_enabled) Reveal(); }
    private void Deactivated(object? sender, EventArgs args) => _pressedKeys.Clear();
    private void Closed(object? sender, EventArgs args) => Dispose();

    private void Hide()
    {
        _shownHeight = _tray.ActualHeight;
        _hidden = true;
        // Ignore clipped controls during the slide; Hidden also removes them from Tab traversal.
        _content.IsHitTestVisible = false;
        Animate(shown: false);
    }

    private double MeasureShownHeight()
    {
        var width = Math.Max(0, _tray.ActualWidth - _tray.Padding.Left - _tray.Padding.Right -
            _tray.BorderThickness.Left - _tray.BorderThickness.Right);
        _content.Measure(new Size(width, double.PositiveInfinity));
        _shownHeight = Math.Max(1, _content.DesiredSize.Height + _tray.Padding.Top + _tray.Padding.Bottom +
            _tray.BorderThickness.Top + _tray.BorderThickness.Bottom);
        return _shownHeight;
    }

    private void Animate(bool shown)
    {
        var version = ++_animationVersion;
        var fromOffset = _slide.Y;
        _tray.BeginAnimation(FrameworkElement.HeightProperty, null);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        // Reserve the controls row throughout the slide. Expand once before a
        // reveal and collapse once after a hide, keeping the native video outside
        // WPF airspace without resizing its HWND on every animation frame.
        if (shown) _tray.Height = double.NaN;
        _slide.Y = fromOffset;
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        easing.Freeze();
        var slide = new DoubleAnimation(fromOffset, shown ? 0 : _shownHeight, SlideDuration)
            { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        slide.Completed += (_, _) =>
        {
            if (_disposed || version != _animationVersion) return;
            _tray.BeginAnimation(FrameworkElement.HeightProperty, null);
            _slide.BeginAnimation(TranslateTransform.YProperty, null);
            _tray.Height = shown ? double.NaN : 0;
            _slide.Y = shown ? 0 : _shownHeight;
            if (!shown) _content.Visibility = Visibility.Hidden;
        };
        _slide.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void ShowImmediately()
    {
        ++_animationVersion;
        _hidden = false;
        _tray.BeginAnimation(FrameworkElement.HeightProperty, null);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        _tray.Height = double.NaN;
        _slide.Y = 0;
        _content.Visibility = _originalVisibility;
        _content.IsHitTestVisible = _originalHitTestVisible;
    }

    public void Dispose()
    {
        _owner.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _enabled = false;
        _timer.Stop();
        _timer.Tick -= Tick;
        ShowImmediately();
        _wakeZone.Visibility = Visibility.Collapsed;
        _content.RenderTransform = _originalTransform;
        _pointerArea.RemoveHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(PointerMove));
        _pointerArea.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(PointerButton));
        _pointerArea.RemoveHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(PointerButton));
        _pointerArea.RemoveHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(PointerWheel));
        _owner.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(KeyDown));
        _owner.RemoveHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(KeyUp));
        _tray.RemoveHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(KeyboardFocus));
        _owner.Deactivated -= Deactivated;
        _owner.Closed -= Closed;
        _pressedKeys.Clear();
        _disposed = true;
    }
}
