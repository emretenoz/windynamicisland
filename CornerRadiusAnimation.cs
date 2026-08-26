using System.Windows;
using System.Windows.Media.Animation;

namespace WinDynamicIsland;

public sealed class CornerRadiusAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(CornerRadius?), typeof(CornerRadiusAnimation));

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(CornerRadius?), typeof(CornerRadiusAnimation));

    public static readonly DependencyProperty EasingFunctionProperty =
        DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(CornerRadiusAnimation));

    public CornerRadius? From
    {
        get => (CornerRadius?)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public CornerRadius? To
    {
        get => (CornerRadius?)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction
    {
        get => (IEasingFunction?)GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }

    public override Type TargetPropertyType => typeof(CornerRadius);

    protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var from = From ?? (CornerRadius)defaultOriginValue;
        var to = To ?? (CornerRadius)defaultDestinationValue;
        var progress = animationClock.CurrentProgress ?? 0;

        if (EasingFunction is not null)
        {
            progress = EasingFunction.Ease(progress);
        }

        return new CornerRadius(
            from.TopLeft + ((to.TopLeft - from.TopLeft) * progress),
            from.TopRight + ((to.TopRight - from.TopRight) * progress),
            from.BottomRight + ((to.BottomRight - from.BottomRight) * progress),
            from.BottomLeft + ((to.BottomLeft - from.BottomLeft) * progress));
    }
}
