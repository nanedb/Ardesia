using System.Globalization;
using static Ardesia.Calculator.CalcKeys;

namespace Ardesia.Calculator;

/// <summary>
/// Calcolatrice RPN come le HP classiche: pila di quattro registri X, Y, Z, T. Invio copia X in Y; le operazioni
/// a due numeri usano Y e X e fanno scendere la pila (T resta). Dopo Invio o ⌫ il numero che si scrive prende il
/// posto di X, dopo un'operazione invece spinge su il risultato.
/// </summary>
public sealed class RpnCalc(CalculatorSettings settings) : ICalc
{
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Z { get; private set; }
    public double T { get; private set; }
    public double LastX { get; private set; }

    /// <summary>Il prossimo numero spinge su la pila (false dopo Invio e dopo ⌫).</summary>
    bool _lift = true;
    /// <summary>Il numero che si sta scrivendo (null se non se ne sta scrivendo uno).</summary>
    string? _entry;
    string? _error;

    public void Press(string key)
    {
        if (_error != null)
        {
            _error = null;
            if (key == Back) return;
        }
        try { Do(key); }
        catch (CalcException ex) { _error = ex.Message; }
    }

    void Do(string key)
    {
        if (key.Length == 1 && char.IsDigit(key[0]))
        {
            StartEntry();
            if (_entry!.Count(char.IsDigit) < 15) SetEntry(_entry + key);
            return;
        }
        switch (key)
        {
            case Dot:
                StartEntry();
                if (!_entry!.Contains(',') && !_entry.Contains('ᴇ')) SetEntry((_entry is "" or "−" ? _entry + "0" : _entry) + ",");
                return;
            case Eex:
                StartEntry();
                if (!_entry!.Contains('ᴇ')) SetEntry((_entry is "" or "−" ? _entry + "1" : _entry) + "ᴇ");
                return;
            case Chs:
                if (_entry != null)
                {
                    int e = _entry.IndexOf('ᴇ');
                    if (e >= 0) SetEntry(e + 1 < _entry.Length && _entry[e + 1] == '−' ? _entry.Remove(e + 1, 1) : _entry.Insert(e + 1, "−"));
                    else SetEntry(_entry.StartsWith('−') ? _entry[1..] : "−" + _entry);
                }
                else X = -X;
                return;
            case Back:
                if (_entry != null && _entry.Length > 1 && _entry != "−")
                {
                    SetEntry(_entry[..^1]);
                    if (_entry is "−") { _entry = null; X = 0; _lift = false; }
                    return;
                }
                _entry = null;
                X = 0;
                _lift = false;
                return;
            case Clear:
                _entry = null;
                X = Y = Z = T = 0;
                _lift = false;
                return;
            case Enter:
                Finish();
                T = Z; Z = Y; Y = X;
                _lift = false;
                return;
            case Swap:
                Finish();
                (X, Y) = (Y, X);
                _lift = true;
                return;
            case RollDown:
                Finish();
                (X, Y, Z, T) = (Y, Z, T, X);
                _lift = true;
                return;
            case Pi: Finish(); Push(Math.PI); return;
            case CalcKeys.E: Finish(); Push(Math.E); return;
            case CalcKeys.LastX: case CalcKeys.Ans: Finish(); Push(LastX); return;
            case Rnd: Finish(); X = CalcFormatter.Round(X, settings.Format, settings.Digits); _lift = true; return;
            case Pct:
                // percentuale di Y: Y resta, X diventa Y·X/100
                Finish();
                Unary(x => Y * x / 100);
                return;
            case LPar: case RPar:
                return; // le parentesi in RPN non servono
        }

        Func<double, double, double>? binary = key switch
        {
            Add => (y, x) => y + x,
            Sub => (y, x) => y - x,
            Mul => (y, x) => y * x,
            Div => CalcMath.Divide,
            Pow => CalcMath.Pow,
            Root => (y, x) => CalcMath.Root(x, y), // ˣ√y: indice in X
            Ncr => CalcMath.Combinations,
            Npr => CalcMath.Permutations,
            _ => null,
        };
        if (binary != null)
        {
            Finish();
            var r = CalcMath.Check(binary(Y, X));
            LastX = X;
            X = r;
            Y = Z;
            Z = T;
            _lift = true;
            return;
        }

        bool deg = settings.Degrees;
        Func<double, double>? unary = key switch
        {
            Sq => x => x * x,
            Cube => x => x * x * x,
            Sqrt => CalcMath.Sqrt,
            Cbrt => CalcMath.Cbrt,
            Inv => CalcMath.Reciprocal,
            Abs => Math.Abs,
            Sin => x => CalcMath.Sin(x, deg),
            Cos => x => CalcMath.Cos(x, deg),
            Tan => x => CalcMath.Tan(x, deg),
            Asin => x => CalcMath.Asin(x, deg),
            Acos => x => CalcMath.Acos(x, deg),
            Atan => x => CalcMath.Atan(x, deg),
            Ln => CalcMath.Ln,
            Log => CalcMath.Log10,
            Exp => Math.Exp,
            Pow10 => x => Math.Pow(10, x),
            Fact => CalcMath.Factorial,
            _ => null,
        };
        if (unary != null)
        {
            Finish();
            Unary(unary);
        }
    }

    void Unary(Func<double, double> f)
    {
        var r = CalcMath.Check(f(X));
        LastX = X;
        X = r;
        _lift = true;
    }

    void Push(double v)
    {
        if (_lift) { T = Z; Z = Y; Y = X; }
        X = v;
        _lift = true;
    }

    void StartEntry()
    {
        if (_entry != null) return;
        if (_lift) { T = Z; Z = Y; Y = X; }
        _entry = "";
        X = 0;
    }

    void SetEntry(string s)
    {
        _entry = s;
        X = Parse(s);
    }

    /// <summary>Chiude il numero che si sta scrivendo: da qui in poi è X.</summary>
    void Finish()
    {
        if (_entry == null) return;
        X = Parse(_entry);
        _entry = null;
    }

    static double Parse(string s)
    {
        var t = s.Replace(',', '.').Replace('−', '-');
        if (t.EndsWith("ᴇ") || t.EndsWith("ᴇ-")) t = t[..t.IndexOf('ᴇ')]; // esponente non ancora scritto
        t = t.Replace("ᴇ", "E");
        if (t is "" or "-") return 0;
        if (t.EndsWith('.')) t += "0";
        if (t.StartsWith('.') || t.StartsWith("-.")) t = t.Replace(".", "0.");
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsInfinity(v) ? v : 0;
    }

    public IReadOnlyList<CalcLine> Display()
    {
        string F(double v) => CalcFormatter.Format(v, settings.Format, settings.Digits);
        var x = _error != null ? new CalcLine(_error, Error: true, Label: "x")
            : _entry != null ? new CalcLine(EntryText(_entry), Label: "x")
            : new CalcLine(F(X), Label: "x");
        return new[] { new CalcLine(F(T), Label: "t"), new CalcLine(F(Z), Label: "z"), new CalcLine(F(Y), Label: "y"), x };
    }

    /// <summary>Il numero che si sta scrivendo, con l'esponente in alto: «1,5ᴇ−3» → «1,5 · 10⁻³».</summary>
    static string EntryText(string e)
    {
        if (e == "") return "0";
        int i = e.IndexOf('ᴇ');
        if (i < 0) return e;
        var exp = e[(i + 1)..];
        var sup = string.Concat(exp.Select(c => c == '−' ? '⁻' : "⁰¹²³⁴⁵⁶⁷⁸⁹"[c - '0']));
        return e[..i] + " · 10" + (sup is "" or "⁻" ? sup + "□" : sup);
    }

    public string? TextForPage() => _error != null ? null : CalcFormatter.Format(_entry != null ? Parse(_entry) : X, settings.Format, settings.Digits);
}
