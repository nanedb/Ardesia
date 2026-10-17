using System.Globalization;

namespace Ardesia.Editor.Graph;

/// <summary>
/// Traduce le funzioni scritte dal docente in comandi GeoGebra, partendo dall'albero di <see cref="MathParser"/>:
/// GeoGebra riceve prima un cursore per ogni parametro (senza, «f(x) = 1.5x + a» non si disegna), poi le funzioni
/// con nomi inglesi (sen → sin) e moltiplicazioni esplicite, che capisce in qualunque lingua.
/// </summary>
public static class GeoGebraExport
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Nomi delle funzioni per GeoGebra (log di Ardesia è in base 10: in GeoGebra è lg).</summary>
    static readonly Dictionary<string, string> Names = new()
    {
        ["sen"] = "sin", ["tg"] = "tan", ["cotg"] = "cot",
        ["asin"] = "asin", ["arcsin"] = "asin", ["arcsen"] = "asin",
        ["acos"] = "acos", ["arccos"] = "acos",
        ["atan"] = "atan", ["arctan"] = "atan", ["arctg"] = "atan",
        ["√"] = "sqrt", ["log"] = "lg", ["sign"] = "sgn",
    };

    /// <param name="lines">Le funzioni, una per riga, come le ha scritte il docente.</param>
    /// <param name="parameters">Valori e limiti dei parametri già scelti (dal grafico di Ardesia), o null: valgono 1.</param>
    public static IReadOnlyList<string> Commands(IEnumerable<string> lines, IReadOnlyList<GraphParameter>? parameters = null)
    {
        var functions = new List<string>();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = MathText.Normalize(raw);
            if (line.Length == 0) continue;
            try
            {
                var c = MathParser.ParseLine(line);
                foreach (var p in c.Parameters) names.Add(p);
                var expr = ToText(c.Expr);
                functions.Add(c.Kind == CurveKind.Vertical ? "x = " + expr
                    : c.Name != null ? $"{c.Name}(x) = {expr}"
                    : expr);
            }
            catch (MathSyntaxException)
            {
                // GeoGebra forse la capisce lo stesso (o mostra il suo errore): meglio che perderla
                functions.Add(line);
            }
        }

        var commands = new List<string>();
        foreach (var n in names)
        {
            var p = parameters?.FirstOrDefault(q => q.Name == n) ?? new GraphParameter { Name = n };
            double min = Math.Min(p.Min, p.Value), max = Math.Max(p.Max, p.Value);
            commands.Add($"{n} = Slider({Num(min)}, {Num(max)}, {Num(p.Step > 0 ? p.Step : 0.1)})");
            commands.Add($"SetValue({n}, {Num(p.Value)})");
        }
        commands.AddRange(functions);
        return commands;
    }

    /// <summary>L'espressione in sintassi GeoGebra, con le sole parentesi necessarie.</summary>
    public static string ToText(MathNode node) => Write(node, 0);

    // precedenze: 1 = + −, 2 = · /, 3 = meno unario, 4 = potenza, 5 = atomo
    static string Write(MathNode n, int outer)
    {
        var (text, prec) = n switch
        {
            NumNode num => (Num(num.Value), num.Value < 0 ? 3 : 5),
            ConstNode c => (c.Symbol == "π" ? "pi" : "e", 5),
            VarNode v => (v.Name, 5),
            NegNode u => ("-" + Write(u.Arg, 3), 3),
            CallNode call => ($"{(Names.TryGetValue(call.Function, out var g) ? g : call.Function)}({Write(call.Arg, 0)})", 5),
            BinNode { Op: '+' or '-' } b => ($"{Write(b.Left, 1)} {b.Op} {Write(b.Right, 2)}", 1),
            BinNode { Op: '*' } b => ($"{Write(b.Left, 2)} * {Write(b.Right, 3)}", 2),
            BinNode { Op: '/' } b => ($"{Write(b.Left, 2)} / {Write(b.Right, 3)}", 2),
            // a destra di ^ sempre tra parentesi: 2^(3^2), x^(-1)
            BinNode { Op: '^' } b => ($"{Write(b.Left, 5)}^({Write(b.Right, 0)})", 4),
            _ => throw new InvalidOperationException(),
        };
        return prec < outer ? "(" + text + ")" : text;
    }

    static string Num(double v) => v.ToString("0.############", Inv);
}
