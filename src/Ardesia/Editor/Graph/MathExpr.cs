using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ardesia.Editor.Graph;

/// <summary>Conversioni del testo scritto «da quaderno» in espressioni leggibili dalla macchina (usate anche per GeoGebra).</summary>
public static class MathText
{
    const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹";

    /// <summary>Una riga: apici (x²) → x^(2), simboli tipografici → ASCII, virgola decimale (1,5) → punto.</summary>
    public static string Normalize(string line)
    {
        var s = line.Trim();
        s = Regex.Replace(s, "[⁰¹²³⁴⁵⁶⁷⁸⁹]+", m => "^(" + string.Concat(m.Value.Select(Superscript)) + ")");
        s = s.Replace('·', '*').Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace('–', '-').Replace('⋅', '*');
        s = Regex.Replace(s, @"(?<=\d),(?=\d)", ".");
        return s;
    }

    /// <summary>Le righe non vuote di un testo, già normalizzate.</summary>
    public static IReadOnlyList<string> Lines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        return text.Replace("\r\n", "\n").Split('\n').Select(Normalize).Where(s => s.Length > 0).ToList();
    }

    static char Superscript(char c) => Superscripts.IndexOf(c) is var i and >= 0 ? (char)('0' + i) : c;
}

/// <summary>Errore di scrittura in una funzione, con un messaggio in italiano per il docente.</summary>
public sealed class MathSyntaxException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}

/// <summary>Che cosa descrive una riga: una funzione y = f(x) o una retta verticale x = k.</summary>
public enum CurveKind { Function, Vertical }

/// <summary>Una riga del grafico già interpretata.</summary>
public sealed class ParsedCurve
{
    public CurveKind Kind { get; init; }
    /// <summary>Nome dato dal docente (f in «f(x) = …»), altrimenti null.</summary>
    public string? Name { get; init; }
    public MathNode Expr { get; init; } = new NumNode(0);
    /// <summary>Lettere diverse da x usate come parametri (a, b, k…), in ordine alfabetico.</summary>
    public IReadOnlyList<string> Parameters { get; init; } = Array.Empty<string>();

    /// <summary>Compila l'espressione: i parametri sono passati nell'ordine di <paramref name="paramNames"/>.</summary>
    public Func<double, double[], double> Compile(IReadOnlyList<string> paramNames) => Expr.Compile(paramNames);
}

// ───────────────────────────── Albero dell'espressione ─────────────────────────────

public abstract record MathNode
{
    public abstract Func<double, double[], double> Compile(IReadOnlyList<string> paramNames);
    public abstract void CollectParameters(ISet<string> set);
}

public sealed record NumNode(double Value) : MathNode
{
    public override Func<double, double[], double> Compile(IReadOnlyList<string> p) { var v = Value; return (_, _) => v; }
    public override void CollectParameters(ISet<string> set) { }
}

public sealed record VarNode(string Name) : MathNode
{
    public override Func<double, double[], double> Compile(IReadOnlyList<string> p)
    {
        if (Name == "x") return (x, _) => x;
        int i = p.ToList().IndexOf(Name);
        if (i < 0) return (_, _) => 1;
        return (_, a) => i < a.Length ? a[i] : 1;
    }
    public override void CollectParameters(ISet<string> set) { if (Name != "x") set.Add(Name); }
}

public sealed record NegNode(MathNode Arg) : MathNode
{
    public override Func<double, double[], double> Compile(IReadOnlyList<string> p) { var a = Arg.Compile(p); return (x, v) => -a(x, v); }
    public override void CollectParameters(ISet<string> set) => Arg.CollectParameters(set);
}

public sealed record BinNode(char Op, MathNode Left, MathNode Right) : MathNode
{
    public override Func<double, double[], double> Compile(IReadOnlyList<string> p)
    {
        var l = Left.Compile(p);
        var r = Right.Compile(p);
        return Op switch
        {
            '+' => (x, v) => l(x, v) + r(x, v),
            '-' => (x, v) => l(x, v) - r(x, v),
            '*' => (x, v) => l(x, v) * r(x, v),
            '/' => (x, v) => l(x, v) / r(x, v),
            '^' => (x, v) => MathFunctions.Pow(l(x, v), r(x, v)),
            _ => throw new InvalidOperationException(),
        };
    }
    public override void CollectParameters(ISet<string> set) { Left.CollectParameters(set); Right.CollectParameters(set); }
}

public sealed record CallNode(string Function, MathNode Arg) : MathNode
{
    public override Func<double, double[], double> Compile(IReadOnlyList<string> p)
    {
        var a = Arg.Compile(p);
        var f = MathFunctions.Get(Function);
        return (x, v) => f(a(x, v));
    }
    public override void CollectParameters(ISet<string> set) => Arg.CollectParameters(set);
}

/// <summary>Le funzioni che si possono scrivere, con i nomi italiani e inglesi.</summary>
public static class MathFunctions
{
    static readonly Dictionary<string, Func<double, double>> Map = new()
    {
        ["sin"] = Math.Sin, ["sen"] = Math.Sin,
        ["cos"] = Math.Cos,
        ["tan"] = Math.Tan, ["tg"] = Math.Tan,
        ["cot"] = v => 1 / Math.Tan(v), ["cotg"] = v => 1 / Math.Tan(v),
        ["asin"] = Math.Asin, ["arcsin"] = Math.Asin, ["arcsen"] = Math.Asin,
        ["acos"] = Math.Acos, ["arccos"] = Math.Acos,
        ["atan"] = Math.Atan, ["arctan"] = Math.Atan, ["arctg"] = Math.Atan,
        ["sqrt"] = Math.Sqrt, ["√"] = Math.Sqrt,
        ["cbrt"] = Math.Cbrt,
        ["abs"] = Math.Abs,
        ["ln"] = Math.Log,
        ["log"] = Math.Log10,
        ["exp"] = Math.Exp,
        ["floor"] = Math.Floor,
        ["sign"] = v => double.IsNaN(v) ? double.NaN : Math.Sign(v), ["sgn"] = v => double.IsNaN(v) ? double.NaN : Math.Sign(v),
    };

    /// <summary>Nomi in ordine dal più lungo: «arcsin» va riconosciuto prima di «a»·«r»·«c»·«sin».</summary>
    public static readonly string[] Names = Map.Keys.Where(k => k != "√").OrderByDescending(k => k.Length).ToArray();

    public static bool IsFunction(string name) => Map.ContainsKey(name);

    public static Func<double, double> Get(string name) => Map[name];

    /// <summary>Potenza che accetta anche basi negative con esponenti frazionari a denominatore dispari: x^(1/3).</summary>
    public static double Pow(double b, double e)
    {
        if (b >= 0 || double.IsNaN(b) || e == Math.Floor(e)) return Math.Pow(b, e);
        for (int q = 3; q <= 15; q += 2)
        {
            double pq = e * q;
            if (Math.Abs(pq - Math.Round(pq)) < 1e-9)
            {
                double r = Math.Pow(-b, e);
                return ((long)Math.Round(pq) % 2 == 0) ? r : -r;
            }
        }
        return double.NaN;
    }
}

// ───────────────────────────── Lettura del testo ─────────────────────────────

/// <summary>
/// Parser a discesa ricorsiva, fatto in casa. Riconosce «y = …», «f(x) = …», «x = k» o la sola espressione;
/// moltiplicazione sottintesa (2x, 3(x+1), ax), funzioni senza parentesi (sen x), valore assoluto |x|.
/// </summary>
public sealed class MathParser
{
    enum T { Num, Ident, Op, LParen, RParen, Bar, Equals, End }

    readonly record struct Token(T Type, string Text, int Pos, double Value = 0);

    readonly List<Token> _tokens;
    int _i;
    int _absDepth;

    MathParser(List<Token> tokens) => _tokens = tokens;

    /// <summary>Interpreta una riga (già passata da <see cref="MathText.Normalize"/>).</summary>
    public static ParsedCurve ParseLine(string line)
    {
        var tokens = Tokenize(line);
        int eq = tokens.FindIndex(t => t.Type == T.Equals);
        if (tokens.Count(t => t.Type == T.Equals) > 1)
            throw new MathSyntaxException("C'è più di un segno «=»", tokens.Where(t => t.Type == T.Equals).ElementAt(1).Pos);

        CurveKind kind = CurveKind.Function;
        string? name = null;
        List<Token> rhs;
        if (eq < 0)
        {
            rhs = tokens;
        }
        else
        {
            var lhs = tokens.Take(eq).ToList();
            rhs = tokens.Skip(eq + 1).ToList();
            var text = string.Concat(lhs.Select(t => t.Text));
            if (text == "y") { }
            else if (text == "x") kind = CurveKind.Vertical;
            else if (Regex.Match(text, @"^([a-zA-Z])\(x\)$") is { Success: true } m && m.Groups[1].Value != "x") name = m.Groups[1].Value;
            else if (lhs.Count == 0) throw new MathSyntaxException("Manca qualcosa prima del segno «=»", tokens[eq].Pos);
            else throw new MathSyntaxException("A sinistra di «=» scrivi y, f(x) oppure x", lhs[0].Pos);
            if (rhs.Count == 0) throw new MathSyntaxException("Manca l'espressione dopo il segno «=»", tokens[eq].Pos + 1);
        }

        rhs.Add(new Token(T.End, "", line.Length));
        var parser = new MathParser(rhs);
        var expr = parser.ParseExpression();
        if (parser.Peek.Type != T.End)
        {
            var t = parser.Peek;
            throw t.Type == T.RParen
                ? new MathSyntaxException("C'è una parentesi chiusa di troppo", t.Pos)
                : new MathSyntaxException($"Non capisco «{t.Text}» qui", t.Pos);
        }

        var parameters = new SortedSet<string>(StringComparer.Ordinal);
        expr.CollectParameters(parameters);
        if (parameters.Contains("y")) throw new MathSyntaxException("La y può stare solo a sinistra del segno «=»", 0);
        if (kind == CurveKind.Vertical && UsesX(expr)) throw new MathSyntaxException("In «x = …» a destra serve un numero", 0);
        return new ParsedCurve { Kind = kind, Name = name, Expr = expr, Parameters = parameters.ToList() };
    }

    static bool UsesX(MathNode n) => n switch
    {
        VarNode v => v.Name == "x",
        NegNode u => UsesX(u.Arg),
        BinNode b => UsesX(b.Left) || UsesX(b.Right),
        CallNode c => UsesX(c.Arg),
        _ => false,
    };

    static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                var text = s[start..i];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw new MathSyntaxException($"Il numero «{text}» non è scritto bene", start);
                list.Add(new Token(T.Num, text, start, v));
                continue;
            }
            if (char.IsLetter(c))
            {
                int start = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                SplitWord(s[start..i].ToLowerInvariant(), start, list);
                continue;
            }
            switch (c)
            {
                case '+' or '-' or '*' or '/' or '^': list.Add(new Token(T.Op, c.ToString(), i)); break;
                case '(' or '[' or '{': list.Add(new Token(T.LParen, "(", i)); break;
                case ')' or ']' or '}': list.Add(new Token(T.RParen, ")", i)); break;
                case '|': list.Add(new Token(T.Bar, "|", i)); break;
                case '=': list.Add(new Token(T.Equals, "=", i)); break;
                case '√': list.Add(new Token(T.Ident, "√", i)); break;
                case ':': list.Add(new Token(T.Op, "/", i)); break;
                default: throw new MathSyntaxException($"Il simbolo «{c}» non si può usare", i);
            }
            i++;
        }
        return list;
    }

    /// <summary>Una parola: nomi di funzione e costanti noti, altrimenti lettere singole (ax = a·x).</summary>
    static void SplitWord(string word, int pos, List<Token> list)
    {
        int i = 0;
        while (i < word.Length)
        {
            var rest = word[i..];
            var fn = MathFunctions.Names.FirstOrDefault(n => rest.StartsWith(n, StringComparison.Ordinal));
            if (fn != null)
            {
                // «sinx» va bene, «sinn» o «senax» sono quasi sempre errori di battitura
                var after = word[(i + fn.Length)..];
                if (!OnlyKnown(after))
                    throw new MathSyntaxException($"Non conosco «{word}»: scrivi per esempio {fn}(x)", pos);
                list.Add(new Token(T.Ident, fn, pos + i));
                i += fn.Length;
            }
            else if (rest.StartsWith("pi", StringComparison.Ordinal))
            {
                list.Add(new Token(T.Ident, "π", pos + i));
                i += 2;
            }
            else
            {
                list.Add(new Token(T.Ident, word[i].ToString(), pos + i));
                i++;
            }
        }
    }

    /// <summary>La parola si scompone tutta in x, π e nomi di funzione (es. «xcosx»).</summary>
    static bool OnlyKnown(string w)
    {
        if (w.Length == 0) return true;
        if (w[0] == 'x' && OnlyKnown(w[1..])) return true;
        if (w.StartsWith("pi", StringComparison.Ordinal) && OnlyKnown(w[2..])) return true;
        return MathFunctions.Names.Any(n => w.StartsWith(n, StringComparison.Ordinal) && OnlyKnown(w[n.Length..]));
    }

    Token Peek => _tokens[_i];
    Token Next() => _tokens[_i++];

    // espressione := termine (('+' | '-') termine)*
    MathNode ParseExpression()
    {
        var left = ParseTerm();
        while (Peek is { Type: T.Op, Text: "+" or "-" })
        {
            char op = Next().Text[0];
            left = new BinNode(op, left, ParseTerm());
        }
        return left;
    }

    // termine := unario (('*' | '/' | sottintesa) unario)*
    MathNode ParseTerm()
    {
        var left = ParseUnary();
        while (true)
        {
            if (Peek is { Type: T.Op, Text: "*" or "/" })
            {
                char op = Next().Text[0];
                if (Peek.Type == T.End) throw new MathSyntaxException($"Manca qualcosa dopo «{op}»", Peek.Pos);
                left = new BinNode(op, left, ParseUnary());
            }
            else if (StartsFactor(Peek))
            {
                left = new BinNode('*', left, ParsePower());
            }
            else
            {
                return left;
            }
        }
    }

    bool StartsFactor(Token t) => t.Type switch
    {
        T.Num or T.Ident or T.LParen => true,
        T.Bar => _absDepth == 0,
        _ => false,
    };

    // unario := ('-' | '+') unario | potenza        (così -x² = -(x²))
    MathNode ParseUnary()
    {
        if (Peek is { Type: T.Op, Text: "-" }) { Next(); return new NegNode(ParseUnary()); }
        if (Peek is { Type: T.Op, Text: "+" }) { Next(); return ParseUnary(); }
        return ParsePower();
    }

    // potenza := primario ('^' unario)?            (associativa a destra: 2^3^2 = 2^9)
    MathNode ParsePower()
    {
        var b = ParsePrimary();
        if (Peek is { Type: T.Op, Text: "^" })
        {
            Next();
            if (Peek.Type == T.End) throw new MathSyntaxException("Manca l'esponente dopo «^»", Peek.Pos);
            return new BinNode('^', b, ParseUnary());
        }
        return b;
    }

    MathNode ParsePrimary()
    {
        var t = Peek;
        switch (t.Type)
        {
            case T.Num:
                Next();
                return new NumNode(t.Value);
            case T.LParen:
            {
                Next();
                var e = ParseInside();
                if (Peek.Type != T.RParen) throw new MathSyntaxException("Manca una parentesi chiusa", Peek.Pos);
                Next();
                return e;
            }
            case T.Bar:
            {
                Next();
                _absDepth++;
                var e = ParseExpressionInAbs();
                _absDepth--;
                if (Peek.Type != T.Bar) throw new MathSyntaxException("Manca la barra «|» che chiude il valore assoluto", Peek.Pos);
                Next();
                return new CallNode("abs", e);
            }
            case T.Ident:
                Next();
                if (t.Text == "π") return new NumNode(Math.PI);
                if (t.Text == "e") return new NumNode(Math.E);
                if (MathFunctions.IsFunction(t.Text)) return ParseCall(t);
                if (t.Text.Length == 1 && char.IsLetter(t.Text[0])) return new VarNode(t.Text);
                throw new MathSyntaxException($"Non conosco «{t.Text}»", t.Pos);
            case T.End:
                throw new MathSyntaxException("L'espressione finisce troppo presto", t.Pos);
            case T.RParen:
                throw new MathSyntaxException("C'è una parentesi chiusa di troppo", t.Pos);
            default:
                throw new MathSyntaxException($"Non capisco «{t.Text}» qui", t.Pos);
        }
    }

    /// <summary>Dentro le parentesi le barre del valore assoluto ricominciano da capo: |(|x|+1)|.</summary>
    MathNode ParseInside()
    {
        int saved = _absDepth;
        _absDepth = 0;
        var e = ParseExpression();
        _absDepth = saved;
        return e;
    }

    MathNode ParseExpressionInAbs() => ParseExpression();

    /// <summary>
    /// sen(x), sen x, sen 2x (= sen(2x)), sen² x: senza parentesi l'argomento è il prodotto che segue,
    /// fino a + o − o a un'altra funzione (sen x cos x = sen(x)·cos(x)).
    /// </summary>
    MathNode ParseCall(Token fn)
    {
        MathNode? power = null;
        if (Peek is { Type: T.Op, Text: "^" })
        {
            // sen^2 x = (sen x)^2
            Next();
            power = ParsePrimary();
        }
        MathNode arg;
        if (Peek.Type == T.LParen)
        {
            Next();
            arg = ParseInside();
            if (Peek.Type != T.RParen) throw new MathSyntaxException("Manca una parentesi chiusa", Peek.Pos);
            Next();
            var call = new CallNode(fn.Text, arg);
            if (power != null) return new BinNode('^', call, power);
            if (Peek is { Type: T.Op, Text: "^" })
            {
                Next();
                return new BinNode('^', call, ParseUnary());
            }
            return call;
        }
        if (Peek.Type == T.End || !StartsFactor(Peek) && Peek is not { Type: T.Op, Text: "-" })
            throw new MathSyntaxException($"Dopo «{fn.Text}» manca l'argomento", Peek.Pos);
        if (Peek is { Type: T.Op, Text: "-" })
        {
            Next();
            arg = new NegNode(ParsePower());
        }
        else
        {
            arg = ParsePower();
        }
        while (StartsFactor(Peek) && !(Peek.Type == T.Ident && MathFunctions.IsFunction(Peek.Text)))
            arg = new BinNode('*', arg, ParsePower());
        var result = new CallNode(fn.Text, arg);
        return power != null ? new BinNode('^', result, power) : result;
    }
}
