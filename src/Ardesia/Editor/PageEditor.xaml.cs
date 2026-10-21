using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ardesia.Editor.Graph;
using Ardesia.Models;
using WpfPath = System.Windows.Shapes.Path;

namespace Ardesia.Editor;

public enum EditorTool { Select, Pen, Highlighter, Pencil, EraserStroke, EraserPoint, Text, Hand, Shape, Compass, Space }

/// <summary>
/// Il foglio di una pagina: si estende all'infinito verso destra e verso il basso,
/// ospita tratti di penna, caselle di testo, immagini e gli strumenti geometrici.
/// </summary>
public partial class PageEditor : UserControl
{
    public static readonly double[] PenWidths = { 1.5, 2.5, 4, 7, 12 };
    const double MinZoom = 0.25, MaxZoom = 4;
    const string ClipFormat = "Ardesia.Clip.v1";
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    static readonly Brush FocusBorder = Paint.Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0x1E, 0x5A, 0x68)));

    sealed record ClipData(string Token, StrokeCollection Strokes, List<ElementData> Elements, Dictionary<string, byte[]> Media);
    static ClipData? _clip;
    static int _pasteCount;

    enum DragMode { None, Pan, Shape, CompassMove, CompassRadius, CompassDraw, MoveSelection, RulerLine, GraphPan, GraphSlider, Space }

    readonly UndoManager _undo = new();
    readonly DispatcherTimer _commitTimer;
    readonly RulerTool _ruler = new();
    readonly ProtractorTool _protractor = new();
    readonly CompassTool _compass = new();
    readonly WpfPath _shapePreview = new()
    {
        IsHitTestVisible = false,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Visibility = Visibility.Collapsed,
    };

    Notebook? _notebook;
    NotebookNode? _node;
    bool _loading, _pending, _sizeUpdateQueued;
    double _zoom = 1, _minW, _minH;
    EditorTool _tool = EditorTool.Pen;
    Color _penColor = Colors.Black;
    Color _highlighterColor = Color.FromRgb(0xFF, 0xD6, 0x00);
    double _penWidth = 2.5, _highlighterWidth = 4, _pencilWidth = 2.5;
    int _pencilGrade = Pencil.DefaultGrade;

    DragMode _drag;
    Point _dragStart, _shapeEnd, _panStartView;
    double _panStartH, _panStartV;
    Vector _grabOffset;
    Stroke? _arcStroke;
    double _arcPrevAngle;

    public event EventHandler? Modified;
    public event EventHandler? ToolChanged;
    public event EventHandler? ZoomChanged;

    public PageEditor()
    {
        InitializeComponent();

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _commitTimer.Tick += (_, _) =>
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed || _drag != DragMode.None) return;
            CommitPending();
        };

        Overlay.Children.Add(_shapePreview);
        Overlay.Children.Add(_protractor);
        Overlay.Children.Add(_ruler);
        Overlay.Children.Add(_compass);

        Ink.StrokesReplaced += (_, e) =>
        {
            e.PreviousStrokes.StrokesChanged -= Strokes_Changed;
            e.NewStrokes.StrokesChanged += Strokes_Changed;
        };
        Ink.Strokes.StrokesChanged += Strokes_Changed;
        Ink.StrokeCollected += Ink_StrokeCollected;
        Ink.SelectionMoved += (_, _) => MarkChanged();
        Ink.SelectionResized += (_, _) =>
        {
            foreach (var r in Ink.GetSelectedElements().OfType<RichTextBox>()) r.Height = double.NaN;
            MarkChanged();
        };
        Ink.AllowDrop = true;
        Ink.DragOver += Ink_DragOver;
        Ink.Drop += Ink_Drop;

        SetupGraphSliders(); // per primo: un cursore preso col dito non deve far scorrere la pagina né aprire il menu
        SetupContextMenuInput();
        SetupFingerPan();
        SetupDrawingAids();
        // Spostamento della selezione con dito o penna (col mouse è gestito in ContentHost_PreviewMouseDown).
        // Palmo / gomma della penna: gomma grande quanto il contatto (o 60 px se la LIM non ne dà la misura).
        // Mentre si cancella non va disegnata la «scia» della penna: al suo posto un cerchio che segue la gomma.
        Overlay.Children.Add(_eraserRing);
        ContentHost.PreviewStylusDown += (_, e) =>
        {
            bool eraserTool = _tool is EditorTool.EraserPoint or EditorTool.EraserStroke;
            if (!e.StylusDevice.Inverted && !eraserTool) return;
            if (FingerPans && e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Touch && !e.StylusDevice.Inverted) return;
            double size = e.StylusDevice.Inverted ? PalmEraserSize(e) : _tool == EditorTool.EraserPoint ? EraserSize : 16;
            if (e.StylusDevice.Inverted) Ink.EraserShape = new EllipseStylusShape(size, size);
            var p = e.GetPosition(Ink);
            Ink.HideLiveStroke(e.StylusDevice, p);
            _eraserRing.Width = _eraserRing.Height = size * _zoom > 6 ? size : 6 / _zoom;
            MoveEraserRing(p);
            _eraserRing.Visibility = Visibility.Visible;
        };
        ContentHost.PreviewStylusMove += (_, e) =>
        {
            if (_eraserRing.Visibility == Visibility.Visible) MoveEraserRing(e.GetPosition(Ink));
        };
        ContentHost.PreviewStylusUp += (_, e) =>
        {
            if (!e.StylusDevice.Inverted && _eraserRing.Visibility != Visibility.Visible) return;
            Ink.EraserShape = new EllipseStylusShape(EraserSize, EraserSize);
            _eraserRing.Visibility = Visibility.Collapsed;
            Ink.ShowLiveStrokes();
        };
        ContentHost.PreviewStylusDown += (_, e) =>
        {
            if (e.StylusDevice.Inverted) return;
            if (_tool != EditorTool.Select || IsWithin(e.OriginalSource as DependencyObject, TitleArea)) return;
            if (!TryBeginMove(e.GetPosition(Ink))) return;
            ContentHost.CaptureStylus();
            e.Handled = true;
        };
        ContentHost.PreviewStylusMove += (_, e) =>
        {
            if (_drag != DragMode.MoveSelection) return;
            ContinueMove(e.GetPosition(Ink));
            e.Handled = true;
        };
        ContentHost.PreviewStylusUp += (_, e) =>
        {
            if (_drag != DragMode.MoveSelection) return;
            EndDrag();
            e.Handled = true;
        };
        ContentHost.PreviewMouseDown += ContentHost_PreviewMouseDown;
        ContentHost.PreviewMouseMove += ContentHost_PreviewMouseMove;
        ContentHost.PreviewMouseUp += ContentHost_PreviewMouseUp;
        ContentHost.LostMouseCapture += (_, _) => EndDrag();
        ContentHost.PreviewStylusUp += (_, _) => DeferCommit();
        ContentHost.PreviewTouchUp += (_, _) => DeferCommit();
        ContentHost.ManipulationStarting += ContentHost_ManipulationStarting;
        ContentHost.ManipulationDelta += ContentHost_ManipulationDelta;
        ContentHost.ManipulationInertiaStarting += ContentHost_ManipulationInertiaStarting;
        SetupResizePreview();
        SetupTextSelection(); // per ultimo: riceve solo ciò che gli altri gestori non hanno già preso

        Scroller.PreviewMouseWheel += Scroller_PreviewMouseWheel;
        Scroller.ScrollChanged += Scroller_ScrollChanged;
        Scroller.SizeChanged += (_, _) => UpdateCanvasSize();

        TitleBox.TextChanged += (_, _) =>
        {
            TitlePlaceholder.Visibility = TitleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // Il riempimento iniziale tramite binding non è una modifica.
            if (_loading || TitleBox.Text == _shownTitle) return;
            _shownTitle = TitleBox.Text;
            Modified?.Invoke(this, EventArgs.Empty);
        };

        ApplyTool();
    }

    // ───────────────────────────── Proprietà pubbliche ─────────────────────────────

    public NotebookNode? Page => _node;
    public double ZoomLevel => _zoom;
    public RichTextBox? ActiveTextBox { get; private set; }
    public FontFamily TextFontFamily { get; set; } = new(ElementFactory.DefaultFont);
    public double TextFontSize { get; set; } = ElementFactory.DefaultFontSize;
    public ShapeKind ShapeKind { get; set; } = ShapeKind.Line;

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            if (!IsTemporary(_tool)) _lastStableTool = _tool;
            _tool = value;
            ApplyTool();
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    EditorTool _lastStableTool = EditorTool.Pen;
    string _shownTitle = "";

    /// <summary>Strumenti "di passaggio": compasso, forme, sposta pagina, inserisci spazio.</summary>
    public static bool IsTemporary(EditorTool t) => t is EditorTool.Compass or EditorTool.Shape or EditorTool.Hand or EditorTool.Space;

    /// <summary>Esce dal compasso/forme/sposta e torna allo strumento usato prima.</summary>
    public bool ExitTemporaryTool()
    {
        if (!IsTemporary(_tool)) return false;
        EndDrag();
        Tool = _lastStableTool;
        return true;
    }

    public Color CurrentColor => _tool == EditorTool.Highlighter ? _highlighterColor : _penColor;
    public double CurrentWidth => _tool == EditorTool.Highlighter ? _highlighterWidth : _penWidth;

    public bool RulerVisible
    {
        get => _ruler.Visibility == Visibility.Visible;
        set
        {
            if (value == RulerVisible) return;
            if (value) _ruler.SetPose(ViewCenter(), 0);
            _ruler.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public bool ProtractorVisible
    {
        get => _protractor.Visibility == Visibility.Visible;
        set
        {
            if (value == ProtractorVisible) return;
            if (value) _protractor.SetPose(ViewCenter() + new Vector(0, _protractor.Radius / 2), 0);
            _protractor.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    bool IsDarkBackground => _node?.Page is { } d && BackgroundFactory.IsDark(BackgroundFactory.ParseColor(d.BackgroundColor));

    // ───────────────────────────── Caricamento e salvataggio ─────────────────────────────

    public void Load(Notebook nb, NotebookNode? node)
    {
        CommitPending();
        EndDrag();
        _notebook = nb;
        _node = node;
        _loading = true;
        try
        {
            ClearSelection();
            ActiveTextBox = null;
            Ink.Children.Clear();
            var data = node?.Page;
            Ink.Strokes = LoadStrokes(data?.Ink);
            Pencil.UpgradeAll(Ink.Strokes);
            if (data != null)
                foreach (var el in data.Elements)
                    if (ElementFactory.Create(el, nb) is { } ui) AddElement(ui);

            _shownTitle = node?.Title ?? "";
            TitleArea.DataContext = node;
            DateText.Text = data == null ? "" : data.Created.ToString("dddd d MMMM yyyy  ·  HH:mm", It);
            ApplyBackground();
            _minW = _minH = 0;
            _undo.Reset(Capture());
            _pending = false;
        }
        finally
        {
            _loading = false;
        }
        Scroller.ScrollToHome();
        ApplyTool();
        UpdateCanvasSize();
    }

    static StrokeCollection LoadStrokes(byte[]? isf)
    {
        if (isf is not { Length: > 0 }) return new StrokeCollection();
        try
        {
            return new StrokeCollection(new MemoryStream(isf));
        }
        catch
        {
            return new StrokeCollection();
        }
    }

    /// <summary>Scrive lo stato del foglio nei dati della pagina.</summary>
    public void Flush()
    {
        if (_node?.Page is not { } d) return;
        CommitPending();
        if (Ink.Strokes.Count > 0)
        {
            using var ms = new MemoryStream();
            Ink.Strokes.Save(ms, true);
            d.Ink = ms.ToArray();
        }
        else
        {
            d.Ink = null;
        }
        d.Elements = CaptureElements();
    }

    public void FocusTitle()
    {
        Dispatcher.BeginInvoke(() =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    // ───────────────────────────── Sfondo ─────────────────────────────

    public void ApplyBackground()
    {
        if (_node?.Page is not { } d) return;
        var color = BackgroundFactory.ParseColor(d.BackgroundColor);
        Ink.Background = BackgroundFactory.Create(d.Background, color);
        bool dark = BackgroundFactory.IsDark(color);
        TitleBox.Foreground = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x32, 0x31, 0x30));
        TitleBox.CaretBrush = TitleBox.Foreground;
        DateText.Foreground = new SolidColorBrush(dark ? Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF) : Color.FromRgb(0x76, 0x76, 0x76));
    }

    public void SetBackground(PageBackground? kind, string? color)
    {
        if (_node?.Page is not { } d) return;
        if (kind != null) d.Background = kind.Value;
        if (color != null) d.BackgroundColor = color;
        ApplyBackground();
        Modified?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── Strumenti ─────────────────────────────

    void ApplyTool()
    {
        CommitPending();
        if (_tool != EditorTool.Select) ClearSelection();

        switch (_tool)
        {
            case EditorTool.Select:
                Ink.EditingMode = InkCanvasEditingMode.Select;
                break;
            case EditorTool.Pen:
            case EditorTool.Highlighter:
            case EditorTool.Pencil:
                Ink.EditingMode = InkCanvasEditingMode.Ink;
                break;
            case EditorTool.EraserStroke:
                Ink.EditingMode = InkCanvasEditingMode.EraseByStroke;
                break;
            case EditorTool.EraserPoint:
                Ink.EraserShape = new EllipseStylusShape(EraserSize, EraserSize);
                Ink.EditingMode = InkCanvasEditingMode.EraseByPoint;
                break;
            default:
                Ink.EditingMode = InkCanvasEditingMode.None;
                break;
        }
        // Gomma "rovesciata": il palmo (sulle LIM che lo segnalano, es. Promethean) o il retro della penna
        // cancellano solo dove passano, con una gomma grande come un palmo.
        Ink.EditingModeInverted = InkCanvasEditingMode.EraseByPoint;
        ApplyDrawingAttributes();

        Ink.UseCustomCursor = Ink.EditingMode == InkCanvasEditingMode.None;
        Ink.Cursor = _tool switch
        {
            EditorTool.Text => Cursors.IBeam,
            EditorTool.Hand => Cursors.ScrollAll,
            EditorTool.Space => Cursors.SizeNS,
            EditorTool.Shape or EditorTool.Compass => Cursors.Cross,
            _ => null,
        };

        ContentHost.IsManipulationEnabled = _tool == EditorTool.Hand;

        if (_tool == EditorTool.Compass)
        {
            if (_compass.Visibility != Visibility.Visible)
            {
                var c = ViewCenter();
                _compass.Set(new Point(c.X - 75, c.Y + 80), 150, 0);
                _compass.Visibility = Visibility.Visible;
            }
        }
        else
        {
            _compass.Visibility = Visibility.Collapsed;
        }

        foreach (UIElement child in Ink.Children) ApplyInteractivity(child);
    }

    void ApplyInteractivity(UIElement ui)
    {
        // Solo le caselle di testo nello strumento Testo ricevono i clic direttamente. Negli altri casi
        // (in particolare in Seleziona) li gestisce il foglio, così gli oggetti selezionati si possono trascinare.
        ui.IsHitTestVisible = ui is RichTextBox && _tool == EditorTool.Text;
    }

    void ApplyDrawingAttributes()
    {
        Ink.SetPencilMode(_tool == EditorTool.Pencil ? Pencil.BrushFor(Pencil.ColorOf(_pencilGrade), _pencilGrade) : null);
        Ink.LiveEnabled = _tool is EditorTool.Pen or EditorTool.Highlighter or EditorTool.Pencil;
        Ink.DefaultDrawingAttributes = _tool switch
        {
            EditorTool.Highlighter => HighlighterAttributes(),
            EditorTool.Pencil => Pencil.Attributes(_pencilGrade, _pencilWidth),
            _ => PenAttributes(),
        };
        _compass.PencilColor = _penColor;
    }

    DrawingAttributes PenAttributes() => new()
    {
        Color = _penColor,
        Width = _penWidth,
        Height = _penWidth,
        FitToCurve = true,
        IgnorePressure = false,
        StylusTip = StylusTip.Ellipse,
    };

    /// <summary>Diametri (in pixel) della gomma precisa: piccola, media, grande.</summary>
    public static readonly double[] EraserSizes = { 8, 22, 50 };

    double _eraserSize = 22;

    public double EraserSize
    {
        get => _eraserSize;
        set
        {
            _eraserSize = value;
            if (_tool == EditorTool.EraserPoint) ApplyTool();
        }
    }

    readonly System.Windows.Shapes.Ellipse _eraserRing = new()
    {
        StrokeThickness = 2,
        Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0x60, 0x60, 0x60)),
        Fill = new SolidColorBrush(Color.FromArgb(0x25, 0x80, 0x80, 0x80)),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    void MoveEraserRing(Point p)
    {
        Canvas.SetLeft(_eraserRing, p.X - _eraserRing.Width / 2);
        Canvas.SetTop(_eraserRing, p.Y - _eraserRing.Height / 2);
    }

    /// <summary>Diametro della gomma per il palmo: la larghezza del contatto (se la LIM la comunica), tra 40 e 120 px.</summary>
    double PalmEraserSize(StylusEventArgs e)
    {
        try
        {
            var pts = e.GetStylusPoints(Ink);
            var desc = pts.Description;
            if (pts.Count > 0 && desc.HasProperty(StylusPointProperties.Width))
            {
                var info = desc.GetPropertyInfo(StylusPointProperties.Width);
                double perUnitMm = info.Unit switch
                {
                    StylusPointPropertyUnit.Centimeters => 10,
                    StylusPointPropertyUnit.Inches => 25.4,
                    _ => 0,
                };
                if (info.Resolution > 0 && perUnitMm > 0)
                {
                    double mm = pts.Max(p => p.GetPropertyValue(StylusPointProperties.Width)) / info.Resolution * perUnitMm;
                    return Math.Clamp(mm * 96 / 25.4 * 1.3, 40, 120);
                }
            }
        }
        catch
        {
            // misura non disponibile
        }
        return 60;
    }

    /// <summary>L'evidenziatore di WPF è già semitrasparente; con l'alfa lo rendiamo ancora più leggero.</summary>
    static Color HighlighterTint(Color c) => Color.FromArgb(0x8C, c.R, c.G, c.B);

    DrawingAttributes HighlighterAttributes() => new()
    {
        Color = _highlighterColor,
        IsHighlighter = true,
        StylusTip = StylusTip.Rectangle,
        Width = Math.Max(3, _highlighterWidth),
        Height = _highlighterWidth * 3 + 12,
        IgnorePressure = true,
        FitToCurve = true,
    };

    DrawingAttributes GeometryAttributes()
    {
        var da = PenAttributes();
        da.FitToCurve = false;
        da.IgnorePressure = true;
        return da;
    }

    public void SetColor(Color c)
    {
        if (_tool == EditorTool.Select && RestyleSelection(c, null)) return;
        if (_tool == EditorTool.Highlighter) _highlighterColor = c;
        else _penColor = c;
        ApplyDrawingAttributes();
    }

    public void SetWidth(double w)
    {
        if (_tool == EditorTool.Select && RestyleSelection(null, w)) return;
        if (_tool == EditorTool.Highlighter) _highlighterWidth = w;
        else _penWidth = w;
        ApplyDrawingAttributes();
    }

    /// <summary>Imposta il colore della penna senza toccare la selezione (es. al cambio di sfondo).</summary>
    public void SetPenColor(Color c)
    {
        _penColor = c;
        ApplyDrawingAttributes();
    }

    public Color PenColor => _penColor;

    /// <summary>Attiva una penna (o un evidenziatore) con il suo colore e spessore.</summary>
    /// <summary>Attiva la matita con la durezza (0 = 3H … 6 = 3B) e lo spessore indicati.</summary>
    public void UsePencil(int grade, double width)
    {
        _pencilGrade = Pencil.Clamp(grade);
        _pencilWidth = width;
        if (_tool == EditorTool.Pencil) ApplyDrawingAttributes();
        Tool = EditorTool.Pencil;
    }

    public void UsePen(bool highlighter, Color color, double width)
    {
        if (highlighter)
        {
            _highlighterColor = color;
            _highlighterWidth = width;
        }
        else
        {
            _penColor = color;
            _penWidth = width;
        }
        ApplyDrawingAttributes();
        Tool = highlighter ? EditorTool.Highlighter : EditorTool.Pen;
    }

    /// <summary>Nello strumento Seleziona, applica colore e spessore ai tratti selezionati.</summary>
    public bool RestyleSelectionWith(Color color, double width) =>
        _tool == EditorTool.Select && RestyleSelection(color, width);

    bool RestyleSelection(Color? color, double? width)
    {
        var strokes = Ink.GetSelectedStrokes();
        if (strokes.Count == 0) return false;
        foreach (var s in strokes)
        {
            var da = s.DrawingAttributes.Clone();
            if (color is { } c) da.Color = da.IsHighlighter ? HighlighterTint(c) : c;
            if (width is { } w)
            {
                if (da.IsHighlighter)
                {
                    da.Width = Math.Max(3, w);
                    da.Height = w * 3 + 12;
                }
                else
                {
                    da.Width = da.Height = w;
                }
            }
            s.DrawingAttributes = da;
        }
        MarkChanged();
        CommitPending();
        return true;
    }

    // ───────────────────────────── Modifiche e annulla ─────────────────────────────

    void Strokes_Changed(object? sender, StrokeCollectionChangedEventArgs e)
    {
        // La gomma precisa spezza i tratti in tratti "normali": quelli a matita vanno ripristinati.
        if (!_loading && e.Added.Any(s => s is not PencilStroke && Pencil.GradeOf(s.DrawingAttributes) != null))
            Dispatcher.BeginInvoke(() =>
            {
                if (Pencil.NeedsUpgrade(Ink.Strokes)) Pencil.UpgradeAll(Ink.Strokes);
            }, DispatcherPriority.Background);
        MarkChanged();
    }

    void MarkChanged()
    {
        if (_loading || _node == null) return;
        _pending = true;
        _commitTimer.Stop();
        _commitTimer.Start();
        Modified?.Invoke(this, EventArgs.Empty);
        QueueCanvasSizeUpdate();
    }

    void DeferCommit() => Dispatcher.BeginInvoke(CommitPending, DispatcherPriority.Background);

    public void CommitPending()
    {
        if (!_pending) return;
        _pending = false;
        _commitTimer.Stop();
        _undo.Push(Capture());
    }

    PageSnapshot Capture() => new(Ink.Strokes.Clone(), CaptureElements());

    List<ElementData> CaptureElements() =>
        Ink.Children.Cast<UIElement>().Select(ElementFactory.ToData).OfType<ElementData>().ToList();

    void Restore(PageSnapshot s)
    {
        if (_notebook == null) return;
        _loading = true;
        try
        {
            ClearSelection();
            ActiveTextBox = null;
            Ink.Children.Clear();
            Ink.Strokes = s.Strokes.Clone();
            Pencil.UpgradeAll(Ink.Strokes);
            foreach (var el in s.Elements)
                if (ElementFactory.Create(el, _notebook) is { } ui) AddElement(ui);
        }
        finally
        {
            _loading = false;
        }
        ApplyTool();
        UpdateCanvasSize();
        Modified?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        CommitPending();
        if (_undo.Undo() is { } s) Restore(s);
    }

    public void Redo()
    {
        CommitPending();
        if (_undo.Redo() is { } s) Restore(s);
    }

    // ───────────────────────────── Elementi ─────────────────────────────

    void AddElement(UIElement ui)
    {
        if (ui is RichTextBox rtb) HookTextBox(rtb);
        if (ui is FrameworkElement fe) fe.SizeChanged += (_, _) => QueueCanvasSizeUpdate();
        Ink.Children.Add(ui);
        ApplyInteractivity(ui);
    }

    void HookTextBox(RichTextBox rtb)
    {
        rtb.TextChanged += (_, _) => MarkChanged();
        rtb.GotKeyboardFocus += (_, _) =>
        {
            ActiveTextBox = rtb;
            rtb.BorderBrush = FocusBorder;
        };
        rtb.LostKeyboardFocus += (_, _) =>
        {
            rtb.BorderBrush = Brushes.Transparent;
            Dispatcher.BeginInvoke(() => RemoveIfEmpty(rtb), DispatcherPriority.Background);
        };
    }

    void RemoveIfEmpty(RichTextBox rtb)
    {
        if (rtb.IsKeyboardFocusWithin || !Ink.Children.Contains(rtb) || !ElementFactory.IsEmpty(rtb)) return;
        Ink.Children.Remove(rtb);
        if (ActiveTextBox == rtb) ActiveTextBox = null;
        MarkChanged();
    }

    RichTextBox CreateTextBoxAt(Point p)
    {
        var rtb = ElementFactory.CreateTextBox(TextFontFamily, TextFontSize, IsDarkBackground ? Brushes.White : Brushes.Black);
        InkCanvas.SetLeft(rtb, Math.Max(0, p.X - 6));
        InkCanvas.SetTop(rtb, Math.Max(0, p.Y - TextFontSize * 0.7));
        AddElement(rtb);
        Dispatcher.BeginInvoke(() =>
        {
            rtb.Focus();
            Keyboard.Focus(rtb);
        }, DispatcherPriority.Input);
        return rtb;
    }

    public void InsertImage(byte[] bytes, Point? at = null)
    {
        if (_notebook == null || _node == null) return;
        BitmapSource bmp;
        try
        {
            bmp = ElementFactory.LoadBitmap(bytes);
        }
        catch
        {
            MessageBox.Show(Window.GetWindow(this), "Il formato dell'immagine non è supportato.", "Ardesia",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var id = Guid.NewGuid().ToString("N");
        _notebook.Media[id] = bytes;
        double w = bmp.PixelWidth, h = bmp.PixelHeight;
        double scale = Math.Min(1, Math.Min(720 / w, 540 / h));
        var img = ElementFactory.CreateImage(bmp, id, w * scale, h * scale);
        var pos = at ?? VisibleOrigin();
        InkCanvas.SetLeft(img, pos.X);
        InkCanvas.SetTop(img, pos.Y);
        AddElement(img);
        MarkChanged();
        CommitPending();
        Tool = EditorTool.Select;
        Ink.Select(new StrokeCollection(), new UIElement[] { img });
    }

    /// <summary>
    /// Apre GeoGebra: con un grafico, per modificarlo; altrimenti per uno nuovo, prendendo le funzioni
    /// dalla casella di testo selezionata (o in scrittura), e lo mette accanto ad essa.
    /// </summary>
    public void OpenGraph(GraphImage? existing = null, FunctionGraph? from = null)
    {
        if (_notebook == null || _node == null) return;
        CommitPending();
        existing ??= from == null ? Ink.GetSelectedElements().OfType<GraphImage>().FirstOrDefault() : null;

        // le funzioni vengono da un grafico di Ardesia («Apri in GeoGebra») o dalla casella di testo selezionata
        FrameworkElement? source = null;
        string? formulas = null;
        if (from != null)
        {
            source = from;
            formulas = from.State.FunctionsText;
        }
        else if (existing == null && (SelectedTextBoxes().FirstOrDefault() ?? (HasActiveTextBox ? ActiveTextBox : null)) is { } box)
        {
            source = box;
            formulas = new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;
        }

        var win = new GeoGebraWindow(existing?.State, formulas, from?.State.Params) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true || win.Png is not { } png || win.State is not { } state) return;

        BitmapSource bmp;
        try
        {
            bmp = ElementFactory.LoadBitmap(png);
        }
        catch
        {
            return;
        }
        var id = Guid.NewGuid().ToString("N");
        _notebook.Media[id] = png;
        // L'immagine è a doppia risoluzione: a metà dimensione ha la grandezza vista in GeoGebra.
        double w = bmp.PixelWidth / 2.0, h = bmp.PixelHeight / 2.0;

        if (existing != null && Ink.Children.Contains(existing))
        {
            // Stessa posizione e larghezza; l'altezza segue le nuove proporzioni.
            double width = double.IsNaN(existing.Width) ? existing.ActualWidth : existing.Width;
            existing.Source = bmp;
            existing.Tag = id;
            existing.State = state;
            existing.Width = width;
            existing.Height = width * h / w;
            MarkChanged();
            CommitPending();
            Tool = EditorTool.Select;
            Ink.Select(new StrokeCollection(), new UIElement[] { existing });
            return;
        }

        double scale = Math.Min(1, Math.Min(720 / w, 540 / h));
        var graph = ElementFactory.CreateGraph(bmp, id, state, w * scale, h * scale);
        var pos = VisibleOrigin();
        if (source != null && Ink.Children.Contains(source))
        {
            double sx = InkCanvas.GetLeft(source), sy = InkCanvas.GetTop(source);
            pos = new Point((double.IsNaN(sx) ? 0 : sx) + source.ActualWidth + 24, double.IsNaN(sy) ? 0 : sy);
        }
        InkCanvas.SetLeft(graph, pos.X);
        InkCanvas.SetTop(graph, pos.Y);
        AddElement(graph);
        MarkChanged();
        CommitPending();
        Tool = EditorTool.Select;
        Ink.Select(new StrokeCollection(), new UIElement[] { graph });
    }

    /// <summary>
    /// Grafico di Ardesia: con un grafico selezionato apre le sue funzioni; altrimenti ne crea uno nuovo con le
    /// funzioni della casella di testo selezionata (una per riga) o scritte a mano nei tratti selezionati, e lo
    /// mette accanto, o chiede le funzioni.
    /// </summary>
    public void InsertGraph()
    {
        if (_notebook == null || _node == null) return;
        CommitPending();
        if (Ink.GetSelectedElements().OfType<FunctionGraph>().FirstOrDefault() is { } selected)
        {
            EditGraphFunctions(selected);
            return;
        }

        var source = SelectedTextBoxes().FirstOrDefault() ?? (HasActiveTextBox ? ActiveTextBox : null);
        if (source == null && Ink.GetSelectedStrokes().Count > 0)
        {
            InsertGraphFromHandwriting(Ink.GetSelectedStrokes().Clone());
            return;
        }
        IReadOnlyList<string> lines = source == null
            ? Array.Empty<string>()
            : new TextRange(source.Document.ContentStart, source.Document.ContentEnd).Text
                .Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        IReadOnlyList<GraphFunctionData>? functions = null;
        if (lines.Count == 0)
        {
            var dlg = new GraphFunctionsDialog(Array.Empty<GraphFunctionData>()) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.Functions.Count == 0) return;
            functions = dlg.Functions;
            source = null;
        }

        var state = new GraphState();
        if (functions != null) state.SetFunctions(functions);
        else state.SetFunctions(lines);
        var pos = VisibleOrigin();
        if (source != null && Ink.Children.Contains(source))
        {
            // accanto al testo scritto, non al riquadro della casella (largo 520 px anche con poche parole)
            var b = TextBounds(source);
            double sx = InkCanvas.GetLeft(source), sy = InkCanvas.GetTop(source);
            double right = b.IsEmpty ? (double.IsNaN(sx) ? 0 : sx) + source.ActualWidth : b.Right;
            pos = new Point(right + 24, double.IsNaN(sy) ? 0 : sy);
        }
        PlaceNewGraph(state, pos);
    }

    /// <summary>
    /// Grafico dalle funzioni scritte a mano: il riconoscitore di Windows legge i tratti (una funzione per riga),
    /// il docente controlla e corregge nella finestrella, e il grafico va accanto alla scrittura, che resta com'è.
    /// </summary>
    async void InsertGraphFromHandwriting(StrokeCollection strokes)
    {
        var owner = Window.GetWindow(this);
        if (!HandwritingMath.IsAvailable)
        {
            MessageBox.Show(owner, HandwritingMath.NotAvailableMessage, "Ardesia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        IReadOnlyList<string> texts;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            texts = (await HandwritingMath.RecognizeLinesAsync(strokes, owner)).Where(t => t.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            texts = Array.Empty<string>();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        string note = texts.Count > 0
            ? "Ho letto così quello che hai scritto: controlla, correggi se serve, poi «Disegna»."
            : "Non sono riuscito a leggere la scrittura: scrivi la funzione qui sotto, oppure usa «Scrivi a mano».";
        var dlg = new GraphFunctionsDialog(texts.Select(t => new GraphFunctionData { Text = t, Color = "" }), note) { Owner = owner };
        if (dlg.ShowDialog() != true || dlg.Functions.Count == 0) return;

        var state = new GraphState();
        state.SetFunctions(dlg.Functions);
        var b = strokes.GetBounds();
        PlaceNewGraph(state, new Point(b.Right + 24, b.Top));
    }

    /// <summary>Mette sul foglio un grafico nuovo e lo seleziona.</summary>
    void PlaceNewGraph(GraphState state, Point pos)
    {
        var graph = ElementFactory.CreateFunctionGraph(state, 600, 450);
        graph.ResetView();
        InkCanvas.SetLeft(graph, pos.X);
        InkCanvas.SetTop(graph, pos.Y);
        AddElement(graph);
        MarkChanged();
        CommitPending();
        Tool = EditorTool.Select;
        Ink.Select(new StrokeCollection(), new UIElement[] { graph });
    }

    /// <summary>Finestrella con le funzioni del grafico, da correggere o aggiungere.</summary>
    public void EditGraphFunctions(FunctionGraph graph)
    {
        CommitPending();
        var dlg = new GraphFunctionsDialog(graph.State.Functions) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        graph.State.SetFunctions(dlg.Functions);
        graph.Refresh();
        GraphChanged();
    }

    /// <summary>Dopo una modifica allo stato di un grafico: si salva e si può annullare.</summary>
    void GraphChanged()
    {
        MarkChanged();
        CommitPending();
    }

    /// <summary>Voci del menu contestuale di un grafico di Ardesia; le caselle lasciano il menu aperto.</summary>
    void AddGraphMenuItems(ContextMenu menu, FunctionGraph graph)
    {
        menu.Items.Add(MenuEntry("Funzioni del grafico…", "", () => EditGraphFunctions(graph)));
        var show = graph.State.Show;
        void Toggle(string header, Func<bool> get, Action<bool> set)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = get(),
                StaysOpenOnClick = true,
                Padding = new Thickness(6, 7, 6, 7),
            };
            item.Click += (_, _) =>
            {
                set(item.IsChecked);
                graph.InvalidateVisual();
                GraphChanged();
            };
            menu.Items.Add(item);
        }
        Toggle("Zeri", () => show.Zeros, v => show.Zeros = v);
        Toggle("Intersezioni", () => show.Intersections, v => show.Intersections = v);
        Toggle("Massimi e minimi", () => show.Extrema, v => show.Extrema = v);
        Toggle("Derivata", () => show.Derivative, v => show.Derivative = v);
        Toggle("Segno della funzione", () => show.Sign, v => show.Sign = v);
        Toggle("Griglia", () => show.Grid, v => show.Grid = v);
        Toggle("Tacche in π", () => show.Pi, v => show.Pi = v);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Ingrandisci il grafico", "", () => { graph.Zoom(1 / 1.5); GraphChanged(); }));
        menu.Items.Add(MenuEntry("Rimpicciolisci il grafico", "", () => { graph.Zoom(1.5); GraphChanged(); }));
        menu.Items.Add(MenuEntry("Vista standard", "", () => { graph.ResetView(); GraphChanged(); }));
        menu.Items.Add(MenuEntry("Apri in GeoGebra", "", () => OpenGraph(from: graph)));
        menu.Items.Add(new Separator());
    }

    public void BeginTextAtVisibleArea()
    {
        Tool = EditorTool.Text;
        CreateTextBoxAt(VisibleOrigin() + new Vector(6, TextFontSize * 0.7));
    }

    // ───────────────────────────── Formattazione del testo ─────────────────────────────

    IReadOnlyList<RichTextBox> SelectedTextBoxes() =>
        Ink.EditingMode == InkCanvasEditingMode.Select
            ? Ink.GetSelectedElements().OfType<RichTextBox>().ToList()
            : Array.Empty<RichTextBox>();

    bool HasActiveTextBox => ActiveTextBox != null && Ink.Children.Contains(ActiveTextBox);

    public void ApplyTextProperty(DependencyProperty prop, object value)
    {
        var selected = SelectedTextBoxes();
        if (selected.Count > 0)
        {
            foreach (var r in selected)
                new TextRange(r.Document.ContentStart, r.Document.ContentEnd).ApplyPropertyValue(prop, value);
            MarkChanged();
            return;
        }
        if (HasActiveTextBox)
        {
            ActiveTextBox!.Selection.ApplyPropertyValue(prop, value);
            ActiveTextBox.Focus();
            MarkChanged();
        }
    }

    public void ToggleBold() => ToggleTextProperty(TextElement.FontWeightProperty,
        v => v is FontWeight fw && fw == FontWeights.Bold, FontWeights.Bold, FontWeights.Normal);

    public void ToggleItalic() => ToggleTextProperty(TextElement.FontStyleProperty,
        v => v is FontStyle fs && fs == FontStyles.Italic, FontStyles.Italic, FontStyles.Normal);

    public void ToggleUnderline() => ToggleTextProperty(Inline.TextDecorationsProperty,
        v => v is TextDecorationCollection { Count: > 0 }, TextDecorations.Underline, new TextDecorationCollection());

    void ToggleTextProperty(DependencyProperty prop, Func<object, bool> isOn, object on, object off)
    {
        object? current = null;
        var selected = SelectedTextBoxes();
        if (selected.Count > 0)
            current = new TextRange(selected[0].Document.ContentStart, selected[0].Document.ContentEnd).GetPropertyValue(prop);
        else if (HasActiveTextBox)
            current = ActiveTextBox!.Selection.GetPropertyValue(prop);
        else
            return;
        ApplyTextProperty(prop, current != null && current != DependencyProperty.UnsetValue && isOn(current) ? off : on);
    }

    // ───────────────────────────── Selezione e appunti ─────────────────────────────

    void ClearSelection()
    {
        if (Ink.EditingMode == InkCanvasEditingMode.Select)
            Ink.Select(new StrokeCollection(), Array.Empty<UIElement>());
    }

    public bool HasSelection => Ink.EditingMode == InkCanvasEditingMode.Select &&
                                (Ink.GetSelectedStrokes().Count > 0 || Ink.GetSelectedElements().Count > 0);

    public void SelectAll()
    {
        Tool = EditorTool.Select;
        Ink.Select(Ink.Strokes, Ink.Children.Cast<UIElement>());
    }

    public bool DeleteSelection()
    {
        if (!HasSelection) return false;
        var strokes = Ink.GetSelectedStrokes();
        var els = Ink.GetSelectedElements().ToList();
        ClearSelection();
        Ink.Strokes.Remove(strokes);
        foreach (var el in els) Ink.Children.Remove(el);
        MarkChanged();
        CommitPending();
        return true;
    }

    ClipData? ClipFromSelection()
    {
        if (!HasSelection || _notebook == null) return null;
        var strokes = Ink.GetSelectedStrokes().Clone();
        var els = Ink.GetSelectedElements().Select(ElementFactory.ToData).OfType<ElementData>().ToList();
        var media = els.Where(e => e.MediaId != null && _notebook.Media.ContainsKey(e.MediaId))
                       .Select(e => e.MediaId!).Distinct()
                       .ToDictionary(id => id, id => _notebook.Media[id]);
        return new ClipData(Guid.NewGuid().ToString("N"), strokes, els, media);
    }

    public void Copy(bool cut)
    {
        if (ClipFromSelection() is not { } clip) return;
        _clip = clip;
        _pasteCount = 0;
        try
        {
            var data = new DataObject();
            data.SetData(ClipFormat, clip.Token);
            if (clip.Strokes.Count > 0)
            {
                var ms = new MemoryStream();
                clip.Strokes.Save(ms);
                ms.Position = 0;
                data.SetData(StrokeCollection.InkSerializedFormat, ms);
            }
            // Una sola immagine copiata: la mettiamo negli appunti anche come immagine, per incollarla in altre app.
            if (clip.Strokes.Count == 0 && clip.Elements.Count == 1 && clip.Elements[0].MediaId is { } id)
                data.SetImage(ElementFactory.LoadBitmap(clip.Media[id]));
            Clipboard.SetDataObject(data, true);
        }
        catch
        {
            // appunti di sistema occupati: resta comunque la copia interna
        }
        if (cut) DeleteSelection();
    }

    /// <summary>Duplica la selezione poco più in basso a destra, senza toccare gli appunti.</summary>
    public void DuplicateSelection()
    {
        if (ClipFromSelection() is not { } clip) return;
        PasteInternal(clip, null, 24);
    }

    public bool CanPaste
    {
        get
        {
            try
            {
                return Clipboard.ContainsImage() || Clipboard.ContainsText() ||
                       Clipboard.ContainsData(ClipFormat) || Clipboard.ContainsData(StrokeCollection.InkSerializedFormat);
            }
            catch
            {
                return _clip != null;
            }
        }
    }

    /// <summary>Incolla; se è indicato un punto, il contenuto viene messo lì.</summary>
    public void Paste(Point? at = null)
    {
        if (_notebook == null || _node == null) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return;
            if (_clip != null && data.GetDataPresent(ClipFormat) && data.GetData(ClipFormat) as string == _clip.Token)
            {
                _pasteCount++;
                PasteInternal(_clip, at, 24 * _pasteCount);
                return;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            {
                InsertImage(ElementFactory.EncodePng(img), at);
                return;
            }
            if (data.GetDataPresent(StrokeCollection.InkSerializedFormat) &&
                data.GetData(StrokeCollection.InkSerializedFormat) is Stream isf)
            {
                PasteInternal(new ClipData("", new StrokeCollection(isf), new(), new()), at, 24);
                return;
            }
            if (Clipboard.ContainsText())
            {
                var rtb = CreateTextBoxAt(at ?? VisibleOrigin() + new Vector(6, TextFontSize * 0.7));
                ElementFactory.SetPlainText(rtb, Clipboard.GetText());
                Tool = EditorTool.Text;
                MarkChanged();
            }
        }
        catch
        {
            // appunti non leggibili
        }
    }

    /// <summary>Aggiunge alla pagina il contenuto copiato, lo seleziona e attiva Seleziona: si può trascinare subito.</summary>
    void PasteInternal(ClipData clip, Point? at, double shift)
    {
        var bounds = clip.Strokes.Count > 0 ? clip.Strokes.GetBounds() : Rect.Empty;
        foreach (var el in clip.Elements) bounds.Union(new Rect(el.X, el.Y, Math.Max(1, el.Width), Math.Max(1, el.Height)));
        var offset = at is { } p && !bounds.IsEmpty
            ? new Vector(p.X - bounds.X, p.Y - bounds.Y)
            : new Vector(shift, shift);

        var strokes = clip.Strokes.Clone();
        strokes.Transform(new Matrix(1, 0, 0, 1, offset.X, offset.Y), false);
        Pencil.UpgradeAll(strokes);
        foreach (var (id, bytes) in clip.Media) _notebook!.Media.TryAdd(id, bytes);

        var added = new List<UIElement>();
        foreach (var el in clip.Elements)
        {
            var moved = el with { X = el.X + offset.X, Y = el.Y + offset.Y };
            if (ElementFactory.Create(moved, _notebook!) is { } ui)
            {
                AddElement(ui);
                added.Add(ui);
            }
        }
        Ink.Strokes.Add(strokes);
        MarkChanged();
        CommitPending();
        Tool = EditorTool.Select;
        Ink.Select(strokes, added);
    }

    /// <summary>Porta gli oggetti selezionati sopra (o sotto) gli altri.</summary>
    public void ReorderSelection(bool toFront)
    {
        if (!HasSelection) return;
        var strokes = Ink.GetSelectedStrokes();
        var els = Ink.Children.Cast<UIElement>().Where(c => Ink.GetSelectedElements().Contains(c)).ToList();
        ClearSelection();
        foreach (var el in els) Ink.Children.Remove(el);
        Ink.Strokes.Remove(strokes);
        if (toFront)
        {
            foreach (var el in els) Ink.Children.Add(el);
            Ink.Strokes.Add(strokes);
        }
        else
        {
            for (int i = 0; i < els.Count; i++) Ink.Children.Insert(i, els[i]);
            for (int i = 0; i < strokes.Count; i++) Ink.Strokes.Insert(i, strokes[i]);
        }
        MarkChanged();
        CommitPending();
        Ink.Select(strokes, els);
    }

    // ───────────────────────────── Menu contestuale ─────────────────────────────

    /// <summary>L'oggetto nel punto; una casella di testo conta solo dove c'è davvero il testo (il riquadro è molto più largo).</summary>
    UIElement? ElementAt(Point p) =>
        Ink.Children.Cast<UIElement>().LastOrDefault(c => c switch
        {
            RichTextBox r => TextBounds(r) is { IsEmpty: false } b && b.Contains(p),
            FrameworkElement fe => new Rect(InkCanvas.GetLeft(fe) is var x && double.IsNaN(x) ? 0 : x,
                                            InkCanvas.GetTop(fe) is var y && double.IsNaN(y) ? 0 : y,
                                            fe.ActualWidth, fe.ActualHeight).Contains(p),
            _ => false,
        });

    /// <summary>Posizioni del cursore tra i caratteri di una casella di testo, in coordinate della pagina.</summary>
    static List<Rect> TextCaretRects(RichTextBox rtb)
    {
        var list = new List<Rect>();
        double x0 = InkCanvas.GetLeft(rtb), y0 = InkCanvas.GetTop(rtb);
        if (double.IsNaN(x0)) x0 = 0;
        if (double.IsNaN(y0)) y0 = 0;
        var tp = rtb.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        for (int i = 0; tp != null && i < 3000; i++)
        {
            var r = tp.GetCharacterRect(LogicalDirection.Forward);
            if (!r.IsEmpty && !double.IsInfinity(r.X) && !double.IsInfinity(r.Height))
                list.Add(new Rect(x0 + r.X, y0 + r.Y, 0, r.Height));
            tp = tp.GetNextInsertionPosition(LogicalDirection.Forward);
        }
        return list;
    }

    /// <summary>Il riquadro occupato dal testo scritto (un po' allargato, per prenderlo anche col dito).</summary>
    static Rect TextBounds(RichTextBox rtb)
    {
        if (ElementFactory.IsEmpty(rtb)) return Rect.Empty;
        var b = Rect.Empty;
        foreach (var r in TextCaretRects(rtb)) b.Union(r);
        if (!b.IsEmpty) b.Inflate(8, 4);
        return b;
    }

    // ───── Selezione delle caselle di testo col lazo e con un tocco ─────
    // Il lazo di InkCanvas prende un oggetto solo se ne racchiude quasi tutto il riquadro, e le caselle di testo
    // sono larghe 520 px anche con poche parole: qui una casella si prende se il lazo ne racchiude il testo.

    readonly List<Point> _lasso = new();
    bool _lassoActive;

    void SetupTextSelection()
    {
        void Begin(Point p, DependencyObject? src)
        {
            _lassoActive = _tool == EditorTool.Select && _drag == DragMode.None && !IsWithin(src, TitleArea);
            _lasso.Clear();
            if (_lassoActive) _lasso.Add(p);
        }
        void Add(Point p)
        {
            if (_lassoActive) _lasso.Add(p);
        }
        void End()
        {
            if (!_lassoActive) return;
            _lassoActive = false;
            var points = _lasso.ToList();
            // Dopo che InkCanvas ha fatto la sua selezione.
            Dispatcher.BeginInvoke(() => FinishLasso(points), DispatcherPriority.Background);
        }

        ContentHost.PreviewStylusDown += (_, e) => { if (!e.StylusDevice.Inverted) Begin(e.GetPosition(Ink), e.OriginalSource as DependencyObject); };
        ContentHost.PreviewStylusMove += (_, e) => Add(e.GetPosition(Ink));
        ContentHost.PreviewStylusUp += (_, _) => End();
        ContentHost.PreviewMouseLeftButtonDown += (_, e) => { if (e.StylusDevice == null) Begin(e.GetPosition(Ink), e.OriginalSource as DependencyObject); };
        ContentHost.PreviewMouseMove += (_, e) => { if (e.StylusDevice == null) Add(e.GetPosition(Ink)); };
        ContentHost.PreviewMouseLeftButtonUp += (_, e) => { if (e.StylusDevice == null) End(); };
    }

    // ───── Ridimensionamento della selezione: anteprima del contenuto ─────
    // InkCanvas, mentre si trascina una maniglia, mostra solo il rettangolo e cambia il contenuto al rilascio.
    // Qui si disegna anche una copia semitrasparente del contenuto, deformata come il rettangolo.

    readonly Image _resizePreview = new() { Stretch = Stretch.Fill, Opacity = 0.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    InkCanvasSelectionHitResult _resizeHandle;
    Rect _resizeStartBounds;
    Point _resizeStart;
    int? _resizeStylus;

    void SetupResizePreview()
    {
        Overlay.Children.Add(_resizePreview);
        ContentHost.PreviewStylusDown += (_, e) => { if (!e.StylusDevice.Inverted && BeginResizePreview(e.GetPosition(Ink))) _resizeStylus = e.StylusDevice.Id; };
        ContentHost.PreviewStylusMove += (_, e) => { if (_resizeStylus == e.StylusDevice.Id) ContinueResizePreview(e.GetPosition(Ink)); };
        ContentHost.PreviewStylusUp += (_, _) => EndResizePreview();
        ContentHost.PreviewMouseLeftButtonDown += (_, e) => { if (e.StylusDevice == null) BeginResizePreview(e.GetPosition(Ink)); };
        ContentHost.PreviewMouseMove += (_, e) => { if (e.StylusDevice == null && _resizeStylus == null) ContinueResizePreview(e.GetPosition(Ink)); };
        ContentHost.PreviewMouseLeftButtonUp += (_, _) => EndResizePreview();
        ContentHost.LostMouseCapture += (_, _) => { if (_resizeStylus == null) EndResizePreview(); };
        Ink.SelectionResized += (_, _) => EndResizePreview();
        Ink.SelectionChanged += (_, _) => EndResizePreview();
    }

    bool BeginResizePreview(Point p)
    {
        EndResizePreview();
        if (_tool != EditorTool.Select || _drag != DragMode.None || !HasSelection) return false;
        var handle = Ink.HitTestSelection(p);
        if (handle is InkCanvasSelectionHitResult.None or InkCanvasSelectionHitResult.Selection) return false;
        var bounds = Ink.GetSelectionBounds();
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return false;

        // Il contenuto selezionato in un disegno grande esattamente quanto la selezione.
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, bounds);
            dc.PushClip(new RectangleGeometry(bounds));
            foreach (var el in Ink.GetSelectedElements().OfType<FrameworkElement>())
            {
                var r = new Rect(InkCanvas.GetLeft(el) is var x && double.IsNaN(x) ? 0 : x, TopOf(el), el.ActualWidth, el.ActualHeight);
                if (r.Width > 0 && r.Height > 0) dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.Fill }, null, r);
            }
            Ink.GetSelectedStrokes().Draw(dc);
            dc.Pop();
        }
        _resizePreview.Source = new DrawingImage(group);
        _resizeHandle = handle;
        _resizeStartBounds = bounds;
        _resizeStart = p;
        return true;
    }

    void ContinueResizePreview(Point p)
    {
        if (_resizePreview.Source == null) return;
        var b = _resizeStartBounds;
        double dx = p.X - _resizeStart.X, dy = p.Y - _resizeStart.Y;
        double left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom;
        switch (_resizeHandle)
        {
            case InkCanvasSelectionHitResult.TopLeft: left += dx; top += dy; break;
            case InkCanvasSelectionHitResult.Top: top += dy; break;
            case InkCanvasSelectionHitResult.TopRight: right += dx; top += dy; break;
            case InkCanvasSelectionHitResult.Right: right += dx; break;
            case InkCanvasSelectionHitResult.BottomRight: right += dx; bottom += dy; break;
            case InkCanvasSelectionHitResult.Bottom: bottom += dy; break;
            case InkCanvasSelectionHitResult.BottomLeft: left += dx; bottom += dy; break;
            case InkCanvasSelectionHitResult.Left: left += dx; break;
        }
        if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1 && _resizePreview.Visibility != Visibility.Visible) return;
        var r = new Rect(new Point(left, top), new Point(right, bottom)); // trascinando oltre il lato opposto si capovolge come il rettangolo
        _resizePreview.RenderTransformOrigin = new Point(0.5, 0.5);
        _resizePreview.RenderTransform = new ScaleTransform(right < left ? -1 : 1, bottom < top ? -1 : 1);
        Canvas.SetLeft(_resizePreview, r.Left);
        Canvas.SetTop(_resizePreview, r.Top);
        _resizePreview.Width = Math.Max(1, r.Width);
        _resizePreview.Height = Math.Max(1, r.Height);
        _resizePreview.Visibility = Visibility.Visible;
    }

    void EndResizePreview()
    {
        _resizeStylus = null;
        if (_resizePreview.Source == null) return;
        _resizePreview.Visibility = Visibility.Collapsed;
        _resizePreview.Source = null;
    }

    void FinishLasso(List<Point> points)
    {
        if (_tool != EditorTool.Select || points.Count == 0) return;
        var selected = Ink.GetSelectedElements().ToList();
        double length = 0;
        for (int i = 1; i < points.Count; i++) length += (points[i] - points[i - 1]).Length;

        if (length < 10 / _zoom)
        {
            // Un tocco: se InkCanvas non ha preso niente, si prende l'oggetto sotto il dito.
            if (Ink.GetSelectedStrokes().Count == 0 && selected.Count == 0 && ElementAt(points[0]) is { } element)
                Ink.Select(new StrokeCollection(), new[] { element });
            return;
        }

        var add = Ink.Children.OfType<RichTextBox>().Where(r =>
        {
            if (selected.Contains(r)) return false;
            var carets = TextCaretRects(r);
            if (carets.Count == 0 || ElementFactory.IsEmpty(r)) return false;
            int inside = carets.Count(c => InPolygon(points, new Point(c.X, c.Y + c.Height / 2)));
            return inside >= carets.Count * 0.6;
        }).ToList();
        if (add.Count == 0) return;
        Ink.Select(Ink.GetSelectedStrokes(), selected.Concat(add).ToList());
    }

    static bool InPolygon(IReadOnlyList<Point> poly, Point p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>Se il punto cade su un oggetto non selezionato, lo seleziona (e attiva lo strumento Seleziona).</summary>
    void SelectAt(Point p, double tolerance = 10)
    {
        if (HasSelection && Ink.GetSelectionBounds().Contains(p)) return;
        var stroke = Ink.Strokes.HitTest(p, tolerance).LastOrDefault();
        var element = stroke == null ? ElementAt(p) : null;
        if (stroke == null && element == null)
        {
            ClearSelection();
            return;
        }
        if (_tool != EditorTool.Select) Tool = EditorTool.Select;
        Ink.Select(stroke != null ? new StrokeCollection { stroke } : new StrokeCollection(),
                   element != null ? new[] { element } : Array.Empty<UIElement>());
    }

    static MenuItem MenuEntry(string header, string glyph, Action action, bool enabled = true, string? gesture = null)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = enabled,
            InputGestureText = gesture ?? "",
            Padding = new Thickness(6, 7, 6, 7),
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Menu del tasto destro / pressione prolungata. <paramref name="p"/> è in coordinate della pagina.</summary>
    ContextMenu? _openMenu;

    /// <param name="tolerance">Diametro (px) entro cui un tratto viene «preso»: più largo per dito e penna.</param>
    void ShowContextMenu(Point p, double tolerance = 10)
    {
        if (_node == null) return;
        CommitPending();
        SelectAt(p, tolerance / _zoom);
        bool selection = HasSelection;
        bool canPaste = CanPaste;

        var menu = new ContextMenu { FontSize = 15, MinWidth = 230 };
        if (selection)
        {
            if (Ink.GetSelectedElements().OfType<GraphImage>().FirstOrDefault() is { } graph)
            {
                menu.Items.Add(MenuEntry("Modifica il grafico in GeoGebra", "", () => OpenGraph(graph)));
                menu.Items.Add(new Separator());
            }
            else if (Ink.GetSelectedElements().OfType<FunctionGraph>().FirstOrDefault() is { } fgraph)
            {
                AddGraphMenuItems(menu, fgraph);
            }
            else if (Ink.GetSelectedElements().OfType<RichTextBox>().Any())
            {
                menu.Items.Add(MenuEntry("Disegna il grafico di questa funzione", "", InsertGraph));
                menu.Items.Add(MenuEntry("Disegna il grafico con GeoGebra", "", () => OpenGraph()));
                menu.Items.Add(new Separator());
            }
            else if (Ink.GetSelectedStrokes().Count > 0 && Ink.GetSelectedElements().Count == 0 && HandwritingMath.IsAvailable)
            {
                // tratti scritti a mano: si leggono come funzioni
                menu.Items.Add(MenuEntry("Disegna il grafico della funzione scritta", "", InsertGraph));
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(MenuEntry("Taglia", "", () => Copy(cut: true), gesture: "Ctrl+X"));
            menu.Items.Add(MenuEntry("Copia", "", () => Copy(cut: false), gesture: "Ctrl+C"));
            menu.Items.Add(MenuEntry("Incolla qui", "", () => Paste(p), canPaste, "Ctrl+V"));
            menu.Items.Add(MenuEntry("Duplica", "", DuplicateSelection));
            menu.Items.Add(MenuEntry("Elimina", "", () => DeleteSelection(), gesture: "Canc"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Porta in primo piano", "", () => ReorderSelection(true)));
            menu.Items.Add(MenuEntry("Porta in fondo", "", () => ReorderSelection(false)));
        }
        else
        {
            menu.Items.Add(MenuEntry("Incolla qui", "", () => Paste(p), canPaste, "Ctrl+V"));
            menu.Items.Add(MenuEntry("Elimina (tieni premuto su un oggetto)", "", () => { }, enabled: false));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Scrivi un testo qui", "", () =>
            {
                Tool = EditorTool.Text;
                CreateTextBoxAt(p);
            }));
            menu.Items.Add(MenuEntry("Inserisci un'immagine qui…", "", () => InsertImageFromFile(p)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Seleziona tutto", "", SelectAll, Ink.Strokes.Count > 0 || Ink.Children.Count > 0, "Ctrl+A"));

        // Il menu compare un po' a destra del punto, così il dito non copre la prima voce.
        menu.PlacementTarget = Ink;
        menu.Placement = PlacementMode.RelativePoint;
        menu.HorizontalOffset = p.X * _zoom + 12;
        menu.VerticalOffset = p.Y * _zoom + 4;
        menu.IsOpen = true;
        _openMenu = menu;
    }

    public void InsertImageFromFile(Point? at = null)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Inserisci immagine",
            Filter = "Immagini|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Tutti i file|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            InsertImage(File.ReadAllBytes(dlg.FileName), at);
        }
        catch (IOException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Ardesia", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Pressione prolungata (dito o penna) = tasto destro.
    readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    Point _holdStart;
    int _holdDevice = -1;
    bool _holdFired;
    readonly System.Windows.Shapes.Ellipse _holdRing = new()
    {
        Width = 44,
        Height = 44,
        StrokeThickness = 3,
        Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x1E, 0x5A, 0x68)),
        Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x1E, 0x5A, 0x68)),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };


    void SetupContextMenuInput()
    {
        Stylus.SetIsPressAndHoldEnabled(ContentHost, false); // lo gestiamo noi, anche mentre si scrive
        Overlay.Children.Add(_holdRing);
        _holdTimer.Tick += (_, _) =>
        {
            // Pressione riconosciuta: mostriamo un cerchietto; il menu si apre al rilascio
            // (se si aprisse subito, il sollevamento del dito lo richiuderebbe).
            _holdTimer.Stop();
            _holdFired = true;
            if (_drag == DragMode.MoveSelection) EndDrag(); // tenuto fermo sulla selezione: menu, non spostamento
            StraightCancel();
            Canvas.SetLeft(_holdRing, _holdStart.X - _holdRing.Width / 2);
            Canvas.SetTop(_holdRing, _holdStart.Y - _holdRing.Height / 2);
            _holdRing.Visibility = Visibility.Visible;
        };

        ContentHost.PreviewStylusDown += (_, e) =>
        {
            // Un tocco sulla pagina chiude il menu rimasto aperto (col dito non si chiuderebbe da solo).
            if (_openMenu is { IsOpen: true })
            {
                _openMenu.IsOpen = false;
                _openMenu = null;
            }
            if (_holdTimer.IsEnabled)
            {
                _holdTimer.Stop(); // secondo dito: non è una pressione prolungata
                return;
            }
            var src = e.OriginalSource as DependencyObject;
            if (e.StylusDevice.Inverted) return; // il palmo appoggiato cancella, non apre il menu
            if (_tool is EditorTool.Hand or EditorTool.Space) return; // la pressione serve a scorrere / a trascinare lo spazio
            if (_node == null || _drag != DragMode.None || IsWithin(src, TitleArea) ||
                FindAncestor<DraggableTool>(src) != null || (_tool == EditorTool.Text && FindAncestor<TextBoxBase>(src) != null))
                return;
            _holdStart = e.GetPosition(Ink);
            _holdDevice = e.StylusDevice.Id;
            _holdFired = false;
            _holdTimer.Start();
        };
        ContentHost.PreviewStylusMove += (_, e) =>
        {
            if (e.StylusDevice.Id != _holdDevice || (e.GetPosition(Ink) - _holdStart).Length * _zoom <= 10) return;
            _holdTimer.Stop();
            if (_holdFired)
            {
                // Dopo il cerchietto ci si è spostati: niente menu.
                _holdFired = false;
                _holdRing.Visibility = Visibility.Collapsed;
            }
        };
        ContentHost.PreviewStylusUp += (_, _) =>
        {
            _holdTimer.Stop();
            _holdRing.Visibility = Visibility.Collapsed;
            if (!_holdFired) return;
            // Il menu si apre dopo che il foglio ha finito di gestire il rilascio (tratto, lazo…).
            var at = _holdStart;
            Dispatcher.BeginInvoke(() => ShowContextMenu(at, 36), DispatcherPriority.Background);
        };

        // Tasto destro del mouse (quelli simulati dal tocco li gestiamo sopra).
        ContentHost.PreviewMouseRightButtonUp += (_, e) =>
        {
            if (e.StylusDevice != null || _node == null) return;
            var src = e.OriginalSource as DependencyObject;
            if (IsWithin(src, TitleArea) || FindAncestor<DraggableTool>(src) != null) return;
            if (FindAncestor<RichTextBox>(src) is { IsKeyboardFocusWithin: true }) return; // menu del testo
            ShowContextMenu(e.GetPosition(Ink));
            e.Handled = true;
        };
    }

    // ───────────────────────────── Tratti ─────────────────────────────

    void Ink_StrokeCollected(object? sender, InkCanvasStrokeCollectedEventArgs e)
    {
        // La pressione prolungata ha aperto il menu: il puntino lasciato dalla penna non va tenuto.
        if (_holdFired)
        {
            _holdFired = false;
            var b = e.Stroke.GetBounds();
            if (b.Width < 24 && b.Height < 24)
            {
                Ink.Strokes.Remove(e.Stroke);
                return;
            }
        }
        var replacement = Pencil.Upgrade(StraightenIfHeld(e.Stroke) ?? _ruler.TrySnap(e.Stroke) ?? e.Stroke);
        if (!ReferenceEquals(replacement, e.Stroke))
        {
            Ink.Strokes.Remove(e.Stroke);
            Ink.Strokes.Add(replacement);
        }
    }

    void Ink_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Ink_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var p = e.GetPosition(Ink);
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff")) continue;
            try
            {
                InsertImage(File.ReadAllBytes(f), p);
            }
            catch (IOException)
            {
            }
            p.Offset(24, 24);
        }
        e.Handled = true;
    }

    // ───────────────────────────── Mouse, penna e tocco ─────────────────────────────

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    bool IsWithin(DependencyObject? d, DependencyObject ancestor)
    {
        while (d != null)
        {
            if (d == ancestor) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    void ContentHost_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_node == null || _drag != DragMode.None) return;
        if (e.StylusDevice?.Inverted == true) return; // palmo / gomma: lo gestisce il foglio cancellando
        var src = e.OriginalSource as DependencyObject;
        if (FindAncestor<DraggableTool>(src) != null) return;

        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && _tool == EditorTool.Hand))
        {
            _drag = DragMode.Pan;
            _panStartView = e.GetPosition(Scroller);
            _panStartH = Scroller.HorizontalOffset;
            _panStartV = Scroller.VerticalOffset;
            ContentHost.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        var p = e.GetPosition(Ink);
        bool onTitle = IsWithin(src, TitleArea);
        if (e.StylusDevice == null)
        {
            if (TryBeginRulerLine(p, src))
            {
                ContentHost.CaptureMouse();
                e.Handled = true;
                return;
            }
            if (IsInkTool && !onTitle) StraightBegin(p);
        }
        if (_tool == EditorTool.Select && e.StylusDevice == null && !onTitle && TryBeginGraphSlider(p))
        {
            ContentHost.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (_tool == EditorTool.Select && e.ClickCount == 1 && e.StylusDevice == null && !onTitle && TryBeginGraphPan(p))
        {
            ContentHost.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (_tool == EditorTool.Select && e.ClickCount == 1 && e.StylusDevice == null && !onTitle && TryBeginMove(p))
        {
            ContentHost.CaptureMouse();
            e.Handled = true;
            return;
        }
        switch (_tool)
        {
            case EditorTool.Text:
                if (onTitle || FindAncestor<TextBoxBase>(src) != null) return;
                // Un clic su un'immagine la seleziona (per spostarla o ridimensionarla) invece di creare un testo.
                if (ElementAt(p) is (Image or FunctionGraph) and var image)
                {
                    Tool = EditorTool.Select;
                    Ink.Select(new StrokeCollection(), new UIElement[] { image });
                    e.Handled = true;
                    break;
                }
                CreateTextBoxAt(p);
                e.Handled = true;
                break;

            case EditorTool.Space:
                if (onTitle) return;
                BeginSpace(p);
                ContentHost.CaptureMouse();
                e.Handled = true;
                break;

            case EditorTool.Shape:
                if (onTitle) return;
                _drag = DragMode.Shape;
                _dragStart = p;
                UpdateShapePreview(p);
                ContentHost.CaptureMouse();
                e.Handled = true;
                break;

            case EditorTool.Compass:
                if (onTitle) return;
                switch (_compass.HitPart(p))
                {
                    case CompassTool.Part.Needle:
                    case CompassTool.Part.Body:
                        _drag = DragMode.CompassMove;
                        _grabOffset = _compass.Center - p;
                        break;
                    case CompassTool.Part.Pencil:
                        _drag = DragMode.CompassRadius;
                        break;
                    default:
                        _drag = DragMode.CompassDraw;
                        _arcStroke = null;
                        _arcPrevAngle = Math.Atan2(p.Y - _compass.Center.Y, p.X - _compass.Center.X);
                        break;
                }
                ContentHost.CaptureMouse();
                e.Handled = true;
                break;

            case EditorTool.Select when e.ClickCount == 2:
                if (ElementAt(p) is GraphImage graph)
                {
                    e.Handled = true;
                    OpenGraph(graph);
                    break;
                }
                if (ElementAt(p) is FunctionGraph fgraph)
                {
                    e.Handled = true;
                    EditGraphFunctions(fgraph);
                    break;
                }
                var rtb = Ink.Children.OfType<RichTextBox>().LastOrDefault(r =>
                    new Rect(InkCanvas.GetLeft(r), InkCanvas.GetTop(r), r.ActualWidth, r.ActualHeight).Contains(p));
                if (rtb != null)
                {
                    Tool = EditorTool.Text;
                    rtb.Focus();
                    if (rtb.GetPositionFromPoint(e.GetPosition(rtb), true) is { } caret) rtb.CaretPosition = caret;
                    e.Handled = true;
                }
                break;
        }
    }

    void ContentHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            if (e.StylusDevice == null && e.LeftButton == MouseButtonState.Pressed) StraightMove(e.GetPosition(Ink));
            if (e.StylusDevice == null && e.LeftButton == MouseButtonState.Released) UpdateGraphHover(e.GetPosition(Ink));
            return;
        }
        var p = e.GetPosition(Ink);
        switch (_drag)
        {
            case DragMode.Pan:
                var v = e.GetPosition(Scroller);
                ScrollToOffset(_panStartH - (v.X - _panStartView.X), _panStartV - (v.Y - _panStartView.Y));
                break;
            case DragMode.Shape:
                UpdateShapePreview(p);
                break;
            case DragMode.CompassMove:
                _compass.Set(p + _grabOffset, _compass.Radius, _compass.AngleRad);
                break;
            case DragMode.CompassRadius:
                var r = p - _compass.Center;
                _compass.Set(_compass.Center, r.Length, Math.Atan2(r.Y, r.X));
                break;
            case DragMode.CompassDraw:
                ContinueArc(p);
                break;
            case DragMode.MoveSelection:
                ContinueMove(p);
                break;
            case DragMode.RulerLine:
                ContinueRulerLine(p);
                break;
            case DragMode.GraphPan:
                ContinueGraphPan(p);
                break;
            case DragMode.GraphSlider:
                ContinueGraphSlider(p);
                break;
            case DragMode.Space:
                ContinueSpace(p);
                break;
        }
        e.Handled = true;
    }

    void ContentHost_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            DeferCommit();
            return;
        }
        if (_drag == DragMode.Shape) FinishShape();
        EndDrag();
        e.Handled = true;
    }

    void EndDrag()
    {
        if (_drag == DragMode.None) return;
        var ended = _drag;
        _drag = DragMode.None;
        _shapePreview.Visibility = Visibility.Collapsed;
        _arcStroke = null;
        if (ContentHost.IsMouseCaptured) ContentHost.ReleaseMouseCapture();
        if (ContentHost.IsStylusCaptured) ContentHost.ReleaseStylusCapture();
        if (ended == DragMode.MoveSelection) EndMove();
        if (ended == DragMode.RulerLine) EndRulerLine();
        if (ended == DragMode.GraphPan) EndGraphPan();
        if (ended == DragMode.GraphSlider) EndGraphSlider();
        if (ended == DragMode.Space) EndSpace();
        DeferCommit();
    }

    // ───── Aiuti al disegno: righello «dal vivo» e linea che si raddrizza tenendo premuto ─────

    bool IsInkTool => _tool is EditorTool.Pen or EditorTool.Highlighter or EditorTool.Pencil;

    readonly DispatcherTimer _straightTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    readonly WpfPath _straightPreview = new()
    {
        IsHitTestVisible = false,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Visibility = Visibility.Collapsed,
    };
    readonly List<Point> _straightPts = new();
    bool _straightActive, _straightReady;
    Point _straightAnchor, _straightStart;
    StylusDevice? _straightDevice;

    Stroke? _rulerStroke;
    int _rulerSide;
    double _rulerStartX;

    void SetupDrawingAids()
    {
        Overlay.Children.Add(_straightPreview);
        _straightTimer.Tick += (_, _) =>
        {
            // Penna ferma su una linea quasi dritta: la linea storta sparisce e diventa subito dritta.
            _straightTimer.Stop();
            if (!_straightActive || !StraightSegment(_straightPts, out var a, out var b)) return;
            _straightReady = true;
            _straightStart = a;
            Ink.HideLiveStroke(_straightDevice, a);
            var da = Ink.DefaultDrawingAttributes;
            int? grade = Pencil.GradeOf(da);
            _straightPreview.Stroke = grade is { } g ? Pencil.BrushFor(da.Color, g)
                : new SolidColorBrush(da.IsHighlighter ? Color.FromArgb(0x70, da.Color.R, da.Color.G, da.Color.B) : da.Color);
            _straightPreview.StrokeThickness = Math.Max(1, da.IsHighlighter ? da.Height : da.Width);
            _straightPreview.StrokeStartLineCap = _straightPreview.StrokeEndLineCap = da.IsHighlighter ? PenLineCap.Flat : PenLineCap.Round;
            _straightPreview.Data = new LineGeometry(a, b);
            _straightPreview.Visibility = Visibility.Visible;
        };

        ContentHost.PreviewStylusDown += (_, e) =>
        {
            if (e.StylusDevice.Inverted) return;
            var src = e.OriginalSource as DependencyObject;
            var p = e.GetPosition(Ink);
            if (TryBeginRulerLine(p, src))
            {
                e.StylusDevice.Capture(ContentHost);
                e.Handled = true; // il tratto lo disegniamo noi, dritto lungo il bordo
                return;
            }
            if (IsInkTool && !IsWithin(src, TitleArea) && FindAncestor<DraggableTool>(src) == null) StraightBegin(p, e.StylusDevice);
        };
        ContentHost.PreviewStylusMove += (_, e) =>
        {
            if (_drag == DragMode.RulerLine)
            {
                ContinueRulerLine(e.GetPosition(Ink));
                e.Handled = true;
                return;
            }
            StraightMove(e.GetPosition(Ink));
        };
        ContentHost.PreviewStylusUp += (_, e) =>
        {
            if (_drag != DragMode.RulerLine) return;
            EndDrag();
            e.Handled = true;
        };
    }

    /// <summary>Inizio di un tratto vicino al bordo del righello: la linea viene disegnata già dritta e appoggiata al bordo.</summary>
    bool TryBeginRulerLine(Point p, DependencyObject? src)
    {
        if (!IsInkTool || _node == null || _drag != DragMode.None) return false;
        if (FindAncestor<DraggableTool>(src) != null || IsWithin(src, TitleArea)) return false;
        if (!_ruler.TryEdgeAt(p, 36 / _zoom, out _rulerSide, out _rulerStartX)) return false;

        var da = Ink.DefaultDrawingAttributes.Clone();
        da.FitToCurve = false;
        da.IgnorePressure = true;
        var pts = RulerPoints(_rulerStartX, da);
        _rulerStroke = Pencil.GradeOf(da) != null ? new PencilStroke(pts, da) : new Stroke(pts, da);
        Ink.Strokes.Add(_rulerStroke);
        _drag = DragMode.RulerLine;
        StraightCancel();
        return true;
    }

    StylusPointCollection RulerPoints(double toX, DrawingAttributes da)
    {
        double offset = (da.IsHighlighter ? da.Height : da.Width) / 2 + 0.5;
        var a = _ruler.EdgePoint(_rulerSide, _rulerStartX, offset);
        var b = _ruler.EdgePoint(_rulerSide, toX, offset);
        return new StylusPointCollection(new[] { new StylusPoint(a.X, a.Y), new StylusPoint(b.X, b.Y) });
    }

    void ContinueRulerLine(Point p)
    {
        if (_rulerStroke != null) _rulerStroke.StylusPoints = RulerPoints(_ruler.LocalX(p), _rulerStroke.DrawingAttributes);
    }

    void EndRulerLine()
    {
        if (_rulerStroke != null)
        {
            var sp = _rulerStroke.StylusPoints;
            double len = new Vector(sp[sp.Count - 1].X - sp[0].X, sp[sp.Count - 1].Y - sp[0].Y).Length;
            if (len < 2) Ink.Strokes.Remove(_rulerStroke); // solo un tocco: niente linea
            else MarkChanged();
        }
        _rulerStroke = null;
    }

    void StraightBegin(Point p, StylusDevice? device = null)
    {
        if (!IsInkTool || _node == null) return;
        StraightCancel();
        _straightActive = true;
        _straightDevice = device;
        _straightPts.Add(p);
        _straightAnchor = p;
    }

    void StraightMove(Point p)
    {
        if (!_straightActive) return;
        if (_straightReady)
        {
            // Già raddrizzata: muovendo la penna si sposta l'estremità della linea dritta.
            _straightPreview.Data = new LineGeometry(_straightStart, SnapEnd(_straightStart, p));
            return;
        }
        _straightPts.Add(p);
        // Finché la penna si muove il conto alla rovescia riparte; tremolii di pochi pixel non contano.
        if ((p - _straightAnchor).Length * _zoom > 8)
        {
            _straightAnchor = p;
            _straightTimer.Stop();
            _straightTimer.Start();
        }
    }

    void StraightCancel()
    {
        if (_straightReady) Ink.ShowLiveStrokes();
        _straightActive = _straightReady = false;
        _straightDevice = null;
        _straightTimer.Stop();
        _straightPts.Clear();
        _straightPreview.Visibility = Visibility.Collapsed;
    }

    /// <summary>Se la linea è quasi orizzontale o verticale (entro 3°) la rende esattamente tale.</summary>
    static Point SnapEnd(Point a, Point b)
    {
        var chord = b - a;
        double len = chord.Length;
        if (len < 1) return b;
        double angle = Math.Atan2(chord.Y, chord.X) * 180 / Math.PI;
        double nearest = Math.Round(angle / 90) * 90;
        if (Math.Abs(angle - nearest) >= 3) return b;
        double r = nearest * Math.PI / 180;
        return a + new Vector(Math.Cos(r), Math.Sin(r)) * len;
    }

    /// <summary>Se i punti stanno tutti vicino al segmento tra il primo e l'ultimo, restituisce quel segmento.</summary>
    bool StraightSegment(IReadOnlyList<Point> pts, out Point a, out Point b)
    {
        a = b = default;
        if (pts.Count < 2) return false;
        a = pts[0];
        b = pts[^1];
        var chord = b - a;
        double len = chord.Length;
        if (len * _zoom < 40) return false;
        double tolerance = Math.Max(10 / _zoom, len * 0.07);
        var n = new Vector(-chord.Y / len, chord.X / len);
        foreach (var q in pts)
            if (Math.Abs(Vector.Multiply(q - a, n)) > tolerance) return false;
        b = SnapEnd(a, b);
        return true;
    }

    /// <summary>Al termine del tratto: se la linea era stata raddrizzata tenendo premuto, diventa definitivamente dritta.</summary>
    Stroke? StraightenIfHeld(Stroke s)
    {
        bool ready = _straightReady;
        var start = _straightStart;
        StraightCancel();
        if (!ready || s.StylusPoints.Count == 0) return null;
        var last = s.StylusPoints[s.StylusPoints.Count - 1];
        var end = SnapEnd(start, new Point(last.X, last.Y));
        var da = s.DrawingAttributes.Clone();
        da.FitToCurve = false;
        da.IgnorePressure = true;
        var line = new StylusPointCollection(new[] { new StylusPoint(start.X, start.Y), new StylusPoint(end.X, end.Y) });
        return Pencil.GradeOf(da) != null ? new PencilStroke(line, da) : new Stroke(line, da);
    }

    // ───── «Il dito sposta la pagina»: sulle LIM che distinguono penna e dito (es. Newline) ─────

    /// <summary>Se attivo, il tocco col dito fa scorrere/ingrandire la pagina e solo la penna scrive.</summary>
    public bool FingerPans { get; set; }

    readonly Dictionary<int, Point> _fingers = new(); // posizione di ogni dito, in coordinate della finestra
    Point _fingerDownView;
    bool _fingerMoved;
    // gesto cominciato dentro il grafico selezionato (strumento «Seleziona»): muove gli assi, non la pagina
    FunctionGraph? _fingerGraph;
    bool _fingerGraphMoved;

    bool IsFingerPan(StylusEventArgs e) =>
        FingerPans && _node != null && !e.StylusDevice.Inverted &&
        e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Touch;

    /// <summary>
    /// Tocco sul grafico selezionato con «Il dito sposta la pagina» spento (es. Promethean): con «Seleziona» un gesto
    /// che comincia dentro il grafico muove o zooma gli assi come con l'opzione accesa; il resto non cambia.
    /// </summary>
    bool IsGraphTouch(StylusEventArgs e) =>
        !FingerPans && _node != null && _tool == EditorTool.Select && !e.StylusDevice.Inverted &&
        e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Touch &&
        (_fingers.Count > 0 ? _fingerGraph != null : SelectedGraphAt(e.GetPosition(Ink), out bool onEdge) != null && !onEdge);

    void SetupFingerPan()
    {
        ContentHost.PreviewStylusDown += (_, e) =>
        {
            if (!IsFingerPan(e) && !IsGraphTouch(e)) return;
            var src = e.OriginalSource as DependencyObject;
            if (IsWithin(src, TitleArea) || FindAncestor<DraggableTool>(src) != null) return;
            if (_tool == EditorTool.Text && FindAncestor<TextBoxBase>(src) != null) return;
            var v = e.GetPosition(Scroller);
            _fingers[e.StylusDevice.Id] = v;
            if (_fingers.Count == 1)
            {
                _fingerDownView = v;
                _fingerMoved = false;
                _fingerGraph = SelectedGraphAt(e.GetPosition(Ink), out bool onEdge) is { } graph && !onEdge ? graph : null;
                _fingerGraphMoved = false;
            }
            else
            {
                _fingerMoved = true; // con due dita è sempre uno zoom/scorrimento
            }
            e.StylusDevice.Capture(ContentHost);
            e.Handled = true; // il foglio non deve disegnare
        };

        ContentHost.PreviewStylusMove += (_, e) =>
        {
            int id = e.StylusDevice.Id;
            if (!_fingers.TryGetValue(id, out var old)) return;
            var v = e.GetPosition(Scroller);
            if (_fingerGraph != null)
            {
                if (_fingers.Count > 1 || (v - _fingerDownView).Length > 10) _fingerMoved = true;
                if (_fingerMoved) FingerGraphGesture(id, old, v);
                _fingers[id] = v;
                e.Handled = true;
                return;
            }
            if (_fingers.Count == 1)
            {
                if ((v - _fingerDownView).Length > 10) _fingerMoved = true;
                if (_fingerMoved)
                    ScrollToOffset(Scroller.HorizontalOffset - (v.X - old.X), Scroller.VerticalOffset - (v.Y - old.Y));
            }
            else
            {
                // Due dita: zoom in base alla distanza, scorrimento in base al punto medio.
                var other = _fingers.First(f => f.Key != id).Value;
                var oldMid = new Point((old.X + other.X) / 2, (old.Y + other.Y) / 2);
                var newMid = new Point((v.X + other.X) / 2, (v.Y + other.Y) / 2);
                double oldDist = (old - other).Length, newDist = (v - other).Length;
                if (oldDist > 20 && newDist > 20) ZoomAt(_zoom * newDist / oldDist, newMid);
                ScrollToOffset(Scroller.HorizontalOffset - (newMid.X - oldMid.X), Scroller.VerticalOffset - (newMid.Y - oldMid.Y));
            }
            _fingers[id] = v;
            e.Handled = true;
        };

        ContentHost.PreviewStylusUp += (_, e) =>
        {
            if (!_fingers.Remove(e.StylusDevice.Id)) return;
            e.StylusDevice.Capture(null);
            if (_fingers.Count == 0 && !_fingerMoved && !_holdFired) FingerTap(e.GetPosition(Ink));
            if (_fingers.Count == 0 && _fingerGraph != null)
            {
                // un solo passo di «Annulla» per tutto il gesto
                if (_fingerGraphMoved) GraphChanged();
                _fingerGraph = null;
            }
            e.Handled = true;
        };
    }

    /// <summary>
    /// Dita sul grafico selezionato: un dito sposta gli assi, due dita li ingrandiscono o rimpiccioliscono
    /// (pizzico) e li spostano col punto medio. <paramref name="old"/> e <paramref name="now"/> sono la posizione
    /// del dito <paramref name="id"/> prima e dopo, in coordinate della finestra.
    /// </summary>
    void FingerGraphGesture(int id, Point old, Point now)
    {
        if (_fingerGraph == null) return;
        Point ToInk(Point v) => Scroller.TranslatePoint(v, Ink);
        Point oldInk = ToInk(old), nowInk = ToInk(now);
        if (_fingers.Count == 1)
        {
            _fingerGraph.Pan(nowInk - oldInk);
        }
        else
        {
            var other = ToInk(_fingers.First(f => f.Key != id).Value);
            var oldMid = new Point((oldInk.X + other.X) / 2, (oldInk.Y + other.Y) / 2);
            var newMid = new Point((nowInk.X + other.X) / 2, (nowInk.Y + other.Y) / 2);
            double oldDist = (oldInk - other).Length, newDist = (nowInk - other).Length;
            // dita che si allontanano = ingrandire: la finestra degli assi si restringe
            if (oldDist * _zoom > 20 && newDist * _zoom > 20) _fingerGraph.ZoomAt(oldDist / newDist, GraphLocal(_fingerGraph, newMid));
            _fingerGraph.Pan(newMid - oldMid);
        }
        _fingerGraphMoved = true;
    }

    /// <summary>Tocco breve col dito (senza trascinare): fa l'azione «a tocco» dello strumento attivo.</summary>
    void FingerTap(Point p)
    {
        switch (_tool)
        {
            case EditorTool.Text:
                if (ElementAt(p) is (Image or FunctionGraph) and var image)
                {
                    Tool = EditorTool.Select;
                    Ink.Select(new StrokeCollection(), new UIElement[] { image });
                }
                else
                {
                    CreateTextBoxAt(p);
                }
                break;
            case EditorTool.Select:
                SelectAt(p);
                break;
        }
    }

    // ───── Spostamento della selezione (trascinando da un punto qualsiasi al suo interno) ─────

    StrokeCollection _moveStrokes = new();
    List<UIElement> _moveElements = new();
    Point _moveLast;
    bool _moved;

    /// <summary>
    /// L'InkCanvas sposta la selezione solo se la si afferra su un tratto o sul bordo: qui la si può
    /// afferrare anche nel mezzo (ad esempio al centro di un'immagine). Le maniglie restano per ridimensionare.
    /// </summary>
    bool TryBeginMove(Point p)
    {
        if (_tool != EditorTool.Select || !HasSelection || _drag != DragMode.None) return false;
        var b = Ink.GetSelectionBounds();
        if (!b.Contains(p)) return false;
        double r = 14 / _zoom;
        Point[] handles =
        {
            b.TopLeft, b.TopRight, b.BottomLeft, b.BottomRight,
            new(b.Left + b.Width / 2, b.Top), new(b.Left + b.Width / 2, b.Bottom),
            new(b.Left, b.Top + b.Height / 2), new(b.Right, b.Top + b.Height / 2),
        };
        if (handles.Any(h => (h - p).Length < r)) return false;

        _moveStrokes = Ink.GetSelectedStrokes();
        _moveElements = Ink.GetSelectedElements().ToList();
        _moveLast = p;
        _moved = false;
        _drag = DragMode.MoveSelection;
        ClearSelection();
        return true;
    }

    void ContinueMove(Point p)
    {
        var d = p - _moveLast;
        if (Math.Abs(d.X) < 0.01 && Math.Abs(d.Y) < 0.01) return;
        _moveLast = p;
        _moved = true;
        _moveStrokes.Transform(new Matrix(1, 0, 0, 1, d.X, d.Y), false);
        foreach (var el in _moveElements)
        {
            double x = InkCanvas.GetLeft(el), y = InkCanvas.GetTop(el);
            InkCanvas.SetLeft(el, (double.IsNaN(x) ? 0 : x) + d.X);
            InkCanvas.SetTop(el, (double.IsNaN(y) ? 0 : y) + d.Y);
        }
    }

    void EndMove()
    {
        if (_tool == EditorTool.Select) Ink.Select(_moveStrokes, _moveElements);
        if (_moved) MarkChanged();
    }

    // ───────────────────────────── Inserisci spazio ─────────────────────────────
    // Come in OneNote: si tocca tra due righe e si trascina in giù; tutto ciò che comincia sotto quel punto
    // scende. Trascinando in su lo spazio si toglie, ma senza far salire nulla sopra ciò che resta fermo.

    readonly System.Windows.Shapes.Rectangle _spaceBand = new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x1E, 0x5A, 0x68)),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };
    readonly System.Windows.Shapes.Line _spaceLine = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x1E, 0x5A, 0x68)),
        StrokeDashArray = new DoubleCollection { 4, 3 },
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };
    StrokeCollection _spaceStrokes = new();
    List<UIElement> _spaceElements = new();
    double _spaceY, _spaceStartY, _spaceOffset, _spaceMinOffset;

    static double TopOf(UIElement el) => InkCanvas.GetTop(el) is var y && double.IsNaN(y) ? 0 : y;

    void BeginSpace(Point p)
    {
        if (!Overlay.Children.Contains(_spaceBand))
        {
            Overlay.Children.Add(_spaceBand);
            Overlay.Children.Add(_spaceLine);
        }
        _spaceY = _spaceStartY = p.Y;
        _spaceOffset = 0;
        _spaceStrokes = new StrokeCollection(Ink.Strokes.Where(s => s.GetBounds().Top >= p.Y));
        _spaceElements = Ink.Children.Cast<UIElement>().Where(el => TopOf(el) >= p.Y).ToList();
        // Quanto si può risalire: fino a toccare il fondo di ciò che resta fermo (o il bordo del foglio).
        double below = _spaceStrokes.Select(s => s.GetBounds().Top).Concat(_spaceElements.Select(TopOf)).DefaultIfEmpty(p.Y).Min();
        double above = Ink.Strokes.Except(_spaceStrokes).Select(s => s.GetBounds().Bottom)
            .Concat(Ink.Children.Cast<UIElement>().Except(_spaceElements).Select(el => TopOf(el) + el.RenderSize.Height))
            .Where(b => b <= below).DefaultIfEmpty(0).Max();
        _spaceMinOffset = Math.Min(0, above - below);
        _drag = DragMode.Space;
        UpdateSpacePreview();
        _spaceBand.Visibility = _spaceLine.Visibility = Visibility.Visible;
    }

    void ContinueSpace(Point p)
    {
        double offset = Math.Max(_spaceMinOffset, p.Y - _spaceStartY);
        double d = offset - _spaceOffset;
        if (Math.Abs(d) < 0.01) return;
        _spaceOffset = offset;
        _spaceStrokes.Transform(new Matrix(1, 0, 0, 1, 0, d), false);
        foreach (var el in _spaceElements) InkCanvas.SetTop(el, TopOf(el) + d);
        UpdateSpacePreview();
    }

    void UpdateSpacePreview()
    {
        double w = Math.Max(Ink.ActualWidth, Ink.Width);
        double top = Math.Min(_spaceY, _spaceY + _spaceOffset);
        Canvas.SetLeft(_spaceBand, 0);
        Canvas.SetTop(_spaceBand, top);
        _spaceBand.Width = w;
        _spaceBand.Height = Math.Abs(_spaceOffset);
        _spaceLine.X1 = 0;
        _spaceLine.X2 = w;
        _spaceLine.Y1 = _spaceLine.Y2 = _spaceY + _spaceOffset;
        _spaceLine.StrokeThickness = 1.5 / _zoom;
    }

    void EndSpace()
    {
        _spaceBand.Visibility = _spaceLine.Visibility = Visibility.Collapsed;
        _spaceStrokes = new StrokeCollection();
        _spaceElements = new();
        if (Math.Abs(_spaceOffset) > 0.01) MarkChanged();
    }

    // ───────────────────────────── Assi del grafico col mouse ─────────────────────────────

    FunctionGraph? _panGraph;
    Point _graphLast;
    bool _graphMoved, _graphHover;

    /// <summary>
    /// Il grafico di Ardesia selezionato (da solo, con lo strumento «Seleziona») se <paramref name="p"/> cade sopra;
    /// <paramref name="onEdge"/> = sul bordo, dove lo si afferra per spostarlo invece di muovere gli assi.
    /// </summary>
    FunctionGraph? SelectedGraphAt(Point p, out bool onEdge)
    {
        onEdge = false;
        if (_tool != EditorTool.Select || Ink.EditingMode != InkCanvasEditingMode.Select || Ink.GetSelectedStrokes().Count > 0) return null;
        var selected = Ink.GetSelectedElements();
        if (selected.Count != 1 || selected[0] is not FunctionGraph graph) return null;
        double x = InkCanvas.GetLeft(graph), y = InkCanvas.GetTop(graph);
        var bounds = new Rect(double.IsNaN(x) ? 0 : x, double.IsNaN(y) ? 0 : y, graph.ActualWidth, graph.ActualHeight);
        if (!bounds.Contains(p)) return null;
        double edge = 16 / _zoom;
        var inner = bounds;
        inner.Inflate(-edge, -edge);
        onEdge = inner.IsEmpty || !inner.Contains(p);
        return graph;
    }

    /// <summary>Trascinamento dentro il grafico selezionato: sposta gli assi (sul bordo invece si sposta il grafico).</summary>
    bool TryBeginGraphPan(Point p)
    {
        if (_drag != DragMode.None || SelectedGraphAt(p, out bool onEdge) is not { } graph || onEdge) return false;
        _panGraph = graph;
        _graphLast = p;
        _graphMoved = false;
        _drag = DragMode.GraphPan;
        return true;
    }

    void ContinueGraphPan(Point p)
    {
        if (_panGraph == null) return;
        var d = p - _graphLast;
        if (Math.Abs(d.X) < 0.01 && Math.Abs(d.Y) < 0.01) return;
        _graphLast = p;
        _graphMoved = true;
        _panGraph.Pan(d);
    }

    void EndGraphPan()
    {
        // un solo passo di «Annulla» per tutto il trascinamento
        if (_graphMoved) GraphChanged();
        _panGraph = null;
    }

    // ───────────────────────────── Cursori dei parametri ─────────────────────────────

    FunctionGraph? _sliderGraph;
    int _sliderIndex;
    bool _sliderMoved;

    /// <summary>Il grafico di Ardesia con un cursore sotto il punto (solo con lo strumento «Seleziona»), anche se non è selezionato.</summary>
    FunctionGraph? GraphSliderAt(Point p, out int index)
    {
        index = -1;
        if (_tool != EditorTool.Select || _node == null) return null;
        foreach (var graph in Ink.Children.OfType<FunctionGraph>().Reverse())
        {
            double x = InkCanvas.GetLeft(graph), y = InkCanvas.GetTop(graph);
            var local = new Point(p.X - (double.IsNaN(x) ? 0 : x), p.Y - (double.IsNaN(y) ? 0 : y));
            if (local.X < 0 || local.Y < 0 || local.X > graph.ActualWidth || local.Y > graph.ActualHeight) continue;
            index = graph.SliderAt(local);
            return index >= 0 ? graph : null;
        }
        return null;
    }

    static Point GraphLocal(FunctionGraph graph, Point p)
    {
        double x = InkCanvas.GetLeft(graph), y = InkCanvas.GetTop(graph);
        return new Point(p.X - (double.IsNaN(x) ? 0 : x), p.Y - (double.IsNaN(y) ? 0 : y));
    }

    /// <summary>Cursore preso (mouse, dito o penna): il parametro segue subito il punto e il grafico si seleziona.</summary>
    bool TryBeginGraphSlider(Point p)
    {
        if (_drag != DragMode.None || GraphSliderAt(p, out int index) is not { } graph) return false;
        _sliderGraph = graph;
        _sliderIndex = index;
        _sliderMoved = graph.SetSlider(index, GraphLocal(graph, p).X);
        _drag = DragMode.GraphSlider;
        if (!Ink.GetSelectedElements().Contains(graph)) Ink.Select(new StrokeCollection(), new UIElement[] { graph });
        return true;
    }

    void ContinueGraphSlider(Point p)
    {
        if (_sliderGraph != null && _sliderGraph.SetSlider(_sliderIndex, GraphLocal(_sliderGraph, p).X)) _sliderMoved = true;
    }

    void EndGraphSlider()
    {
        // un solo passo di «Annulla» per tutto il trascinamento
        if (_sliderMoved) GraphChanged();
        _sliderGraph = null;
    }

    /// <summary>Col dito e con la penna (col mouse è gestito in ContentHost_PreviewMouseDown).</summary>
    void SetupGraphSliders()
    {
        ContentHost.PreviewStylusDown += (_, e) =>
        {
            if (e.StylusDevice.Inverted || !TryBeginGraphSlider(e.GetPosition(Ink))) return;
            ContentHost.CaptureStylus();
            e.Handled = true; // niente tratto, niente scorrimento della pagina
        };
        ContentHost.PreviewStylusMove += (_, e) =>
        {
            if (_drag != DragMode.GraphSlider) return;
            ContinueGraphSlider(e.GetPosition(Ink));
            e.Handled = true;
        };
        ContentHost.PreviewStylusUp += (_, e) =>
        {
            if (_drag != DragMode.GraphSlider) return;
            EndDrag();
            e.Handled = true;
        };
    }

    /// <summary>Manina sopra il grafico selezionato, dove il trascinamento muove gli assi, e sopra i cursori.</summary>
    void UpdateGraphHover(Point p)
    {
        bool inside = SelectedGraphAt(p, out bool onEdge) != null && !onEdge || GraphSliderAt(p, out _) != null;
        if (inside == _graphHover) return;
        _graphHover = inside;
        Ink.UseCustomCursor = inside || Ink.EditingMode == InkCanvasEditingMode.None;
        Ink.Cursor = inside ? Cursors.Hand : null;
    }

    void UpdateShapePreview(Point p)
    {
        _shapeEnd = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? ShapeBuilder.Constrain(ShapeKind, _dragStart, p) : p;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            foreach (var poly in ShapeBuilder.Build(ShapeKind, _dragStart, _shapeEnd, _penWidth))
            {
                ctx.BeginFigure(poly[0], false, false);
                ctx.PolyLineTo(poly.Skip(1).ToList(), true, true);
            }
        }
        geo.Freeze();
        _shapePreview.Data = geo;
        _shapePreview.Stroke = new SolidColorBrush(_penColor);
        _shapePreview.StrokeThickness = _penWidth;
        _shapePreview.Visibility = Visibility.Visible;
    }

    void FinishShape()
    {
        if ((_shapeEnd - _dragStart).Length < 4) return;
        var strokes = new StrokeCollection();
        foreach (var poly in ShapeBuilder.Build(ShapeKind, _dragStart, _shapeEnd, _penWidth))
            strokes.Add(new Stroke(new StylusPointCollection(poly.Select(q => new StylusPoint(q.X, q.Y))), GeometryAttributes()));
        Ink.Strokes.Add(strokes);
    }

    void ContinueArc(Point p)
    {
        var c = _compass.Center;
        double a = Math.Atan2(p.Y - c.Y, p.X - c.X);
        double delta = a - _arcPrevAngle;
        while (delta > Math.PI) delta -= 2 * Math.PI;
        while (delta < -Math.PI) delta += 2 * Math.PI;
        _arcPrevAngle = a;
        if (Math.Abs(delta) < 1e-6) return;

        double start = _compass.AngleRad, r = _compass.Radius;
        if (_arcStroke == null)
        {
            var p0 = _compass.PencilPoint;
            _arcStroke = new Stroke(new StylusPointCollection(new[] { new StylusPoint(p0.X, p0.Y) }), GeometryAttributes());
            Ink.Strokes.Add(_arcStroke);
        }
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 180)));
        for (int i = 1; i <= steps; i++)
        {
            double ang = start + delta * i / steps;
            _arcStroke.StylusPoints.Add(new StylusPoint(c.X + r * Math.Cos(ang), c.Y + r * Math.Sin(ang)));
        }
        _compass.Set(c, r, start + delta);
        MarkChanged();
    }

    void ContentHost_ManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = Scroller;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
        e.Handled = true;
    }

    void ContentHost_ManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        var d = e.DeltaManipulation;
        // Alcuni pannelli touch mandano valori anomali: li ignoriamo invece di rompere lo zoom.
        double scale = d.Scale.X;
        if (double.IsFinite(scale) && scale > 0.5 && scale < 2 && Math.Abs(scale - 1) > 1e-3)
            ZoomAt(_zoom * scale, e.ManipulationOrigin);
        double dx = Math.Clamp(double.IsFinite(d.Translation.X) ? d.Translation.X : 0, -400, 400);
        double dy = Math.Clamp(double.IsFinite(d.Translation.Y) ? d.Translation.Y : 0, -400, 400);
        ScrollToOffset(Scroller.HorizontalOffset - dx, Scroller.VerticalOffset - dy);
        e.Handled = true;
    }

    void ContentHost_ManipulationInertiaStarting(object? sender, ManipulationInertiaStartingEventArgs e)
    {
        // Niente "lancio" dopo aver sollevato il dito: la pagina si ferma dove la lasci.
        e.TranslationBehavior.DesiredDeceleration = 1e6;
        e.ExpansionBehavior.DesiredDeceleration = 1e6;
        e.Handled = true;
    }

    // ───────────────────────────── Zoom e foglio infinito ─────────────────────────────

    void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // sopra il grafico selezionato (strumento «Seleziona») la rotella fa lo zoom degli assi, non scorre la pagina
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && SelectedGraphAt(e.GetPosition(Ink), out _) is { } graph)
        {
            graph.ZoomAt(e.Delta > 0 ? 1 / 1.15 : 1.15, e.GetPosition(graph));
            MarkChanged();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomAt(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), e.GetPosition(Scroller));
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ScrollToOffset(Scroller.HorizontalOffset - e.Delta, Scroller.VerticalOffset);
            e.Handled = true;
        }
        else if (_node != null)
        {
            ScrollToOffset(Scroller.HorizontalOffset, Scroller.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    public void ZoomIn() => ZoomAt(_zoom * 1.2);
    public void ZoomOut() => ZoomAt(_zoom / 1.2);
    public void ZoomReset() => ZoomAt(1);

    public void ZoomAt(double z, Point? anchorView = null)
    {
        z = Math.Clamp(z, MinZoom, MaxZoom);
        if (Math.Abs(z - _zoom) < 1e-4) return;
        var a = anchorView ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        var content = new Point((Scroller.HorizontalOffset + a.X) / _zoom, (Scroller.VerticalOffset + a.Y) / _zoom);
        _zoom = z;
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = z;
        UpdateCanvasSize();
        Scroller.UpdateLayout();
        Scroller.ScrollToHorizontalOffset(content.X * z - a.X);
        Scroller.ScrollToVerticalOffset(content.Y * z - a.Y);
        ZoomText.Text = $"{Math.Round(z * 100)}%";
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_node == null) return;
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
        {
            UpdateCanvasSize();
            return;
        }
        double vw = Scroller.ViewportWidth, vh = Scroller.ViewportHeight;
        bool grow = false;
        if (e.VerticalChange > 0 && Scroller.VerticalOffset + vh >= Scroller.ExtentHeight - vh * 0.25)
        {
            _minH = Ink.Height + vh / _zoom * 0.5;
            grow = true;
        }
        if (e.HorizontalChange > 0 && Scroller.HorizontalOffset + vw >= Scroller.ExtentWidth - vw * 0.25)
        {
            _minW = Ink.Width + vw / _zoom * 0.5;
            grow = true;
        }
        if (grow) UpdateCanvasSize();
    }

    void QueueCanvasSizeUpdate()
    {
        if (_sizeUpdateQueued) return;
        _sizeUpdateQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _sizeUpdateQueued = false;
            UpdateCanvasSize();
        }, DispatcherPriority.Background);
    }

    Rect ContentBounds()
    {
        var r = Ink.Strokes.Count > 0 ? Ink.Strokes.GetBounds() : Rect.Empty;
        foreach (UIElement c in Ink.Children)
        {
            if (c is not FrameworkElement fe) continue;
            double x = InkCanvas.GetLeft(fe), y = InkCanvas.GetTop(fe);
            if (double.IsNaN(x)) x = 0;
            if (double.IsNaN(y)) y = 0;
            double w = fe.ActualWidth > 0 ? fe.ActualWidth : double.IsNaN(fe.Width) ? 0 : fe.Width;
            double h = fe.ActualHeight > 0 ? fe.ActualHeight : double.IsNaN(fe.Height) ? 0 : fe.Height;
            r.Union(new Rect(x, y, w, h));
        }
        return r;
    }

    /// <summary>Il foglio è sempre più grande del contenuto di mezza finestra: c'è sempre spazio a destra e in basso.</summary>
    void UpdateCanvasSize()
    {
        double vw = Math.Max(200, Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth) / _zoom;
        double vh = Math.Max(200, Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight) / _zoom;
        var b = ContentBounds();
        double right = b.IsEmpty ? 0 : b.Right, bottom = Math.Max(b.IsEmpty ? 0 : b.Bottom, 120);
        Ink.Width = Math.Ceiling(Math.Max(Math.Max(vw, right + vw * 0.5), _minW));
        Ink.Height = Math.Ceiling(Math.Max(bottom + vh, _minH));
    }

    /// <summary>Scorre alla posizione indicata; se si va oltre il bordo destro o inferiore, il foglio si allunga.</summary>
    void ScrollToOffset(double h, double v)
    {
        if (!double.IsFinite(h) || !double.IsFinite(v)) return;
        // Il foglio si allunga al massimo di una schermata per volta.
        h = Math.Min(h, Scroller.ScrollableWidth + Scroller.ViewportWidth);
        v = Math.Min(v, Scroller.ScrollableHeight + Scroller.ViewportHeight);
        bool grow = false;
        if (v > Scroller.ScrollableHeight)
        {
            _minH = Math.Max(_minH, (v + Scroller.ViewportHeight) / _zoom + Scroller.ViewportHeight / _zoom * 0.5);
            grow = true;
        }
        if (h > Scroller.ScrollableWidth)
        {
            _minW = Math.Max(_minW, (h + Scroller.ViewportWidth) / _zoom + Scroller.ViewportWidth / _zoom * 0.5);
            grow = true;
        }
        if (grow)
        {
            UpdateCanvasSize();
            Scroller.UpdateLayout();
        }
        Scroller.ScrollToHorizontalOffset(Math.Max(0, h));
        Scroller.ScrollToVerticalOffset(Math.Max(0, v));
    }

    Point ViewCenter() => new(
        (Scroller.HorizontalOffset + Scroller.ViewportWidth / 2) / _zoom,
        (Scroller.VerticalOffset + Scroller.ViewportHeight / 2) / _zoom);

    Point VisibleOrigin()
    {
        double x = Scroller.HorizontalOffset / _zoom + 60;
        double y = Scroller.VerticalOffset / _zoom;
        return new Point(x, y + (y < 100 ? 140 : 40));
    }

    // ───────────────────────────── Demo (solo per test visivi) ─────────────────────────────

    internal void DrawDemo()
    {
        var da = PenAttributes();
        da.Color = Color.FromRgb(0x1E, 0x5A, 0xC8);
        var pts = new StylusPointCollection();
        for (int i = 0; i <= 200; i++)
            pts.Add(new StylusPoint(80 + i * 3, 330 - 60 * Math.Sin(i / 16.0)));
        Ink.Strokes.Add(new Stroke(pts, da));
        ShapeKind = ShapeKind.Triangle;
        _dragStart = new Point(760, 240);
        _shapeEnd = new Point(1000, 420);
        FinishShape();
        var rtb = CreateTextBoxAt(new Point(80, 470));
        ElementFactory.SetPlainText(rtb, "Teorema di Pitagora: a² + b² = c²");
        RulerVisible = true;
        _ruler.SetPose(new Point(620, 640), -12);
        ProtractorVisible = true;
        _protractor.SetPose(new Point(980, 420), 0);
        Tool = EditorTool.Compass;
        _compass.Set(new Point(560, 260), 110, 0);
        _drag = DragMode.CompassDraw;
        _arcPrevAngle = 0;
        for (int i = 1; i <= 50; i++)
            ContinueArc(new Point(560 + 300 * Math.Cos(i * 0.1), 260 + 300 * Math.Sin(i * 0.1)));
        _drag = DragMode.None;
        _arcStroke = null;

        // grafico di Ardesia con parametro, zeri, intersezioni, estremi e segno
        var state = new GraphState();
        state.SetFunctions(new[] { "y = x² − 4", "f(x) = 1,5x + a", "x = 3" });
        state.Show.Zeros = state.Show.Intersections = state.Show.Extrema = state.Show.Sign = true;
        var graph = ElementFactory.CreateFunctionGraph(state, 560, 420);
        graph.ResetView();
        InkCanvas.SetLeft(graph, 1080);
        InkCanvas.SetTop(graph, 130);
        AddElement(graph);
        MarkChanged();
    }
}
