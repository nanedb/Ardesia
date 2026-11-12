using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using static Ardesia.Calculator.CalcKeys;

namespace Ardesia.Calculator;

/// <summary>
/// Calcolatrice scientifica di Ardesia: algebrica o RPN, numeri normali, a decimali fissi (FIX) o in notazione
/// scientifica (SCI) con i decimali scelti. Finestra non modale sopra Ardesia, ridimensionabile (tutto si ingrandisce
/// insieme: sulle LIM la si può fare grande), con i tasti grandi per il dito; funziona anche dalla tastiera.
/// «Metti sul foglio» scrive il risultato in una casella di testo della pagina.
/// </summary>
public sealed class CalculatorWindow : Window
{
    /// <summary>Un tasto: cosa c'è scritto e cosa fa, normale e con «2nd»; per RPN eventualmente un altro tasto.</summary>
    sealed record KeyDef(string Label, string Key, string? Label2 = null, string? Key2 = null, string? RpnLabel = null, string? RpnKey = null, string Kind = "fn");

    readonly CalculatorSettings _settings;
    readonly Action<string>? _insert;
    readonly Action _saveSettings;
    readonly AlgebraicCalc _alg;
    readonly RpnCalc _rpn;
    ICalc Calc => _settings.Rpn ? _rpn : _alg;

    bool _second;
    readonly StackPanel _lines = new();
    readonly TextBlock _status = new() { FontSize = 12, Foreground = Brushes.DimGray };
    readonly List<(Button Button, KeyDef Def)> _keys = new();
    readonly ToggleButton _algButton, _rpnButton, _normalButton, _fixButton, _sciButton, _degButton, _radButton;
    readonly TextBlock _digitsText = new() { FontSize = 15, MinWidth = 18, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _digitsMinus, _digitsPlus, _insertButton;
    readonly TextBlock _note = new() { FontSize = 12, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 2, 0) };

    static readonly Brush Accent = Application.Current?.TryFindResource("AccentBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x1E, 0x5A, 0x68));
    static readonly Brush AccentLight = Application.Current?.TryFindResource("AccentLightBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0xDD, 0xED, 0xF0));

    /// <param name="insert">Mette il testo sul foglio (null: niente pulsante).</param>
    /// <param name="saveSettings">Salva le impostazioni (le scelte e la posizione della finestra).</param>
    public CalculatorWindow(CalculatorSettings settings, Action<string>? insert, Action saveSettings)
    {
        _settings = settings;
        _insert = insert;
        _saveSettings = saveSettings;
        _settings.Digits = Math.Clamp(_settings.Digits, 0, CalcFormatter.MaxDigits);
        _alg = new AlgebraicCalc(settings);
        _rpn = new RpnCalc(settings);

        Title = "Calcolatrice";
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        FontFamily = new FontFamily("Segoe UI");
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xF9));
        Width = settings.Width ?? 460;
        Height = settings.Height ?? 720;
        MinWidth = 300;
        MinHeight = 420;

        var root = new StackPanel { Width = 440, Margin = new Thickness(10) };

        // modalità: algebrica / RPN, formato, decimali, angoli
        var modes = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _algButton = Segment("Algebrica", "Si scrive l'espressione e = la calcola", () => SetRpn(false));
        _rpnButton = Segment("RPN", "Notazione polacca inversa: prima i numeri (Invio fra uno e l'altro), poi l'operazione", () => SetRpn(true));
        modes.Children.Add(Group(_algButton, _rpnButton));
        _normalButton = Segment("Normale", "Tutte le cifre che servono (fino a 12)", () => SetFormat(CalcFormat.Normal));
        _fixButton = Segment("FIX", "Numero fisso di decimali, arrotondato", () => SetFormat(CalcFormat.Fix));
        _sciButton = Segment("SCI", "Notazione scientifica con il numero di decimali scelto", () => SetFormat(CalcFormat.Sci));
        modes.Children.Add(Group(_normalButton, _fixButton, _sciButton));
        _digitsMinus = SmallButton("−", "Un decimale in meno", () => SetDigits(_settings.Digits - 1));
        _digitsPlus = SmallButton("+", "Un decimale in più", () => SetDigits(_settings.Digits + 1));
        var digits = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 6), VerticalAlignment = VerticalAlignment.Center };
        digits.Children.Add(new TextBlock { Text = "decimali", FontSize = 13, Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        digits.Children.Add(_digitsMinus);
        digits.Children.Add(_digitsText);
        digits.Children.Add(_digitsPlus);
        modes.Children.Add(digits);
        _degButton = Segment("Gradi", "Angoli in gradi sessagesimali", () => SetDegrees(true));
        _radButton = Segment("Radianti", "Angoli in radianti", () => SetDegrees(false));
        modes.Children.Add(Group(_degButton, _radButton));
        root.Children.Add(modes);

        // display
        var displayGrid = new Grid();
        displayGrid.Children.Add(_lines);
        _status.HorizontalAlignment = HorizontalAlignment.Left;
        _status.VerticalAlignment = VerticalAlignment.Top;
        displayGrid.Children.Add(_status);
        root.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xD4)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Height = 150,
            Child = displayGrid,
        });

        // tasti
        var pad = new Grid();
        for (int c = 0; c < 6; c++) pad.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < 7; r++) pad.RowDefinitions.Add(new RowDefinition { Height = new GridLength(r < 3 ? 52 : 62) });
        void Add(int row, int col, KeyDef def, int colSpan = 1, int rowSpan = 1)
        {
            var b = new Button { Style = KeyStyle, Focusable = false, Margin = new Thickness(3) };
            b.Click += (_, _) => Press(def);
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            Grid.SetColumnSpan(b, colSpan);
            Grid.SetRowSpan(b, rowSpan);
            pad.Children.Add(b);
            _keys.Add((b, def));
        }
        Add(0, 0, new KeyDef("2nd", "2nd", Kind: "second"));
        Add(0, 1, new KeyDef("sin", Sin, "sin⁻¹", Asin));
        Add(0, 2, new KeyDef("cos", Cos, "cos⁻¹", Acos));
        Add(0, 3, new KeyDef("tan", Tan, "tan⁻¹", Atan));
        Add(0, 4, new KeyDef("π", Pi, "e", CalcKeys.E));
        Add(0, 5, new KeyDef("C", Clear, Kind: "clear"));
        Add(1, 0, new KeyDef("x²", Sq, "x³", Cube));
        Add(1, 1, new KeyDef("√x", Sqrt, "∛x", Cbrt));
        Add(1, 2, new KeyDef("yˣ", Pow, "ˣ√y", Root));
        Add(1, 3, new KeyDef("1/x", Inv, "|x|", Abs));
        Add(1, 4, new KeyDef("ln", Ln, "eˣ", Exp));
        Add(1, 5, new KeyDef("log", Log, "10ˣ", Pow10));
        Add(2, 0, new KeyDef("n!", Fact));
        Add(2, 1, new KeyDef("nCr", Ncr, "nPr", Npr));
        Add(2, 2, new KeyDef("(", LPar, RpnLabel: "x↔y", RpnKey: Swap));
        Add(2, 3, new KeyDef(")", RPar, RpnLabel: "R↓", RpnKey: RollDown));
        Add(2, 4, new KeyDef("×10ˣ", Eex));
        Add(2, 5, new KeyDef("⌫", Back, Kind: "clear"));
        string[] digitsGrid = { "789", "456", "123" };
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
            {
                var d = digitsGrid[r][c].ToString();
                Add(3 + r, c, new KeyDef(d, d, Kind: "digit"));
            }
        Add(3, 3, new KeyDef("÷", Div, Kind: "op"));
        Add(4, 3, new KeyDef("×", Mul, Kind: "op"));
        Add(5, 3, new KeyDef("−", Sub, Kind: "op"));
        Add(6, 3, new KeyDef("+", CalcKeys.Add, Kind: "op"));
        Add(3, 4, new KeyDef("±", Chs));
        Add(4, 4, new KeyDef("%", Pct));
        Add(3, 5, new KeyDef("Ans", CalcKeys.Ans, RpnLabel: "LASTx", RpnKey: CalcKeys.LastX));
        Add(4, 5, new KeyDef("Arrot.", Rnd));
        Add(5, 4, new KeyDef("=", Enter, RpnLabel: "Invio", Kind: "enter"), colSpan: 2, rowSpan: 2);
        Add(6, 0, new KeyDef("0", "0", Kind: "digit"), colSpan: 2);
        Add(6, 2, new KeyDef(",", Dot, Kind: "digit"));
        root.Children.Add(pad);

        // sotto: metti sul foglio, copia
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 3, 0) };
        _insertButton = new Button { Content = "Metti sul foglio", Height = 38, Padding = new Thickness(14, 0, 14, 0), Focusable = false, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Scrive il risultato in una casella di testo della pagina" };
        if (Application.Current?.TryFindResource("AccentButton") is Style accent) _insertButton.Style = accent;
        _insertButton.Click += (_, _) => { if (Calc.TextForPage() is { } t) _insert?.Invoke(t); };
        if (_insert == null) _insertButton.Visibility = Visibility.Collapsed;
        var copy = new Button { Content = "Copia", Height = 38, Padding = new Thickness(14, 0, 14, 0), Focusable = false, ToolTip = "Copia il risultato negli appunti" };
        if (Application.Current?.TryFindResource("PlainButton") is Style plain) copy.Style = plain;
        copy.Click += (_, _) => { if (Calc.TextForPage() is { } t) try { Clipboard.SetText(t); } catch { } };
        bottom.Children.Add(_insertButton);
        bottom.Children.Add(copy);
        root.Children.Add(bottom);
        root.Children.Add(_note);

        Content = new Viewbox { Child = root, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };

        TextInput += OnTextInput;
        PreviewKeyDown += OnKeyDown;
        Closing += (_, _) => SavePlacement();
        Refresh();
    }

    /// <summary>Accanto al bordo destro della finestra principale, o dov'era l'ultima volta (se è ancora su uno schermo).</summary>
    public void Place(Window owner)
    {
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = SystemParameters.VirtualScreenWidth > 0
            ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
            : SystemParameters.WorkArea;
        if (_settings.Left is { } l && _settings.Top is { } t && area.Contains(new Point(l + 40, t + 20)))
        {
            Left = l;
            Top = t;
            return;
        }
        var work = SystemParameters.WorkArea;
        Height = Math.Min(Height, work.Height - 40);
        double ownerRight = owner.WindowState == WindowState.Maximized ? work.Right : owner.Left + owner.ActualWidth;
        double ownerTop = owner.WindowState == WindowState.Maximized ? work.Top : owner.Top;
        Left = Math.Max(work.Left, ownerRight - Width - 30);
        Top = Math.Max(work.Top, ownerTop + 150);
    }

    void SavePlacement()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Width = ActualWidth;
            _settings.Height = ActualHeight;
        }
        _saveSettings();
    }

    void Press(KeyDef def)
    {
        if (def.Key == "2nd") { _second = !_second; Refresh(); return; }
        string key = _settings.Rpn && def.RpnKey != null ? def.RpnKey
            : _second && def.Key2 != null ? def.Key2
            : def.Key;
        _second = false;
        Calc.Press(key);
        Refresh();
    }

    /// <summary>Per le prove: preme dei tasti («2nd» compreso).</summary>
    internal void PressForTest(params string[] keys)
    {
        foreach (var k in keys)
            if (k == "2nd") { _second = !_second; Refresh(); }
            else PressKey(k);
    }

    void PressKey(string key)
    {
        _second = false;
        Calc.Press(key);
        Refresh();
    }

    void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (var ch in e.Text)
        {
            string? key = ch switch
            {
                >= '0' and <= '9' => ch.ToString(),
                ',' or '.' => Dot,
                '+' => Add,
                '-' => Sub,
                '*' or 'x' => Mul,
                '/' or ':' => Div,
                '^' => Pow,
                '(' => LPar,
                ')' => RPar,
                '!' => Fact,
                '%' => Pct,
                'e' or 'E' => Eex,
                'p' => Pi,
                '=' => Enter,
                _ => null,
            };
            if (key != null) PressKey(key);
        }
        e.Handled = true;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        string? key = e.Key switch
        {
            Key.Enter or Key.Return => Enter,
            Key.Back => Back,
            Key.Escape or Key.Delete => Clear,
            _ => null,
        };
        if (key == null) return;
        PressKey(key);
        e.Handled = true;
    }

    void SetRpn(bool rpn) { _settings.Rpn = rpn; _second = false; Refresh(); }
    void SetFormat(CalcFormat f) { _settings.Format = f; Refresh(); }
    void SetDigits(int d) { _settings.Digits = Math.Clamp(d, 0, CalcFormatter.MaxDigits); Refresh(); }
    void SetDegrees(bool deg) { _settings.Degrees = deg; Refresh(); }

    void Refresh()
    {
        _algButton.IsChecked = !_settings.Rpn;
        _rpnButton.IsChecked = _settings.Rpn;
        _normalButton.IsChecked = _settings.Format == CalcFormat.Normal;
        _fixButton.IsChecked = _settings.Format == CalcFormat.Fix;
        _sciButton.IsChecked = _settings.Format == CalcFormat.Sci;
        _degButton.IsChecked = _settings.Degrees;
        _radButton.IsChecked = !_settings.Degrees;
        bool digitsUsed = _settings.Format != CalcFormat.Normal;
        _digitsText.Text = _settings.Digits.ToString();
        _digitsText.Opacity = digitsUsed ? 1 : 0.4;
        _digitsMinus.IsEnabled = digitsUsed && _settings.Digits > 0;
        _digitsPlus.IsEnabled = digitsUsed && _settings.Digits < CalcFormatter.MaxDigits;

        foreach (var (b, def) in _keys)
        {
            bool rpnKey = _settings.Rpn && def.RpnLabel != null;
            string label = rpnKey ? def.RpnLabel! : def.Label;
            b.Tag = def.Kind == "second" && _second ? "secondOn" : def.Kind;
            b.FontSize = def.Kind is "digit" or "op" || label == "=" ? 26 : label.Length > 3 ? 15 : 19;
            b.Foreground = def.Kind == "enter" || def.Kind == "second" && _second ? Brushes.White : Brushes.Black;
            if (def.Label2 == null || rpnKey)
            {
                b.Content = label;
                continue;
            }
            // la seconda funzione sempre scritta in piccolo sopra, come sulle calcolatrici vere;
            // con «2nd» acceso è lei in evidenza e la funzione normale si attenua
            var main = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, Opacity = _second ? 0.35 : 1 };
            var second = new TextBlock
            {
                Text = def.Label2,
                FontSize = 12.5,
                Foreground = Accent,
                FontWeight = _second ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, -2),
            };
            var stack = new StackPanel { Margin = new Thickness(0, -3, 0, 0) };
            stack.Children.Add(second);
            stack.Children.Add(main);
            b.Content = stack;
        }

        _status.Text = (_settings.Rpn ? "RPN" : "ALG") + "   " +
                       (_settings.Format switch { CalcFormat.Fix => "FIX " + _settings.Digits, CalcFormat.Sci => "SCI " + _settings.Digits, _ => "" }) +
                       (_settings.Format == CalcFormat.Normal ? "" : "   ") + (_settings.Degrees ? "GRADI" : "RAD") +
                       (_second ? "   2nd" : "");

        _lines.Children.Clear();
        var lines = Calc.Display();
        if (_settings.Rpn)
        {
            _lines.Margin = new Thickness(0, 14, 0, 0);
            for (int i = 0; i < lines.Count; i++)
            {
                bool isX = i == lines.Count - 1;
                _lines.Children.Add(Line(lines[i], isX ? 34 : 17, isX ? 50 : 24));
            }
        }
        else
        {
            _lines.Margin = new Thickness(0, 22, 0, 0);
            _lines.Children.Add(Line(lines[0], 20, 34, gray: true));
            _lines.Children.Add(Line(lines[1], 40, 64));
        }
        _insertButton.IsEnabled = Calc.TextForPage() != null;
        _note.Text = _settings.Rpn
            ? "RPN: scrivi il primo numero, Invio, il secondo, poi l'operazione (3 Invio 4 + → 7)."
            : "";
        _note.Visibility = _settings.Rpn ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Una riga del display, che si rimpicciolisce se il testo non ci sta.</summary>
    static UIElement Line(CalcLine line, double size, double height, bool gray = false)
    {
        var text = new TextBlock
        {
            Text = line.Text,
            FontSize = line.Error ? Math.Min(size, 22) : size,
            Foreground = line.Error ? new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)) : gray ? Brushes.DimGray : Brushes.Black,
            FontWeight = line.Error ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var fit = new Viewbox { Child = text, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Right };
        var grid = new Grid { Height = height };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(line.Label == "" ? 0 : 26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (line.Label != "")
            grid.Children.Add(new TextBlock { Text = line.Label, FontSize = 14, FontStyle = FontStyles.Italic, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(fit, 1);
        grid.Children.Add(fit);
        return grid;
    }

    ToggleButton Segment(string text, string tip, Action click)
    {
        var b = new ToggleButton { Content = text, Style = SegmentStyle, Focusable = false, ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    static Button SmallButton(string text, string tip, Action click)
    {
        var b = new Button { Content = text, Style = KeyStyle, Tag = "fn", Width = 32, Height = 32, FontSize = 17, Focusable = false, ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    static Border Group(params ToggleButton[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in buttons) panel.Children.Add(b);
        return new Border
        {
            Child = panel,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xD4)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 8, 6),
            ClipToBounds = true,
            Background = Brushes.White,
        };
    }

    static readonly Style KeyStyle = (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Setter Property="Background" Value="#F1F3F4" />
          <Setter Property="Foreground" Value="Black" />
          <Setter Property="FontFamily" Value="Segoe UI" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="B" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="{TemplateBinding Background}" CornerRadius="7"
                        BorderBrush="#D5DADD" BorderThickness="1">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="Tag" Value="digit"><Setter TargetName="B" Property="Background" Value="White" /></Trigger>
                  <Trigger Property="Tag" Value="op"><Setter TargetName="B" Property="Background" Value="#DDEDF0" /></Trigger>
                  <Trigger Property="Tag" Value="clear"><Setter TargetName="B" Property="Background" Value="#F6E3E1" /></Trigger>
                  <Trigger Property="Tag" Value="enter"><Setter TargetName="B" Property="Background" Value="#1E5A68" /><Setter TargetName="B" Property="BorderBrush" Value="#1E5A68" /></Trigger>
                  <Trigger Property="Tag" Value="secondOn"><Setter TargetName="B" Property="Background" Value="#1E5A68" /><Setter TargetName="B" Property="BorderBrush" Value="#1E5A68" /></Trigger>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="B" Property="BorderBrush" Value="#1E5A68" /></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="B" Property="Opacity" Value="0.7" /></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter TargetName="B" Property="Opacity" Value="0.4" /></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    static readonly Style SegmentStyle = (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ToggleButton">
          <Setter Property="FontFamily" Value="Segoe UI" />
          <Setter Property="FontSize" Value="14" />
          <Setter Property="Foreground" Value="Black" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ToggleButton">
                <Border x:Name="B" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent" Padding="10,5,10,5">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="B" Property="Background" Value="#EEF3F4" /></Trigger>
                  <Trigger Property="IsChecked" Value="True"><Setter TargetName="B" Property="Background" Value="#1E5A68" /><Setter Property="Foreground" Value="White" /></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);
}
