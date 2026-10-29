using System.Text;
using System.Text.RegularExpressions;

namespace Ardesia.Editor.Formula;

/// <summary>
/// Traduce una formula LaTeX nella scrittura lineare del grafico di Ardesia e di GeoGebra (vedi Graph/MathExpr.cs):
/// <c>y = \frac{1}{x^2+1}</c> → <c>y = ((1)/(x^(2)+1))</c>, <c>\sqrt{x}</c> → <c>sqrt(x)</c>, <c>\sen x</c> → <c>sen x</c>.
/// Serve quando si chiede un grafico da una casella di testo che contiene formule.
/// </summary>
public static class LatexToPlain
{
    static readonly Regex Dollars = new(@"\$\$(.+?)\$\$|\$(.+?)\$", RegexOptions.Compiled);

    /// <summary>
    /// Una riga di testo: le parti tra dollari si traducono; una riga senza dollari si traduce tutta se è LaTeX
    /// (<see cref="LatexLayout.LooksLikeLatex"/>), altrimenti resta com'è.
    /// </summary>
    public static string Line(string line)
    {
        if (line.Contains('$'))
            return Dollars.Replace(line, m => Convert(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
        // 1{,}5: la virgola dei decimali come si scrive in LaTeX, anche in una riga senza comandi
        return LatexLayout.LooksLikeLatex(line) ? Convert(line) : line.Replace("{,}", ",");
    }

    /// <summary>Traduce una formula LaTeX (senza dollari).</summary>
    public static string Convert(string latex)
    {
        int i = 0;
        var sb = new StringBuilder();
        ReadUntil(latex, ref i, sb, '\0');
        return Regex.Replace(sb.ToString(), @"[ \t]+", " ").Trim();
    }

    static readonly Dictionary<string, string> Words = new()
    {
        ["cdot"] = "*", ["times"] = "*", ["ast"] = "*", ["div"] = "/", ["pi"] = "π", ["infty"] = "∞",
        ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥", ["neq"] = "≠",
        ["left"] = "", ["right"] = "", ["big"] = "", ["Big"] = "", ["bigg"] = "", ["Bigg"] = "",
        ["bigl"] = "", ["bigr"] = "", ["Bigl"] = "", ["Bigr"] = "", ["displaystyle"] = "", ["limits"] = "",
        ["{"] = "(", ["}"] = ")", ["lbrace"] = "(", ["rbrace"] = ")", ["vert"] = "|", ["|"] = "|", ["mid"] = "|",
        [","] = " ", [";"] = " ", [":"] = " ", ["!"] = "", [" "] = " ", ["quad"] = " ", ["qquad"] = " ",
        ["%"] = "%", ["e"] = "e",
    };

    static readonly Dictionary<string, string> Greek = new()
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε", ["varepsilon"] = "ε",
        ["theta"] = "θ", ["lambda"] = "λ", ["mu"] = "μ", ["omega"] = "ω", ["phi"] = "φ", ["varphi"] = "φ",
        ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["Delta"] = "Δ",
    };

    /// <summary>Copia in <paramref name="sb"/> fino a <paramref name="end"/> (o alla fine), traducendo i comandi.</summary>
    static void ReadUntil(string s, ref int i, StringBuilder sb, char end)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (c == end) { i++; return; }
            switch (c)
            {
                case '{':
                    i++;
                    // {,} è la virgola dei decimali all'italiana (1{,}5): resta virgola
                    if (i + 1 < s.Length && s[i] == ',' && s[i + 1] == '}') { sb.Append(','); i += 2; break; }
                    sb.Append('(');
                    ReadUntil(s, ref i, sb, '}');
                    sb.Append(')');
                    break;
                case '}':
                    i++; // graffa di troppo
                    break;
                case '^':
                    i++;
                    sb.Append("^(").Append(Arg(s, ref i)).Append(')');
                    break;
                case '_':
                    i++;
                    Arg(s, ref i); // pedici (x_0, f_1): il grafico non li conosce, si tolgono
                    break;
                case '\\':
                    Command(s, ref i, sb);
                    break;
                case '&':
                    i++;
                    break;
                default:
                    sb.Append(c);
                    i++;
                    break;
            }
        }
    }

    /// <summary>L'argomento di ^, _, \frac…: {…} o un solo carattere / comando, già tradotto.</summary>
    static string Arg(string s, ref int i)
    {
        while (i < s.Length && s[i] == ' ') i++;
        if (i >= s.Length) return "";
        var sb = new StringBuilder();
        if (s[i] == '{')
        {
            i++;
            ReadUntil(s, ref i, sb, '}');
        }
        else if (s[i] == '\\') Command(s, ref i, sb);
        else sb.Append(s[i++]);
        return sb.ToString().Trim();
    }

    static void Command(string s, ref int i, StringBuilder sb)
    {
        int start = ++i;
        if (i < s.Length && !char.IsAsciiLetter(s[i])) i++;
        else while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
        var name = s[start..i];
        switch (name)
        {
            case "frac" or "dfrac" or "tfrac" or "cfrac":
            {
                var num = Arg(s, ref i);
                var den = Arg(s, ref i);
                sb.Append("((").Append(num).Append(")/(").Append(den).Append("))");
                return;
            }
            case "sqrt":
            {
                string? index = null;
                if (i < s.Length && s[i] == '[')
                {
                    int close = s.IndexOf(']', i);
                    if (close > i) { index = Convert(s[(i + 1)..close]); i = close + 1; }
                }
                var arg = Arg(s, ref i);
                sb.Append(index == null ? $"sqrt({arg})" : $"({arg})^(1/({index}))");
                return;
            }
            case "text" or "mathrm" or "textrm" or "operatorname" or "mathit" or "mathbf":
                sb.Append(Arg(s, ref i));
                return;
            case "cases" or "begin" or "end":
                Arg(s, ref i);
                return;
        }
        if (name is "left" or "right" && i < s.Length && s[i] == '.')
        {
            i++; // \right. : nessuna parentesi
            return;
        }
        if (Words.TryGetValue(name, out var w))
        {
            sb.Append(w);
            return;
        }
        if (Greek.TryGetValue(name, out var g))
        {
            sb.Append(g);
            return;
        }
        // funzioni (\sin, \sen, \ln…), lettere greche, simboli: il nome senza la barra, staccato da ciò che segue
        sb.Append(' ').Append(name).Append(' ');
    }
}
