using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RemoteDesk.UI;

internal static class MotionBehaviors
{
    private sealed class SpotlightState
    {
        public SpotlightAdorner? Adorner { get; set; }
    }

    private sealed class SpotlightAdorner : Adorner
    {
        private readonly Border _owner;
        private Point _pointer;

        public SpotlightAdorner(Border adornedElement)
            : base(adornedElement)
        {
            _owner = adornedElement;
            IsHitTestVisible = false;
            Opacity = 0.0;
        }

        public void UpdatePointer(Point pointer)
        {
            _pointer = pointer;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var width = _owner.ActualWidth;
            var height = _owner.ActualHeight;
            if (width <= 1 || height <= 1)
                return;

            var radius = Math.Max(0, _owner.CornerRadius.TopLeft);
            var glowColor = _owner.TryFindResource("SpotlightGlowColor") is Color gc
                ? gc
                : Color.FromArgb(0x16, 0x00, 0x7A, 0xFF);
            var rimColor = _owner.TryFindResource("SpotlightRimColor") is Color rc
                ? rc
                : Color.FromArgb(0x40, 0x00, 0x7A, 0xFF);

            var clipRect = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
            var borderRadius = Math.Max(0, radius - 0.5);
            var borderRect = new RectangleGeometry(
                new Rect(0.5, 0.5, Math.Max(0, width - 1.0), Math.Max(0, height - 1.0)),
                borderRadius,
                borderRadius);

            var surfaceBrush = new RadialGradientBrush(glowColor, Colors.Transparent)
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = _pointer,
                GradientOrigin = _pointer,
                RadiusX = 190,
                RadiusY = 190,
            };

            var rimBrush = new RadialGradientBrush(rimColor, Colors.Transparent)
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = _pointer,
                GradientOrigin = _pointer,
                RadiusX = 140,
                RadiusY = 140,
            };
            var rimPen = new Pen(rimBrush, 1.1);

            dc.PushClip(clipRect);
            dc.DrawGeometry(surfaceBrush, null, clipRect);
            dc.DrawGeometry(null, rimPen, borderRect);
            dc.Pop();
        }
    }

    private static readonly ConditionalWeakTable<Border, SpotlightState> SpotlightStates = new();

    public static readonly DependencyProperty EnableSpotlightProperty =
        DependencyProperty.RegisterAttached(
            "EnableSpotlight",
            typeof(bool),
            typeof(MotionBehaviors),
            new PropertyMetadata(false, OnEnableSpotlightChanged));

    public static bool GetEnableSpotlight(DependencyObject obj) => (bool)obj.GetValue(EnableSpotlightProperty);
    public static void SetEnableSpotlight(DependencyObject obj, bool value) => obj.SetValue(EnableSpotlightProperty, value);

    public static readonly DependencyProperty EnablePressScaleProperty =
        DependencyProperty.RegisterAttached(
            "EnablePressScale",
            typeof(bool),
            typeof(MotionBehaviors),
            new PropertyMetadata(false, OnEnablePressScaleChanged));

    public static bool GetEnablePressScale(DependencyObject obj) => (bool)obj.GetValue(EnablePressScaleProperty);
    public static void SetEnablePressScale(DependencyObject obj, bool value) => obj.SetValue(EnablePressScaleProperty, value);

    public static readonly DependencyProperty EnablePulseProperty =
        DependencyProperty.RegisterAttached(
            "EnablePulse",
            typeof(bool),
            typeof(MotionBehaviors),
            new PropertyMetadata(false, OnEnablePulseChanged));

    public static bool GetEnablePulse(DependencyObject obj) => (bool)obj.GetValue(EnablePulseProperty);
    public static void SetEnablePulse(DependencyObject obj, bool value) => obj.SetValue(EnablePulseProperty, value);

    // ==================== 1. REACT BITS SPOTLIGHT CARD ====================

    private static void OnEnableSpotlightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
            return;

        if ((bool)e.NewValue)
        {
            border.MouseEnter -= OnSpotlightMouseEnter;
            border.MouseMove -= OnSpotlightMouseMove;
            border.MouseLeave -= OnSpotlightMouseLeave;
            border.Unloaded -= OnSpotlightUnloaded;

            border.MouseEnter += OnSpotlightMouseEnter;
            border.MouseMove += OnSpotlightMouseMove;
            border.MouseLeave += OnSpotlightMouseLeave;
            border.Unloaded += OnSpotlightUnloaded;
        }
        else
        {
            border.MouseEnter -= OnSpotlightMouseEnter;
            border.MouseMove -= OnSpotlightMouseMove;
            border.MouseLeave -= OnSpotlightMouseLeave;
            border.Unloaded -= OnSpotlightUnloaded;
            DetachSpotlightAdorner(border);
        }
    }

    private static void OnSpotlightMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border border || !border.IsEnabled)
            return;

        var adorner = EnsureSpotlightAdorner(border);
        if (adorner is null)
            return;

        adorner.UpdatePointer(e.GetPosition(border));
        var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        adorner.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    private static void OnSpotlightMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Border border)
            return;

        if (SpotlightStates.TryGetValue(border, out var state) && state.Adorner is { } adorner)
            adorner.UpdatePointer(e.GetPosition(border));
    }

    private static void OnSpotlightMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border)
            return;

        if (SpotlightStates.TryGetValue(border, out var state) && state.Adorner is { } adorner)
        {
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            adorner.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
    }

    private static void OnSpotlightUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border border)
            DetachSpotlightAdorner(border);
    }

    private static SpotlightAdorner? EnsureSpotlightAdorner(Border border)
    {
        var state = SpotlightStates.GetOrCreateValue(border);
        if (state.Adorner is not null)
            return state.Adorner;

        var layer = AdornerLayer.GetAdornerLayer(border);
        if (layer is null)
            return null;

        var adorner = new SpotlightAdorner(border);
        layer.Add(adorner);
        state.Adorner = adorner;
        return adorner;
    }

    private static void DetachSpotlightAdorner(Border border)
    {
        if (!SpotlightStates.TryGetValue(border, out var state) || state.Adorner is null)
            return;

        var layer = AdornerLayer.GetAdornerLayer(border);
        layer?.Remove(state.Adorner);
        state.Adorner = null;
    }

    // ==================== 2. TACTILE PRESS SCALE ====================

    private static void OnEnablePressScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        element.PreviewMouseLeftButtonDown -= OnPressDown;
        element.PreviewMouseLeftButtonUp -= OnPressRelease;
        element.MouseLeave -= OnPressRelease;
        element.LostMouseCapture -= OnPressRelease;

        if ((bool)e.NewValue)
        {
            element.PreviewMouseLeftButtonDown += OnPressDown;
            element.PreviewMouseLeftButtonUp += OnPressRelease;
            element.MouseLeave += OnPressRelease;
            element.LostMouseCapture += OnPressRelease;
        }
    }

    private static void OnPressDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement element || !element.IsEnabled)
            return;

        var scale = EnsureScaleTransform(element);
        var anim = new DoubleAnimation(0.97, TimeSpan.FromMilliseconds(80))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static void OnPressRelease(object sender, RoutedEventArgs e)
    {
        if (sender is not UIElement element)
            return;

        if (!TryGetScaleTransform(element, out var scale))
            return;

        var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(165))
        {
            EasingFunction = new BackEase { Amplitude = 0.26, EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static ScaleTransform EnsureScaleTransform(UIElement element)
    {
        if (element.RenderTransformOrigin == default)
            element.RenderTransformOrigin = new Point(0.5, 0.5);

        if (TryGetScaleTransform(element, out var existing))
            return existing;

        var scale = new ScaleTransform(1.0, 1.0);
        if (element.RenderTransform is null || element.RenderTransform == Transform.Identity)
        {
            element.RenderTransform = scale;
        }
        else if (element.RenderTransform is TransformGroup group)
        {
            if (group.IsFrozen)
            {
                group = group.Clone();
                element.RenderTransform = group;
            }
            group.Children.Add(scale);
        }
        else
        {
            var next = new TransformGroup();
            next.Children.Add(element.RenderTransform);
            next.Children.Add(scale);
            element.RenderTransform = next;
        }

        return scale;
    }

    private static bool TryGetScaleTransform(UIElement element, out ScaleTransform scale)
    {
        switch (element.RenderTransform)
        {
            case ScaleTransform st when !st.IsFrozen:
                scale = st;
                return true;
            case TransformGroup group:
                foreach (var child in group.Children)
                {
                    if (child is ScaleTransform gst && !gst.IsFrozen)
                    {
                        scale = gst;
                        return true;
                    }
                }
                break;
        }

        scale = null!;
        return false;
    }

    // ==================== 3. ONLINE DOT BREATHING PULSE ====================

    private static void OnEnablePulseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        element.Loaded -= OnPulseElementLoaded;
        element.Unloaded -= OnPulseElementUnloaded;

        if ((bool)e.NewValue)
        {
            element.Loaded += OnPulseElementLoaded;
            element.Unloaded += OnPulseElementUnloaded;
            if (element.IsLoaded)
                StartPulseAnimation(element);
        }
        else
        {
            StopPulseAnimation(element);
        }
    }

    private static void OnPulseElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && GetEnablePulse(element))
            StartPulseAnimation(element);
    }

    private static void OnPulseElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            StopPulseAnimation(element);
    }

    private static void StartPulseAnimation(UIElement element)
    {
        var pulse = new DoubleAnimation(1.0, 0.42, TimeSpan.FromMilliseconds(1200))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        element.BeginAnimation(UIElement.OpacityProperty, pulse);
    }

    private static void StopPulseAnimation(UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
    }

    // ==================== 4. PAGE & SLIDING CAPSULE HELPERS ====================

    public static void AnimatePageEnter(FrameworkElement? page)
    {
        if (page is null)
            return;

        TranslateTransform? translate = page.RenderTransform switch
        {
            TranslateTransform tt when !tt.IsFrozen => tt,
            TransformGroup tg => tg.Children.OfType<TranslateTransform>().FirstOrDefault(t => !t.IsFrozen),
            _ => null,
        };

        if (translate is null)
        {
            translate = new TranslateTransform();
            page.RenderTransform = translate;
        }

        page.BeginAnimation(UIElement.OpacityProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);

        var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var slide = new DoubleAnimation(8.0, 0.0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        page.BeginAnimation(UIElement.OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    public static void AnimateVerticalIndicator(TranslateTransform? transform, double targetY, bool animate)
    {
        if (transform is null)
            return;

        if (!animate)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = targetY;
            return;
        }

        var anim = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(210))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        transform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    public static void AnimateHorizontalIndicator(TranslateTransform? transform, double targetX, bool animate)
    {
        if (transform is null)
            return;

        if (!animate)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = targetX;
            return;
        }

        var anim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        transform.BeginAnimation(TranslateTransform.XProperty, anim);
    }
}
