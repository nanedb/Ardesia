using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ardesia;

/// <summary>
/// Strumento diagnostico: mostra che tipo di input arriva da Windows (penna o tocco)
/// e quanto è largo il contatto, per capire se la LIM distingue penna, dito e palmo.
/// </summary>
public sealed class TouchTestWindow : Window
{
    readonly Canvas _canvas = new() { Background = Brushes.White, ClipToBounds = true };
    readonly ListBox _log = new() { FontFamily = new FontFamily("Consolas"), FontSize = 13, BorderThickness = new Thickness(0) };
    readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) };
    readonly Dictionary<int, (string Kind, double MaxMm)> _contacts = new();
    int _penCount, _eraserCount, _touchCount, _mouseCount, _withSize, _touchWithSize;
    double _touchMin = double.MaxValue, _touchMax;
    double _minMm = double.MaxValue, _maxMm;

    public TouchTestWindow()
    {
        Title = "Prova del tocco della LIM – Ardesia";
        Width = 1100;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        Background = (Brush)Application.Current.FindResource("NavBrush");

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 10),
            Text = "Tocca e traccia linee nel riquadro bianco: prima con la PENNA, poi con un DITO, poi con il PALMO della mano " +
                   "(e, se la penna ha due punte, con la punta grossa). A destra compare ciò che Windows riceve. " +
                   "Le linee blu arrivano come «penna», quelle arancioni come «tocco», quelle rosa come «gomma» (palmo o retro della penna).",
        };

        var clear = new Button { Content = "Ricomincia", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 8, 0) };
        clear.Click += (_, _) => Reset();
        var close = new Button { Content = "Chiudi", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(clear);
        buttons.Children.Add(close);

        var right = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(_summary, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        right.Children.Add(_summary);
        right.Children.Add(buttons);
        right.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Child = _log });

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        Grid.SetColumnSpan(intro, 2);
        grid.Children.Add(intro);
        var canvasBorder = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _canvas };
        Grid.SetRow(canvasBorder, 1);
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 1);
        grid.Children.Add(canvasBorder);
        grid.Children.Add(right);
        Content = grid;

        _canvas.PreviewStylusDown += (_, e) => OnStylus(e, down: true);
        _canvas.PreviewStylusMove += (_, e) => OnStylus(e, down: false);
        _canvas.PreviewStylusUp += (_, e) => Finish(e.StylusDevice.Id);
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (e.StylusDevice != null) return; // generato da penna/tocco: già registrato
            _mouseCount++;
            Add("Mouse (non è un input della LIM)");
            UpdateSummary();
        };
        Reset();
    }

    void Reset()
    {
        _canvas.Children.Clear();
        _log.Items.Clear();
        _contacts.Clear();
        _penCount = _eraserCount = _touchCount = _mouseCount = _withSize = _touchWithSize = 0;
        _touchMin = double.MaxValue;
        _touchMax = 0;
        _minMm = double.MaxValue;
        _maxMm = 0;
        UpdateSummary();
    }

    void OnStylus(StylusEventArgs e, bool down)
    {
        var device = e.StylusDevice;
        var tablet = device.TabletDevice;
        bool isTouch = tablet?.Type == TabletDeviceType.Touch;
        string kind = isTouch ? "tocco" : device.Inverted ? "gomma/palmo" : "penna";

        var points = e.GetStylusPoints(_canvas);
        double? mm = ContactWidthMm(points);

        if (down)
        {
            if (isTouch) _touchCount++; else if (device.Inverted) _eraserCount++; else _penCount++;
            _contacts[device.Id] = (kind, mm ?? 0);
            Add($"↓ {kind,-14} dispositivo: «{tablet?.Name}»");
        }
        else if (_contacts.TryGetValue(device.Id, out var c) && mm is { } v && v > c.MaxMm)
        {
            _contacts[device.Id] = (c.Kind, v);
        }

        var brush = isTouch ? Brushes.DarkOrange : device.Inverted ? Brushes.MediumVioletRed : Brushes.RoyalBlue;
        foreach (var p in points)
        {
            double d = Math.Clamp((mm ?? 1.5) * 96 / 25.4, 3, 80);
            var dot = new Ellipse { Width = d, Height = d, Fill = brush, Opacity = 0.35 };
            Canvas.SetLeft(dot, p.X - d / 2);
            Canvas.SetTop(dot, p.Y - d / 2);
            _canvas.Children.Add(dot);
        }
        if (_canvas.Children.Count > 4000) _canvas.Children.RemoveRange(0, 1000);
        e.Handled = true;
    }

    void Finish(int id)
    {
        if (!_contacts.Remove(id, out var c)) return;
        if (c.MaxMm > 0)
        {
            _withSize++;
            _minMm = Math.Min(_minMm, c.MaxMm);
            _maxMm = Math.Max(_maxMm, c.MaxMm);
            if (c.Kind == "tocco")
            {
                _touchWithSize++;
                _touchMin = Math.Min(_touchMin, c.MaxMm);
                _touchMax = Math.Max(_touchMax, c.MaxMm);
            }
            Add($"↑ {c.Kind,-14} larghezza massima del contatto: {c.MaxMm.ToString("0.0", CultureInfo.InvariantCulture)} mm");
        }
        else
        {
            Add($"↑ {c.Kind,-14} (la LIM non comunica la larghezza del contatto)");
        }
        UpdateSummary();
    }

    /// <summary>Larghezza del contatto in millimetri, se il dispositivo la comunica.</summary>
    static double? ContactWidthMm(StylusPointCollection points)
    {
        var desc = points.Description;
        if (points.Count == 0 || !desc.HasProperty(StylusPointProperties.Width)) return null;
        var info = desc.GetPropertyInfo(StylusPointProperties.Width);
        int raw = points.Max(p => p.GetPropertyValue(StylusPointProperties.Width));
        if (info.Resolution <= 0) return null;
        double perUnit = info.Unit switch
        {
            StylusPointPropertyUnit.Centimeters => 10,
            StylusPointPropertyUnit.Inches => 25.4,
            _ => 0,
        };
        return perUnit == 0 ? null : raw / info.Resolution * perUnit;
    }

    void Add(string line)
    {
        _log.Items.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        _log.ScrollIntoView(_log.Items[^1]);
    }

    void UpdateSummary()
    {
        string penVsFinger = (_penCount, _touchCount) switch
        {
            (0, 0) => "",
            ( > 0, > 0) => "✔ La LIM distingue la penna dal dito: attiva «Il dito sposta la pagina» (scheda Visualizza) per scrivere solo con la penna e scorrere la pagina col dito.",
            ( > 0, 0) => "La penna arriva come «penna». Prova anche col dito per vedere se arriva come «tocco».",
            (0, > 0) when _touchWithSize > 0 && _touchMax > _touchMin * 2 =>
                "Penna e dito arrivano entrambi come «tocco», ma la larghezza del contatto cambia: si potranno distinguere dalla misura.",
            (0, > 0) when _touchWithSize > 0 =>
                "Penna e dito arrivano entrambi come «tocco», con larghezze simili: Windows non riesce a distinguerli.",
            _ => "✘ Penna e dito arrivano entrambi come «tocco», senza misura del contatto: Windows non può distinguerli.",
        };
        string eraser = _eraserCount > 0
            ? "✔ Il palmo (o il retro della penna) arriva come «gomma»: in Ardesia cancella direttamente dove passa, con qualsiasi strumento."
            : _touchCount + _penCount > 0 ? "Prova anche ad appoggiare il palmo: alcune LIM lo segnalano come gomma." : "";
        string verdict = _penCount + _touchCount + _eraserCount == 0
            ? "Nessun contatto ancora."
            : string.Join("\n\n", new[] { penVsFinger, eraser }.Where(s => s.Length > 0));
        _summary.Text =
            $"Penna: {_penCount}   Tocco: {_touchCount}   Gomma/palmo: {_eraserCount}   Mouse: {_mouseCount}\n" +
            (_withSize > 0
                ? $"Larghezza contatti: da {_minMm.ToString("0.0", CultureInfo.InvariantCulture)} a {_maxMm.ToString("0.0", CultureInfo.InvariantCulture)} mm\n"
                : "") +
            "\n" + verdict;
    }
}
