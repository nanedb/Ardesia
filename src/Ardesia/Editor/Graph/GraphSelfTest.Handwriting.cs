using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;

namespace Ardesia.Editor.Graph;

public static partial class GraphSelfTest
{
    /// <summary>Prove della scrittura a mano: MathML → testo, righe, e (se Windows lo ha) il riconoscitore vero.</summary>
    static void Handwriting(Action<bool, string> check, StringBuilder log)
    {
        const string ns = "<m:math xmlns:m=\"http://www.w3.org/1998/Math/MathML\">";
        // MathML come lo dà il riconoscitore di Windows (presi dalle prove del 17/10/2026)
        (string MathML, string Expected)[] mathml =
        {
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:msup><m:mrow><m:mi>x</m:mi></m:mrow><m:mrow><m:mn>2</m:mn></m:mrow></m:msup><m:mo>-</m:mo><m:mn>4</m:mn>", "y = x² − 4"),
            ("<m:mi>f</m:mi><m:mfenced open=\"(\" close=\")\"><m:mrow><m:mi>x</m:mi></m:mrow></m:mfenced><m:mo>=</m:mo><m:mn>2</m:mn><m:mi>x</m:mi><m:mo>+</m:mo><m:mn>1</m:mn>", "f(x) = 2x + 1"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:mi>s</m:mi><m:mi>ⅇ</m:mi><m:mi>Π</m:mi><m:mi>x</m:mi>", "y = sen x"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:mrow><m:mo mathvariant=\"normal\">sin</m:mo><m:mo>⁡</m:mo><m:mrow><m:mi>x</m:mi></m:mrow></m:mrow>", "y = sin x"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:msub><m:mrow><m:mi>t</m:mi></m:mrow><m:mrow><m:mn>9</m:mn></m:mrow></m:msub><m:mi>x</m:mi>", "y = tg x"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:mfrac><m:mrow><m:mn>1</m:mn></m:mrow><m:mrow><m:mi>x</m:mi></m:mrow></m:mfrac>", "y = 1/x"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:mfrac><m:mrow><m:mi>x</m:mi><m:mo>+</m:mo><m:mn>1</m:mn></m:mrow><m:mrow><m:mi>x</m:mi><m:mo>-</m:mo><m:mn>2</m:mn></m:mrow></m:mfrac>", "y = (x + 1)/(x − 2)"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:msqrt><m:mrow><m:mi>x</m:mi></m:mrow></m:msqrt>", "y = √(x)"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:mn>2,5</m:mn><m:mi>x</m:mi>", "y = 2,5x"),
            ("<m:mi>Y</m:mi><m:mo>=</m:mo><m:mo>-</m:mo><m:msup><m:mi>x</m:mi><m:mn>2</m:mn></m:msup>", "y = −x²"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:mn>3</m:mn><m:mfrac><m:mn>1</m:mn><m:mi>x</m:mi></m:mfrac>", "y = 3(1/x)"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:msup><m:mi>x</m:mi><m:mrow><m:mi>a</m:mi><m:mo>+</m:mo><m:mn>1</m:mn></m:mrow></m:msup>", "y = x^(a + 1)"),
            // esponente sul nome della funzione (segnalato dall'utente il 18/10/2026: usciva «sin4(x)»)
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:mrow><m:msup><m:mo mathvariant=\"normal\">sin</m:mo><m:mrow><m:mn>4</m:mn></m:mrow></m:msup><m:mo>⁡</m:mo><m:mrow><m:mfenced open=\"(\" close=\")\"><m:mrow><m:mi>x</m:mi></m:mrow></m:mfenced></m:mrow></m:mrow>", "y = sin⁴(x)"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:mrow><m:msup><m:mo mathvariant=\"normal\">sin</m:mo><m:mrow><m:mn>2</m:mn></m:mrow></m:msup><m:mo>⁡</m:mo><m:mrow><m:mi>x</m:mi></m:mrow></m:mrow>", "y = sin² x"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:mi>s</m:mi><m:mi>ⅇ</m:mi><m:msup><m:mi>Π</m:mi><m:mn>2</m:mn></m:msup><m:mi>x</m:mi>", "y = sen² x"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:msup><m:mi>cos</m:mi><m:mn>3</m:mn></m:msup><m:mfenced><m:mrow><m:mn>2</m:mn><m:mi>x</m:mi></m:mrow></m:mfenced>", "y = cos³(2x)"),
            ("<m:mi>y</m:mi><m:mo>=</m:mo><m:msup><m:mfenced><m:mrow><m:mi>x</m:mi><m:mo>-</m:mo><m:mn>1</m:mn></m:mrow></m:mfenced><m:mn>2</m:mn></m:msup>", "y = (x − 1)²"),
        };
        foreach (var (xml, expected) in mathml)
        {
            string text = MathMLText.ToText(ns + xml + "</m:math>");
            bool parses = true;
            try { MathParser.ParseLine(MathText.Normalize(text)); }
            catch (MathSyntaxException) { parses = false; }
            check(text == expected && parses, $"MathML → «{text}» (atteso «{expected}»){(parses ? "" : ", il parser non lo capisce")}");
        }
        check(MathMLText.ToText("non è MathML") == "", "MathML sbagliato → testo vuoto");
        try
        {
            var c = MathParser.ParseLine(MathText.Normalize("y = sin⁴(x)"));
            double v = c.Compile(c.Parameters)(Math.PI / 4, Array.Empty<double>());
            check(Math.Abs(v - 0.25) < 1e-12, $"«y = sin⁴(x)» in π/4: {v} (atteso 0.25)");
        }
        catch (MathSyntaxException ex)
        {
            check(false, "«y = sin⁴(x)»: " + ex.Message);
        }

        // ── righe ──
        var two = new StrokeCollection();
        two.Add(StrokeFont.Write("y=x^2-4", 40, 20, 20));
        int first = two.Count;
        two.Add(StrokeFont.Fraction("y=", "x+1", "x-2", 40, 20, 140));
        var lines = HandwritingMath.SplitLines(two);
        check(lines.Count == 2 && lines[0].Count == first && lines[1].Count == two.Count - first,
            $"righe: {lines.Count} ({string.Join(", ", lines.Select(l => l.Count))} tratti; attese 2: {first}, {two.Count - first})");

        // ── riconoscitore vero ──
        if (!HandwritingMath.IsAvailable)
        {
            log.AppendLine("nota    Windows non ha il riconoscimento delle formule (micaut.dll): prova saltata.");
            return;
        }
        (StrokeCollection Ink, string Expected)[] samples =
        {
            (StrokeFont.Write("y=x^2-4", 40, 0, 0), "y = x² − 4"),
            (StrokeFont.Write("f(x)=2x+1", 40, 0, 0), "f(x) = 2x + 1"),
            (StrokeFont.Fraction("y=", "x+1", "x-2", 40, 0, 0), "y = (x + 1)/(x − 2)"),
            (StrokeFont.Sqrt("y=", "x", 40, 0, 0), "y = √(x)"),
            (StrokeFont.Write("y=sen x", 40, 0, 0), "y = sen x"),
            (StrokeFont.Write("y=sin^4(x)", 40, 0, 0), "y = sin⁴(x)"),
        };
        HandwritingMath.Trace = m => log.AppendLine("nota    " + m);
        var watch = Stopwatch.StartNew();
        IReadOnlyList<string> results;
        try
        {
            results = HandwritingMath.Recognize(samples.Select(s => s.Ink).ToList());
        }
        catch (Exception ex)
        {
            check(false, "riconoscitore di Windows: " + ex.Message);
            return;
        }
        log.AppendLine($"nota    riconoscimento di {samples.Length} righe in {watch.ElapsedMilliseconds} ms");
        for (int i = 0; i < samples.Length; i++)
        {
            string text = i < results.Count ? MathMLText.ToText(results[i]) : "";
            check(text == samples[i].Expected, $"scritto a mano «{samples[i].Expected}» → «{text}»");
        }
    }

    /// <summary>
    /// Caratteri a tratto singolo per scrivere «a mano» nelle prove: altezza delle cifre = 1, y verso il basso.
    /// Più puliti della scrittura vera, ma il riconoscitore li tratta allo stesso modo.
    /// </summary>
    public static class StrokeFont
    {
        record Glyph(double Width, List<List<Point>> Strokes);

        static List<Point> L(params double[] v)
        {
            var r = new List<Point>();
            for (int i = 0; i < v.Length; i += 2) r.Add(new Point(v[i], v[i + 1]));
            return r;
        }

        static List<Point> Arc(double cx, double cy, double rx, double ry, double a0, double a1, int n = 16)
        {
            var r = new List<Point>();
            for (int i = 0; i <= n; i++)
            {
                double a = (a0 + (a1 - a0) * i / n) * Math.PI / 180;
                r.Add(new Point(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a)));
            }
            return r;
        }

        static List<Point> Cat(params List<Point>[] p) => p.SelectMany(q => q).ToList();
        static Glyph G(double w, params List<Point>[] s) => new(w, s.ToList());

        static readonly Dictionary<char, Glyph> Map = new()
        {
            ['x'] = G(0.45, L(0, 0.5, 0.45, 1), L(0.45, 0.5, 0, 1)),
            ['y'] = G(0.5, L(0, 0.5, 0.25, 0.95), L(0.5, 0.5, 0.25, 1.0, 0.12, 1.4, 0.02, 1.45)),
            ['='] = G(0.5, L(0, 0.6, 0.5, 0.6), L(0, 0.8, 0.5, 0.8)),
            ['-'] = G(0.45, L(0, 0.72, 0.45, 0.72)),
            ['+'] = G(0.5, L(0, 0.72, 0.5, 0.72), L(0.25, 0.47, 0.25, 0.97)),
            ['1'] = G(0.3, L(0, 0.25, 0.25, 0, 0.25, 1)),
            ['2'] = G(0.5, Cat(Arc(0.25, 0.27, 0.24, 0.24, 190, 380), L(0.0, 1, 0.5, 1))),
            ['3'] = G(0.5, Cat(Arc(0.24, 0.25, 0.24, 0.23, 200, 450), Arc(0.24, 0.74, 0.26, 0.26, -90, 160))),
            ['4'] = G(0.5, L(0.35, 0, 0, 0.7, 0.5, 0.7), L(0.38, 0.35, 0.38, 1)),
            ['f'] = G(0.4, Cat(Arc(0.38, 0.2, 0.18, 0.18, -20, -170), L(0.2, 0.2, 0.2, 1.0)), L(0.02, 0.5, 0.4, 0.5)),
            ['('] = G(0.25, Arc(0.4, 0.5, 0.35, 0.55, 230, 130)),
            [')'] = G(0.25, Arc(-0.15, 0.5, 0.35, 0.55, -50, 50)),
            ['s'] = G(0.4, Cat(Arc(0.2, 0.62, 0.18, 0.12, -20, -270), Arc(0.2, 0.87, 0.2, 0.13, -90, 150))),
            ['e'] = G(0.45, Cat(L(0.02, 0.75, 0.43, 0.75), Arc(0.225, 0.75, 0.225, 0.25, 0, -320))),
            ['i'] = G(0.1, L(0.05, 0.5, 0.05, 1), L(0.05, 0.3, 0.05, 0.34)),
            ['n'] = G(0.45, L(0.02, 0.5, 0.02, 1), Cat(Arc(0.235, 0.7, 0.215, 0.2, 180, 360), L(0.45, 0.7, 0.45, 1))),
        };

        /// <summary>Scrive un testo; '^' rende apice il carattere seguente, ' ' è uno spazio.</summary>
        public static StrokeCollection Write(string text, double size, double x0, double y0)
        {
            var res = new StrokeCollection();
            double x = x0;
            bool sup = false;
            foreach (char c in text)
            {
                if (c == '^') { sup = true; continue; }
                if (c == ' ') { x += 0.35 * size; continue; }
                var g = Map[c];
                double s = sup ? size * 0.5 : size, dy = sup ? -0.05 * size : 0;
                foreach (var st in g.Strokes) res.Add(Stroke(st.Select(p => new Point(x + p.X * s, y0 + dy + p.Y * s))));
                x += (g.Width + 0.22) * s;
                sup = false;
            }
            return res;
        }

        /// <summary>pre num/den, con la linea di frazione.</summary>
        public static StrokeCollection Fraction(string pre, string num, string den, double size, double x0, double y0)
        {
            var res = Write(pre, size, x0, y0);
            double x = x0 + Width(pre) * size;
            double w = Math.Max(Width(num), Width(den)) * size * 0.8 + 0.2 * size;
            res.Add(Write(num, size * 0.8, x + 0.1 * size, y0 - 0.55 * size));
            res.Add(Stroke(new[] { new Point(x, y0 + 0.7 * size), new Point(x + w, y0 + 0.7 * size) }));
            res.Add(Write(den, size * 0.8, x + 0.1 * size, y0 + 0.85 * size));
            return res;
        }

        /// <summary>pre √(rad).</summary>
        public static StrokeCollection Sqrt(string pre, string rad, double size, double x0, double y0)
        {
            var res = Write(pre, size, x0, y0);
            double x = x0 + Width(pre) * size, w = Width(rad) * size + 0.1 * size;
            res.Add(Stroke(new[]
            {
                new Point(x, y0 + 0.6 * size), new Point(x + 0.12 * size, y0 + 0.5 * size), new Point(x + 0.3 * size, y0 + size),
                new Point(x + 0.5 * size, y0 - 0.15 * size), new Point(x + 0.6 * size + w, y0 - 0.15 * size),
            }));
            res.Add(Write(rad, size, x + 0.65 * size, y0));
            return res;
        }

        static double Width(string t)
        {
            double w = 0;
            bool sup = false;
            foreach (char c in t)
            {
                if (c == '^') { sup = true; continue; }
                if (c == ' ') { w += 0.35; continue; }
                w += (Map[c].Width + 0.22) * (sup ? 0.5 : 1);
                sup = false;
            }
            return w;
        }

        /// <summary>Un tratto con un punto ogni ~1 px, come quelli veri.</summary>
        static Stroke Stroke(IEnumerable<Point> points)
        {
            var p = points.ToList();
            var dense = new StylusPointCollection { new StylusPoint(p[0].X, p[0].Y) };
            for (int i = 1; i < p.Count; i++)
            {
                var a = p[i - 1];
                var b = p[i];
                int n = Math.Max(1, (int)((b - a).Length / 1.0));
                for (int k = 1; k <= n; k++) dense.Add(new StylusPoint(a.X + (b.X - a.X) * k / n, a.Y + (b.Y - a.Y) * k / n));
            }
            return new Stroke(dense);
        }
    }
}
