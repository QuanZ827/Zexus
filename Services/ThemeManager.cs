using System;
using System.Windows;
using System.Windows.Media;

namespace Zexus.Services
{
    public enum ThemeMode
    {
        Dark,
        Light
    }

    /// <summary>
    /// Central theme management. Holds Dark/Light Material-3 color palettes as static
    /// properties and (Step 7) registers each one as a frozen SolidColorBrush in
    /// Application.Current.Resources so XAML can bind via {DynamicResource ...}Brush
    /// and pick up theme swaps automatically.
    ///
    /// Legacy ColXxx properties are preserved as aliases for the M3 token they replaced
    /// so ChatWindow.xaml.cs (which still builds Revit icons in code-behind) keeps working.
    /// </summary>
    public static class ThemeManager
    {
        public static ThemeMode Current { get; private set; } = ThemeMode.Dark;
        public static event Action ThemeChanged;

        // ─── Material 3 — Surface hierarchy ───
        public static Color Surface { get; private set; }
        public static Color SurfaceDim { get; private set; }
        public static Color SurfaceBright { get; private set; }
        public static Color SurfaceContainerLowest { get; private set; }
        public static Color SurfaceContainerLow { get; private set; }
        public static Color SurfaceContainer { get; private set; }
        public static Color SurfaceContainerHigh { get; private set; }
        public static Color SurfaceContainerHighest { get; private set; }
        public static Color SurfaceVariant { get; private set; }

        // ─── M3 — Content colors ───
        public static Color OnSurface { get; private set; }
        public static Color OnSurfaceVariant { get; private set; }
        public static Color Outline { get; private set; }
        public static Color OutlineVariant { get; private set; }

        // ─── M3 — Primary / Secondary / Tertiary ───
        public static Color Primary { get; private set; }
        public static Color OnPrimary { get; private set; }
        public static Color PrimaryContainer { get; private set; }
        public static Color OnPrimaryContainer { get; private set; }
        public static Color Secondary { get; private set; }
        public static Color SecondaryContainer { get; private set; }
        public static Color OnSecondaryContainer { get; private set; }
        public static Color Tertiary { get; private set; }
        public static Color TertiaryContainer { get; private set; }

        // ─── M3 — Functional ───
        public static Color Error { get; private set; }
        public static Color ErrorContainer { get; private set; }
        public static Color Success { get; private set; }
        public static Color Warning { get; private set; }

        // ─── Glass surfaces ───
        public static Color GlassPanel { get; private set; }       // L2
        public static Color GlassFloating { get; private set; }    // L3
        public static Color GlassBorder { get; private set; }
        public static Color GlassBorderFloating { get; private set; }

        // ─── Legacy aliases (kept so code-behind MapToolResultToOutputRecord etc. keep compiling) ───
        public static Color ColBg => Surface;
        public static Color ColSurface => GlassPanel;
        public static Color ColCard => GlassFloating;
        public static Color ColBorder => Outline;
        public static Color ColPrimary => PrimaryContainer;
        public static Color ColPrimaryLt => Primary;
        public static Color ColAccent => SecondaryContainer;
        public static Color ColSuccess => Success;
        public static Color ColWarning => Warning;
        public static Color ColError => Error;
        public static Color ColText => OnSurface;
        public static Color ColTextSec => OnSurfaceVariant;
        public static Color ColMuted => Outline;
        public static Color ColGlass => GlassPanel;
        public static Color ColGlassBorder => GlassBorder;
        public static Color ColCodeBg => SurfaceContainerLowest;

        static ThemeManager()
        {
            var saved = ConfigManager.Config.Theme;
            var mode = string.Equals(saved, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeMode.Light
                : ThemeMode.Dark;
            ApplyPalette(mode);
        }

        public static void SetTheme(ThemeMode mode)
        {
            if (mode == Current) return;
            ApplyPalette(mode);
            ConfigManager.SetTheme(mode.ToString());
            ThemeChanged?.Invoke();
        }

        public static void Toggle()
            => SetTheme(Current == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark);

        /// <summary>
        /// Re-publish brush resources after the visual tree is up. Called by ChatWindow
        /// on Loaded; safe to call whenever Application.Current becomes available.
        /// </summary>
        public static void EnsureBrushResources() => RegisterBrushResources();

        private static void ApplyPalette(ThemeMode mode)
        {
            Current = mode;
            if (mode == ThemeMode.Dark) ApplyDark();
            else ApplyLight();
            RegisterBrushResources();
        }

        // ─── Dark palette (M3 + Stitch) ───
        private static void ApplyDark()
        {
            // Surfaces
            Surface                  = Rgb(0x12, 0x13, 0x17);
            SurfaceDim               = Rgb(0x12, 0x13, 0x17);
            SurfaceBright            = Rgb(0x38, 0x39, 0x3E);
            SurfaceContainerLowest   = Rgb(0x0D, 0x0E, 0x12);
            SurfaceContainerLow      = Rgb(0x1A, 0x1B, 0x20);
            SurfaceContainer         = Rgb(0x1E, 0x1F, 0x24);
            SurfaceContainerHigh     = Rgb(0x29, 0x2A, 0x2E);
            SurfaceContainerHighest  = Rgb(0x34, 0x34, 0x39);
            SurfaceVariant           = Rgb(0x34, 0x34, 0x39);

            // Content
            OnSurface         = Rgb(0xE3, 0xE2, 0xE8);
            OnSurfaceVariant  = Rgb(0xC2, 0xC6, 0xD5);
            Outline           = Rgb(0x8C, 0x90, 0x9F);
            OutlineVariant    = Rgb(0x42, 0x47, 0x53);

            // Primary
            Primary              = Rgb(0xAD, 0xC6, 0xFF);
            OnPrimary            = Rgb(0x00, 0x2E, 0x69);
            PrimaryContainer     = Rgb(0x4D, 0x8E, 0xFE);
            OnPrimaryContainer   = Rgb(0x00, 0x28, 0x5C);

            // Secondary
            Secondary               = Rgb(0xC9, 0xBF, 0xFF);
            SecondaryContainer      = Rgb(0x47, 0x20, 0xCA);
            OnSecondaryContainer    = Rgb(0xBA, 0xAE, 0xFF);

            // Tertiary
            Tertiary             = Rgb(0xFB, 0xAB, 0xFF);
            TertiaryContainer    = Rgb(0xE1, 0x4E, 0xF6);

            // Functional
            Error           = Rgb(0xFF, 0xB4, 0xAB);
            ErrorContainer  = Rgb(0x93, 0x00, 0x0A);
            Success         = Rgb(0x22, 0xC5, 0x5E);
            Warning         = Rgb(0xF5, 0x9E, 0x0B);

            // Glass surfaces (translucent overlays — alpha encoded in ARGB)
            GlassPanel           = Argb(0x0A, 0xFF, 0xFF, 0xFF); // 4% white
            GlassFloating        = Argb(0x14, 0xFF, 0xFF, 0xFF); // 8% white
            GlassBorder          = Argb(0x14, 0xFF, 0xFF, 0xFF); // 8% white
            GlassBorderFloating  = Argb(0x26, 0xFF, 0xFF, 0xFF); // 15% white
        }

        // ─── Light palette (derived M3 light) ───
        private static void ApplyLight()
        {
            // Surfaces — lifted, low-chroma
            Surface                  = Rgb(0xF5, 0xF6, 0xFA);
            SurfaceDim               = Rgb(0xE6, 0xE7, 0xEC);
            SurfaceBright            = Rgb(0xFF, 0xFF, 0xFF);
            SurfaceContainerLowest   = Rgb(0xFF, 0xFF, 0xFF);
            SurfaceContainerLow      = Rgb(0xEF, 0xF0, 0xF5);
            SurfaceContainer         = Rgb(0xE9, 0xEA, 0xEF);
            SurfaceContainerHigh     = Rgb(0xE2, 0xE3, 0xE8);
            SurfaceContainerHighest  = Rgb(0xDC, 0xDD, 0xE2);
            SurfaceVariant           = Rgb(0xE0, 0xE3, 0xEC);

            // Content
            OnSurface         = Rgb(0x1A, 0x1C, 0x22);
            OnSurfaceVariant  = Rgb(0x44, 0x47, 0x52);
            Outline           = Rgb(0x74, 0x77, 0x80);
            OutlineVariant    = Rgb(0xC4, 0xC6, 0xCD);

            // Primary
            Primary              = Rgb(0x1A, 0x4B, 0xBF);
            OnPrimary            = Rgb(0xFF, 0xFF, 0xFF);
            PrimaryContainer     = Rgb(0x4D, 0x8E, 0xFE);
            OnPrimaryContainer   = Rgb(0xFF, 0xFF, 0xFF);

            // Secondary
            Secondary               = Rgb(0x3D, 0x1F, 0x9F);
            SecondaryContainer      = Rgb(0x6C, 0x47, 0xFF);
            OnSecondaryContainer    = Rgb(0xFF, 0xFF, 0xFF);

            // Tertiary
            Tertiary             = Rgb(0x90, 0x21, 0xA8);
            TertiaryContainer    = Rgb(0xE1, 0x4E, 0xF6);

            // Functional
            Error           = Rgb(0xD9, 0x33, 0x25);
            ErrorContainer  = Rgb(0xFF, 0xDA, 0xD6);
            Success         = Rgb(0x0D, 0x9A, 0x6D);
            Warning         = Rgb(0xD9, 0x7B, 0x06);

            // Glass surfaces — darker tints over light backgrounds
            GlassPanel           = Argb(0x14, 0x00, 0x00, 0x00); // 8% black
            GlassFloating        = Argb(0xCC, 0xFF, 0xFF, 0xFF); // 80% white
            GlassBorder          = Argb(0x18, 0x00, 0x00, 0x00); // 9% black
            GlassBorderFloating  = Argb(0x33, 0x00, 0x00, 0x00); // 20% black
        }

        // ─── Brush resource registration ───
        private static void RegisterBrushResources()
        {
            var app = Application.Current;
            if (app == null) return;
            var r = app.Resources;

            // Surfaces
            r["SurfaceBrush"]                  = Frozen(Surface);
            r["SurfaceDimBrush"]               = Frozen(SurfaceDim);
            r["SurfaceBrightBrush"]            = Frozen(SurfaceBright);
            r["SurfaceContainerLowestBrush"]   = Frozen(SurfaceContainerLowest);
            r["SurfaceContainerLowBrush"]      = Frozen(SurfaceContainerLow);
            r["SurfaceContainerBrush"]         = Frozen(SurfaceContainer);
            r["SurfaceContainerHighBrush"]     = Frozen(SurfaceContainerHigh);
            r["SurfaceContainerHighestBrush"]  = Frozen(SurfaceContainerHighest);
            r["SurfaceVariantBrush"]           = Frozen(SurfaceVariant);

            // Content
            r["OnSurfaceBrush"]         = Frozen(OnSurface);
            r["OnSurfaceVariantBrush"]  = Frozen(OnSurfaceVariant);
            r["OutlineBrush"]           = Frozen(Outline);
            r["OutlineVariantBrush"]    = Frozen(OutlineVariant);

            // Primary
            r["PrimaryBrush"]              = Frozen(Primary);
            r["OnPrimaryBrush"]            = Frozen(OnPrimary);
            r["PrimaryContainerBrush"]     = Frozen(PrimaryContainer);
            r["OnPrimaryContainerBrush"]   = Frozen(OnPrimaryContainer);

            // Secondary
            r["SecondaryBrush"]               = Frozen(Secondary);
            r["SecondaryContainerBrush"]      = Frozen(SecondaryContainer);
            r["OnSecondaryContainerBrush"]    = Frozen(OnSecondaryContainer);

            // Tertiary
            r["TertiaryBrush"]             = Frozen(Tertiary);
            r["TertiaryContainerBrush"]    = Frozen(TertiaryContainer);

            // Functional
            r["ErrorBrush"]            = Frozen(Error);
            r["ErrorContainerBrush"]   = Frozen(ErrorContainer);
            r["SuccessBrush"]          = Frozen(Success);
            r["WarningBrush"]          = Frozen(Warning);

            // Glass
            r["GlassPanelBrush"]            = Frozen(GlassPanel);
            r["GlassFloatingBrush"]         = Frozen(GlassFloating);
            r["GlassBorderBrush"]           = Frozen(GlassBorder);
            r["GlassBorderFloatingBrush"]   = Frozen(GlassBorderFloating);

            // Tinted overlays used by Stitch bubbles & cards
            r["PrimaryTint10Brush"]   = Frozen(WithAlpha(PrimaryContainer, 0x1A));   // 10%
            r["PrimaryTint20Brush"]   = Frozen(WithAlpha(PrimaryContainer, 0x33));   // 20%
            r["TertiaryTint10Brush"]  = Frozen(WithAlpha(TertiaryContainer, 0x1A));  // 10%
            r["BubbleOverlayBrush"]   = Frozen(Argb(0x33, 0x00, 0x00, 0x00));        // #20000000-ish

            // Gradient brushes (rebuild each theme swap)
            r["UserBubbleGradientBrush"] = FrozenLinearGradient(135,
                (Argb(0x1A, 0x4D, 0x8E, 0xFE), 0.0),
                (Argb(0x0D, 0xA8, 0x55, 0xF7), 1.0));
            r["SendButtonGradientBrush"] = FrozenLinearGradient(135,
                (Rgb(0x4D, 0x8E, 0xFE), 0.0),
                (Rgb(0xA8, 0x55, 0xF7), 0.5),
                (Rgb(0xE1, 0x4E, 0xF6), 1.0));
            r["SendButtonGradientHoverBrush"] = FrozenLinearGradient(135,
                (Rgb(0x5A, 0x95, 0xF5), 0.0),
                (Rgb(0xB8, 0x68, 0xFF), 0.5),
                (Rgb(0xEF, 0x60, 0xFF), 1.0));
        }

        // ─── Helpers ───
        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
        private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

        private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static LinearGradientBrush FrozenLinearGradient(double angleDeg, params (Color color, double offset)[] stops)
        {
            // angle 135deg = top-left → bottom-right (matches the Stitch spec direction).
            var rad = angleDeg * Math.PI / 180.0;
            var b = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(Math.Cos(rad), Math.Sin(rad))
            };
            foreach (var (color, offset) in stops)
                b.GradientStops.Add(new GradientStop(color, offset));
            b.Freeze();
            return b;
        }
    }
}
