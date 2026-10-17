using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Ardesia.Editor.Graph;

/// <summary>
/// Finestrella «Funzioni del grafico»: una funzione per riga, ognuna con la casella per mostrarla o toglierla
/// dal grafico, e i simboli più usati a portata di dito. Sotto ogni riga la formula composta, per vedere subito
/// come è stata capita; la striscia «Scrivi a mano» legge le funzioni scritte con la penna.
/// </summary>
public sealed class GraphFunctionsDialog : Window
{
    /// <summary>Una riga della finestrella: casella, colore della curva, testo, pulsante per toglierla.</summary>
    sealed class Row
    {
        public required Grid Panel;
        public required CheckBox Visible;
        public required TextBox Box;
        public required FormulaView Preview;
        /// <summary>Colore della funzione da cui viene la riga ("" per le righe nuove): resta anche se la si corregge.</summary>
        public string Color = "";
    }

    readonly StackPanel _rows = new();
    readonly List<Row> _list = new();
    readonly TextBlock _check;
    TextBox? _lastBox;
    readonly Border _writeArea;
    readonly InkCanvas _writeInk;
    readonly TextBlock _writeHint, _writeStatus;
    readonly DispatcherTimer _writeTimer = new() { Interval = TimeSpan.FromSeconds(1.1) };
    bool _recognizing;

    /// <param name="note">Avviso in evidenza sopra le righe (per esempio dopo il riconoscimento della scrittura).</param>
    /// <param name="startWriting">Apre subito la striscia per scrivere a mano.</param>
    public GraphFunctionsDialog(IEnumerable<GraphFunctionData> functions, string? note = null, bool startWriting = false)
    {
        Title = "Funzioni del grafico";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        if (Application.Current.TryFindResource("TextBrush") is Brush fg) Foreground = fg;

        var root = new StackPanel { Margin = new Thickness(26, 20, 26, 20), Width = 600 };
        root.Children.Add(new TextBlock { Text = "Funzioni del grafico", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(new TextBlock
        {
            Text = "Una per riga, per esempio  y = x² − 4,  f(x) = 2x + 1,  y = a·sen(x),  x = 3.\n" +
                   "Le lettere diverse da x (a, b, k…) diventano parametri. Togli la spunta per nascondere una funzione.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 10),
        });
        if (note != null)
        {
            var accentBrush = Application.Current.TryFindResource("AccentBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x5A, 0x68));
            root.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Foreground = accentBrush, Margin = new Thickness(0, 0, 0, 10) });
        }

        root.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var add = new Button { Content = "+ Aggiungi una funzione", Height = 36, Padding = new Thickness(10, 0, 10, 0), Focusable = false };
        if (Application.Current.TryFindResource("PlainButton") is Style addStyle) add.Style = addStyle;
        add.Click += (_, _) => AddRow(new GraphFunctionData { Text = "", Color = "" }, _list.Count).Box.Focus();
        tools.Children.Add(add);
        var write = new Button { Content = "✍ Scrivi a mano", Height = 36, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0), Focusable = false };
        if (Application.Current.TryFindResource("PlainButton") is Style writeStyle) write.Style = writeStyle;
        if (!HandwritingMath.IsAvailable)
        {
            write.IsEnabled = false;
            write.ToolTip = HandwritingMath.NotAvailableMessage;
            ToolTipService.SetShowOnDisabled(write, true);
        }
        write.Click += (_, _) => ShowWriting(_writeArea!.Visibility != Visibility.Visible);
        tools.Children.Add(write);
        root.Children.Add(tools);

        // striscia per scrivere a mano: si legge quando la penna si ferma
        _writeInk = new InkCanvas { Height = 180, Background = Brushes.White };
        _writeInk.DefaultDrawingAttributes = new DrawingAttributes { Color = Colors.Black, Width = 3.5, Height = 3.5, FitToCurve = true };
        _writeHint = new TextBlock
        {
            Text = "Scrivi qui la funzione, una per riga: la leggo appena ti fermi.",
            Foreground = Brushes.Gray,
            Margin = new Thickness(12, 10, 12, 0),
            IsHitTestVisible = false,
        };
        var writeGrid = new Grid();
        writeGrid.Children.Add(_writeInk);
        writeGrid.Children.Add(_writeHint);
        var clear = new Button { Content = "Pulisci", Height = 34, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Focusable = false };
        if (Application.Current.TryFindResource("PlainButton") is Style clearStyle) clear.Style = clearStyle;
        clear.Click += (_, _) => { _writeInk.Strokes.Clear(); _writeStatus!.Text = ""; UpdateWriteHint(); };
        writeGrid.Children.Add(clear);
        _writeStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = Brushes.DimGray };
        var writePanel = new StackPanel();
        writePanel.Children.Add(new Border { BorderBrush = Brushes.Silver, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = writeGrid, ClipToBounds = true });
        writePanel.Children.Add(_writeStatus);
        _writeArea = new Border { Child = writePanel, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        root.Children.Add(_writeArea);
        _writeInk.StrokeCollected += (_, _) => { UpdateWriteHint(); RestartWriteTimer(); };
        // mentre si scrive non si legge
        _writeInk.PreviewMouseDown += (_, _) => _writeTimer.Stop();
        _writeInk.PreviewStylusDown += (_, _) => _writeTimer.Stop();
        _writeInk.PreviewTouchDown += (_, _) => _writeTimer.Stop();
        _writeInk.StrokeErased += (_, _) => UpdateWriteHint();
        _writeTimer.Tick += (_, _) => { _writeTimer.Stop(); RecognizeWriting(); };
        Closed += (_, _) => _writeTimer.Stop();

        // simboli difficili da scrivere con la tastiera della LIM
        var keys = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, insert) in new[] { ("x²", "²"), ("x³", "³"), ("^", "^"), ("√", "√("), ("π", "π"), ("|x|", "|"), ("·", "·"), ("( )", "()"), ("sen", "sen("), ("cos", "cos("), ("ln", "ln(") })
        {
            var b = new Button { Content = label, MinWidth = 46, Height = 38, FontSize = 17, Margin = new Thickness(0, 0, 6, 6), Focusable = false };
            if (Application.Current.TryFindResource("PlainButton") is Style st) b.Style = st;
            b.Click += (_, _) => Insert(insert);
            keys.Children.Add(b);
        }
        root.Children.Add(keys);

        _check = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MinHeight = 20 };
        root.Children.Add(_check);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Disegna", IsDefault = false, Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        if (Application.Current.TryFindResource("AccentButton") is Style accent) ok.Style = accent;
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "Annulla", IsCancel = true, MinWidth = 90 };
        if (Application.Current.TryFindResource("PlainButton") is Style plain) cancel.Style = plain;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        foreach (var f in functions) AddRow(f, _list.Count);
        if (_list.Count == 0) AddRow(new GraphFunctionData { Text = "", Color = "" }, 0);

        Content = root;
        if (startWriting && HandwritingMath.IsAvailable) ShowWriting(true);
        Loaded += (_, _) =>
        {
            // si continua a scrivere sulla prima riga vuota, o in fondo all'ultima
            var row = _list.FirstOrDefault(r => r.Box.Text.Length == 0) ?? _list[^1];
            row.Box.Focus();
            row.Box.CaretIndex = row.Box.Text.Length;
        };
        Validate();
    }

    /// <summary>Le righe non vuote: testo come è scritto (la legenda lo mostra uguale), visibilità e colore di partenza.</summary>
    public IReadOnlyList<GraphFunctionData> Functions =>
        _list.Where(r => r.Box.Text.Trim().Length > 0)
            .Select(r => new GraphFunctionData { Text = r.Box.Text.Trim(), Color = r.Color, Hidden = r.Visible.IsChecked != true })
            .ToList();

    Row AddRow(GraphFunctionData f, int index)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // casella grande, da toccare col dito sulla LIM
        var visible = new CheckBox
        {
            IsChecked = !f.Hidden,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            LayoutTransform = new ScaleTransform(1.7, 1.7),
            Focusable = false,
            ToolTip = "Mostra la funzione nel grafico",
        };
        Grid.SetColumn(visible, 0);
        grid.Children.Add(visible);

        // trattino del colore della curva (vuoto per le righe nuove: il colore si sceglie disegnando)
        var swatch = new Rectangle { Width = 22, Height = 5, RadiusX = 2, RadiusY = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        if (f.Color.Length > 0 && ColorConverter.ConvertFromString(f.Color) is Color c) swatch.Fill = new SolidColorBrush(c);
        Grid.SetColumn(swatch, 1);
        grid.Children.Add(swatch);

        var box = new TextBox
        {
            Text = f.Text,
            FontSize = 22,
            FontFamily = new FontFamily("Cambria Math, Segoe UI"),
            Padding = new Thickness(6, 3, 6, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(box, 2);
        grid.Children.Add(box);

        var remove = new Button { Content = "✕", FontSize = 15, Margin = new Thickness(6, 0, 0, 0), Focusable = false, ToolTip = "Togli questa riga" };
        if (Application.Current.TryFindResource("PlainButton") is Style st) remove.Style = st;
        // dopo lo stile, che ha una larghezza minima da pulsante con testo
        remove.MinWidth = 0;
        remove.Width = remove.Height = 40;
        remove.Padding = new Thickness(0);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        // la formula come sarà scritta nella legenda; le formule lunghe si rimpiccioliscono
        var preview = new FormulaView();
        var previewBox = new Viewbox { Child = preview, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, MaxHeight = 80, Margin = new Thickness(8, 4, 0, 2) };
        Grid.SetRow(previewBox, 1);
        Grid.SetColumn(previewBox, 2);
        grid.Children.Add(previewBox);

        var row = new Row { Panel = grid, Visible = visible, Box = box, Preview = preview, Color = f.Color };
        _list.Insert(index, row);
        _rows.Children.Insert(index, grid);

        visible.Click += (_, _) => box.Opacity = visible.IsChecked == true ? 1 : 0.5;
        box.Opacity = f.Hidden ? 0.5 : 1;
        box.TextChanged += (_, _) => Validate();
        box.GotKeyboardFocus += (_, _) => _lastBox = box;
        box.PreviewKeyDown += (_, e) => RowKey(row, e);
        DataObject.AddPastingHandler(box, (_, e) => PasteLines(row, e));
        remove.Click += (_, _) => RemoveRow(row, focusPrevious: false);
        Validate();
        return row;
    }

    void RemoveRow(Row row, bool focusPrevious)
    {
        int i = _list.IndexOf(row);
        if (i < 0) return;
        if (_list.Count == 1)
        {
            // l'ultima riga si svuota soltanto: ne resta sempre una dove scrivere
            row.Box.Clear();
            row.Color = "";
            row.Box.Focus();
            return;
        }
        _list.RemoveAt(i);
        _rows.Children.Remove(row.Panel);
        if (_lastBox == row.Box) _lastBox = null;
        var next = _list[Math.Clamp(focusPrevious ? i - 1 : i, 0, _list.Count - 1)];
        next.Box.Focus();
        next.Box.CaretIndex = focusPrevious ? next.Box.Text.Length : 0;
        Validate();
    }

    /// <summary>Invio = riga nuova sotto; Canc su una riga vuota la toglie; frecce = riga sopra/sotto.</summary>
    void RowKey(Row row, KeyEventArgs e)
    {
        int i = _list.IndexOf(row);
        switch (e.Key)
        {
            case Key.Enter:
                var box = row.Box;
                // il testo dopo il cursore va nella riga nuova, come in un editor
                string after = box.Text[box.CaretIndex..];
                box.Text = box.Text[..box.CaretIndex];
                var added = AddRow(new GraphFunctionData { Text = after, Color = "" }, i + 1);
                added.Box.Focus();
                added.Box.CaretIndex = 0;
                e.Handled = true;
                break;
            case Key.Back when row.Box.Text.Length == 0 && _list.Count > 1:
                RemoveRow(row, focusPrevious: true);
                e.Handled = true;
                break;
            case Key.Up when i > 0:
                _list[i - 1].Box.Focus();
                e.Handled = true;
                break;
            case Key.Down when i < _list.Count - 1:
                _list[i + 1].Box.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Un testo di più righe incollato si divide in più funzioni.</summary>
    void PasteLines(Row row, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (lines.Count < 2) return;
        e.CancelCommand();
        var box = row.Box;
        int at = box.SelectionStart;
        string before = box.Text[..at], after = box.Text[(at + box.SelectionLength)..];
        box.Text = before + lines[0];
        int i = _list.IndexOf(row);
        Row last = row;
        for (int k = 1; k < lines.Count; k++)
            last = AddRow(new GraphFunctionData { Text = lines[k] + (k == lines.Count - 1 ? after : ""), Color = "" }, i + k);
        last.Box.Focus();
        last.Box.CaretIndex = lines[^1].Length;
    }

    void Insert(string s)
    {
        var box = _lastBox ?? _list[^1].Box;
        int at = box.CaretIndex;
        box.SelectedText = "";
        box.Text = box.Text.Insert(at, s);
        // tra le parentesi appena scritte
        box.CaretIndex = at + (s.EndsWith("()") ? s.Length - 1 : s.Length);
        box.Focus();
    }

    void Validate()
    {
        if (_check == null) return;
        var errors = new List<string>();
        int n = 0, written = 0;
        foreach (var row in _list)
        {
            n++;
            var line = row.Box.Text.Trim();
            bool bad = false;
            row.Preview.Box = null;
            if (line.Length > 0)
            {
                written++;
                try
                {
                    var curve = MathParser.ParseLine(MathText.Normalize(line));
                    row.Preview.Box = MathLayout.Line(curve, 22, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                }
                catch (MathSyntaxException ex)
                {
                    errors.Add($"Riga {n}: {ex.Message}");
                    bad = true;
                }
            }
            // riga sbagliata: bordo rosso
            if (bad)
            {
                row.Box.BorderBrush = ErrorBrush;
                row.Box.BorderThickness = new Thickness(2);
            }
            else
            {
                row.Box.ClearValue(BorderBrushProperty);
                row.Box.ClearValue(BorderThicknessProperty);
            }
        }
        _check.Text = errors.Count == 0 ? (written == 0 ? "" : "✓ Tutto a posto") : string.Join("\n", errors);
        _check.Foreground = errors.Count == 0 ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)) : ErrorBrush;
    }

    // ── scrittura a mano ──

    void ShowWriting(bool on)
    {
        _writeArea.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) _writeTimer.Stop();
    }

    /// <summary>Tratti già pronti nella striscia, come se fossero scritti ora (per le istantanee di prova).</summary>
    internal void AddWriting(StrokeCollection strokes)
    {
        ShowWriting(true);
        _writeInk.Strokes.Add(strokes);
        UpdateWriteHint();
        RestartWriteTimer();
    }

    void UpdateWriteHint() => _writeHint.Visibility = _writeInk.Strokes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void RestartWriteTimer()
    {
        _writeTimer.Stop();
        _writeTimer.Start();
    }

    /// <summary>Legge i tratti della striscia; ogni riga riconosciuta va in una riga della finestrella.</summary>
    async void RecognizeWriting()
    {
        if (_recognizing || _writeInk.Strokes.Count == 0) return;
        _recognizing = true;
        _writeStatus.Text = "Leggo…";
        var taken = new StrokeCollection(_writeInk.Strokes);
        bool unreadable = false;
        try
        {
            var texts = (await HandwritingMath.RecognizeLinesAsync(taken, this)).Where(t => t.Length > 0).ToList();
            if (texts.Count == 0)
            {
                unreadable = true;
                _writeStatus.Text = "Non riesco a leggere: prova a scrivere più grande, oppure premi «Pulisci» e riscrivi.";
                return;
            }
            foreach (var t in texts) PutRecognized(t);
            _writeInk.Strokes.Remove(new StrokeCollection(taken.Where(_writeInk.Strokes.Contains)));
            _writeStatus.Text = "Ho letto:  " + string.Join("    ", texts) + "\nSe non va bene, correggi la riga qui sopra o riscrivi.";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            unreadable = true;
            _writeStatus.Text = "Il riconoscimento della scrittura non funziona: " + ex.Message;
        }
        finally
        {
            _recognizing = false;
            UpdateWriteHint();
            // tratti scritti mentre leggevo: si leggono dopo
            if (!unreadable && _writeInk.Strokes.Count > 0 && !_writeTimer.IsEnabled) RestartWriteTimer();
        }
    }

    /// <summary>Il testo letto va nella riga vuota su cui si stava scrivendo, o nella prima vuota, o in una nuova.</summary>
    void PutRecognized(string text)
    {
        var row = _list.FirstOrDefault(r => r.Box == _lastBox && r.Box.Text.Trim().Length == 0)
                  ?? _list.FirstOrDefault(r => r.Box.Text.Trim().Length == 0)
                  ?? AddRow(new GraphFunctionData { Text = "", Color = "" }, _list.Count);
        row.Box.Text = text;
        row.Box.CaretIndex = text.Length;
    }

    /// <summary>Una formula composta da <see cref="MathLayout"/>.</summary>
    sealed class FormulaView : FrameworkElement
    {
        MathLayout.MathBox? _box;

        public MathLayout.MathBox? Box
        {
            get => _box;
            set
            {
                _box = value;
                Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }

        protected override Size MeasureOverride(Size available) => _box == null ? new Size() : new Size(_box.Width, _box.Height);

        protected override void OnRender(DrawingContext dc) => _box?.Draw(dc, 0, _box.Ascent);
    }

    static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
}
