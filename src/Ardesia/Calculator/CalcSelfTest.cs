using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Ardesia.Calculator.CalcKeys;

namespace Ardesia.Calculator;

/// <summary>Prove della calcolatrice: <c>Ardesia.exe --prova-calcolatrice [registro.txt]</c> (codice d'uscita 1 se qualcosa non va).</summary>
public static class CalcSelfTest
{
    public static string Run(out int failures)
    {
        var log = new StringBuilder();
        int fail = 0;
        void Check(string what, string got, string expected)
        {
            bool ok = got == expected;
            if (!ok) fail++;
            log.AppendLine((ok ? "ok    " : "ERRORE") + "  " + what + "  →  «" + got + "»" + (ok ? "" : "   atteso «" + expected + "»"));
        }

        // scrittura dei numeri
        string F(double v, CalcFormat f, int d) => CalcFormatter.Format(v, f, d);
        Check("normale 0,1+0,2", F(0.1 + 0.2, CalcFormat.Normal, 0), "0,3");
        Check("normale 1/3", F(1.0 / 3, CalcFormat.Normal, 0), "0,333333333333");
        Check("normale 2/3", F(2.0 / 3, CalcFormat.Normal, 0), "0,666666666667");
        Check("normale 123456,5", F(123456.5, CalcFormat.Normal, 0), "123 456,5");
        Check("normale 1234", F(1234, CalcFormat.Normal, 0), "1234");
        Check("normale −0,000123", F(-0.000123, CalcFormat.Normal, 0), "−0,000123");
        Check("normale 6,02e23", F(6.02e23, CalcFormat.Normal, 0), "6,02 · 10²³");
        Check("normale 1,6e−19", F(1.6e-19, CalcFormat.Normal, 0), "1,6 · 10⁻¹⁹");
        Check("normale 1e12", F(1e12, CalcFormat.Normal, 0), "1 · 10¹²");
        Check("normale 999999999999", F(999999999999, CalcFormat.Normal, 0), "999 999 999 999");
        Check("FIX 2 di 2,675", F(2.675, CalcFormat.Fix, 2), "2,68");
        Check("FIX 2 di 1,005", F(1.005, CalcFormat.Fix, 2), "1,01");
        Check("FIX 2 di −2,675", F(-2.675, CalcFormat.Fix, 2), "−2,68");
        Check("FIX 0 di 2,5", F(2.5, CalcFormat.Fix, 0), "3");
        Check("FIX 3 di π", F(Math.PI, CalcFormat.Fix, 3), "3,142");
        Check("FIX 4 di 7", F(7, CalcFormat.Fix, 4), "7,0000");
        Check("FIX 2 di −0,001 (niente −0,00)", F(-0.001, CalcFormat.Fix, 2), "−1,00 · 10⁻³");
        Check("FIX 2 di 0,0000123 → SCI", F(0.0000123, CalcFormat.Fix, 2), "1,23 · 10⁻⁵");
        Check("FIX 2 di 1e20 → SCI", F(1e20, CalcFormat.Fix, 2), "1,00 · 10²⁰");
        Check("SCI 3 di 12345", F(12345, CalcFormat.Sci, 3), "1,235 · 10⁴");
        Check("SCI 2 di 9,996", F(9.996, CalcFormat.Sci, 2), "1,00 · 10¹");
        Check("SCI 4 di −0,00012345", F(-0.00012345, CalcFormat.Sci, 4), "−1,2345 · 10⁻⁴");
        Check("SCI 0 di 6,5e−7", F(6.5e-7, CalcFormat.Sci, 0), "7 · 10⁻⁷");
        Check("SCI 2 di 0", F(0, CalcFormat.Sci, 2), "0,00 · 10⁰");
        Check("FIX 2 di 0", F(0, CalcFormat.Fix, 2), "0,00");
        Check("normale 1e300·1e300", F(double.PositiveInfinity, CalcFormat.Normal, 0), "∞");
        Check("arrotonda FIX 2 di 2,675", CalcFormatter.Round(2.675, CalcFormat.Fix, 2).ToString(System.Globalization.CultureInfo.InvariantCulture), "2.68");
        Check("arrotonda SCI 2 di 12345", CalcFormatter.Round(12345, CalcFormat.Sci, 2).ToString(System.Globalization.CultureInfo.InvariantCulture), "12300");

        // algebrica
        string Alg(string keys, CalcFormat f = CalcFormat.Normal, int d = 4, bool deg = true)
        {
            var c = new AlgebraicCalc(new CalculatorSettings { Format = f, Digits = d, Degrees = deg });
            foreach (var k in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries)) c.Press(k);
            return c.Display()[^1].Text;
        }
        Check("2 + 3 × 4", Alg("2 add 3 mul 4 enter"), "14");
        Check("(2 + 3) × 4", Alg("lpar 2 add 3 rpar mul 4 enter"), "20");
        Check("−2²", Alg("sub 2 sq enter"), "−4");
        Check("2^3^2", Alg("2 pow 3 pow 2 enter"), "512");
        Check("2^−1", Alg("2 pow sub 1 enter"), "0,5");
        Check("sin 30°", Alg("sin 3 0 enter"), "0,5");
        Check("cos 90°", Alg("cos 9 0 enter"), "0");
        Check("tan 90°", Alg("tan 9 0 enter"), "La tangente di 90° non esiste");
        Check("sin π rad", Alg("sin pi enter", deg: false), "0");
        Check("sin⁻¹(0,5)", Alg("asin 0 dot 5 enter"), "30");
        Check("2π (per sottinteso)", Alg("2 pi enter", CalcFormat.Fix, 5), "6,28319");
        Check("3(1+2) parentesi aperta", Alg("3 lpar 1 add 2 enter"), "9");
        Check("√(2) FIX 4", Alg("sqrt 2 enter", CalcFormat.Fix, 4), "1,4142");
        Check("1 ÷ 0", Alg("1 div 0 enter"), "Non si può dividere per zero");
        Check("5!", Alg("5 fact enter"), "120");
        Check("10 C 3", Alg("1 0 ncr 3 enter"), "120");
        Check("2 × 5 C 2 (prima le combinazioni)", Alg("2 mul 5 ncr 2 enter"), "20");
        Check("3 ˣ√ −8", Alg("3 root sub 8 enter"), "−2");
        Check("1,5ᴇ3 + 1", Alg("1 dot 5 eex 3 add 1 enter"), "1501");
        Check("1ᴇ−3 (± sull'esponente)", Alg("1 eex 3 chs enter"), "0,001");
        Check("±: 5 ± = −5", Alg("5 chs enter"), "−5");
        Check("Ans: 2+3 = ×2 =", Alg("2 add 3 enter mul 2 enter"), "10");
        Check("dopo = una cifra ricomincia", Alg("2 add 3 enter 7 enter"), "7");
        Check("⌫ dopo = riapre l'espressione", Alg("2 add 3 enter back back 4 enter"), "6");
        Check("ln e", Alg("ln e enter"), "1");
        Check("log 1000", Alg("log 1 0 0 0 enter"), "3");
        Check("2 × 10^(3)", Alg("2 pow10 3 enter"), "2000");
        Check("50%", Alg("5 0 pct enter"), "0,5");
        Check("0,1 + 0,2", Alg("0 dot 1 add 0 dot 2 enter"), "0,3");
        Check("ln(−1)", Alg("ln sub 1 enter"), "Il logaritmo vuole un numero maggiore di 0");
        Check("2 + (incompleta)", Alg("2 add enter"), "Espressione incompleta");
        Check("2 ) parentesi in più", Alg("2 rpar enter"), "C'è una parentesi chiusa in più");
        Check("RND FIX 2 di 2/3, poi ×3", Alg("2 div 3 enter rnd mul 3 enter", CalcFormat.Fix, 2), "2,01");
        Check("espressione mostrata", Alg("2 add 3 mul sin 3 0"), "2+3×sin(30");

        // RPN
        string Rpn(string keys, CalcFormat f = CalcFormat.Normal, int d = 4, int line = 3)
        {
            var c = new RpnCalc(new CalculatorSettings { Rpn = true, Format = f, Digits = d });
            foreach (var k in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries)) c.Press(k);
            return c.Display()[line].Text;
        }
        Check("3 Invio 4 +", Rpn("3 enter 4 add"), "7");
        Check("3 Invio 4 + 5 × (pila che sale)", Rpn("3 enter 4 add 5 mul"), "35");
        Check("10 Invio 4 −", Rpn("1 0 enter 4 sub"), "6");
        Check("2 Invio 10 yˣ", Rpn("2 enter 1 0 pow"), "1024");
        Check("Invio Invio duplica: 3 Invio × = 9", Rpn("3 enter mul"), "9");
        Check("x↔y", Rpn("1 enter 2 swap"), "1");
        Check("x↔y, y", Rpn("1 enter 2 swap", line: 2), "2");
        Check("R↓", Rpn("1 enter 2 enter 3 rolldown"), "2");
        Check("T si ripete: 2 Invio Invio Invio × × ×", Rpn("2 enter enter enter mul mul mul"), "16");
        Check("⌫ cancella x e il numero dopo lo sostituisce", Rpn("5 enter 9 back back 7 add"), "12");
        Check("⌫ di una cifra", Rpn("1 2 3 back"), "12");
        Check("± durante la scrittura", Rpn("5 chs enter 3 add"), "−2");
        Check("± dopo un'operazione", Rpn("2 enter 3 add chs"), "−5");
        Check("ᴇ durante la scrittura", Rpn("1 dot 5 eex 3"), "1,5 · 10³");
        Check("ᴇ con esponente negativo", Rpn("2 eex 3 chs enter", CalcFormat.Sci, 1), "2,0 · 10⁻³");
        Check("LASTx", Rpn("2 enter 3 add lastx"), "3");
        Check("percentuale di y: 200 Invio 15 %", Rpn("2 0 0 enter 1 5 pct"), "30");
        Check("percentuale: y resta", Rpn("2 0 0 enter 1 5 pct", line: 2), "200");
        Check("1 Invio 0 ÷ errore", Rpn("1 enter 0 div"), "Non si può dividere per zero");
        Check("errore: la pila resta", Rpn("1 enter 0 div back"), "0");
        Check("errore: la pila resta (y)", Rpn("1 enter 0 div back", line: 2), "1");
        Check("sin 30", Rpn("3 0 sin"), "0,5");
        Check("ˣ√y: 27 Invio 3 ˣ√y", Rpn("2 7 enter 3 root"), "3");
        Check("FIX 2: 1 Invio 3 ÷", Rpn("1 enter 3 div", CalcFormat.Fix, 2), "0,33");
        Check("SCI 3: 6,02ᴇ23", Rpn("6 dot 0 2 eex 2 3 enter", CalcFormat.Sci, 3), "6,020 · 10²³");
        Check("π dopo Invio sostituisce x", Rpn("2 enter pi mul", CalcFormat.Fix, 4), "6,2832");
        Check("C azzera", Rpn("1 enter 2 clear", line: 2), "0");

        log.Insert(0, $"Prove della calcolatrice: {fail} errori\n\n");
        failures = fail;
        return log.ToString();
    }

    /// <summary>Fotografa la finestra della calcolatrice (algebrica e RPN) accanto al registro.</summary>
    public static void RenderSamples(string pathAlgebraic, string pathRpn)
    {
        void Shot(CalculatorSettings s, string path, params string[] keys)
        {
            var w = new CalculatorWindow(s, _ => { }, () => { }) { Left = -20000, Top = -20000, ShowActivated = false };
            w.Show();
            w.PressForTest(keys);
            w.UpdateLayout();
            var content = (FrameworkElement)w.Content;
            var bmp = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(w.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
            w.Close();
        }
        Shot(new CalculatorSettings { Format = CalcFormat.Fix, Digits = 4 }, pathAlgebraic,
            "2", Add, "3", Mul, Sin, "3", "0", RPar, Add, Sqrt, "2", Enter);
        Shot(new CalculatorSettings { Rpn = true, Format = CalcFormat.Sci, Digits = 3 }, pathRpn,
            "6", Dot, "0", "2", Eex, "2", "3", Enter, "1", "2", Mul, "4", Enter, "7");
    }
}
