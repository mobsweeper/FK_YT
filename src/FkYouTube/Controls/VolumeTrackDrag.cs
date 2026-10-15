using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FkYouTube.Controls;

/// <summary>Continue a move-to-point track press as a captured volume drag.</summary>
public static class VolumeTrackDrag
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(VolumeTrackDrag), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty DraggingProperty = DependencyProperty.RegisterAttached(
        "Dragging", typeof(bool), typeof(VolumeTrackDrag), new PropertyMetadata(false));

    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Slider slider) return;
        if ((bool)args.NewValue)
        {
            // Slider's class handler handles a track press before ordinary handlers.
            slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down), true);
            slider.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(Move), true);
            slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Up), true);
            slider.LostMouseCapture += LostCapture;
            slider.Unloaded += Unloaded;
        }
        else
        {
            slider.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down));
            slider.RemoveHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(Move));
            slider.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Up));
            slider.LostMouseCapture -= LostCapture;
            slider.Unloaded -= Unloaded;
            Finish(slider);
        }
    }

    private static void Down(object sender, MouseButtonEventArgs args)
    {
        var slider = (Slider)sender;
        if (!slider.IsEnabled || IsThumbPress(args.OriginalSource as DependencyObject, slider) ||
            slider.Template.FindName("PART_Track", slider) is not Track track) return;
        slider.Focus();
        SetFromPoint(slider, track, args.GetPosition(track));
        if (Mouse.Capture(slider, CaptureMode.SubTree)) slider.SetValue(DraggingProperty, true);
        args.Handled = true;
    }

    private static void Move(object sender, MouseEventArgs args)
    {
        var slider = (Slider)sender;
        if (!(bool)slider.GetValue(DraggingProperty) || Mouse.Captured != slider ||
            slider.Template.FindName("PART_Track", slider) is not Track track) return;
        SetFromPoint(slider, track, args.GetPosition(track));
        args.Handled = true;
    }

    private static void Up(object sender, MouseButtonEventArgs args)
    {
        var slider = (Slider)sender;
        if (!(bool)slider.GetValue(DraggingProperty)) return;
        Finish(slider);
        args.Handled = true;
    }

    private static void LostCapture(object sender, MouseEventArgs args) => ((Slider)sender).SetValue(DraggingProperty, false);
    private static void Unloaded(object sender, RoutedEventArgs args) => Finish((Slider)sender);

    private static void Finish(Slider slider)
    {
        slider.SetValue(DraggingProperty, false);
        if (Mouse.Captured == slider) slider.ReleaseMouseCapture();
    }

    private static void SetFromPoint(Slider slider, Track track, Point point)
    {
        track.UpdateLayout();
        var value = track.ValueFromPoint(point);
        if (double.IsFinite(value))
            slider.SetCurrentValue(RangeBase.ValueProperty, Math.Clamp(value, slider.Minimum, slider.Maximum));
    }

    private static bool IsThumbPress(DependencyObject? source, Slider slider)
    {
        for (var current = source; current is not null && current != slider;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is Thumb) return true;
        return false;
    }
}
