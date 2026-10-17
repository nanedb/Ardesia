using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ardesia.Editor.Graph;

/// <summary>
/// Traduce il MathML del riconoscitore di Windows nel testo che si scrive in «Funzioni del grafico»
/// (y = x² − 4, y = (x + 1)/(x − 2), y = √(x), y = sen x…), correggendo gli sbagli noti del riconoscitore,
/// che è inglese: «sen» letto come s·e·π, «tg» come t con pedice 9.
/// </summary>
public static class MathMLText
{
    const string SuperDigits = "⁰¹²³⁴⁵⁶⁷⁸⁹";

    /// <summary>Il testo di una formula, o "" se il MathML è vuoto o illeggibile.</summary>
    public static string ToText(string? mathml)
    {
        if (string.IsNullOrWhiteSpace(mathml)) return "";
        XElement root;
        try { root = XElement.Parse(mathml); }
        catch (System.Xml.XmlException) { return ""; }
        var sb = new StringBuilder();
        Row(root.Elements(), sb);
        return Fix(sb.ToString());
    }

    /// <summary>Pezzi di una riga uno dopo l'altro, con gli spazi attorno agli operatori.</summary>
    static void Row(IEnumerable<XElement> items, StringBuilder sb)
    {
        var list = items.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            string name = e.Name.LocalName;
            if (name == "mo")
            {
                Operator(e.Value.Trim(), sb, IsFunctionMo(e));
                continue;
            }
            if (name == "mi" && e.Value.Trim().Length > 1 && MathFunctions.IsFunction(e.Value.Trim()))
            {
                Append(sb, e.Value.Trim() + " ");
                continue;
            }
            string part = Node(e);
            if (part.Length == 0) continue;
            // una frazione in mezzo a un prodotto sottinteso va tra parentesi: (3/2)x, non 3/2x
            if (name == "mfrac" && part.Contains('/') &&
                (EndsWithFactor(sb) || (i + 1 < list.Count && list[i + 1].Name.LocalName is not "mo")))
                part = "(" + part + ")";
            Append(sb, part);
        }
    }

    static bool IsFunctionMo(XElement e)
    {
        var s = e.Value.Trim();
        return s.Length > 1 && s.All(char.IsLetter);
    }

    static void Operator(string op, StringBuilder sb, bool function)
    {
        if (function)
        {
            Append(sb, op + " ");
            return;
        }
        switch (op)
        {
            case "⁡": case "⁢": case "": // applicazione di funzione, prodotto invisibile
                return;
            case "=": case "+": case "-": case "−": case "±": case "<": case ">": case "≤": case "≥":
            {
                string o = op == "-" ? "−" : op;
                string before = sb.ToString().TrimEnd();
                // segno davanti (y = −x², (−1)): senza spazio dopo
                bool unary = o is "−" or "+" or "±" && (before.Length == 0 || before.EndsWith('=') || before.EndsWith('(') || before.EndsWith('|') && CountChar(before, '|') % 2 == 1);
                TrimEnd(sb);
                if (unary) sb.Append(before.Length == 0 ? "" : " ").Append(o);
                else sb.Append(' ').Append(o).Append(' ');
                return;
            }
            case "*": case "×": case "⋅": case "·": case "∙":
                TrimEnd(sb);
                sb.Append('·');
                return;
            case "/": case "÷":
                TrimEnd(sb);
                sb.Append('/');
                return;
            default:
                Append(sb, op);
                return;
        }
    }

    /// <summary>Un elemento MathML come testo.</summary>
    static string Node(XElement e)
    {
        var kids = e.Elements().ToList();
        switch (e.Name.LocalName)
        {
            case "mi":
                return Identifier(e.Value.Trim());
            case "mn":
                return e.Value.Trim();
            case "mo":
            {
                var sb = new StringBuilder();
                Operator(e.Value.Trim(), sb, IsFunctionMo(e));
                return sb.ToString();
            }
            case "mtext":
                return e.Value.Trim();
            case "mspace":
                return "";
            case "mfenced":
            {
                string open = (string?)e.Attribute("open") ?? "(", close = (string?)e.Attribute("close") ?? ")";
                string sep = ((string?)e.Attribute("separators") ?? ",").Trim();
                var parts = kids.Select(Inner).ToList();
                return open + string.Join((sep.Length > 0 ? sep[..1] : ",") + " ", parts) + close;
            }
            case "msup" when kids.Count == 2:
            {
                string bas = Inner(kids[0]), exp = Inner(kids[1]);
                return Wrap(bas, forPower: true) + Power(exp);
            }
            case "msub" when kids.Count == 2:
            {
                string bas = Inner(kids[0]), sub = Inner(kids[1]);
                // «tg» scritto a mano: il riconoscitore vede una t con pedice 9 (o g)
                if (bas == "t" && sub is "9" or "g") return "tg ";
                if (bas is "log" or "log ") return "log_" + sub + " ";
                return bas + "_" + sub;
            }
            case "msubsup" when kids.Count == 3:
                return Inner(kids[0]) + "_" + Inner(kids[1]) + Power(Inner(kids[2]));
            case "mfrac" when kids.Count == 2:
                return Wrap(Inner(kids[0]), forPower: false) + "/" + Wrap(Inner(kids[1]), forPower: false);
            case "msqrt":
                return "√(" + Inner(e) + ")";
            case "mroot" when kids.Count == 2:
            {
                string arg = Inner(kids[0]), index = Inner(kids[1]);
                return "(" + arg + ")^(1/" + index + ")";
            }
            default: // math, mrow, mstyle, munderover e simili: i figli in fila
                return Inner(e);
        }
    }

    /// <summary>I figli di un elemento come una riga.</summary>
    static string Inner(XElement e)
    {
        if (!e.HasElements) return e.Name.LocalName == "mi" ? Identifier(e.Value.Trim()) : e.Value.Trim();
        if (e.Name.LocalName is "mi" or "mn" or "mo") return Node(e);
        var sb = new StringBuilder();
        Row(e.Elements(), sb);
        return sb.ToString().Trim();
    }

    static string Identifier(string s) => s switch
    {
        "ⅇ" or "ℯ" => "e",
        "Π" or "ϖ" => "π",
        _ => s,
    };

    /// <summary>Esponente: x², x¹⁰ con le cifre in apice; altrimenti x^n o x^(…).</summary>
    static string Power(string exp)
    {
        if (exp.Length > 0 && exp.All(char.IsDigit)) return string.Concat(exp.Select(c => SuperDigits[c - '0']));
        return "^" + Wrap(exp, forPower: false);
    }

    /// <summary>Tra parentesi se non è un pezzo solo (numero, lettera, lettera con apice, già tra parentesi).</summary>
    static string Wrap(string s, bool forPower)
    {
        s = s.Trim();
        // √(x)² sarebbe ambiguo: la radice sotto un esponente va tra parentesi
        if (Atom.IsMatch(s) && !(forPower && s.StartsWith('√'))) return s;
        if (s.StartsWith('(') && s.EndsWith(')') && Balanced(s[1..^1])) return s;
        return "(" + s + ")";
    }

    static readonly Regex Atom = new(@"^(\d+([.,]\d+)?|[A-Za-zπ][⁰¹²³⁴⁵⁶⁷⁸⁹]*|\d+[⁰¹²³⁴⁵⁶⁷⁸⁹]*|√\(.*\))$");

    static bool Balanced(string s)
    {
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    static void Append(StringBuilder sb, string part)
    {
        if (part.Length == 0) return;
        // due numeri vicini (2 e 1/x) non devono fondersi in uno solo
        if (sb.Length > 0 && char.IsDigit(sb[^1]) && char.IsDigit(part[0])) sb.Append('·');
        sb.Append(part);
    }

    static bool EndsWithFactor(StringBuilder sb)
    {
        for (int i = sb.Length - 1; i >= 0; i--)
        {
            if (sb[i] == ' ') continue;
            return char.IsLetterOrDigit(sb[i]) || sb[i] is ')' or 'π' || SuperDigits.Contains(sb[i]);
        }
        return false;
    }

    static void TrimEnd(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
    }

    static int CountChar(string s, char c) => s.Count(x => x == c);

    /// <summary>Gli sbagli noti del riconoscitore e un po' d'ordine negli spazi.</summary>
    static string Fix(string s)
    {
        s = s.Trim();
        // «sen» italiano: s·e·n, con la n spesso letta come π
        s = Regex.Replace(s, @"(?<![A-Za-z])(arc)?se[nπη]\s*", m => (m.Groups[1].Value) + "sen ");
        // spazio tra il nome della funzione e l'argomento senza parentesi: sen x, cos 2x
        s = Regex.Replace(s, @"(arcsen|arcsin|arccos|arctg|arctan|sen|sin|cos|tg|tan|ln|log)\s*(?=[A-Za-z0-9π√|])", "$1 ");
        s = Regex.Replace(s, @"(arcsen|arcsin|arccos|arctg|arctan|sen|sin|cos|tg|tan|ln|log)\s+(?=\()", "$1");
        // a inizio riga: Y = … → y = …, F(x) = … → f(x) = …
        s = Regex.Replace(s, @"^Y(?=\s*=)", "y");
        s = Regex.Replace(s, @"^([FGH])(?=\(x\)\s*=)", m => m.Value.ToLowerInvariant());
        s = Regex.Replace(s, @"\s{2,}", " ");
        return s.Trim();
    }
}
