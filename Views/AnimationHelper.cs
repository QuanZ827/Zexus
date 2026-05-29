using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Zexus.Views
{
    /// <summary>
    /// Lightweight entrance/idle animation helpers (Step 8).
    /// </summary>
    public static class AnimationHelper
    {
        /// <summary>
        /// Fade-in + slide-up: opacity 0→1, translateY 10→0 over 400ms with CubicEase.
        /// Applied to new chat bubbles / cards as they're realized.
        /// </summary>
        public static void ApplyFadeInUp(FrameworkElement element, int delayMs = 0)
        {
            if (element == null) return;

            element.Opacity = 0;
            var translate = new TranslateTransform(0, 10);
            element.RenderTransform = translate;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var begin = TimeSpan.FromMilliseconds(delayMs);
            var dur = TimeSpan.FromMilliseconds(400);

            var fade = new DoubleAnimation(0, 1, dur) { EasingFunction = ease, BeginTime = begin };
            var slide = new DoubleAnimation(10, 0, dur) { EasingFunction = ease, BeginTime = begin };

            // Animate the transform directly (no Storyboard target-name resolution needed).
            element.BeginAnimation(UIElement.OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        /// <summary>
        /// Continuous 360° rotation (1.5s, linear, forever) for the inline-status spinner icon.
        /// Returns the RotateTransform so the caller can stop it by removing the element.
        /// </summary>
        public static RotateTransform ApplySpin(FrameworkElement element)
        {
            if (element == null) return null;

            var rotate = new RotateTransform(0);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = rotate;

            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.5))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
            return rotate;
        }
    }

    /// <summary>
    /// Attached behaviour: set <c>views:Entrance.Animate="True"</c> on a container
    /// (or use it in an ItemContainerStyle setter) to fade-in-up the element on Loaded.
    /// Used by the chat ItemsControl so each new message bubble animates in.
    /// </summary>
    public static class Entrance
    {
        public static readonly DependencyProperty AnimateProperty =
            DependencyProperty.RegisterAttached(
                "Animate", typeof(bool), typeof(Entrance),
                new PropertyMetadata(false, OnAnimateChanged));

        public static bool GetAnimate(DependencyObject obj) => (bool)obj.GetValue(AnimateProperty);
        public static void SetAnimate(DependencyObject obj, bool value) => obj.SetValue(AnimateProperty, value);

        private static void OnAnimateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement fe)) return;
            if (e.NewValue is bool b && b)
            {
                if (fe.IsLoaded) AnimationHelper.ApplyFadeInUp(fe);
                else fe.Loaded += OnLoadedOnce;
            }
        }

        private static void OnLoadedOnce(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                fe.Loaded -= OnLoadedOnce;
                AnimationHelper.ApplyFadeInUp(fe);
            }
        }
    }
}
