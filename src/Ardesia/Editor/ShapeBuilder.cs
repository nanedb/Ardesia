using System.Windows;

namespace Ardesia.Editor;

public enum ShapeKind { Line, Arrow, Rectangle, Ellipse, Triangle }

public static class ShapeBuilder
{
    /// <summary>Restituisce le spezzate che compongono la forma (ognuna diventa un tratto).</summary>
    public static List<Point[]> Build(ShapeKind kind, Point a, Point b, double penWidth)
    {
        var result = new List<Point[]>();
        switch (kind)
        {
            case ShapeKind.Line:
                result.Add(new[] { a, b });
                break;

            case ShapeKind.Arrow:
            {
                result.Add(new[] { a, b });
                var dir = b - a;
                if (dir.Length < 1) break;
                dir.Normalize();
                double size = Math.Max(16, penWidth * 5);
                result.Add(new[] { b - Rotate(dir, 28) * size, b, b - Rotate(dir, -28) * size });
                break;
            }

            case ShapeKind.Rectangle:
                result.Add(new[] { a, new Point(b.X, a.Y), b, new Point(a.X, b.Y), a });
                break;

            case ShapeKind.Ellipse:
            {
                var c = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                double rx = Math.Abs(b.X - a.X) / 2, ry = Math.Abs(b.Y - a.Y) / 2;
                int n = Math.Clamp((int)(Math.Max(rx, ry) / 2), 48, 240);
                var pts = new Point[n + 1];
                for (int i = 0; i <= n; i++)
                {
                    double t = 2 * Math.PI * i / n;
                    pts[i] = new Point(c.X + rx * Math.Cos(t), c.Y + ry * Math.Sin(t));
                }
                result.Add(pts);
                break;
            }

            case ShapeKind.Triangle:
            {
                var top = new Point((a.X + b.X) / 2, a.Y);
                result.Add(new[] { top, new Point(b.X, b.Y), new Point(a.X, b.Y), top });
                break;
            }
        }
        return result;
    }

    /// <summary>Con Maiusc premuto: linee a multipli di 15°, quadrati, cerchi.</summary>
    public static Point Constrain(ShapeKind kind, Point a, Point b)
    {
        var v = b - a;
        if (kind is ShapeKind.Line or ShapeKind.Arrow)
        {
            double ang = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 12)) * (Math.PI / 12);
            return a + new Vector(Math.Cos(ang), Math.Sin(ang)) * v.Length;
        }
        double s = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
        return new Point(a.X + Math.CopySign(s, v.X == 0 ? 1 : v.X), a.Y + Math.CopySign(s, v.Y == 0 ? 1 : v.Y));
    }

    static Vector Rotate(Vector v, double degrees)
    {
        double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        return new Vector(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }
}
