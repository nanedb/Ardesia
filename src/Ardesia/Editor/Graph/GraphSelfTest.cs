using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ardesia.Editor.Graph;

/// <summary>Prova automatica di parser e calcoli (Ardesia.exe --prova-grafico [registro.txt]).</summary>
public static class GraphSelfTest
{
    /// <summary>Esegue le prove e restituisce il registro; <paramref name="failures"/> conta quelle andate male.</summary>
    public static string Run(out int failures)
    {
        var log = new StringBuilder();
        int fail = 0;
        var inv = CultureInfo.InvariantCulture;

        void Check(bool ok, string what)
        {
            log.AppendLine((ok ? "ok    " : "ERRORE") + "  " + what);
            if (!ok) fail++;
        }

        // ── valori ──
        (string Text, double X, double Expected)[] values =
        {
            ("2x+1", 3, 7),
            ("y = -x^2", 3, -9),
            ("-x²", 2, -4),
            ("x² − 4", 3, 5),
            ("f(x) = 1,5x", 2, 3),
            ("2πx", 1, 2 * Math.PI),
            ("sen x", Math.PI / 2, 1),
            ("sen 2x", Math.PI / 4, 1),
            ("sin(x)^2 + cos(x)^2", 0.7, 1),
            ("sen² x", Math.PI / 2, 1),
            ("|x − 1|", -2, 3),
            ("2|x|+|x-1|", -1, 4),
            ("√x", 9, 3),
            ("√(x+7)", 9, 4),
            ("sqrt x", 16, 4),
            ("(x+1)(x-1)", 3, 8),
            ("3(x+1)", 1, 6),
            ("x^(1/3)", -8, -2),
            ("2^3^2", 0, 512),
            ("1/x", 4, 0.25),
            ("e^x", 1, Math.E),
            ("ln x", Math.E, 1),
            ("log(x)", 1000, 3),
            ("x·sen x", Math.PI / 2, Math.PI / 2),
            ("x : 2", 5, 2.5),
            ("tg x", Math.PI / 4, 1),
            ("y = 3", 10, 3),
            ("X^2", 3, 9),
            ("2^-1", 0, 0.5),
            ("arcsen x", 1, Math.PI / 2),
            ("senxcosx", Math.PI / 4, 0.5),
        };
        foreach (var (text, x, expected) in values)
        {
            try
            {
                var c = MathParser.ParseLine(MathText.Normalize(text));
                double y = c.Compile(c.Parameters)(x, c.Parameters.Select(_ => 1.0).ToArray());
                Check(Math.Abs(y - expected) < 1e-9, $"«{text}» in x = {x.ToString(inv)}: {y.ToString(inv)} (atteso {expected.ToString(inv)})");
            }
            catch (MathSyntaxException ex)
            {
                Check(false, $"«{text}»: {ex.Message}");
            }
        }

        // ── parametri e tipi ──
        try
        {
            var c = MathParser.ParseLine(MathText.Normalize("y = a·x² + bx + c"));
            Check(string.Join(",", c.Parameters) == "a,b,c", "parametri di «y = a·x² + bx + c»: " + string.Join(",", c.Parameters));
            double y = c.Compile(c.Parameters)(2, new[] { 1.0, -3, 2 });
            Check(Math.Abs(y) < 1e-12, $"a=1, b=−3, c=2 in x=2: {y.ToString(inv)}");
            var v = MathParser.ParseLine("x = 2");
            Check(v.Kind == CurveKind.Vertical, "«x = 2» è una retta verticale");
            var n = MathParser.ParseLine("g(x) = x");
            Check(n.Name == "g", "nome di «g(x) = x»: " + n.Name);
        }
        catch (MathSyntaxException ex)
        {
            Check(false, "parametri: " + ex.Message);
        }

        // ── errori ──
        foreach (var bad in new[] { "(x+1", "x+1)", "2*", "sinn(x)", "senax", "y = x = 2", "x = x+1", "#x", "y = y + 1", "sen" })
        {
            try
            {
                MathParser.ParseLine(MathText.Normalize(bad));
                Check(false, $"«{bad}» doveva dare errore");
            }
            catch (MathSyntaxException ex)
            {
                Check(true, $"«{bad}» → {ex.Message}");
            }
        }

        // ── zeri, estremi, intersezioni ──
        Func<double, double> F(string text)
        {
            var c = MathParser.ParseLine(MathText.Normalize(text));
            var compiled = c.Compile(c.Parameters);
            var args = c.Parameters.Select(_ => 1.0).ToArray();
            return x => compiled(x, args);
        }
        void Zeros(string text, params double[] expected)
        {
            var z = GraphMath.Zeros(F(text), -10, 10);
            bool ok = z.Count == expected.Length && z.Zip(expected).All(p => Math.Abs(p.First - p.Second) < 1e-6);
            Check(ok, $"zeri di «{text}»: {string.Join("; ", z.Select(v => v.ToString("0.######", inv)))}");
        }
        Zeros("x² − 4", -2, 2);
        Zeros("x²", 0);
        Zeros("(x-1)^2", 1);
        Zeros("1/x");
        Zeros("x³ − x", -1, 0, 1);
        Zeros("sen x", -3 * Math.PI, -2 * Math.PI, -Math.PI, 0, Math.PI, 2 * Math.PI, 3 * Math.PI);
        Zeros("tg x", -3 * Math.PI, -2 * Math.PI, -Math.PI, 0, Math.PI, 2 * Math.PI, 3 * Math.PI);
        Zeros("√x", 0);
        Zeros("x² + 1");

        void Extrema(string text, params (double X, bool Max)[] expected)
        {
            var e = GraphMath.Extrema(F(text), -10, 10, 20);
            bool ok = e.Count == expected.Length && e.Zip(expected).All(p => Math.Abs(p.First.X - p.Second.X) < 1e-5 && p.First.IsMax == p.Second.Max);
            Check(ok, $"estremi di «{text}»: {string.Join("; ", e.Select(v => (v.IsMax ? "max " : "min ") + v.X.ToString("0.######", inv)))}");
        }
        Extrema("x² − 2x", (1, false));
        Extrema("−x² + 4", (0, true));
        Extrema("x³ − 3x", (-1, true), (1, false));
        Extrema("|x|", (0, false));
        Extrema("1/x");
        Extrema("tg x");

        var inter = GraphMath.Zeros(x => F("x²")(x) - F("x + 2")(x), -10, 10);
        Check(inter.Count == 2 && Math.Abs(inter[0] + 1) < 1e-6 && Math.Abs(inter[1] - 2) < 1e-6,
            "intersezioni di x² e x + 2: " + string.Join("; ", inter.Select(v => v.ToString("0.######", inv))));

        var d = GraphMath.Derivative(F("x³"));
        Check(Math.Abs(d(2) - 12) < 1e-5, "derivata di x³ in 2: " + d(2).ToString(inv));

        // ── campionamento: tan x si spezza agli asintoti, √x arriva fino a 0 ──
        var tan = GraphMath.Sample(Math.Tan, -5, 5, -7, 7, 800);
        Check(tan.Count == 5, $"tan x tra −5 e 5: {tan.Count} tratti (attesi 5)");
        var rec = GraphMath.Sample(x => 1 / x, -5, 5, -7, 7, 800);
        Check(rec.Count == 2, $"1/x: {rec.Count} tratti (attesi 2)");
        var sq = GraphMath.Sample(Math.Sqrt, -5, 5, -7, 7, 800);
        Check(sq.Count == 1 && Math.Abs(sq[0][0].X) < 1e-6, $"√x: {sq.Count} tratto, comincia in {sq.FirstOrDefault()?.FirstOrDefault().X.ToString(inv)}");
        var steep = GraphMath.Sample(x => Math.Atan(x * 1000), -5, 5, -2, 2, 800);
        Check(steep.Count == 1, $"arctan(1000x) (ripida ma continua): {steep.Count} tratti (atteso 1)");

        // ── formati ──
        Check(GraphMath.Format(1.5) == "1,5", "1.5 → " + GraphMath.Format(1.5));
        Check(GraphMath.Format(-2) == "−2", "−2 → " + GraphMath.Format(-2));
        Check(GraphMath.FormatPi(Math.PI / 2) == "π/2", "π/2 → " + GraphMath.FormatPi(Math.PI / 2));
        Check(GraphMath.FormatPi(-3 * Math.PI / 4) == "−3π/4", "−3π/4 → " + GraphMath.FormatPi(-3 * Math.PI / 4));
        Check(GraphMath.FormatPi(2 * Math.PI) == "2π", "2π → " + GraphMath.FormatPi(2 * Math.PI));
        Check(GraphMath.FormatPoint(1.5, -2, false) == "(1,5; −2)", "punto → " + GraphMath.FormatPoint(1.5, -2, false));
        Check(GraphMath.NiceStep(20, 600) == 2, "passo per 20 unità su 600 px: " + GraphMath.NiceStep(20, 600));

        // ── stato: funzioni nascoste e colori ──
        var st = new GraphState();
        st.SetFunctions(new[] { "y = x²", "y = a·x + b", "x = 3" });
        string red = st.Functions[1].Color;
        // la seconda si corregge e si nasconde: tiene il suo colore; la terza è nuova
        st.SetFunctions(new[]
        {
            new GraphFunctionData { Text = "y = x²", Color = st.Functions[0].Color },
            new GraphFunctionData { Text = "y = a·x + 2b", Color = red, Hidden = true },
            new GraphFunctionData { Text = "y = 1/x", Color = "" },
        });
        Check(st.Functions[1].Color == red && st.Functions[1].Hidden, "funzione corretta e nascosta: stesso colore, nascosta");
        Check(st.Functions.Select(f => f.Color).Distinct().Count() == 3, "colori diversi: " + string.Join(" ", st.Functions.Select(f => f.Color)));
        var back = GraphState.FromJson(st.ToJson());
        Check(back.Functions[1].Hidden && !back.Functions[0].Hidden, "«nascosta» si salva e si rilegge");
        Check(!st.ToJson().Contains("\"hidden\":false"), "le funzioni visibili non scrivono «hidden»");
        Check(st.FunctionsText == "y = x²" + Environment.NewLine + "y = 1/x", "a GeoGebra vanno solo le visibili");
        _ = new FunctionGraph { State = back }; // Refresh aggiorna i parametri
        Check(back.Params.Select(p => p.Name).SequenceEqual(new[] { "a", "b" }), "i parametri delle funzioni nascoste restano");

        // ── comandi per GeoGebra ──
        foreach (var (text, expected) in new[]
        {
            ("f(x) = 1,5x + a", "f(x) = 1.5 * x + a"),
            ("y = sen 2x", "sin(2 * x)"),
            ("-x²", "-x^(2)"),
            ("(x+1)(x-1)", "(x + 1) * (x - 1)"),
            ("2^3^2", "2^(3^(2))"),
            ("x - (x - 1)", "x - (x - 1)"),
            ("1/(2x)", "1 / (2 * x)"),
            ("log x + 2π", "lg(x) + 2 * pi"),
            ("|x − 1|", "abs(x - 1)"),
            ("x = 3", "x = 3"),
            ("(-x)^2", "(-x)^(2)"),
        })
        {
            var cmd = GeoGebraExport.Commands(new[] { text }).Last();
            Check(cmd == expected, $"GeoGebra «{text}» → «{cmd}» (atteso «{expected}»)");
        }
        var ggb = GeoGebraExport.Commands(new[] { "y = a·x² + b" }, new List<GraphParameter> { new() { Name = "a", Value = 2.5 } });
        Check(ggb.Count == 5 && ggb[0] == "a = Slider(-5, 5, 0.1)" && ggb[1] == "SetValue(a, 2.5)" && ggb[2].StartsWith("b = Slider") && ggb[4] == "a * x^(2) + b",
            "GeoGebra: prima i cursori, poi la funzione: " + string.Join(" | ", ggb));

        log.AppendLine();
        log.AppendLine(fail == 0 ? "Tutte le prove sono andate bene." : $"{fail} prove non sono andate bene.");
        failures = fail;
        return log.ToString();
    }

    /// <summary>Un grafico con formule difficili da comporre (frazioni, radici, esponenti, valori assoluti), salvato in PNG.</summary>
    public static void RenderLegendSample(string png)
    {
        var state = new GraphState();
        state.SetFunctions(new[]
        {
            "y = x² − 4", "f(x) = 1,5x + a", "g(x) = (x+1)/(x−1)", "y = √(x+4) − 1/x", "y = 2^-x", "y = |x − 1| + sen² x",
            "h(x) = a·x³ − bx", "y = -(x-1)^2/(2x)", "y = e^(x/2) - ln(x+1)", "y = 3(x+1)(x−2)", "x = 3", "y = sen(2x + π) + cos x",
        });
        var graph = ElementFactory.CreateFunctionGraph(state, 900, 700);
        graph.ResetView();
        graph.Measure(new Size(900, 700));
        graph.Arrange(new Rect(0, 0, 900, 700));
        var bmp = new RenderTargetBitmap(1800, 1400, 192, 192, PixelFormats.Pbgra32);
        bmp.Render(graph);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(png);
        enc.Save(fs);
    }
}
