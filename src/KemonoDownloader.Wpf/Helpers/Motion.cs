using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
// 项目同时启用了 UseWindowsForms，System.Drawing 会带进 Point/Size/Brush 等重名类型，
// 这里显式限定到 WPF 版本。
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Colors = System.Windows.Media.Colors;

namespace KemonoDownloader.Wpf.Helpers;

/// <summary>
/// 主题动效的运行时部分：波纹需要真实鼠标坐标，纯 ControlTemplate 拿不到，
/// 因此用附加属性挂 <see cref="ButtonBase.PreviewMouseLeftButtonDown"/>，在按钮模板的
/// ButtonBorder 上叠一个 <see cref="Adorner"/> 画扩散圆。
/// </summary>
public static class Motion
{
    private const string ButtonBorderName = "ButtonBorder";
    private const string AccentBrushKey = "AccentBrush";
    private const double RippleDurationMilliseconds = 260;
    private const double RippleStartOpacity = 0.45;
    private const double RippleStartScale = 0.15;

    public static readonly DependencyProperty RippleProperty = DependencyProperty.RegisterAttached(
        "Ripple", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnRippleChanged));

    /// <summary>系统设置里关闭「动画效果」时为 false，所有运行时动效据此降级。</summary>
    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    public static bool GetRipple(DependencyObject element) => (bool)element.GetValue(RippleProperty);

    public static void SetRipple(DependencyObject element, bool value) => element.SetValue(RippleProperty, value);

    private static void OnRippleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ButtonBase button) return;
        button.PreviewMouseLeftButtonDown -= OnPress;
        if (args.NewValue is true) button.PreviewMouseLeftButtonDown += OnPress;
    }

    private static void OnPress(object sender, MouseButtonEventArgs args)
    {
        if (!AnimationsEnabled) return;
        if (sender is not ButtonBase button) return;

        // 模板被替换或尚未应用时没有 ButtonBorder：必须静默返回，
        // PreviewMouseLeftButtonDown 先于 Command，这里抛异常会让按钮点不动。
        if (button.Template?.FindName(ButtonBorderName, button) is not Border border) return;

        var layer = AdornerLayer.GetAdornerLayer(border);
        if (layer is null) return;

        var origin = args.GetPosition(border);
        var size = border.RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        // 半径取到最远角的距离，保证从任意落点扩散都能覆盖整个按钮
        var radius = Math.Sqrt(Math.Max(DistanceSquared(origin, 0, 0),
            Math.Max(DistanceSquared(origin, size.Width, 0),
                Math.Max(DistanceSquared(origin, 0, size.Height), DistanceSquared(origin, size.Width, size.Height)))));
        if (radius <= 0) return;

        var scale = new ScaleTransform(RippleStartScale, RippleStartScale);
        var ellipse = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = button.TryFindResource(AccentBrushKey) as Brush ?? new SolidColorBrush(Colors.White),
            Opacity = RippleStartOpacity,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale
        };

        var cornerRadius = border.CornerRadius.TopLeft;
        var adorner = new RippleAdorner(border, ellipse, origin, radius, cornerRadius);
        layer.Add(adorner);

        var storyboard = CreateRippleStoryboard(ellipse, scale);
        storyboard.Completed += (_, _) =>
        {
            // 动画结束后必须摘掉 Adorner，否则每次点击都会留一层视觉树，
            // AdornerLayer.GetAdorners(button) 会持续增长。
            if (adorner.Parent is AdornerLayer owner) owner.Remove(adorner);
            adorner.Detach();
        };
        storyboard.Begin();
    }

    private static Storyboard CreateRippleStoryboard(Ellipse ellipse, ScaleTransform scale)
    {
        var duration = TimeSpan.FromMilliseconds(RippleDurationMilliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var scaleX = new DoubleAnimation(RippleStartScale, 1, duration) { EasingFunction = easing };
        Storyboard.SetTarget(scaleX, scale);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath(ScaleTransform.ScaleXProperty));

        var scaleY = new DoubleAnimation(RippleStartScale, 1, duration) { EasingFunction = easing };
        Storyboard.SetTarget(scaleY, scale);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath(ScaleTransform.ScaleYProperty));

        var fade = new DoubleAnimation(RippleStartOpacity, 0, duration) { EasingFunction = easing };
        Storyboard.SetTarget(fade, ellipse);
        Storyboard.SetTargetProperty(fade, new PropertyPath(UIElement.OpacityProperty));

        var storyboard = new Storyboard();
        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Children.Add(fade);
        return storyboard;
    }

    private static double DistanceSquared(Point point, double x, double y)
    {
        var dx = point.X - x;
        var dy = point.Y - y;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>
    /// 覆盖整个按钮的透明叠层，扩散圆按点击点摆放，并按按钮圆角裁剪，溢出不会画到相邻控件上。
    /// </summary>
    private sealed class RippleAdorner : Adorner
    {
        private readonly Ellipse _circle;
        private readonly Point _origin;
        private readonly double _radius;
        private readonly double _cornerRadius;

        public RippleAdorner(UIElement adornedElement, Ellipse circle, Point origin, double radius, double cornerRadius)
            : base(adornedElement)
        {
            _circle = circle;
            _origin = origin;
            _radius = radius;
            _cornerRadius = cornerRadius;
            IsHitTestVisible = false;
            AddVisualChild(circle);
            AddLogicalChild(circle);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _circle;

        protected override Size MeasureOverride(Size constraint)
        {
            _circle.Measure(constraint);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _circle.Arrange(new Rect(_origin.X - _radius, _origin.Y - _radius, _radius * 2, _radius * 2));
            Clip = new RectangleGeometry(new Rect(finalSize), _cornerRadius, _cornerRadius);
            return finalSize;
        }

        public void Detach()
        {
            RemoveLogicalChild(_circle);
            RemoveVisualChild(_circle);
        }
    }
}
