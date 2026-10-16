using System.Globalization;
using System.Windows;

namespace Ardesia.Editor.Graph;

/// <summary>Calcoli numerici del grafico: campionamento delle curve, zeri, estremi, intersezioni, tacche degli assi.</summary>
public static class GraphMath
{
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    // ───────────────────────────── Campionamento ─────────────────────────────

    /// <summary>
    /// I tratti continui della curva tra <paramref name="xmin"/> e <paramref name="xmax"/>, in coordinate matematiche.
    /// La curva si spezza dove la funzione non esiste o salta (asintoti di 1/x e tan x), così non compare la riga verticale finta.
    /// </summary>
    public static List<List<Point>> Sample(Func<double, double> f, double xmin, double xmax, double ymin, double ymax, int samples)
    {
        var result = new List<List<Point>>();
        if (!(xmax > xmin) || samples < 2) return result;
        double range = ymax - ymin;
        double clamp = Math.Max(Math.Abs(ymin), Math.Abs(ymax)) + range * 50;
        double dx = (xmax - xmin) / samples;
        List<Point>? current = null;
        double px = xmin, py = f(xmin);

        void Add(double x, double y)
        {
            current ??= new List<Point>();
            current.Add(new Point(x, Math.Clamp(y, -clamp, clamp)));
        }
        void Close()
        {
            if (current is { Count: > 1 }) result.Add(current);
            current = null;
        }

        if (Finite(py)) Add(px, py);
        for (int i = 1; i <= samples; i++)
        {
            double x = i == samples ? xmax : xmin + i * dx;
            double y = f(x);
            bool a = Finite(py), b = Finite(y);
            if (a && b)
            {
                if (Math.Abs(y - py) > range * 0.25 && IsJump(f, px, x, range))
                {
                    Close();
                    Add(x, y);
                }
                else
                {
                    Add(x, y);
                }
            }
            else if (a && !b)
            {
                // fine del dominio: si cerca il bordo, per arrivarci con la curva (√x verso destra, …)
                var (ex, ey) = Edge(f, px, x);
                if (Finite(ey)) Add(ex, ey);
                Close();
            }
            else if (!a && b)
            {
                var (ex, ey) = Edge(f, x, px);
                if (Finite(ey)) Add(ex, ey);
                Add(x, y);
            }
            px = x;
            py = y;
        }
        Close();
        return result;
    }

    static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>Ultimo punto dove la funzione esiste, cercato a metà tra <paramref name="inside"/> (esiste) e <paramref name="outside"/>.</summary>
    static (double, double) Edge(Func<double, double> f, double inside, double outside)
    {
        double yIn = f(inside);
        for (int k = 0; k < 40; k++)
        {
            double m = (inside + outside) / 2, ym = f(m);
            if (Finite(ym)) { inside = m; yIn = ym; }
            else outside = m;
        }
        return (inside, yIn);
    }

    /// <summary>Un salto vero (asintoto, discontinuità) resta grande anche restringendo l'intervallo; una salita ripida no.</summary>
    static bool IsJump(Func<double, double> f, double a, double b, double range)
    {
        double fa = f(a), fb = f(b);
        for (int k = 0; k < 40; k++)
        {
            double m = (a + b) / 2, fm = f(m);
            if (!Finite(fm)) return true;
            if (Math.Abs(fm - fa) > Math.Abs(fb - fm)) { b = m; fb = fm; }
            else { a = m; fa = fm; }
        }
        return Math.Abs(fb - fa) > range * 0.02;
    }

    // ───────────────────────────── Punti notevoli ─────────────────────────────

    /// <summary>Zeri di f nell'intervallo: cambi di segno (bisezione) e zeri «a tocco» come in x² (minimi di |f|).</summary>
    public static List<double> Zeros(Func<double, double> f, double a, double b, int samples = 1200)
    {
        var zeros = new List<double>();
        if (!(b > a)) return zeros;
        double dx = (b - a) / samples;
        var xs = new double[samples + 1];
        var ys = new double[samples + 1];
        for (int i = 0; i <= samples; i++)
        {
            xs[i] = a + i * dx;
            ys[i] = f(xs[i]);
        }
        double scale = 1;
        foreach (var y in ys) if (Finite(y)) scale = Math.Max(scale, Math.Min(Math.Abs(y), 1e6));

        for (int i = 0; i < samples; i++)
        {
            double y0 = ys[i], y1 = ys[i + 1];
            if (!Finite(y0) || !Finite(y1)) continue;
            if (y0 == 0) { AddUnique(zeros, xs[i], dx); continue; }
            if (Math.Sign(y0) != Math.Sign(y1) && y1 != 0)
            {
                double lo = xs[i], hi = xs[i + 1], flo = y0;
                for (int k = 0; k < 80; k++)
                {
                    double m = (lo + hi) / 2, fm = f(m);
                    if (!Finite(fm)) break;
                    if (Math.Sign(fm) == Math.Sign(flo)) { lo = m; flo = fm; }
                    else hi = m;
                }
                double z = (lo + hi) / 2;
                // 1/x cambia segno in 0 ma non ha zeri: lì il valore esplode invece di avvicinarsi a 0
                if (Math.Abs(f(z)) < 1e-6 * (1 + Math.Abs(y0) + Math.Abs(y1))) AddUnique(zeros, z, dx);
            }
        }
        if (Finite(ys[samples]) && ys[samples] == 0) AddUnique(zeros, xs[samples], dx);

        // zeri senza cambio di segno: minimo locale di |f| che arriva a 0
        for (int i = 1; i < samples; i++)
        {
            double y0 = ys[i - 1], y1 = ys[i], y2 = ys[i + 1];
            if (!Finite(y0) || !Finite(y1) || !Finite(y2) || y1 == 0) continue;
            if (Math.Sign(y0) != Math.Sign(y1) || Math.Sign(y2) != Math.Sign(y1)) continue;
            if (!(Math.Abs(y1) <= Math.Abs(y0) && Math.Abs(y1) <= Math.Abs(y2))) continue;
            double x = GoldenMin(v => Math.Abs(f(v)), xs[i - 1], xs[i + 1]);
            if (Math.Abs(f(x)) < 1e-9 * scale) AddUnique(zeros, x, dx);
        }
        zeros.Sort();
        return zeros;
    }

    /// <summary>Massimi e minimi relativi dentro l'intervallo (non agli estremi del dominio visibile).</summary>
    public static List<(double X, double Y, bool IsMax)> Extrema(Func<double, double> f, double a, double b, double yRange, int samples = 1200)
    {
        var list = new List<(double, double, bool)>();
        if (!(b > a)) return list;
        double dx = (b - a) / samples;
        double prevX = a, prevY = f(a), curX = a + dx, curY = f(curX);
        for (int i = 2; i <= samples; i++)
        {
            double nx = a + i * dx, ny = f(nx);
            if (Finite(prevY) && Finite(curY) && Finite(ny))
            {
                bool max = curY > prevY && curY >= ny;
                bool min = curY < prevY && curY <= ny;
                if (max || min)
                {
                    double x = max ? GoldenMin(v => -f(v), prevX, nx) : GoldenMin(f, prevX, nx);
                    double y = f(x), d = dx * 0.05;
                    double l = f(x - d), r = f(x + d);
                    // vicino a un asintoto il «massimo» è finto: i valori accanto sono lontanissimi
                    bool real = Finite(y) && Finite(l) && Finite(r)
                                && Math.Abs(y - l) < yRange * 1e-3 && Math.Abs(y - r) < yRange * 1e-3
                                && (max ? y >= l && y >= r : y <= l && y <= r);
                    if (real && !list.Any(e => Math.Abs(e.Item1 - x) < dx)) list.Add((x, y, max));
                }
            }
            prevX = curX; prevY = curY;
            curX = nx; curY = ny;
        }
        return list;
    }

    /// <summary>Derivata numerica (differenze centrali).</summary>
    public static Func<double, double> Derivative(Func<double, double> f) => x =>
    {
        double h = 1e-5 * Math.Max(1, Math.Abs(x));
        return (f(x + h) - f(x - h)) / (2 * h);
    };

    static double GoldenMin(Func<double, double> f, double a, double b)
    {
        const double g = 0.6180339887498949;
        double c = b - g * (b - a), d = a + g * (b - a);
        double fc = f(c), fd = f(d);
        for (int k = 0; k < 100 && Math.Abs(b - a) > 1e-13 * (1 + Math.Abs(a)); k++)
        {
            if (fc < fd || double.IsNaN(fd)) { b = d; d = c; fd = fc; c = b - g * (b - a); fc = f(c); }
            else { a = c; c = d; fc = fd; d = a + g * (b - a); fd = f(d); }
        }
        return (a + b) / 2;
    }

    static void AddUnique(List<double> list, double x, double dx)
    {
        if (Math.Abs(x) < dx * 1e-6) x = 0;
        if (!list.Any(v => Math.Abs(v - x) < dx)) list.Add(x);
    }

    // ───────────────────────────── Assi ─────────────────────────────

    /// <summary>Passo «tondo» delle tacche (1, 2, 5 × 10ⁿ) perché siano a circa <paramref name="targetPx"/> pixel l'una dall'altra.</summary>
    public static double NiceStep(double range, double pixels, double targetPx = 70)
    {
        if (!(range > 0) || !(pixels > 0)) return 1;
        double raw = range * targetPx / pixels;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        return (n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10) * mag;
    }

    /// <summary>Passo delle tacche in multipli di π (π/4, π/2, π, 2π…).</summary>
    public static double PiStep(double range, double pixels, double targetPx = 70)
    {
        double raw = range * targetPx / pixels;
        foreach (var k in new[] { 0.25, 0.5, 1, 2, 5, 10, 20, 50 })
            if (k * Math.PI >= raw * 0.75) return k * Math.PI;
        return NiceStep(range, pixels, targetPx);
    }

    /// <summary>Numero all'italiana per le etichette: virgola decimale, segno meno tipografico, al massimo 2 decimali.</summary>
    public static string Format(double v, double step = 0.01)
    {
        if (Math.Abs(v) < 1e-12) v = 0;
        int decimals = step >= 1 ? 0 : Math.Min(6, (int)Math.Ceiling(-Math.Log10(step) - 1e-9));
        decimals = Math.Max(decimals, 0);
        var s = Math.Round(v, decimals).ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), It);
        if (s == "-0") s = "0";
        return s.Replace('-', '−');
    }

    /// <summary>Un numero come multiplo semplice di π («π/2», «−3π/4», «2π»), oppure null se non lo è.</summary>
    public static string? FormatPi(double v)
    {
        if (Math.Abs(v) < 1e-9) return "0";
        foreach (int den in new[] { 1, 2, 3, 4, 6 })
        {
            double k = v / Math.PI * den;
            if (Math.Abs(k - Math.Round(k)) > 1e-6) continue;
            long num = (long)Math.Round(k);
            long gcd = Gcd(Math.Abs(num), den);
            num /= gcd;
            long d = den / gcd;
            string sign = num < 0 ? "−" : "";
            long an = Math.Abs(num);
            string top = an == 1 ? "π" : an + "π";
            return sign + (d == 1 ? top : top + "/" + d);
        }
        return null;
    }

    static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);

    /// <summary>Coordinate di un punto per l'etichetta: «(1,5; −2)».</summary>
    public static string FormatPoint(double x, double y, bool pi)
    {
        string fx = (pi ? FormatPi(x) : null) ?? Format(x);
        return $"({fx}; {Format(y)})";
    }
}
