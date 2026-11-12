namespace Ardesia.Calculator;

/// <summary>Le scelte della calcolatrice, salvate nelle impostazioni di questo PC (AppSettings.Calculator).</summary>
public sealed class CalculatorSettings
{
    /// <summary>Notazione polacca inversa (RPN) invece dell'algebrica.</summary>
    public bool Rpn { get; set; }
    public CalcFormat Format { get; set; } = CalcFormat.Normal;
    /// <summary>Decimali per FIX e SCI (0–9).</summary>
    public int Digits { get; set; } = 4;
    public bool Degrees { get; set; } = true;
    /// <summary>Posizione e dimensione della finestra (null = accanto al bordo destro della finestra di Ardesia).</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}

/// <summary>Una riga del display: il testo e se è un errore (in rosso).</summary>
public readonly record struct CalcLine(string Text, bool Error = false, string Label = "");

/// <summary>Le due calcolatrici (algebrica e RPN) viste dalla finestra.</summary>
public interface ICalc
{
    /// <summary>Esegue un tasto (vedi <see cref="CalcKeys"/>).</summary>
    void Press(string key);
    /// <summary>Le righe del display dall'alto in basso; l'ultima è quella grande.</summary>
    IReadOnlyList<CalcLine> Display();
    /// <summary>Il risultato da mettere sul foglio (per l'algebrica con il calcolo: «2 + 3 = 5»), null se non c'è.</summary>
    string? TextForPage();
}

/// <summary>I nomi dei tasti, comuni alle due calcolatrici.</summary>
public static class CalcKeys
{
    public const string Dot = "dot", Eex = "eex", Chs = "chs", Back = "back", Clear = "clear", Enter = "enter";
    public const string Add = "add", Sub = "sub", Mul = "mul", Div = "div", Pow = "pow", Root = "root";
    public const string Sq = "sq", Cube = "cube", Sqrt = "sqrt", Cbrt = "cbrt", Inv = "inv", Abs = "abs";
    public const string Sin = "sin", Cos = "cos", Tan = "tan", Asin = "asin", Acos = "acos", Atan = "atan";
    public const string Ln = "ln", Log = "log", Exp = "exp", Pow10 = "pow10", Fact = "fact", Ncr = "ncr", Npr = "npr", Pct = "pct";
    public const string Pi = "pi", E = "e", LPar = "lpar", RPar = "rpar", Ans = "ans", LastX = "lastx";
    public const string Swap = "swap", RollDown = "rolldown", Rnd = "rnd";
}
