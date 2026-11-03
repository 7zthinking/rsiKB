using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KBEA.App.Helpers;

/// <summary>附加属性：为 ScrollViewer 的鼠标滚轮提供带缓动的平滑滚动。内部仍有滚动余量的嵌套 ScrollViewer 优先自行滚动。</summary>
public static class SmoothScroll
{
    private static readonly Duration ScrollDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(OnOffsetChanged));
    private static readonly DependencyProperty AnimationProperty = DependencyProperty.RegisterAttached(
        "Animation", typeof(DoubleAnimation), typeof(SmoothScroll), new PropertyMetadata(null));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnabledChanged));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        if ((bool)e.NewValue) viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        else viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer viewer && !double.IsNaN((double)e.NewValue)) viewer.ScrollToVerticalOffset((double)e.NewValue);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, viewer); node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer inner && inner.ScrollableHeight > 0) return;

        var target = Math.Clamp(viewer.VerticalOffset - e.Delta, 0, viewer.ScrollableHeight);
        e.Handled = true;
        if (Math.Abs(target - viewer.VerticalOffset) < 0.1) return;

        var animation = (DoubleAnimation?)viewer.GetValue(AnimationProperty);
        if (animation is null)
        {
            animation = new DoubleAnimation { Duration = ScrollDuration, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            viewer.SetValue(AnimationProperty, animation);
        }
        animation.From = viewer.VerticalOffset;
        animation.To = target;
        viewer.BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
