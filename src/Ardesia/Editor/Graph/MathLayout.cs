using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Ardesia.Editor.Graph;

/// <summary>
/// Piccolo impaginatore di formule fatto in casa, nello stile di LaTeX: parte dall'albero di <see cref="MathParser"/>
/// e compone frazioni, esponenti, radici e valori assoluti; lettere in corsivo, numeri e nomi di funzione dritti.
/// Le parentesi si rimettono solo dove servono (l'albero non le conserva).
/// </summary>
public static class MathLayout
{
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    static readonly Typeface Upright = new(new FontFamily("Cambria, Cambria Math, Times New Roman"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface Italic = new(new FontFamily("Cambria, Cambria Math, Times New Roman"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Funzioni che si scrivono come nome dritto seguito dall'argomento (sen x, ln x…).</summary>
    static readonly HashSet<string> Named = new() { "sin", "sen", "cos", "tan", "tg", "cot", "cotg", "asin", "arcsin", "arcsen", "acos", "arccos", "atan", "arctan", "arctg", "ln", "log", "floor", "sign", "sgn" };

    /// <summary>La riga intera: «y = …», «f(x) = …» o «x = k».</summary>
    public static MathBox Line(ParsedCurve curve, double size, Brush brush, double ppd)
    {
        var b = new Builder(brush, ppd);
        var lhs = curve.Kind == CurveKind.Vertical ? b.Var("x", size)
            : curve.Name != null ? b.Row(b.Var(curve.Name, size), b.Fence(b.Var("x", size), size, "(", ")"))
            : b.Var("y", size);
        return b.Row(lhs, b.Op("=", size, 0.3), b.Build(curve.Expr, size).Box);
    }

    /// <summary>Solo l'espressione (per esempio dopo «derivata di»).</summary>
    public static MathBox Expression(MathNode node, double size, Brush brush, double ppd) => new Builder(brush, ppd).Build(node, size).Box;

    /// <summary>Testo semplice nel carattere delle formule (corsivo per i nomi: «a = 1,5»).</summary>
    public static MathBox Text(string text, double size, Brush brush, double ppd, bool italic = false) =>
        new Builder(brush, ppd).Text(text, size, italic);

    // precedenze: 1 = + −, 2 = prodotto, 3 = meno unario, 4 = potenza e frazione, 5 = atomo
    readonly record struct Built(MathBox Box, int Prec, bool LeadingDigit);

    sealed class Builder(Brush brush, double ppd)
    {
        /// <summary>Dentro un esponente: le frazioni si scrivono in linea (e^(x/2)), sarebbero troppo piccole.</summary>
        int _script;

        public MathBox Text(string s, double size, bool italic) => new TextBox(new FormattedText(s, It, FlowDirection.LeftToRight, italic ? Italic : Upright, size, brush, ppd), size, italic);
        public MathBox Var(string s, double size) => Text(s, size, italic: true);
        public MathBox Op(string s, double size, double space = 0.22) => new RowOf(new MathBox[] { new SpaceBox(size * space), Text(s, size, false), new SpaceBox(size * space) });
        public MathBox Row(params MathBox[] items) => new RowOf(items);
        public MathBox Fence(MathBox inner, double size, string open, string close) => new FenceBox(inner, size, open, close, brush, this);

        Built Wrap(Built b, int outer, double size) =>
            b.Prec < outer ? new Built(Fence(b.Box, size, "(", ")"), 5, false) : b;

        public Built Build(MathNode n, double size)
        {
            switch (n)
            {
                case NumNode num:
                    return new Built(Text(num.Value.ToString("0.############", It), size, false), 5, true);
                case ConstNode c:
                    return new Built(Var(c.Symbol, size), 5, false);
                case VarNode v:
                    return new Built(Var(v.Name, size), 5, false);
                case NegNode u:
                {
                    var arg = Wrap(Build(u.Arg, size), 3, size);
                    return new Built(Row(Text("−", size, false), arg.Box), 3, false);
                }
                case BinNode { Op: '+' or '-' } b:
                {
                    var l = Wrap(Build(b.Left, size), 1, size);
                    var r = Build(b.Right, size);
                    // x − (x − 1), x + (−1): la destra tra parentesi se è una somma o comincia col meno
                    r = Wrap(r, b.Op == '-' || r.Prec == 3 ? 2 : 1, size);
                    if (r.Prec == 3) r = new Built(Fence(r.Box, size, "(", ")"), 5, false);
                    return new Built(Row(l.Box, Op(b.Op == '-' ? "−" : "+", size), r.Box), 1, l.LeadingDigit);
                }
                case BinNode { Op: '*' } b:
                {
                    var l = Wrap(Build(b.Left, size), 2, size);
                    var r = Build(b.Right, size);
                    if (r.Prec == 3) r = new Built(Fence(r.Box, size, "(", ")"), 5, false);
                    r = Wrap(r, 2, size);
                    // 2x, ax, 3(x+1) senza segno; «·» se il docente l'ha scritto o se due numeri si toccherebbero
                    bool dot = !b.Implicit || r.LeadingDigit;
                    var row = dot ? Row(l.Box, Op("·", size, 0.15), r.Box) : Row(l.Box, new SpaceBox(size * 0.04), r.Box);
                    return new Built(row, 2, l.LeadingDigit);
                }
                case BinNode { Op: '/' } b when _script > 0:
                {
                    var l = Wrap(Build(b.Left, size), 2, size);
                    var r = Build(b.Right, size);
                    r = Wrap(r, 3, size);
                    return new Built(Row(l.Box, Text("/", size, false), r.Box), 2, l.LeadingDigit);
                }
                case BinNode { Op: '/', Left: NegNode neg } b:
                {
                    // −1/x si scrive come −(1 su x)
                    var f = Fraction(neg.Arg, b.Right, size);
                    return new Built(Row(Text("−", size, false), f), 3, false);
                }
                case BinNode { Op: '/' } b:
                    return new Built(Fraction(b.Left, b.Right, size), 4, false);
                case BinNode { Op: '^', Left: CallNode call } b when Named.Contains(call.Function) && b.Right is NumNode:
                {
                    // sen² x
                    var name = new SupBox(Text(call.Function, size, false), Script(b.Right, size), size, italic: false);
                    return new Built(Row(name, Argument(call.Arg, size)), 2, false);
                }
                case BinNode { Op: '^' } b:
                {
                    // (x + 1)², (−x)², (1/x)², (sen x)³: la base tra parentesi se non è un atomo
                    var bas = Wrap(Build(b.Left, size), 5, size);
                    var exp = Script(b.Right, size);
                    return new Built(new SupBox(bas.Box, exp, size, italic: bas.Box is TextBox { IsItalic: true }), 4, bas.LeadingDigit);
                }
                case CallNode { Function: "sqrt" or "√" } call:
                    return new Built(new RootBox(Build(call.Arg, size).Box, null, size, brush), 5, false);
                case CallNode { Function: "cbrt" } call:
                    return new Built(new RootBox(Build(call.Arg, size).Box, Text("3", Small(Small(size)), false), size, brush), 5, false);
                case CallNode { Function: "abs" } call:
                    return new Built(new FenceBox(Build(call.Arg, size).Box, size, "|", "|", brush, this), 5, false);
                case CallNode { Function: "exp" } call:
                    return new Built(new SupBox(Var("e", size), Script(call.Arg, size), size, italic: true), 4, false);
                case CallNode call:
                    return new Built(Row(Text(call.Function, size, false), Argument(call.Arg, size)), 2, false);
                default:
                    return new Built(Text("?", size, false), 5, false);
            }
        }

        static double Small(double size) => Math.Max(8, size * 0.7);

        MathBox Script(MathNode n, double size)
        {
            _script++;
            try
            {
                return Build(n, Small(size)).Box;
            }
            finally
            {
                _script--;
            }
        }

        MathBox Fraction(MathNode num, MathNode den, double size)
        {
            double s = Math.Max(9, size * 0.88);
            return new FracBox(Build(num, s).Box, Build(den, s).Box, size, brush);
        }

        /// <summary>sen x, sen 2x, ln x senza parentesi; sen(x + 1) con le parentesi.</summary>
        MathBox Argument(MathNode arg, double size)
        {
            static bool Atom(MathNode m) => m is VarNode or ConstNode or NumNode;
            bool bare = Atom(arg) || arg is BinNode { Op: '*', Implicit: true } p && Atom(p.Left) && Atom(p.Right);
            var built = Build(arg, size);
            return bare ? Row(new SpaceBox(size * 0.16), built.Box) : Fence(built.Box, size, "(", ")");
        }
    }

    // ───────────────────────────── Scatole ─────────────────────────────

    /// <summary>Un pezzo di formula: larghezza, altezza sopra la linea di base e profondità sotto.</summary>
    public abstract class MathBox
    {
        public double Width { get; protected set; }
        public double Ascent { get; protected set; }
        public double Descent { get; protected set; }
        public double Height => Ascent + Descent;
        public abstract void Draw(DrawingContext dc, double x, double baseline);
    }

    sealed class SpaceBox : MathBox
    {
        public SpaceBox(double w) => Width = w;
        public override void Draw(DrawingContext dc, double x, double baseline) { }
    }

    sealed class TextBox : MathBox
    {
        readonly FormattedText _ft;
        public bool IsItalic { get; }

        public TextBox(FormattedText ft, double size, bool italic)
        {
            _ft = ft;
            IsItalic = italic;
            // misure fisse in proporzione al corpo: quelle del carattere (Cambria Math soprattutto) sono troppo alte
            Width = ft.WidthIncludingTrailingWhitespace;
            Ascent = size * 0.72;
            Descent = size * 0.24;
        }

        public override void Draw(DrawingContext dc, double x, double baseline) => dc.DrawText(_ft, new Point(x, baseline - _ft.Baseline));
    }

    /// <summary>Più pezzi uno dopo l'altro, sulla stessa linea di base.</summary>
    public sealed class RowOf : MathBox
    {
        readonly MathBox[] _items;

        public RowOf(params MathBox[] items)
        {
            _items = items;
            Width = items.Sum(i => i.Width);
            Ascent = items.Length == 0 ? 0 : items.Max(i => i.Ascent);
            Descent = items.Length == 0 ? 0 : items.Max(i => i.Descent);
        }

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            foreach (var i in _items)
            {
                i.Draw(dc, x, baseline);
                x += i.Width;
            }
        }
    }

    sealed class SupBox : MathBox
    {
        readonly MathBox _base, _exp;
        readonly double _raise, _gap;

        public SupBox(MathBox bas, MathBox exp, double size, bool italic)
        {
            _base = bas;
            _exp = exp;
            _raise = Math.Max(size * 0.42, bas.Ascent - exp.Ascent * 0.45);
            _gap = italic ? size * 0.07 : size * 0.03; // correzione del corsivo: x² non deve toccare la x
            Width = bas.Width + _gap + exp.Width;
            Ascent = Math.Max(bas.Ascent, _raise + exp.Ascent);
            Descent = Math.Max(bas.Descent, exp.Descent - _raise);
        }

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            _base.Draw(dc, x, baseline);
            _exp.Draw(dc, x + _base.Width + _gap, baseline - _raise);
        }
    }

    internal sealed class FracBox : MathBox
    {
        readonly MathBox _num, _den;
        readonly double _axis, _gap, _pad;
        readonly Pen _pen;

        public FracBox(MathBox num, MathBox den, double size, Brush brush)
        {
            _num = num;
            _den = den;
            _axis = size * 0.27; // altezza della linea di frazione: a metà del segno «−»
            _gap = size * 0.14;
            _pad = size * 0.12;
            _pen = new Pen(brush, Math.Max(1, size * 0.06));
            _pen.Freeze();
            Width = Math.Max(num.Width, den.Width) + 2 * _pad;
            Ascent = _axis + _gap + num.Height;
            Descent = Math.Max(0, _gap + den.Height - _axis);
        }

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            double bar = baseline - _axis;
            dc.DrawLine(_pen, new Point(x + _pad * 0.4, bar), new Point(x + Width - _pad * 0.4, bar));
            _num.Draw(dc, x + (Width - _num.Width) / 2, bar - _gap - _num.Descent);
            _den.Draw(dc, x + (Width - _den.Width) / 2, bar + _gap + _den.Ascent);
        }
    }

    internal sealed class RootBox : MathBox
    {
        readonly MathBox _arg;
        readonly MathBox? _index;
        readonly double _sign, _gap;
        readonly Pen _pen;

        public RootBox(MathBox arg, MathBox? index, double size, Brush brush)
        {
            _arg = arg;
            _index = index;
            _sign = size * 0.6;
            _gap = size * 0.12;
            _pen = new Pen(brush, Math.Max(1, size * 0.06)) { LineJoin = PenLineJoin.Round };
            _pen.Freeze();
            Width = _sign + arg.Width + size * 0.08;
            Ascent = arg.Ascent + _gap + _pen.Thickness;
            Descent = arg.Descent;
            if (index != null) Ascent = Math.Max(Ascent, arg.Ascent * 0.4 + index.Height + _gap);
        }

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            double top = baseline - _arg.Ascent - _gap, bottom = baseline + _arg.Descent;
            double mid = baseline - (_arg.Ascent - _arg.Descent) * 0.35;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(x + _sign * 0.05, mid + _sign * 0.08), false, false);
                g.LineTo(new Point(x + _sign * 0.25, mid - _sign * 0.05), true, true);
                g.LineTo(new Point(x + _sign * 0.5, bottom), true, true);
                g.LineTo(new Point(x + _sign * 0.9, top), true, true);
                g.LineTo(new Point(x + Width, top), true, true);
            }
            geo.Freeze();
            dc.DrawGeometry(null, _pen, geo);
            _index?.Draw(dc, x + _sign * 0.05, mid - _sign * 0.12 - _index.Descent);
            _arg.Draw(dc, x + _sign, baseline);
        }
    }

    /// <summary>Parentesi o barre del valore assoluto: col carattere se il contenuto è basso, disegnate se è alto (frazioni).</summary>
    sealed class FenceBox : MathBox
    {
        readonly MathBox _inner;
        readonly MathBox? _open, _close;
        readonly double _side, _pad;
        readonly Pen _pen;
        public bool Bars { get; }

        public FenceBox(MathBox inner, double size, string open, string close, Brush brush, Builder b)
        {
            _inner = inner;
            Bars = open == "|";
            _pen = new Pen(brush, Math.Max(1, size * 0.065)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _pen.Freeze();
            bool tall = inner.Height > size * 1.15;
            if (!tall)
            {
                _open = b.Text(open, size, false);
                _close = b.Text(close, size, false);
                _pad = Bars ? size * 0.06 : 0;
                Width = _open.Width + inner.Width + _close.Width + 2 * _pad;
                Ascent = Math.Max(inner.Ascent, _open.Ascent + size * 0.05);
                Descent = Math.Max(inner.Descent, _open.Descent);
            }
            else
            {
                _side = size * (Bars ? 0.25 : 0.38);
                _pad = size * 0.05;
                Width = inner.Width + 2 * (_side + _pad);
                Ascent = inner.Ascent + size * 0.08;
                Descent = inner.Descent + size * 0.08;
            }
        }

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            if (_open != null && _close != null)
            {
                _open.Draw(dc, x, baseline);
                _inner.Draw(dc, x + _open.Width + _pad, baseline);
                _close.Draw(dc, x + _open.Width + _inner.Width + 2 * _pad, baseline);
                return;
            }
            double top = baseline - Ascent, bottom = baseline + Descent;
            double l = x + _side * 0.55, r = x + Width - _side * 0.55;
            if (Bars)
            {
                dc.DrawLine(_pen, new Point(l, top), new Point(l, bottom));
                dc.DrawLine(_pen, new Point(r, top), new Point(r, bottom));
            }
            else
            {
                double bow = _side * 0.45;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(l + bow * 0.5, top), false, false);
                    g.QuadraticBezierTo(new Point(l - bow, (top + bottom) / 2), new Point(l + bow * 0.5, bottom), true, true);
                    g.BeginFigure(new Point(r - bow * 0.5, top), false, false);
                    g.QuadraticBezierTo(new Point(r + bow, (top + bottom) / 2), new Point(r - bow * 0.5, bottom), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, _pen, geo);
            }
            _inner.Draw(dc, x + _side + _pad, baseline);
        }
    }
}
