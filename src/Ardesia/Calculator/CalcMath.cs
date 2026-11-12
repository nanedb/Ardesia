namespace Ardesia.Calculator;

/// <summary>Errore di calcolo con un messaggio in italiano da mostrare sul display.</summary>
public sealed class CalcException(string message) : Exception(message);

/// <summary>Le funzioni della calcolatrice, in gradi o in radianti.</summary>
public static class CalcMath
{
    public static double Sin(double a, bool degrees)
    {
        if (degrees && TryQuarter(a, out int q)) return q switch { 0 => 0, 1 => 1, 2 => 0, _ => -1 };
        return Clean(Math.Sin(degrees ? Rad(a) : a));
    }

    public static double Cos(double a, bool degrees)
    {
        if (degrees && TryQuarter(a, out int q)) return q switch { 0 => 1, 1 => 0, 2 => -1, _ => 0 };
        return Clean(Math.Cos(degrees ? Rad(a) : a));
    }

    public static double Tan(double a, bool degrees)
    {
        if (degrees && TryQuarter(a, out int q))
        {
            if (q % 2 == 1) throw new CalcException("La tangente di " + (q == 1 ? "90°" : "270°") + " non esiste");
            return 0;
        }
        if (!degrees && Math.Abs(Math.Cos(a)) < 1e-15) throw new CalcException("La tangente di π/2 non esiste");
        return Clean(Math.Tan(degrees ? Rad(a) : a));
    }

    public static double Asin(double v, bool degrees)
    {
        if (v < -1 || v > 1) throw new CalcException("L'arcoseno vuole un numero fra −1 e 1");
        return Angle(Math.Asin(v), degrees);
    }

    public static double Acos(double v, bool degrees)
    {
        if (v < -1 || v > 1) throw new CalcException("L'arcocoseno vuole un numero fra −1 e 1");
        return Angle(Math.Acos(v), degrees);
    }

    public static double Atan(double v, bool degrees) => Angle(Math.Atan(v), degrees);

    public static double Ln(double v)
    {
        if (v <= 0) throw new CalcException("Il logaritmo vuole un numero maggiore di 0");
        return Math.Log(v);
    }

    public static double Log10(double v)
    {
        if (v <= 0) throw new CalcException("Il logaritmo vuole un numero maggiore di 0");
        return Math.Log10(v);
    }

    public static double Sqrt(double v)
    {
        if (v < 0) throw new CalcException("La radice quadrata di un numero negativo non esiste nei reali");
        return Math.Sqrt(v);
    }

    public static double Cbrt(double v) => Math.Cbrt(v);

    public static double Reciprocal(double v)
    {
        if (v == 0) throw new CalcException("Non si può dividere per zero");
        return 1 / v;
    }

    public static double Divide(double a, double b)
    {
        if (b == 0) throw new CalcException("Non si può dividere per zero");
        return a / b;
    }

    /// <summary>Potenza, con le radici dispari dei negativi (−8^(1/3) = −2) che Math.Pow non fa.</summary>
    public static double Pow(double b, double e)
    {
        if (b == 0 && e < 0) throw new CalcException("Zero elevato a un numero negativo non esiste");
        if (b == 0 && e == 0) throw new CalcException("0⁰ non è definito");
        if (b < 0 && e != Math.Floor(e))
        {
            // esponente frazionario: va bene se è 1/dispari (radice dispari)
            double inv = 1 / e;
            double n = Math.Round(inv);
            if (Math.Abs(inv - n) < 1e-9 && Math.Abs(n) % 2 == 1) return -Math.Pow(-b, e);
            throw new CalcException("Potenza di un numero negativo con esponente non intero");
        }
        return Math.Pow(b, e);
    }

    /// <summary>Radice n-esima di x (anche dispari di un negativo).</summary>
    public static double Root(double n, double x)
    {
        if (n == 0) throw new CalcException("La radice di indice 0 non esiste");
        if (x < 0)
        {
            if (n == Math.Floor(n) && Math.Abs(n) % 2 == 1) return -Math.Pow(-x, 1 / n);
            throw new CalcException("La radice di indice pari di un numero negativo non esiste nei reali");
        }
        return Math.Pow(x, 1 / n);
    }

    public static double Factorial(double v)
    {
        if (v < 0 || v != Math.Floor(v)) throw new CalcException("Il fattoriale vuole un numero intero ≥ 0");
        if (v > 170) throw new CalcException("Numero troppo grande");
        double r = 1;
        for (int i = 2; i <= (int)v; i++) r *= i;
        return r;
    }

    /// <summary>Combinazioni (n su k).</summary>
    public static double Combinations(double n, double k)
    {
        CheckNK(n, k);
        k = Math.Min(k, n - k);
        double r = 1;
        for (int i = 1; i <= (int)k; i++) r = r * (n - k + i) / i;
        return Math.Round(r);
    }

    /// <summary>Disposizioni semplici di n elementi a k a k.</summary>
    public static double Permutations(double n, double k)
    {
        CheckNK(n, k);
        double r = 1;
        for (int i = 0; i < (int)k; i++) r *= n - i;
        return r;
    }

    static void CheckNK(double n, double k)
    {
        if (n < 0 || k < 0 || n != Math.Floor(n) || k != Math.Floor(k))
            throw new CalcException("Combinazioni e disposizioni vogliono numeri interi ≥ 0");
        if (k > n) throw new CalcException("k non può essere maggiore di n");
    }

    /// <summary>Il risultato, o un errore se non è un numero vero.</summary>
    public static double Check(double v)
    {
        if (double.IsNaN(v)) throw new CalcException("Risultato non definito");
        if (double.IsInfinity(v)) throw new CalcException("Numero troppo grande");
        return v;
    }

    static double Rad(double deg) => deg * Math.PI / 180;

    static double Angle(double rad, bool degrees) => degrees ? rad * 180 / Math.PI : rad;

    /// <summary>Angolo in gradi multiplo esatto di 90°: quale quarto di giro (0–3).</summary>
    static bool TryQuarter(double deg, out int quarter)
    {
        quarter = 0;
        if (Math.Abs(deg) > 1e15) return false;
        double q = deg / 90;
        if (q != Math.Floor(q)) return false;
        quarter = (int)(((q % 4) + 4) % 4);
        return true;
    }

    /// <summary>Toglie i residui come sen(π) = 1,2·10⁻¹⁶.</summary>
    static double Clean(double v) => Math.Abs(v) < 1e-15 ? 0 : v;
}
