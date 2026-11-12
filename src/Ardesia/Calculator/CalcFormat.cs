using System.Globalization;
using System.Text;

namespace Ardesia.Calculator;

/// <summary>Come la calcolatrice mostra i numeri.</summary>
public enum CalcFormat
{
    /// <summary>Tutte le cifre che servono (fino a 12 significative), in notazione scientifica solo se il numero è enorme o minuscolo.</summary>
    Normal,
    /// <summary>Numero fisso di decimali (FIX), arrotondato.</summary>
    Fix,
    /// <summary>Notazione scientifica con un numero fisso di decimali nella mantissa (SCI).</summary>
    Sci,
}

/// <summary>
/// Scrittura dei numeri all'italiana (virgola decimale, segno meno vero, 10 con l'esponente in alto) e arrotondamento.
/// L'arrotondamento è quello «da quaderno»: si parte dalle 15 cifre significative del numero (quelle di cui un double
/// è sicuro), così 2,675 con due decimali diventa 2,68 e non 2,67 come farebbe il valore binario esatto.
/// </summary>
public static class CalcFormatter
{
    public const int MaxDigits = 9;
    const int NormalDigits = 12;

    /// <summary>Il numero scritto per il display.</summary>
    public static string Format(double v, CalcFormat format, int digits)
    {
        if (double.IsNaN(v)) return "Errore";
        if (double.IsInfinity(v)) return v > 0 ? "∞" : "−∞";
        digits = Math.Clamp(digits, 0, MaxDigits);
        if (v == 0) v = 0; // niente −0
        switch (format)
        {
            case CalcFormat.Fix:
            {
                // troppo grande per i decimali fissi, o così piccolo da sembrare zero: notazione scientifica
                if (Math.Abs(v) >= 1e15) return Sci(v, digits);
                var d = Round15(v);
                var r = Math.Round(d, digits, MidpointRounding.AwayFromZero);
                if (r == 0 && v != 0) return Sci(v, digits);
                return Plain(r.ToString("F" + digits, CultureInfo.InvariantCulture));
            }
            case CalcFormat.Sci:
                return Sci(v, digits);
            default:
            {
                var (mant, exp) = Split(v, NormalDigits - 1);
                if (exp >= NormalDigits || exp < -9)
                    return Sci(mant, exp, trim: true);
                var d = Round15(v);
                var r = Math.Round(d, Math.Max(0, NormalDigits - 1 - exp), MidpointRounding.AwayFromZero);
                return Plain(Trim(r.ToString("F" + Math.Max(0, NormalDigits - 1 - exp), CultureInfo.InvariantCulture)));
            }
        }
    }

    /// <summary>Il numero arrotondato esattamente come lo mostra il display (tasto «Arrotonda», RND).</summary>
    public static double Round(double v, CalcFormat format, int digits)
    {
        if (double.IsNaN(v) || double.IsInfinity(v) || v == 0) return v;
        digits = Math.Clamp(digits, 0, MaxDigits);
        if (format == CalcFormat.Fix && Math.Abs(v) < 1e15)
        {
            var r = Math.Round(Round15(v), digits, MidpointRounding.AwayFromZero);
            if (r != 0) return (double)r;
        }
        int dec = format == CalcFormat.Normal ? NormalDigits - 1 : digits;
        var (mant, exp) = Split(v, dec);
        return double.Parse(mant.ToString(CultureInfo.InvariantCulture) + "E" + exp, CultureInfo.InvariantCulture);
    }

    static string Sci(double v, int digits)
    {
        var (mant, exp) = Split(v, digits);
        return Sci(mant, exp, trim: false, digits);
    }

    static string Sci(decimal mant, int exp, bool trim, int digits = 0)
    {
        var m = mant.ToString("F" + (trim ? NormalDigits - 1 : digits), CultureInfo.InvariantCulture);
        if (trim) m = Trim(m);
        return Plain(m) + " · 10" + Superscript(exp);
    }

    /// <summary>Mantissa (1 ≤ |m| &lt; 10) arrotondata a <paramref name="decimals"/> decimali, ed esponente.</summary>
    static (decimal Mantissa, int Exponent) Split(double v, int decimals)
    {
        // le 15 cifre significative: "d.dddddddddddddE+xxx"
        var s = v.ToString("E14", CultureInfo.InvariantCulture);
        int e = s.IndexOf('E');
        var mant = decimal.Parse(s[..e], CultureInfo.InvariantCulture);
        int exp = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture);
        mant = Math.Round(mant, decimals, MidpointRounding.AwayFromZero);
        if (Math.Abs(mant) >= 10) { mant = Math.Round(mant / 10, decimals, MidpointRounding.AwayFromZero); exp++; }
        return (mant, exp);
    }

    /// <summary>Il numero con 15 cifre significative, come decimal (0 se è troppo piccolo per un decimal).</summary>
    static decimal Round15(double v)
    {
        var s = v.ToString("E14", CultureInfo.InvariantCulture);
        try { return decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
        catch (OverflowException) { return 0; }
    }

    static string Trim(string s) => s.Contains('.') ? s.TrimEnd('0').TrimEnd('.') : s;

    /// <summary>Virgola decimale, segno meno tipografico e cifre intere a gruppi di tre (con uno spazio sottile) da 10 000 in su.</summary>
    static string Plain(string s)
    {
        bool neg = s.StartsWith('-');
        if (neg) s = s[1..];
        int dot = s.IndexOf('.');
        var intPart = dot < 0 ? s : s[..dot];
        var frac = dot < 0 ? "" : "," + s[(dot + 1)..];
        if (intPart.Length > 4)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < intPart.Length; i++)
            {
                if (i > 0 && (intPart.Length - i) % 3 == 0) sb.Append(' ');
                sb.Append(intPart[i]);
            }
            intPart = sb.ToString();
        }
        if (neg && (intPart.Trim('0', ' ') != "" || frac.Trim(',', '0') != "")) intPart = "−" + intPart;
        return intPart + frac;
    }

    const string Sup = "⁰¹²³⁴⁵⁶⁷⁸⁹";

    public static string Superscript(int n)
    {
        var sb = new StringBuilder();
        if (n < 0) sb.Append('⁻');
        foreach (var c in Math.Abs(n).ToString(CultureInfo.InvariantCulture)) sb.Append(Sup[c - '0']);
        return sb.ToString();
    }
}
