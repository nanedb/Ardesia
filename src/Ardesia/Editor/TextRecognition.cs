using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Ink;
using Ardesia.Editor.Graph;
using Microsoft.Win32;

namespace Ardesia.Editor;

/// <summary>
/// Riconoscimento della scrittura a mano (testo, non formule) con il riconoscitore di Windows della vecchia
/// piattaforma Tablet PC (InkObj.dll, COM, senza librerie): «Riconoscimento grafia italiana Microsoft» su Windows
/// in italiano. Le righe le divide Ardesia (<see cref="HandwritingMath.SplitLines"/>), una richiesta per riga.
/// Serve anche a trovare un indirizzo web scritto a mano («Apri il link»).
/// </summary>
public static class TextRecognition
{
    static readonly Guid RecognizersClsid = new("9fd4e808-f6e6-4e65-98d3-aa39054c1255");
    static readonly Guid ContextClsid = new("aac46a37-9229-4fc0-8cce-4497569bf4d1");
    const int Italian = 0x0410;

    static bool? _available;

    public const string NotAvailableMessage =
        "Su questo PC Windows non ha il riconoscimento della scrittura a mano. Si aggiunge da Impostazioni › Data/ora e lingua › Lingua, nelle opzioni della lingua italiana (Scrittura a mano).";

    /// <summary>Windows ha un riconoscitore di scrittura (non solo quello dei gesti).</summary>
    public static bool IsAvailable => _available ??= Probe();

    /// <summary>Perché il riconoscitore non c'è (per le prove).</summary>
    public static string? ProbeError { get; private set; }

    static bool Probe()
    {
        try
        {
            using (var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{ContextClsid}}}\InprocServer32"))
                if (key == null)
                {
                    ProbeError = "InkRecognizerContext non registrato";
                    return false;
                }
            return CreateRecognizer() != null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            ProbeError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>Il riconoscitore italiano, se c'è, altrimenti quello predefinito; null se non ce n'è.</summary>
    static object? CreateRecognizer()
    {
        dynamic recognizers = Activator.CreateInstance(Type.GetTypeFromCLSID(RecognizersClsid)!)!;
        try
        {
            if (recognizers.Count == 0)
            {
                ProbeError = "nessun riconoscitore";
                return null;
            }
            foreach (int lcid in new[] { Italian, 0 })
            {
                try
                {
                    object? r = recognizers.GetDefaultRecognizer(lcid);
                    if (r != null) return r;
                    ProbeError = $"lingua {lcid}: nessun riconoscitore";
                }
                catch (COMException ex)
                {
                    // nessun riconoscitore per questa lingua
                    ProbeError = $"lingua {lcid}: {ex.Message}";
                }
            }
            return null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(recognizers);
        }
    }

    /// <summary>Il testo scritto a mano, riga per riga ("" se non si legge nulla).</summary>
    public static string Recognize(StrokeCollection strokes, bool web = false)
    {
        if (!IsAvailable) throw new InvalidOperationException(NotAvailableMessage);
        var lines = SplitLines(strokes);
        dynamic? recognizer = CreateRecognizer();
        if (recognizer == null) throw new InvalidOperationException(NotAvailableMessage);
        var result = new List<string>();
        foreach (var line in lines)
        {
            string text = RecognizeLine(recognizer, line, web).Trim();
            if (text.Length > 0) result.Add(text);
        }
        return string.Join("\n", result);
    }

    /// <summary>
    /// Le righe di <see cref="HandwritingMath.SplitLines"/>, ma i puntini delle i e gli accenti di una riga senza
    /// lettere alte («sei», «più») non fanno una riga a sé: le fasce basse si uniscono alla riga più vicina.
    /// </summary>
    public static IReadOnlyList<StrokeCollection> SplitLines(StrokeCollection strokes)
    {
        var lines = HandwritingMath.SplitLines(strokes).Select(l => new StrokeCollection(l)).ToList();
        if (lines.Count < 2) return lines;
        // altezza tipica di un tratto, senza quelli piatti o minuscoli (puntini, trattini)
        var heights = strokes.Select(s => s.GetBounds().Height).Where(h => h > 4).OrderBy(h => h).ToList();
        double typical = heights.Count > 0 ? heights[heights.Count / 2] : 20;
        for (int i = 0; i < lines.Count && lines.Count > 1; i++)
        {
            // una fascia fatta solo di segnetti: puntini delle i, accenti, apostrofi
            var b = lines[i].GetBounds();
            if (b.Height >= typical * 0.5 || lines[i].Any(s => s.GetBounds() is var sb && Math.Max(sb.Width, sb.Height) > typical * 0.6)) continue;
            // la riga vera più vicina (di solito quella sotto: il puntino sta sopra le lettere)
            int best = -1;
            double bestGap = double.MaxValue;
            for (int j = 0; j < lines.Count; j++)
            {
                if (j == i) continue;
                var o = lines[j].GetBounds();
                double gap = o.Top >= b.Bottom ? o.Top - b.Bottom : b.Top >= o.Bottom ? (b.Top - o.Bottom) * 1.5 : 0;
                if (gap < bestGap) { bestGap = gap; best = j; }
            }
            if (best < 0 || bestGap > typical * 0.8) continue;
            foreach (var s in lines[i]) lines[best].Add(s);
            lines.RemoveAt(i);
            i = -1; // si ricomincia: le altezze sono cambiate
        }
        return lines.Select(l => new StrokeCollection(l.OrderBy(strokes.IndexOf))).ToList();
    }

    static string RecognizeLine(dynamic recognizer, StrokeCollection line, bool web)
    {
        // (la proprietà Recognizer del contesto non si può impostare via IDispatch: il contesto lo crea il riconoscitore)
        dynamic context = recognizer.CreateRecognizerContext();
        try
        {
            // il «factoid» va impostato prima dei tratti
            if (web)
            {
                try { context.Factoid = "WEB"; }
                catch (COMException) { return ""; }
            }
            dynamic ink = HandwritingMath.ToInkDisp(line);
            context.Strokes = ink.Strokes;
            context.EndInkInput();
            int status;
            dynamic? r = context.Recognize(out status);
            return r == null ? "" : (string)r.TopString;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Debug.WriteLine("riconoscimento: " + ex.Message);
            return "";
        }
        finally
        {
            Marshal.FinalReleaseComObject(context);
        }
    }

    // ───────────── indirizzi web ─────────────

    static readonly Regex UrlPattern = new(
        @"\b(?:https?://[^\s<>""]+|www\.[^\s<>""]+|[a-z0-9][a-z0-9-]*(?:\.[a-z0-9-]+)*\.(?:it|com|org|net|edu|gov|eu|info|io|app|dev|me|tv|ch|fr|de|uk|es)(?![a-z0-9-])(?:/[^\s<>""]*)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Il primo indirizzo web nel testo, completo di https://; null se non ce n'è.</summary>
    public static Uri? FindUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match m in UrlPattern.Matches(text))
        {
            // la punteggiatura finale fa parte della frase, non dell'indirizzo
            string s = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '»', '\'', '"');
            // indirizzo e-mail, o un indirizzo con un altro protocollo (ftp://…, file://…)
            if (s.Contains('@') || m.Index > 0 && text[m.Index - 1] is '@' or '/' or '.' or ':') continue;
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                s = "https://" + s;
            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.'))
                return uri;
        }
        return null;
    }

    /// <summary>Un indirizzo web scritto a mano: prima la lettura normale, poi quella «da indirizzo».</summary>
    public static Uri? FindHandwrittenUrl(StrokeCollection strokes)
    {
        if (!IsAvailable || strokes.Count == 0 || strokes.Count > 300) return null;
        try
        {
            return FindUrl(Recognize(strokes)) ?? FindUrl(Recognize(strokes, web: true));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Apre l'indirizzo nel browser predefinito (solo http e https).</summary>
    public static bool Open(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
