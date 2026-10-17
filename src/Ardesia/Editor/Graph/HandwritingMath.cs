using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Ink;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Ardesia.Editor.Graph;

/// <summary>
/// Riconoscimento delle formule scritte a mano con il «Math Input Control» di Windows (micaut.dll, quello del
/// vecchio Pannello di input matematico), senza librerie esterne. Il controllo ha una sua finestra e dà il
/// risultato (MathML) solo quando si preme «Insert»: lo si tiene fuori da tutti gli schermi, gli si caricano i
/// tratti e si preme «Insert» con UI Automation. Riconosce un'espressione per volta: le righe le divide Ardesia.
/// </summary>
public static class HandwritingMath
{
    static readonly Guid MathInputControlClsid = new("c561816c-14d8-4090-830c-98d994b21c7b");
    static readonly Guid InkDispClsid = new("937c1a34-151d-4610-9ca6-a8cc9bdb5d83");

    static bool? _available;

    /// <summary>Registro facoltativo di quello che succede (per le prove).</summary>
    public static Action<string>? Trace;

    /// <summary>Windows ha il riconoscimento delle formule (il componente potrebbe sparire in futuro).</summary>
    public static bool IsAvailable => _available ??= Registered(MathInputControlClsid) && Registered(InkDispClsid);

    public const string NotAvailableMessage = "Su questo PC Windows non ha il riconoscimento delle formule scritte a mano: scrivi la funzione con la tastiera.";

    static bool Registered(Guid clsid)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid}}}\InprocServer32");
            return key?.GetValue(null) is string dll && System.IO.File.Exists(Environment.ExpandEnvironmentVariables(dll));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Divide i tratti in righe (dall'alto in basso): fasce orizzontali separate da uno spazio vuoto. Numeratore e
    /// denominatore di una frazione stanno sopra e sotto il resto della formula: li tiene attaccati la linea di
    /// frazione (un tratto piatto e largo), che prende i tratti appena sopra e appena sotto di lei.
    /// </summary>
    public static IReadOnlyList<StrokeCollection> SplitLines(StrokeCollection strokes)
    {
        var items = strokes.Select(s => (Stroke: s, Box: s.GetBounds())).ToList();
        if (items.Count == 0) return Array.Empty<StrokeCollection>();
        // altezza tipica di un tratto, senza contare quelli piatti (=, −, linea di frazione)
        var heights = items.Select(t => t.Box.Height).Where(h => h > 4).OrderBy(h => h).ToList();
        double typical = heights.Count > 0 ? heights[heights.Count / 2] : 20;
        double tolerance = typical * 0.15;

        // gruppi: all'inizio un tratto per gruppo
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        for (int b = 0; b < items.Count; b++)
        {
            var bar = items[b].Box;
            if (bar.Height > typical * 0.3 || bar.Width < typical * 0.8) continue;
            for (int s = 0; s < items.Count; s++)
            {
                if (s == b) continue;
                var box = items[s].Box;
                double cx = (box.Left + box.Right) / 2;
                if (cx < bar.Left || cx > bar.Right) continue;
                double gap = box.Bottom <= bar.Top ? bar.Top - box.Bottom : box.Top >= bar.Bottom ? box.Top - bar.Bottom : 0;
                if (gap < typical) Union(s, b);
            }
        }

        // poi le fasce: gruppi che si sovrappongono in altezza stanno nella stessa riga
        var groups = Enumerable.Range(0, items.Count).GroupBy(Find)
            .Select(g => (Members: g.ToList(), Top: g.Min(i => items[i].Box.Top), Bottom: g.Max(i => items[i].Box.Bottom)))
            .OrderBy(g => g.Top).ToList();
        var lines = new List<StrokeCollection>();
        StrokeCollection? current = null;
        double bottom = double.NegativeInfinity;
        foreach (var g in groups)
        {
            if (current == null || g.Top > bottom + tolerance)
            {
                current = new StrokeCollection();
                lines.Add(current);
                bottom = g.Bottom;
            }
            // nell'ordine in cui sono stati scritti
            foreach (int i in g.Members) current.Add(items[i].Stroke);
            bottom = Math.Max(bottom, g.Bottom);
        }
        return lines.Select(l => new StrokeCollection(l.OrderBy(strokes.IndexOf))).ToList();
    }

    /// <summary>Riconosce i tratti (anche su più righe) e restituisce il testo di ogni riga, per «Funzioni del grafico».</summary>
    public static async Task<IReadOnlyList<string>> RecognizeLinesAsync(StrokeCollection strokes, Window? owner)
    {
        var mathml = await RecognizeAsync(SplitLines(strokes), owner);
        return mathml.Select(MathMLText.ToText).ToList();
    }

    /// <summary>Il MathML di ogni gruppo di tratti ("" se il riconoscitore non ha dato nulla).</summary>
    public static async Task<IReadOnlyList<string>> RecognizeAsync(IReadOnlyList<StrokeCollection> lines, Window? owner)
    {
        var results = new List<string>();
        if (lines.Count == 0) return results;
        if (!IsAvailable) throw new InvalidOperationException(NotAvailableMessage);

        dynamic mic = Activator.CreateInstance(Type.GetTypeFromCLSID(MathInputControlClsid)!)!;
        var sink = new Sink();
        var container = (IConnectionPointContainer)(object)mic;
        var iid = typeof(IMathInputControlEvents).GUID;
        container.FindConnectionPoint(ref iid, out var point);
        point!.Advise(sink, out int cookie);
        try
        {
            // fuori da tutti gli schermi (coordinate fisiche dello schermo virtuale)
            int x = GetSystemMetrics(SmXVirtualScreen) - 4000, y = GetSystemMetrics(SmYVirtualScreen) - 3000;
            mic.SetPosition(x, y, x + 800, y + 500);
            foreach (var line in lines)
            {
                mic.Show();
                mic.Clear();
                var done = sink.Expect();
                mic.LoadInk(ToInkDisp(line));
                string? result = null;
                var hwnd = FindWindow();
                Trace?.Invoke($"finestra del controllo: {(hwnd is { } h ? h.ToString("X") : "non trovata")}");
                // «Insert» conta solo a riconoscimento finito: si ripreme finché arriva il risultato (al massimo ~5 s)
                for (int attempt = 0; hwnd != null && attempt < 10 && result == null; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 100 : 400);
                    if (!PressInsert(hwnd.Value)) break;
                    var first = await Task.WhenAny(done, Task.Delay(100));
                    if (first == done) result = done.Result;
                }
                Trace?.Invoke(result == null ? "nessun risultato" : "risultato: " + result.Length + " caratteri");
                results.Add(result ?? "");
            }
        }
        finally
        {
            try { mic.Hide(); } catch (COMException) { }
            try { point.Unadvise(cookie); } catch (COMException) { }
            Marshal.FinalReleaseComObject(mic);
            owner?.Activate();
        }
        return results;
    }

    /// <summary>Come <see cref="RecognizeAsync"/>, ma aspetta il risultato (per le prove automatiche).</summary>
    public static IReadOnlyList<string> Recognize(IReadOnlyList<StrokeCollection> lines)
    {
        var task = RecognizeAsync(lines, null);
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>I tratti di WPF (in 1/96 di pollice) come inchiostro COM (in centesimi di millimetro).</summary>
    static object ToInkDisp(StrokeCollection strokes)
    {
        dynamic ink = Activator.CreateInstance(Type.GetTypeFromCLSID(InkDispClsid)!)!;
        const double himetric = 2540.0 / 96;
        foreach (var s in strokes)
        {
            var pts = s.StylusPoints;
            if (pts.Count == 0) continue;
            // un punto da solo (il puntino della i) diventa un trattino minuscolo
            int n = Math.Max(pts.Count, 2);
            var data = new int[n * 2];
            for (int i = 0; i < n; i++)
            {
                var p = pts[Math.Min(i, pts.Count - 1)];
                data[2 * i] = (int)Math.Round(p.X * himetric) + (i >= pts.Count ? 1 : 0);
                data[2 * i + 1] = (int)Math.Round(p.Y * himetric);
            }
            ink.CreateStroke(data, null);
        }
        return ink;
    }

    /// <summary>La finestra del controllo, creata da questo thread.</summary>
    static IntPtr? FindWindow()
    {
        IntPtr found = IntPtr.Zero;
        var name = new StringBuilder(64);
        EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
        {
            name.Clear();
            GetClassName(h, name, name.Capacity);
            if (name.ToString() == "MathInput_Window") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : found;
    }

    /// <summary>Preme «Insert» (la finestra è in inglese anche su Windows italiano).</summary>
    static bool PressInsert(IntPtr hwnd)
    {
        try
        {
            var window = AutomationElement.FromHandle(hwnd);
            var buttons = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement b in buttons)
            {
                if (b.Current.Name is "Insert" or "Inserisci" && b.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
                {
                    ((InvokePattern)p).Invoke();
                    return true;
                }
            }
            Trace?.Invoke($"«Insert» non trovato tra {buttons.Count} pulsanti");
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            Trace?.Invoke("UI Automation: " + ex.Message);
        }
        return false;
    }

    // ── eventi del controllo ──

    [Guid("683336b5-a47d-4358-96f9-875a472ae70a"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch), ComVisible(true)]
    public interface IMathInputControlEvents
    {
        [DispId(0)] void Insert(string recoResult);
        [DispId(1)] void Close();
        [DispId(2)] void Paint(IntPtr hdc, int left, int top, int right, int bottom, int element, int state);
        [DispId(3)] void Clear();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Sink : IMathInputControlEvents
    {
        TaskCompletionSource<string> _result = new();

        public Task<string> Expect()
        {
            _result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _result.Task;
        }

        public void Insert(string recoResult) => _result.TrySetResult(recoResult ?? "");
        public void Close() => _result.TrySetResult("");
        public void Paint(IntPtr hdc, int left, int top, int right, int bottom, int element, int state) { }
        public void Clear() { }
    }

    const int SmXVirtualScreen = 76, SmYVirtualScreen = 77;
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
}
