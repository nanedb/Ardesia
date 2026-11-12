using System.Globalization;
using static Ardesia.Calculator.CalcKeys;

namespace Ardesia.Calculator;

/// <summary>
/// Calcolatrice algebrica: si scrive l'espressione intera (con le precedenze, le parentesi e la moltiplicazione
/// sottintesa: 2π, 3(1+2)) e «=» la calcola. Dopo «=» un operatore continua dal risultato (Ans), una cifra ne
/// comincia una nuova, ⌫ riapre l'espressione per correggerla. Le parentesi lasciate aperte si chiudono da sole.
/// </summary>
public sealed class AlgebraicCalc(CalculatorSettings settings) : ICalc
{
    enum K { Num, Op, Func, LPar, RPar, Const, Post }

    readonly record struct Tok(K Kind, string Text);

    List<Tok> _toks = new();
    List<Tok> _last = new();
    bool _shown;
    string? _error;
    public double Ans { get; private set; }

    public void Press(string key)
    {
        if (key == Clear) { _toks.Clear(); _shown = false; _error = null; return; }
        if (_error != null)
        {
            // dopo un errore si torna a correggere l'espressione
            _error = null;
            _shown = false;
            _toks = new List<Tok>(_last);
            if (key == Back) return;
        }
        if (key == Enter) { Evaluate(); return; }
        if (key == Rnd)
        {
            if (!_shown && _toks.Count > 0) { Evaluate(); if (_error != null) return; }
            Ans = CalcFormatter.Round(Ans, settings.Format, settings.Digits);
            if (!_shown) { _last = new List<Tok> { new(K.Const, "Ans") }; _shown = true; }
            return;
        }
        if (_shown)
        {
            _shown = false;
            if (key == Back) { _toks = new List<Tok>(_last); return; }
            _toks.Clear();
            // un operatore continua dal risultato
            if (IsBinary(key) || IsPostfix(key)) _toks.Add(new Tok(K.Const, "Ans"));
            if (key == Chs) { _toks.Add(new Tok(K.Op, "−")); _toks.Add(new Tok(K.Const, "Ans")); return; }
        }

        switch (key)
        {
            case Back:
                if (_toks.Count == 0) return;
                var lastTok = _toks[^1];
                if (lastTok.Kind == K.Num && lastTok.Text.Length > 1) _toks[^1] = lastTok with { Text = lastTok.Text[..^1] };
                else _toks.RemoveAt(_toks.Count - 1);
                return;
            case Dot:
                if (LastIs(K.Num, out var n))
                {
                    if (!n.Contains(',') && !n.Contains('ᴇ')) _toks[^1] = new Tok(K.Num, n + ",");
                }
                else _toks.Add(new Tok(K.Num, "0,"));
                return;
            case Eex:
                if (LastIs(K.Num, out n)) { if (!n.Contains('ᴇ')) _toks[^1] = new Tok(K.Num, n + "ᴇ"); }
                else _toks.Add(new Tok(K.Num, "1ᴇ"));
                return;
            case Chs:
                ChangeSign();
                return;
            case LPar: _toks.Add(new Tok(K.LPar, "(")); return;
            case RPar: _toks.Add(new Tok(K.RPar, ")")); return;
            case Pi: _toks.Add(new Tok(K.Const, "π")); return;
            case CalcKeys.E: _toks.Add(new Tok(K.Const, "e")); return;
            case CalcKeys.Ans: _toks.Add(new Tok(K.Const, "Ans")); return;
        }
        if (key.Length == 1 && char.IsDigit(key[0]))
        {
            if (LastIs(K.Num, out var num)) { if (num.Length < 24) _toks[^1] = new Tok(K.Num, num + key); }
            else _toks.Add(new Tok(K.Num, key));
            return;
        }
        if (IsBinary(key))
        {
            var op = BinaryText(key);
            if (_toks.Count == 0)
            {
                if (key == Sub) { _toks.Add(new Tok(K.Op, "−")); return; }
                _toks.Add(new Tok(K.Const, "Ans"));
            }
            // due operatori di fila: il secondo sostituisce il primo (ma «×−» è un numero negativo)
            if (_toks[^1].Kind == K.Op && key != Sub) _toks[^1] = new Tok(K.Op, op);
            else _toks.Add(new Tok(K.Op, op));
            return;
        }
        if (IsPostfix(key))
        {
            if (_toks.Count == 0) _toks.Add(new Tok(K.Const, "Ans"));
            _toks.Add(new Tok(K.Post, key switch { Sq => "²", Cube => "³", Inv => "⁻¹", Fact => "!", _ => "%" }));
            return;
        }
        var func = key switch
        {
            Sin => "sin(", Cos => "cos(", Tan => "tan(", Asin => "sin⁻¹(", Acos => "cos⁻¹(", Atan => "tan⁻¹(",
            Ln => "ln(", Log => "log(", Sqrt => "√(", Cbrt => "∛(", Abs => "abs(", Exp => "e^(", Pow10 => "10^(",
            _ => null,
        };
        if (func != null)
        {
            // «2» seguito da «10^(» si leggerebbe 210^(…): il per si scrive
            if (key == Pow10 && LastIs(K.Num, out _)) _toks.Add(new Tok(K.Op, "×"));
            _toks.Add(new Tok(K.Func, func));
        }
    }

    void ChangeSign()
    {
        if (LastIs(K.Num, out var n))
        {
            int e = n.IndexOf('ᴇ');
            if (e >= 0)
            {
                // segno dell'esponente
                _toks[^1] = new Tok(K.Num, e + 1 < n.Length && n[e + 1] == '−' ? n.Remove(e + 1, 1) : n.Insert(e + 1, "−"));
                return;
            }
            int i = _toks.Count - 1;
            if (i > 0 && _toks[i - 1] is { Kind: K.Op, Text: "−" } && (i == 1 || _toks[i - 2].Kind is K.Op or K.LPar or K.Func))
                _toks.RemoveAt(i - 1); // era già negativo
            else
                _toks.Insert(i, new Tok(K.Op, "−"));
            return;
        }
        _toks.Add(new Tok(K.Op, "−"));
    }

    static bool IsBinary(string key) => key is Add or Sub or Mul or Div or Pow or Root or Ncr or Npr;
    static bool IsPostfix(string key) => key is Sq or Cube or Inv or Fact or Pct;

    static string BinaryText(string key) => key switch
    {
        Add => "+", Sub => "−", Mul => "×", Div => "÷", Pow => "^", Root => "ˣ√", Ncr => "C", _ => "P",
    };

    bool LastIs(K kind, out string text)
    {
        text = "";
        if (_toks.Count == 0 || _toks[^1].Kind != kind) return false;
        text = _toks[^1].Text;
        return true;
    }

    void Evaluate()
    {
        if (_toks.Count == 0) return;
        _last = new List<Tok>(_toks);
        try
        {
            Ans = CalcMath.Check(new Parser(_toks, settings.Degrees, Ans).Run());
            // le parentesi lasciate aperte, chiuse anche nell'espressione mostrata
            int open = _toks.Count(t => t.Kind is K.LPar or K.Func) - _toks.Count(t => t.Kind == K.RPar);
            for (int i = 0; i < open; i++) _last.Add(new Tok(K.RPar, ")"));
            _shown = true;
        }
        catch (CalcException ex) { _error = ex.Message; }
        catch (OverflowException) { _error = "Numero troppo grande"; }
    }

    public IReadOnlyList<CalcLine> Display()
    {
        if (_error != null) return new[] { new CalcLine(Text(_last)), new CalcLine(_error, Error: true) };
        if (_shown) return new[] { new CalcLine(Text(_last) + " ="), new CalcLine(Format(Ans)) };
        return new[] { new CalcLine(""), new CalcLine(_toks.Count == 0 ? "0" : Text(_toks)) };
    }

    public string? TextForPage()
    {
        if (!_shown) return null;
        var expr = Text(_last);
        var result = Format(Ans);
        return expr == "Ans" ? result : expr + " = " + result;
    }

    string Format(double v) => CalcFormatter.Format(v, settings.Format, settings.Digits);

    static string Text(List<Tok> toks) => string.Concat(toks.Select(t => t.Text));

    /// <summary>Lettore a discesa ricorsiva: + − ; × ÷ e per sottinteso ; segno ; C P ; ^ ˣ√ ; ² ³ ⁻¹ ! % ; numeri, costanti, funzioni, parentesi.</summary>
    sealed class Parser(List<Tok> toks, bool degrees, double ans)
    {
        int _i;

        public double Run()
        {
            var v = Expr();
            if (_i < toks.Count)
                throw new CalcException(toks[_i].Kind == K.RPar ? "C'è una parentesi chiusa in più" : "Espressione non valida");
            return v;
        }

        Tok? Peek => _i < toks.Count ? toks[_i] : null;

        bool IsOp(string op) => Peek is { Kind: K.Op } t && t.Text == op;

        double Expr()
        {
            var v = Term();
            while (true)
            {
                if (IsOp("+")) { _i++; v += Term(); }
                else if (IsOp("−")) { _i++; v -= Term(); }
                else return v;
            }
        }

        double Term()
        {
            var v = Unary();
            while (true)
            {
                if (IsOp("×")) { _i++; v *= Unary(); }
                else if (IsOp("÷")) { _i++; v = CalcMath.Divide(v, Unary()); }
                else if (Peek is { Kind: K.Num or K.Const or K.Func or K.LPar }) v *= Combin(); // 2π, 3(1+2)
                else return v;
            }
        }

        double Unary()
        {
            if (IsOp("−")) { _i++; return -Unary(); }
            if (IsOp("+")) { _i++; return Unary(); }
            return Combin();
        }

        double Combin()
        {
            var v = Power();
            while (true)
            {
                if (IsOp("C")) { _i++; v = CalcMath.Combinations(v, Power()); }
                else if (IsOp("P")) { _i++; v = CalcMath.Permutations(v, Power()); }
                else return v;
            }
        }

        double Power()
        {
            var b = Postfix();
            if (IsOp("^")) { _i++; return CalcMath.Pow(b, Unary()); }
            if (IsOp("ˣ√")) { _i++; return CalcMath.Root(b, Unary()); }
            return b;
        }

        double Postfix()
        {
            var v = Primary();
            while (Peek is { Kind: K.Post } p)
            {
                _i++;
                v = p.Text switch
                {
                    "²" => v * v,
                    "³" => v * v * v,
                    "⁻¹" => CalcMath.Reciprocal(v),
                    "!" => CalcMath.Factorial(v),
                    _ => v / 100,
                };
            }
            return v;
        }

        double Primary()
        {
            if (Peek is not { } t) throw new CalcException("Espressione incompleta");
            _i++;
            switch (t.Kind)
            {
                case K.Num: return Number(t.Text);
                case K.Const: return t.Text switch { "π" => Math.PI, "e" => Math.E, _ => ans };
                case K.LPar:
                {
                    var v = Expr();
                    Close();
                    return v;
                }
                case K.Func:
                {
                    var a = Expr();
                    Close();
                    return t.Text switch
                    {
                        "sin(" => CalcMath.Sin(a, degrees),
                        "cos(" => CalcMath.Cos(a, degrees),
                        "tan(" => CalcMath.Tan(a, degrees),
                        "sin⁻¹(" => CalcMath.Asin(a, degrees),
                        "cos⁻¹(" => CalcMath.Acos(a, degrees),
                        "tan⁻¹(" => CalcMath.Atan(a, degrees),
                        "ln(" => CalcMath.Ln(a),
                        "log(" => CalcMath.Log10(a),
                        "√(" => CalcMath.Sqrt(a),
                        "∛(" => CalcMath.Cbrt(a),
                        "abs(" => Math.Abs(a),
                        "e^(" => Math.Exp(a),
                        _ => Math.Pow(10, a),
                    };
                }
                case K.RPar: throw new CalcException("Manca qualcosa prima della parentesi chiusa");
                default: throw new CalcException("Manca un numero dopo «" + t.Text + "»");
            }
        }

        /// <summary>Parentesi chiusa, o fine dell'espressione (le parentesi aperte si chiudono da sole).</summary>
        void Close()
        {
            if (Peek is { Kind: K.RPar }) _i++;
            else if (Peek != null) throw new CalcException("Manca una parentesi chiusa");
        }

        static double Number(string s)
        {
            if (s.EndsWith('ᴇ') || s.EndsWith("ᴇ−")) throw new CalcException("Manca l'esponente dopo ᴇ");
            var t = s.Replace(',', '.').Replace("ᴇ", "E").Replace('−', '-');
            if (t.EndsWith('.')) t += "0";
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new CalcException("Numero non valido: " + s);
            return v;
        }
    }
}
