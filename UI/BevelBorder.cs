using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HddDefrag.UI;

/// <summary>Art des 3D-Rahmens im Windows-98-Stil.</summary>
public enum BevelKind
{
    /// <summary>Erhaben (Schaltfläche): hell oben/links, dunkel unten/rechts.</summary>
    Raised,
    /// <summary>Gedrückte Schaltfläche.</summary>
    Pressed,
    /// <summary>Abgesenkt (Listen, Textfelder, Fortschrittsbalken).</summary>
    Sunken,
    /// <summary>Geätzte Linie (Gruppenrahmen).</summary>
    Etched,
}

/// <summary>
/// Zeichnet einen klassischen 2 Pixel breiten 3D-Rahmen um sein Kind (ohne Rundungen, ohne Animation).
/// </summary>
public class BevelBorder : Decorator
{
    private const double Thickness = 2;

    private static readonly Brush White = Make(0xFF, 0xFF, 0xFF);
    private static readonly Brush Light = Make(0xDF, 0xDF, 0xDF);
    private static readonly Brush Shadow = Make(0x80, 0x80, 0x80);
    private static readonly Brush DarkShadow = Make(0x40, 0x40, 0x40);
    private static readonly Brush Black = Make(0x00, 0x00, 0x00);

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(BevelKind), typeof(BevelBorder),
        new FrameworkPropertyMetadata(BevelKind.Raised, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(BevelBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public BevelBorder()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public BevelKind Kind
    {
        get => (BevelKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    private static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double border = Thickness * 2;
        if (Child == null) return new Size(border, border);
        Child.Measure(new Size(Math.Max(0, constraint.Width - border), Math.Max(0, constraint.Height - border)));
        return new Size(Child.DesiredSize.Width + border, Child.DesiredSize.Height + border);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        double border = Thickness * 2;
        Child?.Arrange(new Rect(Thickness, Thickness, Math.Max(0, arrangeSize.Width - border), Math.Max(0, arrangeSize.Height - border)));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        if (Background != null) dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));

        switch (Kind)
        {
            case BevelKind.Raised:
                Frame(dc, w, h, White, Black, Light, Shadow);
                break;
            case BevelKind.Pressed:
                Frame(dc, w, h, Black, White, Shadow, Light);
                break;
            case BevelKind.Sunken:
                Frame(dc, w, h, Shadow, White, DarkShadow, Light);
                break;
            case BevelKind.Etched:
                Outline(dc, White, 1, 1, w - 1, h - 1);
                Outline(dc, Shadow, 0, 0, w - 1, h - 1);
                break;
        }
    }

    private static void Line(DrawingContext dc, Brush brush, double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(brush, null, new Rect(x, y, w, h));
    }

    private static void Outline(DrawingContext dc, Brush brush, double x, double y, double w, double h)
    {
        Line(dc, brush, x, y, w, 1);
        Line(dc, brush, x, y + h - 1, w, 1);
        Line(dc, brush, x, y, 1, h);
        Line(dc, brush, x + w - 1, y, 1, h);
    }

    private static void Frame(DrawingContext dc, double w, double h, Brush outerTl, Brush outerBr, Brush innerTl, Brush innerBr)
    {
        // Äußere Linie
        Line(dc, outerTl, 0, 0, w - 1, 1);
        Line(dc, outerTl, 0, 0, 1, h - 1);
        Line(dc, outerBr, 0, h - 1, w, 1);
        Line(dc, outerBr, w - 1, 0, 1, h);
        // Innere Linie
        Line(dc, innerTl, 1, 1, w - 3, 1);
        Line(dc, innerTl, 1, 1, 1, h - 3);
        Line(dc, innerBr, 1, h - 2, w - 2, 1);
        Line(dc, innerBr, w - 2, 1, 1, h - 2);
    }
}
