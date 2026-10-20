using System.Windows;
using System.Windows.Media;
using Ardesia.Models;

namespace Ardesia.Editor;

public static class BackgroundFactory
{
    public const double Mm = 96.0 / 25.4;

    public static Color ParseColor(string? s)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(s)) return (Color)ColorConverter.ConvertFromString(s)!;
        }
        catch (FormatException)
        {
        }
        return Colors.White;
    }

    public static bool IsDark(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 128;

    public static Brush Create(PageBackground kind, Color bg)
    {
        if (kind == PageBackground.Blank)
        {
            var solid = new SolidColorBrush(bg);
            solid.Freeze();
            return solid;
        }

        double cell = kind switch
        {
            PageBackground.LinesNarrow => 8 * Mm,
            PageBackground.LinesWide => 11 * Mm,
            PageBackground.GridSmall => 5 * Mm,
            PageBackground.GridLarge => 10 * Mm,
            PageBackground.GridXLarge => 15 * Mm,
            _ => 5 * Mm,
        };

        var line = new SolidColorBrush(IsDark(bg) ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Color.FromRgb(0xB9, 0xCF, 0xE6));
        line.Freeze();

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(bg), null, new RectangleGeometry(new Rect(0, 0, cell, cell))));
        switch (kind)
        {
            case PageBackground.LinesNarrow:
            case PageBackground.LinesWide:
                group.Children.Add(new GeometryDrawing(line, null, new RectangleGeometry(new Rect(0, cell - 1, cell, 1))));
                break;
            case PageBackground.GridSmall:
            case PageBackground.GridLarge:
            case PageBackground.GridXLarge:
                group.Children.Add(new GeometryDrawing(line, null, new RectangleGeometry(new Rect(0, 0, cell, 1))));
                group.Children.Add(new GeometryDrawing(line, null, new RectangleGeometry(new Rect(0, 0, 1, cell))));
                break;
            case PageBackground.Dots:
                var dot = new SolidColorBrush(IsDark(bg) ? Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF) : Color.FromRgb(0x9A, 0xB4, 0xD0));
                dot.Freeze();
                group.Children.Add(new GeometryDrawing(dot, null, new EllipseGeometry(new Point(cell / 2, cell / 2), 1.4, 1.4)));
                break;
        }

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, cell, cell),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, cell, cell),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        return brush;
    }
}
