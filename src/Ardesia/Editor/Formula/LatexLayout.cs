using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Ardesia.Editor.Graph;
using MathBox = Ardesia.Editor.Graph.MathLayout.MathBox;

namespace Ardesia.Editor.Formula;

/// <summary>
/// Impaginatore delle formule scritte in LaTeX nelle caselle di testo: legge la sintassi usata a scuola
/// (frazioni, radici, esponenti e pedici, lettere greche, simboli, parentesi che crescono, sistemi e matrici,
/// accenti, somme, integrali e limiti) e la compone con le scatole di <see cref="MathLayout"/>.
/// Non si arrabbia mai: un comando sconosciuto resta scritto in rosso, le graffe mancanti si chiudono da sole.
/// </summary>
public sealed class LatexLayout
{
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    static readonly FontFamily MathFont = new("Cambria, Cambria Math, Segoe UI Symbol, Times New Roman");
    static readonly Brush ErrorBrush = Paint.Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));

    /// <summary>Classi di TeX: decidono gli spazi tra un pezzo e l'altro.</summary>
    enum K { Ord, Op, Bin, Rel, Open, Close, Punct, Inner }

    sealed record Atom(MathBox Box, K Kind, bool Limits = false, bool Italic = false);

    enum Face { Upright, Italic, Bold, BoldItalic }

    readonly string _s;
    readonly Brush _brush;
    readonly double _ppd, _base;
    int _i;

    LatexLayout(string latex, double size, Brush brush, double ppd)
    {
        _s = latex;
        _base = size;
        _brush = brush;
        _ppd = ppd;
    }

    /// <summary>Altezza dell'asse matematico sopra la linea di base (dove stanno le linee di frazione e il «−»).</summary>
    public static double Axis(double size) => size * 0.27;

    /// <summary>Vero se nel testo c'è qualcosa che si scrive solo in LaTeX (un comando conosciuto, ^{…}, _{…}).</summary>
    public static bool LooksLikeLatex(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '^' or '_' && i + 1 < text.Length && text[i + 1] == '{') return true;
            if (text[i] != '\\') continue;
            int j = i + 1;
            while (j < text.Length && char.IsAsciiLetter(text[j])) j++;
            if (j > i + 1 && IsKnownCommand(text.Substring(i + 1, j - i - 1))) return true;
        }
        return false;
    }

    /// <summary>Compone la formula. <paramref name="errors"/> è vero se qualcosa non si è capito.</summary>
    public static MathBox Build(string latex, double size, Brush brush, double ppd, out bool errors)
    {
        var p = new LatexLayout(latex, size, brush, ppd);
        var atoms = new List<Atom>();
        while (true)
        {
            var (list, stop) = p.ParseList(size, closers: false);
            atoms.AddRange(list);
            if (stop == "eof") break;
            p.SkipTerminator(stop); // graffa chiusa di troppo, «&» o «\\» fuori posto: si salta
            p.Errors = true;
        }
        errors = p.Errors;
        return p.MakeRow(atoms, size);
    }

    bool Errors { get; set; }

    // ───────────────────────────── Lettura ─────────────────────────────

    bool Eof => _i >= _s.Length;
    char Cur => _s[_i];

    void SkipWs()
    {
        while (!Eof && char.IsWhiteSpace(Cur)) _i++;
    }

    /// <summary>Il comando che comincia qui («\frac» → «frac», «\,» → «,»), senza consumarlo.</summary>
    string? PeekCommand()
    {
        if (Eof || Cur != '\\' || _i + 1 >= _s.Length) return null;
        int j = _i + 1;
        if (!char.IsAsciiLetter(_s[j])) return _s[j].ToString();
        while (j < _s.Length && char.IsAsciiLetter(_s[j])) j++;
        return _s.Substring(_i + 1, j - _i - 1);
    }

    string ReadCommand()
    {
        var c = PeekCommand()!;
        _i += 1 + c.Length;
        return c;
    }

    /// <summary>Che cosa ferma la lista qui: «}», «&», «nl» (\\), «right», «end», una parentesi chiusa, «eof».</summary>
    string? Terminator(bool closers)
    {
        if (Eof) return "eof";
        if (Cur == '}') return "}";
        if (Cur == '&') return "&";
        if (closers && Cur is ')' or ']') return Cur.ToString();
        if (Cur == '\\')
        {
            var c = PeekCommand();
            if (c == "\\") return "nl";
            if (c is "right" or "end") return c;
        }
        return null;
    }

    void SkipTerminator(string t)
    {
        switch (t)
        {
            case "nl": _i += 2; break;
            case "right":
                _i += 6;
                ReadDelimiter();
                break;
            case "end":
                _i += 4;
                ReadRawGroup();
                break;
            case "eof": break;
            default: _i++; break;
        }
    }

    /// <summary>Il contenuto grezzo di {…} (o il carattere / comando che segue), per \text, \begin e simili.</summary>
    string ReadRawGroup()
    {
        SkipWs();
        if (Eof) return "";
        if (Cur != '{')
        {
            if (Cur == '\\') return "\\" + ReadCommand();
            return _s[_i++].ToString();
        }
        int depth = 0, start = ++_i;
        while (!Eof)
        {
            if (Cur == '\\') { _i += 2; continue; }
            if (Cur == '{') depth++;
            else if (Cur == '}' && depth-- == 0) break;
            _i++;
        }
        var text = _s.Substring(start, Math.Min(_i, _s.Length) - start);
        if (!Eof) _i++;
        else Errors = true;
        return text;
    }

    /// <summary>Il simbolo dopo \left, \right, \big…: «(», «\{», «|», «.» (nessuno).</summary>
    string ReadDelimiter()
    {
        SkipWs();
        if (Eof) return ".";
        if (Cur == '\\')
        {
            var c = ReadCommand();
            return c switch
            {
                "{" or "lbrace" => "{",
                "}" or "rbrace" => "}",
                "|" or "Vert" => "‖",
                "vert" or "mid" => "|",
                "langle" => "⟨",
                "rangle" => "⟩",
                "lfloor" => "⌊",
                "rfloor" => "⌋",
                "lceil" => "⌈",
                "rceil" => "⌉",
                _ => ".",
            };
        }
        return _s[_i++] switch { '<' => "⟨", '>' => "⟩", var ch => ch.ToString() };
    }

    // ───────────────────────────── Liste e atomi ─────────────────────────────

    /// <summary>Legge pezzi fino a un terminatore (che non consuma). Con <paramref name="closers"/> anche «)» e «]» fermano.</summary>
    (List<Atom> Atoms, string Stop) ParseList(double size, bool closers)
    {
        var atoms = new List<Atom>();
        while (true)
        {
            SkipWs();
            if (Terminator(closers) is { } stop) return (atoms, stop);

            // parentesi tonde e quadre: crescono con quello che contengono (i docenti non scrivono sempre \left)
            if (Cur is '(' or '[')
            {
                char open = Cur;
                _i++;
                var (inner, end) = ParseList(size, closers: true);
                if (end is ")" or "]")
                {
                    _i++;
                    var box = Delimited(open.ToString(), MakeRow(inner, size), end, size);
                    atoms.Add(WithScripts(new Atom(box, K.Ord), size)); // f(x), 2(x+1): niente spazi intorno
                }
                else
                {
                    atoms.Add(new Atom(Glyph(open.ToString(), size, Face.Upright), K.Open));
                    atoms.AddRange(inner);
                }
                continue;
            }

            if (ParseAtom(size) is { } atom) atoms.Add(WithScripts(atom, size));
        }
    }

    /// <summary>Un pezzo: carattere, numero, gruppo {…} o comando. Null se non disegna niente (\displaystyle…).</summary>
    Atom? ParseAtom(double size)
    {
        SkipWs();
        if (Eof) return null;
        char c = Cur;

        if (c == '{')
        {
            _i++;
            var (inner, stop) = ParseList(size, closers: false);
            if (stop == "}") _i++;
            else Errors = true;
            return new Atom(MakeRow(inner, size), K.Ord);
        }
        if (c == '\\') return ParseCommand(size);
        if (c is '^' or '_')
        {
            // esponente senza base: x^2 dopo uno spazio, o ^ all'inizio
            return new Atom(new MathLayout.RowOf(), K.Ord);
        }

        // numeri, anche con la virgola all'italiana (3,14) o col punto
        if (char.IsAsciiDigit(c))
        {
            int start = _i;
            while (!Eof && (char.IsAsciiDigit(Cur) ||
                            Cur is ',' or '.' && _i + 1 < _s.Length && char.IsAsciiDigit(_s[_i + 1]))) _i++;
            return new Atom(Glyph(_s[start.._i], size, Face.Upright), K.Ord);
        }

        _i++;
        if (char.IsLetter(c))
            return new Atom(Glyph(c.ToString(), size, Face.Italic), K.Ord, Italic: true);

        return c switch
        {
            '+' => new Atom(Glyph("+", size, Face.Upright), K.Bin),
            '-' or '−' => new Atom(Glyph("−", size, Face.Upright), K.Bin),
            '*' => new Atom(Glyph("∗", size, Face.Upright), K.Bin),
            '=' or '<' or '>' or ':' => new Atom(Glyph(c.ToString(), size, Face.Upright), K.Rel),
            ')' or ']' or '!' or '?' => new Atom(Glyph(c.ToString(), size, Face.Upright), K.Close),
            ',' or ';' => new Atom(Glyph(c.ToString(), size, Face.Upright), K.Punct),
            '\'' => new Atom(Glyph("′", size, Face.Upright), K.Ord),
            '~' => new Atom(Space(size * 0.33), K.Ord),
            _ when "±∓×÷·∪∩∧∨".Contains(c) => new Atom(Glyph(c.ToString(), size, Face.Upright), K.Bin),
            _ when "≤≥≠≈≡→←⇒⇐⇔∈∉⊂⊆⊃⊇∼≅∝⊥∥".Contains(c) => new Atom(Glyph(c.ToString(), size, Face.Upright), K.Rel),
            _ => new Atom(Glyph(c.ToString(), size, Face.Upright), K.Ord),
        };
    }

    /// <summary>Esponenti, pedici e apici («'») dopo un pezzo.</summary>
    Atom WithScripts(Atom atom, double size)
    {
        MathBox? sup = null, sub = null;
        int primes = 0;
        while (true)
        {
            int save = _i;
            SkipWs();
            if (Eof) { _i = save; break; }
            if (Cur == '\'') { primes++; _i++; continue; }
            if (Cur == '^' && sup == null) { _i++; sup = ParseArg(ScriptSize(size)); continue; }
            if (Cur == '_' && sub == null) { _i++; sub = ParseArg(ScriptSize(size)); continue; }
            if (Cur == '\\' && PeekCommand() is "limits" or "nolimits")
            {
                bool limits = ReadCommand() == "limits";
                atom = atom with { Limits = limits };
                continue;
            }
            _i = save;
            break;
        }
        if (primes > 0)
        {
            var p = Glyph(new string('′', primes), ScriptSize(size) * 1.15, Face.Upright);
            sup = sup == null ? p : new MathLayout.RowOf(p, sup);
        }
        if (sup == null && sub == null) return atom;
        MathBox box = atom.Limits
            ? new LimitsBox(atom.Box, sup, sub, size)
            : new ScriptBox(atom.Box, sup, sub, size, atom.Italic);
        return atom with { Box = box };
    }

    /// <summary>Argomento di un comando o di ^ e _: {…} oppure un solo pezzo.</summary>
    MathBox ParseArg(double size)
    {
        SkipWs();
        if (!Eof && Cur == '{')
        {
            _i++;
            var (inner, stop) = ParseList(size, closers: false);
            if (stop == "}") _i++;
            else Errors = true;
            return MakeRow(inner, size);
        }
        if (Terminator(false) != null)
        {
            Errors = true;
            return Space(0);
        }
        if (!Eof && Cur is '(' or '[') return Glyph(_s[_i++].ToString(), size, Face.Upright);
        return ParseAtom(size)?.Box ?? Space(0);
    }

    double ScriptSize(double size) => Math.Max(size * 0.7, _base * 0.5);

    /// <summary>Grandezza di numeratore e denominatore di \frac: un po' più piccoli in riga, come in un esponente se già piccoli.</summary>
    double FracSize(double size) => IsScript(size) ? ScriptSize(size) : size * 0.9;

    /// <summary>Esponenti, pedici e frazioni dentro le frazioni: lì gli spazi intorno a + e = si tolgono, come in TeX.</summary>
    bool IsScript(double size) => size < _base * 0.8;

    // ───────────────────────────── Comandi ─────────────────────────────

    static readonly Dictionary<string, string> Greek = new()
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ϵ", ["varepsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["varpi"] = "ϖ", ["rho"] = "ρ",
        ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "ϕ",
        ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
    };

    static readonly Dictionary<string, string> GreekUpper = new()
    {
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π",
        ["Sigma"] = "Σ", ["Upsilon"] = "Υ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
    };

    static readonly Dictionary<string, (string Text, K Kind)> Symbols = new()
    {
        // operazioni
        ["cdot"] = ("·", K.Bin), ["times"] = ("×", K.Bin), ["div"] = ("÷", K.Bin), ["pm"] = ("±", K.Bin),
        ["mp"] = ("∓", K.Bin), ["ast"] = ("∗", K.Bin), ["circ"] = ("∘", K.Bin), ["cup"] = ("∪", K.Bin),
        ["cap"] = ("∩", K.Bin), ["setminus"] = ("∖", K.Bin), ["wedge"] = ("∧", K.Bin), ["land"] = ("∧", K.Bin),
        ["vee"] = ("∨", K.Bin), ["lor"] = ("∨", K.Bin), ["oplus"] = ("⊕", K.Bin), ["otimes"] = ("⊗", K.Bin),
        ["bullet"] = ("•", K.Bin),
        // relazioni
        ["leq"] = ("≤", K.Rel), ["le"] = ("≤", K.Rel), ["leqslant"] = ("≤", K.Rel), ["geq"] = ("≥", K.Rel),
        ["ge"] = ("≥", K.Rel), ["geqslant"] = ("≥", K.Rel), ["neq"] = ("≠", K.Rel), ["ne"] = ("≠", K.Rel),
        ["approx"] = ("≈", K.Rel), ["equiv"] = ("≡", K.Rel), ["sim"] = ("∼", K.Rel), ["simeq"] = ("≃", K.Rel),
        ["cong"] = ("≅", K.Rel), ["propto"] = ("∝", K.Rel), ["in"] = ("∈", K.Rel), ["notin"] = ("∉", K.Rel),
        ["ni"] = ("∋", K.Rel), ["subset"] = ("⊂", K.Rel), ["subseteq"] = ("⊆", K.Rel), ["supset"] = ("⊃", K.Rel),
        ["supseteq"] = ("⊇", K.Rel), ["to"] = ("→", K.Rel), ["rightarrow"] = ("→", K.Rel),
        ["leftarrow"] = ("←", K.Rel), ["gets"] = ("←", K.Rel), ["leftrightarrow"] = ("↔", K.Rel),
        ["Rightarrow"] = ("⇒", K.Rel), ["Leftarrow"] = ("⇐", K.Rel), ["Leftrightarrow"] = ("⇔", K.Rel),
        ["implies"] = ("⟹", K.Rel), ["impliedby"] = ("⟸", K.Rel), ["iff"] = ("⟺", K.Rel),
        ["longrightarrow"] = ("⟶", K.Rel), ["Longrightarrow"] = ("⟹", K.Rel), ["mapsto"] = ("↦", K.Rel),
        ["perp"] = ("⊥", K.Rel), ["parallel"] = ("∥", K.Rel), ["mid"] = ("|", K.Rel), ["ll"] = ("≪", K.Rel),
        ["gg"] = ("≫", K.Rel), ["nless"] = ("≮", K.Rel), ["ngtr"] = ("≯", K.Rel), ["uparrow"] = ("↑", K.Rel),
        ["downarrow"] = ("↓", K.Rel), ["nearrow"] = ("↗", K.Rel), ["searrow"] = ("↘", K.Rel),
        // simboli
        ["infty"] = ("∞", K.Ord), ["partial"] = ("∂", K.Ord), ["nabla"] = ("∇", K.Ord), ["emptyset"] = ("∅", K.Ord),
        ["varnothing"] = ("∅", K.Ord), ["forall"] = ("∀", K.Ord), ["exists"] = ("∃", K.Ord), ["nexists"] = ("∄", K.Ord),
        ["neg"] = ("¬", K.Ord), ["lnot"] = ("¬", K.Ord), ["angle"] = ("∠", K.Ord), ["measuredangle"] = ("∡", K.Ord),
        ["triangle"] = ("△", K.Ord), ["square"] = ("□", K.Ord), ["degree"] = ("°", K.Ord), ["prime"] = ("′", K.Ord),
        ["ldots"] = ("…", K.Inner), ["dots"] = ("…", K.Inner), ["cdots"] = ("⋯", K.Inner), ["vdots"] = ("⋮", K.Ord),
        ["ddots"] = ("⋱", K.Ord), ["hbar"] = ("ℏ", K.Ord), ["ell"] = ("ℓ", K.Ord), ["Re"] = ("ℜ", K.Ord),
        ["Im"] = ("ℑ", K.Ord), ["aleph"] = ("ℵ", K.Ord), ["%"] = ("%", K.Ord), ["$"] = ("$", K.Ord),
        ["#"] = ("#", K.Ord), ["&"] = ("&", K.Ord), ["_"] = ("_", K.Ord), ["backslash"] = ("\\", K.Ord),
        ["{"] = ("{", K.Open), ["}"] = ("}", K.Close), ["lbrace"] = ("{", K.Open), ["rbrace"] = ("}", K.Close),
        ["langle"] = ("⟨", K.Open), ["rangle"] = ("⟩", K.Close), ["lfloor"] = ("⌊", K.Open), ["rfloor"] = ("⌋", K.Close),
        ["lceil"] = ("⌈", K.Open), ["rceil"] = ("⌉", K.Close), ["vert"] = ("|", K.Ord), ["|"] = ("‖", K.Ord),
        ["Vert"] = ("‖", K.Ord), ["colon"] = (":", K.Punct), ["euro"] = ("€", K.Ord), ["checkmark"] = ("✓", K.Ord),
        ["star"] = ("⋆", K.Bin), ["dagger"] = ("†", K.Ord),
    };

    /// <summary>Nomi di funzione dritti; quelli con i limiti sotto (\lim_{x\to 0}) in <see cref="LimitNames"/>.</summary>
    static readonly HashSet<string> FunctionNames = new()
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "coth",
        "log", "ln", "lg", "exp", "det", "dim", "gcd", "arg", "deg", "ker", "hom", "Pr",
        // all'italiana
        "sen", "tg", "cotg", "arcsen", "arctg", "senh", "tgh", "mcd", "mcm",
    };

    static readonly HashSet<string> LimitNames = new() { "lim", "max", "min", "sup", "inf", "limsup", "liminf" };

    static readonly Dictionary<string, (string Glyph, bool Limits)> BigOps = new()
    {
        ["sum"] = ("∑", true), ["prod"] = ("∏", true), ["coprod"] = ("∐", true), ["bigcup"] = ("⋃", true),
        ["bigcap"] = ("⋂", true), ["int"] = ("∫", false), ["iint"] = ("∬", false), ["iiint"] = ("∭", false),
        ["oint"] = ("∮", false),
    };

    static readonly Dictionary<string, double> Spaces = new()
    {
        [","] = 0.167, ["thinspace"] = 0.167, [":"] = 0.222, [">"] = 0.222, ["medspace"] = 0.222, [";"] = 0.278,
        ["thickspace"] = 0.278, [" "] = 0.333, ["quad"] = 1, ["qquad"] = 2, ["!"] = -0.167, ["enspace"] = 0.5,
    };

    static readonly HashSet<string> Structural = new()
    {
        "frac", "dfrac", "tfrac", "cfrac", "binom", "dbinom", "tbinom", "sqrt", "left", "right", "begin", "end",
        "text", "textrm", "mathrm", "mbox", "textit", "mathit", "textbf", "mathbf", "boldsymbol", "bm", "mathbb",
        "mathcal", "operatorname", "vec", "overrightarrow", "overleftarrow", "overline", "bar", "hat", "widehat",
        "tilde", "widetilde", "dot", "ddot", "underline", "boxed", "displaystyle", "textstyle", "limits",
        "nolimits", "big", "Big", "bigg", "Bigg", "bigl", "bigr", "Bigl", "Bigr", "biggl", "biggr", "not",
        "overbrace", "underbrace", "stackrel", "overset", "underset", "pmod", "bmod", "mod", "hline", "phantom",
    };

    static bool IsKnownCommand(string c) =>
        Greek.ContainsKey(c) || GreekUpper.ContainsKey(c) || Symbols.ContainsKey(c) || FunctionNames.Contains(c) ||
        LimitNames.Contains(c) || BigOps.ContainsKey(c) || Spaces.ContainsKey(c) || Structural.Contains(c);

    Atom? ParseCommand(double size)
    {
        var c = ReadCommand();

        if (Greek.TryGetValue(c, out var g)) return new Atom(Glyph(g, size, Face.Italic), K.Ord, Italic: true);
        if (GreekUpper.TryGetValue(c, out var gu)) return new Atom(Glyph(gu, size, Face.Upright), K.Ord);
        if (Symbols.TryGetValue(c, out var sym)) return new Atom(Glyph(sym.Text, size, Face.Upright), sym.Kind);
        if (Spaces.TryGetValue(c, out var em)) return new Atom(Space(size * em), K.Ord);
        if (FunctionNames.Contains(c)) return new Atom(Glyph(c, size, Face.Upright), K.Op);
        if (LimitNames.Contains(c)) return new Atom(Glyph(c, size, Face.Upright), K.Op, Limits: !IsScript(size));
        if (BigOps.TryGetValue(c, out var big))
            return new Atom(new BigOpBox(big.Glyph, size, big.Limits ? 1.35 : 1.6, this), K.Op, Limits: big.Limits && !IsScript(size));

        switch (c)
        {
            case "frac" or "dfrac" or "tfrac" or "cfrac":
            {
                double s = c switch { "dfrac" or "cfrac" => size, "tfrac" => ScriptSize(size), _ => FracSize(size) };
                var num = ParseArg(s);
                var den = ParseArg(s);
                return new Atom(new MathLayout.FracBox(num, den, size, _brush), K.Inner);
            }
            case "binom" or "dbinom" or "tbinom":
            {
                double s = c == "dbinom" ? size : c == "tbinom" ? ScriptSize(size) : FracSize(size);
                var top = ParseArg(s);
                var bottom = ParseArg(s);
                var stack = new GridBox(new[] { new[] { top }, new[] { bottom } }, size, _ => 0.5, 0, 0.1);
                return new Atom(Delimited("(", stack, ")", size, forceDrawn: true), K.Inner);
            }
            case "sqrt":
            {
                MathBox? index = null;
                SkipWs();
                if (!Eof && Cur == '[')
                {
                    _i++;
                    var (inner, stop) = ParseList(ScriptSize(ScriptSize(size)), closers: true);
                    if (stop == "]") _i++;
                    index = MakeRow(inner, ScriptSize(ScriptSize(size)));
                }
                return new Atom(new MathLayout.RootBox(ParseArg(size), index, size, _brush), K.Ord);
            }
            case "left":
            {
                var open = ReadDelimiter();
                var (inner, stop) = ParseList(size, closers: false);
                string close = ".";
                if (stop == "right")
                {
                    _i += 6;
                    close = ReadDelimiter();
                }
                else Errors = true;
                return new Atom(Delimited(open, MakeRow(inner, size), close, size), K.Ord);
            }
            case "big" or "Big" or "bigg" or "Bigg" or "bigl" or "bigr" or "Bigl" or "Bigr" or "biggl" or "biggr":
            {
                var d = ReadDelimiter();
                double h = size * (c.StartsWith("bigg") || c.StartsWith("Bigg") ? 2.4 : c.StartsWith("Big") ? 1.8 : 1.3);
                var box = new DelimBox(d, h, size, this);
                return new Atom(box, c.EndsWith('r') ? K.Close : c.EndsWith('l') ? K.Open : K.Ord);
            }
            case "begin":
                return new Atom(ParseEnvironment(ReadRawGroup().Trim(), size), K.Inner);
            case "text" or "textrm" or "mbox" or "textup" or "textnormal":
                return new Atom(Glyph(ReadRawGroup().Replace("\\", ""), size, Face.Upright), K.Ord);
            case "textit":
                return new Atom(Glyph(ReadRawGroup(), size, Face.Italic), K.Ord);
            case "textbf":
                return new Atom(Glyph(ReadRawGroup(), size, Face.Bold), K.Ord);
            case "mathrm" or "operatorname":
            {
                var name = ReadRawGroup();
                bool fn = c == "operatorname";
                return new Atom(Glyph(StripBraces(name), size, Face.Upright), fn ? K.Op : K.Ord);
            }
            case "mathit":
                return new Atom(Glyph(StripBraces(ReadRawGroup()), size, Face.Italic), K.Ord, Italic: true);
            case "mathbf":
                return new Atom(Glyph(StripBraces(ReadRawGroup()), size, Face.Bold), K.Ord);
            case "boldsymbol" or "bm":
                return new Atom(Glyph(StripBraces(ReadRawGroup()), size, Face.BoldItalic), K.Ord);
            case "mathbb":
                return new Atom(Glyph(DoubleStruck(StripBraces(ReadRawGroup())), size, Face.Upright), K.Ord);
            case "mathcal":
                return new Atom(Glyph(Calligraphic(StripBraces(ReadRawGroup())), size, Face.Upright), K.Ord);
            case "vec" or "overrightarrow" or "overleftarrow" or "overline" or "bar" or "hat" or "widehat"
                or "tilde" or "widetilde" or "dot" or "ddot" or "underline":
            {
                var arg = ParseArg(size);
                bool wide = c is "overrightarrow" or "overleftarrow" or "overline" or "widehat" or "widetilde" or "underline";
                return new Atom(new AccentBox(arg, c, size, wide, _brush), K.Ord);
            }
            case "boxed":
                return new Atom(new FrameBox(ParseArg(size), size, _brush), K.Ord);
            case "overset" or "stackrel" or "underset":
            {
                var over = ParseArg(ScriptSize(size));
                var bas = ParseAtom(size) ?? new Atom(Space(0), K.Ord);
                var box = c == "underset" ? new LimitsBox(bas.Box, null, over, size) : new LimitsBox(bas.Box, over, null, size);
                return new Atom(box, bas.Kind);
            }
            case "overbrace" or "underbrace":
            {
                var arg = ParseArg(size);
                return new Atom(new AccentBox(arg, c, size, true, _brush), K.Ord, Limits: true);
            }
            case "not":
            {
                var next = ParseAtom(size);
                if (next == null) return null;
                return next with { Box = new AccentBox(next.Box, "not", size, false, _brush) };
            }
            case "pmod":
            {
                var arg = ParseArg(size);
                var row = new MathLayout.RowOf(Space(size * 0.5), Glyph("(mod ", size, Face.Upright), arg, Glyph(")", size, Face.Upright));
                return new Atom(row, K.Ord);
            }
            case "bmod" or "mod":
                return new Atom(Glyph("mod", size, Face.Upright), K.Bin);
            case "phantom":
            {
                var arg = ParseArg(size);
                return new Atom(new PhantomBox(arg), K.Ord);
            }
            case "displaystyle" or "textstyle" or "scriptstyle" or "limits" or "nolimits" or "hline" or "nonumber"
                or "notag" or "relax" or "allowbreak" or "\\":
                return null;
        }

        // comando sconosciuto: resta scritto, in rosso, così si vede l'errore di battitura
        Errors = true;
        return new Atom(Glyph("\\" + c, size, Face.Upright, ErrorBrush), K.Ord);
    }

    static string StripBraces(string s) => s.Replace("{", "").Replace("}", "").Replace("\\", "");

    static string DoubleStruck(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
            sb.Append(ch switch
            {
                'R' => "ℝ", 'N' => "ℕ", 'Z' => "ℤ", 'Q' => "ℚ", 'C' => "ℂ", 'P' => "ℙ", 'H' => "ℍ",
                >= 'A' and <= 'Z' => char.ConvertFromUtf32(0x1D538 + ch - 'A'),
                >= 'a' and <= 'z' => char.ConvertFromUtf32(0x1D552 + ch - 'a'),
                >= '0' and <= '9' => char.ConvertFromUtf32(0x1D7D8 + ch - '0'),
                _ => ch.ToString(),
            });
        return sb.ToString();
    }

    static string Calligraphic(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
            sb.Append(ch switch
            {
                'B' => "ℬ", 'E' => "ℰ", 'F' => "ℱ", 'H' => "ℋ", 'I' => "ℐ", 'L' => "ℒ", 'M' => "ℳ", 'R' => "ℛ",
                >= 'A' and <= 'Z' => char.ConvertFromUtf32(0x1D49C + ch - 'A'),
                _ => ch.ToString(),
            });
        return sb.ToString();
    }

    /// <summary>\begin{…} … \end{…}: sistemi (cases), matrici, allineamenti.</summary>
    MathBox ParseEnvironment(string name, double size)
    {
        if (name == "array") ReadRawGroup(); // l'elenco delle colonne {cc|c}: si allinea tutto al centro
        bool aligned = name is "aligned" or "align" or "align*" or "split" or "eqnarray" or "eqnarray*" or "alignat";
        if (name == "alignat") ReadRawGroup();
        var rows = new List<MathBox[]>();
        var cells = new List<MathBox>();
        while (true)
        {
            var (atoms, stop) = ParseList(size, closers: false);
            // nelle colonne dispari di «aligned» la riga comincia con «=»: serve lo spazio prima, come dopo una lettera
            bool afterOrd = aligned && cells.Count % 2 == 1;
            cells.Add(MakeRow(atoms, size, afterOrd));
            if (stop == "&") { _i++; continue; }
            if (stop == "nl")
            {
                _i += 2;
                SkipWs();
                if (!Eof && Cur == '[') ReadUntil(']'); // \\[4pt]
                rows.Add(cells.ToArray());
                cells.Clear();
                continue;
            }
            if (stop == "end")
            {
                _i += 4;
                ReadRawGroup();
            }
            else
            {
                Errors = true;
                if (stop == "}") _i++;
            }
            break;
        }
        if (cells.Count > 1 || cells.Count == 1 && cells[0].Width > 0 || rows.Count == 0) rows.Add(cells.ToArray());

        Func<int, double> align = name switch
        {
            "cases" or "dcases" => _ => 0,
            _ when aligned => col => col % 2 == 0 ? 1 : 0,
            _ => _ => 0.5,
        };
        double colGap = name is "cases" or "dcases" ? size * 1.0 : aligned ? 0 : size * 0.8;
        var grid = new GridBox(rows.ToArray(), size, align, colGap, 0.35, aligned ? size * 1.0 : 0);
        return name switch
        {
            "cases" or "dcases" => Delimited("{", grid, ".", size, forceDrawn: true),
            "pmatrix" => Delimited("(", grid, ")", size, forceDrawn: true),
            "bmatrix" => Delimited("[", grid, "]", size, forceDrawn: true),
            "Bmatrix" => Delimited("{", grid, "}", size, forceDrawn: true),
            "vmatrix" => Delimited("|", grid, "|", size, forceDrawn: true),
            "Vmatrix" => Delimited("‖", grid, "‖", size, forceDrawn: true),
            "rcases" => Delimited(".", grid, "}", size, forceDrawn: true),
            _ => grid,
        };
    }

    void ReadUntil(char end)
    {
        while (!Eof && Cur != end) _i++;
        if (!Eof) _i++;
    }

    // ───────────────────────────── Righe e spazi ─────────────────────────────

    MathBox MakeRow(List<Atom> atoms, double size, bool afterOrd = false)
    {
        if (atoms.Count == 0) return new MathLayout.RowOf();
        bool script = IsScript(size);
        // Un «+» o «−» senza niente a sinistra è un segno, non un'operazione (TeX fa lo stesso).
        var kinds = atoms.Select(a => a.Kind).ToArray();
        for (int i = 0; i < kinds.Length; i++)
        {
            if (kinds[i] != K.Bin) continue;
            var prev = i > 0 ? kinds[i - 1] : afterOrd ? K.Ord : K.Open;
            var next = i + 1 < kinds.Length ? kinds[i + 1] : K.Close;
            if (prev is K.Bin or K.Op or K.Rel or K.Open or K.Punct || next is K.Rel or K.Close or K.Punct)
                kinds[i] = K.Ord;
        }
        var items = new List<MathBox>();
        var last = afterOrd ? K.Ord : (K?)null;
        for (int i = 0; i < atoms.Count; i++)
        {
            if (last is { } l && Gap(l, kinds[i], script) is > 0 and var gap) items.Add(Space(size * gap));
            items.Add(atoms[i].Box);
            last = kinds[i];
        }
        return new MathLayout.RowOf(items.ToArray());
    }

    /// <summary>Spazio tra due pezzi, in em, secondo la tabella di TeX.</summary>
    static double Gap(K l, K r, bool script)
    {
        const double thin = 0.167, med = 0.222, thick = 0.278;
        if (r == K.Bin || l == K.Bin) return script ? 0 : med;
        if (r == K.Rel || l == K.Rel) return script || l == K.Rel && r == K.Rel ? 0 : thick;
        if (r == K.Op) return l is K.Ord or K.Op or K.Close or K.Inner ? thin : 0;
        if (l == K.Op) return r is K.Ord or K.Inner ? thin : 0;
        if (script) return 0;
        if (l == K.Punct) return thin;
        if (r == K.Inner) return l is K.Ord or K.Close or K.Inner or K.Punct ? thin : 0;
        if (l == K.Inner) return r is K.Ord or K.Open or K.Inner ? thin : 0;
        return 0;
    }

    // ───────────────────────────── Pezzi base ─────────────────────────────

    static MathBox Space(double w) => new SpaceBox(w);

    FormattedText Text(string s, double size, Face face, Brush? brush = null) =>
        new(s, It, FlowDirection.LeftToRight,
            new Typeface(MathFont, face is Face.Italic or Face.BoldItalic ? FontStyles.Italic : FontStyles.Normal,
                face is Face.Bold or Face.BoldItalic ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, brush ?? _brush, _ppd);

    MathBox Glyph(string s, double size, Face face, Brush? brush = null) => new GlyphBox(Text(s, size, face, brush), size);

    /// <summary>Contenuto tra parentesi: col carattere se è basso, disegnate alte quanto serve se è alto.</summary>
    MathBox Delimited(string open, MathBox inner, string close, double size, bool forceDrawn = false)
    {
        bool tall = forceDrawn || inner.Height > size * 1.2;
        double h = Math.Max(inner.Ascent - Axis(size), inner.Descent + Axis(size)) * 2 + size * 0.1;
        MathBox Side(string d) => d == "." ? Space(size * 0.05)
            : tall ? new DelimBox(d, h, size, this)
            : Glyph(d, size, Face.Upright);
        return new MathLayout.RowOf(Side(open), inner, Side(close));
    }

    // ───────────────────────────── Scatole ─────────────────────────────

    sealed class SpaceBox : MathBox
    {
        public SpaceBox(double w) => Width = w;
        public override void Draw(DrawingContext dc, double x, double baseline) { }
    }

    sealed class PhantomBox : MathBox
    {
        public PhantomBox(MathBox inner)
        {
            Width = inner.Width;
            Ascent = inner.Ascent;
            Descent = inner.Descent;
        }

        public override void Draw(DrawingContext dc, double x, double baseline) { }
    }

    /// <summary>Testo: misure fisse in proporzione al corpo, come in <see cref="MathLayout"/> (quelle dei caratteri sono troppo alte).</summary>
    sealed class GlyphBox : MathBox
    {
        readonly FormattedText _ft;

        public GlyphBox(FormattedText ft, double size)
        {
            _ft = ft;
            Width = ft.WidthIncludingTrailingWhitespace;
            Ascent = size * 0.72;
            Descent = size * 0.24;
        }

        public override void Draw(DrawingContext dc, double x, double baseline) => dc.DrawText(_ft, new Point(x, baseline - _ft.Baseline));
    }

    /// <summary>∑, ∫…: più grandi del testo, centrati sull'asse; misure prese dal disegno vero del carattere.</summary>
    sealed class BigOpBox : MathBox
    {
        readonly FormattedText _ft;
        readonly Rect _ink;
        readonly double _axis;

        public BigOpBox(string glyph, double size, double scale, LatexLayout p)
        {
            _ft = p.Text(glyph, size * scale, Face.Upright);
            _ink = _ft.BuildGeometry(new Point(0, 0)).Bounds;
            _axis = Axis(size);
            Width = Math.Max(_ft.WidthIncludingTrailingWhitespace, _ink.Right) + size * 0.05;
            Ascent = _ink.Height / 2 + _axis;
            Descent = _ink.Height / 2 - _axis;
        }

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            // il centro del disegno sull'asse
            double top = baseline - _axis - _ink.Height / 2;
            dc.DrawText(_ft, new Point(x, top - _ink.Top));
        }
    }

    /// <summary>Esponente e pedice a destra di un pezzo.</summary>
    sealed class ScriptBox : MathBox
    {
        readonly MathBox _base;
        readonly MathBox? _sup, _sub;
        readonly double _raise, _drop, _supGap;

        public ScriptBox(MathBox bas, MathBox? sup, MathBox? sub, double size, bool italic)
        {
            _base = bas;
            _sup = sup;
            _sub = sub;
            _supGap = italic ? size * 0.07 : size * 0.03; // correzione del corsivo: x² non deve toccare la x
            if (sup != null) _raise = Math.Max(size * 0.42, bas.Ascent - sup.Ascent * 0.45);
            if (sub != null) _drop = Math.Max(size * 0.2, bas.Descent + sub.Ascent * 0.5 - size * 0.24 * 0.5);
            if (sup != null && sub != null)
            {
                // tra esponente e pedice almeno un filo di spazio
                double gap = (_raise - sup.Descent) - (sub.Ascent - _drop);
                if (gap < size * 0.12) _drop += size * 0.12 - gap;
            }
            Width = bas.Width + Math.Max(sup == null ? 0 : _supGap + sup.Width, sub?.Width ?? 0) + size * 0.03;
            Ascent = Math.Max(bas.Ascent, sup == null ? 0 : _raise + sup.Ascent);
            Descent = Math.Max(bas.Descent, sub == null ? 0 : _drop + sub.Descent);
        }

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            _base.Draw(dc, x, baseline);
            _sup?.Draw(dc, x + _base.Width + _supGap, baseline - _raise);
            _sub?.Draw(dc, x + _base.Width, baseline + _drop);
        }
    }

    /// <summary>Limiti sopra e sotto: ∑, lim, \overset.</summary>
    sealed class LimitsBox : MathBox
    {
        readonly MathBox _base;
        readonly MathBox? _over, _under;
        readonly double _gap;

        public LimitsBox(MathBox bas, MathBox? over, MathBox? under, double size)
        {
            _base = bas;
            _over = over;
            _under = under;
            _gap = size * 0.1;
            Width = Math.Max(bas.Width, Math.Max(over?.Width ?? 0, under?.Width ?? 0));
            Ascent = bas.Ascent + (over == null ? 0 : _gap + over.Height);
            Descent = bas.Descent + (under == null ? 0 : _gap + under.Height);
        }

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            _base.Draw(dc, x + (Width - _base.Width) / 2, baseline);
            _over?.Draw(dc, x + (Width - _over.Width) / 2, baseline - _base.Ascent - _gap - _over.Descent);
            _under?.Draw(dc, x + (Width - _under.Width) / 2, baseline + _base.Descent + _gap + _under.Ascent);
        }
    }

    /// <summary>Tabella di righe e colonne (matrici, sistemi, allineamenti), centrata sull'asse.</summary>
    sealed class GridBox : MathBox
    {
        readonly MathBox[][] _rows;
        readonly double[] _colW, _rowA, _rowD;
        readonly Func<int, double> _align;
        readonly double _colGap, _rowGap, _axis, _pairGap;

        public GridBox(MathBox[][] rows, double size, Func<int, double> align, double colGap, double rowGap, double pairGap = 0)
        {
            _rows = rows;
            _align = align;
            _colGap = colGap;
            _pairGap = pairGap;
            _rowGap = size * rowGap;
            _axis = Axis(size);
            int cols = rows.Length == 0 ? 0 : rows.Max(r => r.Length);
            _colW = new double[cols];
            _rowA = new double[rows.Length];
            _rowD = new double[rows.Length];
            for (int r = 0; r < rows.Length; r++)
            {
                _rowA[r] = size * 0.72;
                _rowD[r] = size * 0.24;
                for (int c = 0; c < rows[r].Length; c++)
                {
                    _colW[c] = Math.Max(_colW[c], rows[r][c].Width);
                    _rowA[r] = Math.Max(_rowA[r], rows[r][c].Ascent);
                    _rowD[r] = Math.Max(_rowD[r], rows[r][c].Descent);
                }
            }
            Width = _colW.Sum() + Enumerable.Range(1, Math.Max(0, cols - 1)).Sum(GapBefore);
            double h = _rowA.Sum() + _rowD.Sum() + _rowGap * Math.Max(0, rows.Length - 1);
            Ascent = h / 2 + _axis;
            Descent = h / 2 - _axis;
        }

        /// <summary>In «aligned» le colonne vanno a coppie (destra & sinistra): spazio solo tra una coppia e l'altra.</summary>
        double GapBefore(int col) => _pairGap > 0 ? (col % 2 == 0 ? _pairGap : 0) : _colGap;

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            double y = baseline - Ascent;
            for (int r = 0; r < _rows.Length; r++)
            {
                double rowBase = y + _rowA[r];
                double cx = x;
                for (int c = 0; c < _colW.Length; c++)
                {
                    if (c > 0) cx += GapBefore(c);
                    if (c < _rows[r].Length)
                    {
                        var cell = _rows[r][c];
                        cell.Draw(dc, cx + (_colW[c] - cell.Width) * _align(c), rowBase);
                    }
                    cx += _colW[c];
                }
                y = rowBase + _rowD[r] + _rowGap;
            }
        }
    }

    /// <summary>Parentesi disegnata, alta <paramref name="height"/> e centrata sull'asse.</summary>
    sealed class DelimBox : MathBox
    {
        readonly string _d;
        readonly double _h, _axis;
        readonly Pen _pen;
        readonly MathBox? _glyph;

        public DelimBox(string d, double height, double size, LatexLayout p)
        {
            _d = d;
            _h = Math.Max(height, size);
            _axis = Axis(size);
            _pen = new Pen(p._brush, Math.Max(1, size * 0.065)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            _pen.Freeze();
            if (_h <= size * 1.15 && d != ".") _glyph = p.Glyph(d, size, Face.Upright);
            Width = _glyph?.Width ?? d switch
            {
                "|" => size * 0.3,
                "‖" => size * 0.45,
                "{" or "}" => size * 0.5,
                "." => size * 0.05,
                _ => size * Math.Min(0.6, 0.3 + _h / size * 0.04),
            };
            Ascent = _glyph?.Ascent ?? _h / 2 + _axis;
            Descent = _glyph?.Descent ?? _h / 2 - _axis;
        }

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            if (_glyph != null)
            {
                _glyph.Draw(dc, x, baseline);
                return;
            }
            double top = baseline - Ascent, bottom = baseline + Descent, mid = (top + bottom) / 2, w = Width;
            double pad = _pen.Thickness;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                switch (_d)
                {
                    case "(" or ")":
                    {
                        bool left = _d == "(";
                        double outer = left ? x + w * 0.25 : x + w * 0.75, inner = left ? x + w * 0.8 : x + w * 0.2;
                        g.BeginFigure(new Point(inner, top), false, false);
                        g.BezierTo(new Point(outer - (inner - outer) * 0.15, top + (bottom - top) * 0.25),
                                   new Point(outer - (inner - outer) * 0.15, bottom - (bottom - top) * 0.25),
                                   new Point(inner, bottom), true, true);
                        break;
                    }
                    case "[" or "]" or "⌊" or "⌋" or "⌈" or "⌉":
                    {
                        bool left = _d is "[" or "⌊" or "⌈";
                        double stem = left ? x + w * 0.3 : x + w * 0.7, end = left ? x + w * 0.85 : x + w * 0.15;
                        bool topBar = _d is "[" or "]" or "⌈" or "⌉", bottomBar = _d is "[" or "]" or "⌊" or "⌋";
                        g.BeginFigure(new Point(topBar ? end : stem, top), false, false);
                        if (topBar) g.LineTo(new Point(stem, top), true, true);
                        g.LineTo(new Point(stem, bottom), true, true);
                        if (bottomBar) g.LineTo(new Point(end, bottom), true, true);
                        break;
                    }
                    case "{" or "}":
                    {
                        // graffa: punte alle estremità, becco a metà
                        double s = _d == "{" ? 1 : -1;
                        double tip = _d == "{" ? x + w * 0.1 : x + w * 0.9;
                        double stem = _d == "{" ? x + w * 0.5 : x + w * 0.5;
                        double end = _d == "{" ? x + w * 0.9 : x + w * 0.1;
                        double r = Math.Min(w * 0.4, (bottom - top) * 0.1);
                        g.BeginFigure(new Point(end, top + pad / 2), false, false);
                        g.QuadraticBezierTo(new Point(stem, top + pad / 2), new Point(stem, top + r), true, true);
                        g.LineTo(new Point(stem, mid - r), true, true);
                        g.QuadraticBezierTo(new Point(stem, mid), new Point(tip, mid), true, true);
                        g.QuadraticBezierTo(new Point(stem, mid), new Point(stem, mid + r), true, true);
                        g.LineTo(new Point(stem, bottom - r), true, true);
                        g.QuadraticBezierTo(new Point(stem, bottom - pad / 2), new Point(end, bottom - pad / 2), true, true);
                        _ = s;
                        break;
                    }
                    case "|":
                        g.BeginFigure(new Point(x + w / 2, top), false, false);
                        g.LineTo(new Point(x + w / 2, bottom), true, true);
                        break;
                    case "‖":
                        g.BeginFigure(new Point(x + w * 0.3, top), false, false);
                        g.LineTo(new Point(x + w * 0.3, bottom), true, true);
                        g.BeginFigure(new Point(x + w * 0.7, top), false, false);
                        g.LineTo(new Point(x + w * 0.7, bottom), true, true);
                        break;
                    case "⟨" or "⟩":
                    {
                        bool left = _d == "⟨";
                        g.BeginFigure(new Point(left ? x + w * 0.8 : x + w * 0.2, top), false, false);
                        g.LineTo(new Point(left ? x + w * 0.2 : x + w * 0.8, mid), true, true);
                        g.LineTo(new Point(left ? x + w * 0.8 : x + w * 0.2, bottom), true, true);
                        break;
                    }
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, _pen, geo);
        }
    }

    /// <summary>Segni sopra o sotto: vettore, barra, cappello, tilde, punti, sottolineatura, graffe, barra di negazione.</summary>
    sealed class AccentBox : MathBox
    {
        readonly MathBox _inner;
        readonly string _kind;
        readonly double _size, _room;
        readonly Pen _pen;
        readonly Brush _brush;

        public AccentBox(MathBox inner, string kind, double size, bool wide, Brush brush)
        {
            _inner = inner;
            _kind = kind;
            _size = size;
            _brush = brush;
            _pen = new Pen(brush, Math.Max(1, size * 0.055)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            _pen.Freeze();
            _room = kind switch
            {
                "vec" or "overrightarrow" or "overleftarrow" => size * 0.3,
                "hat" or "widehat" or "tilde" or "widetilde" => size * 0.28,
                "overbrace" or "underbrace" => size * 0.35,
                "not" => 0,
                _ => size * 0.16,
            };
            Width = Math.Max(inner.Width, kind is "vec" or "hat" or "tilde" or "dot" or "ddot" or "bar" ? size * 0.45 : 0);
            bool below = kind is "underline" or "underbrace";
            Ascent = inner.Ascent + (below ? 0 : _room);
            Descent = inner.Descent + (below ? _room : 0);
            _ = wide;
        }

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            _inner.Draw(dc, x + (Width - _inner.Width) / 2, baseline);
            double s = _size, l = x + s * 0.06, r = x + Width - s * 0.02;
            double y = baseline - _inner.Ascent - _room * 0.45; // a metà dello spazio sopra
            // sopra una lettera sola il segno si sposta un po' a destra, come il corsivo
            if (_kind is "vec" or "hat" or "tilde" or "dot" or "ddot" or "bar" && _inner.Width < s * 0.8)
            {
                l += s * 0.08;
                r += s * 0.08;
            }
            switch (_kind)
            {
                case "vec" or "overrightarrow" or "overleftarrow":
                {
                    bool leftHead = _kind == "overleftarrow";
                    dc.DrawLine(_pen, new Point(l, y), new Point(r, y));
                    double hx = leftHead ? l : r, d = leftHead ? 1 : -1;
                    dc.DrawLine(_pen, new Point(hx, y), new Point(hx + d * s * 0.16, y - s * 0.1));
                    dc.DrawLine(_pen, new Point(hx, y), new Point(hx + d * s * 0.16, y + s * 0.1));
                    break;
                }
                case "overline" or "bar":
                    dc.DrawLine(_pen, new Point(l, y), new Point(r, y));
                    break;
                case "underline":
                {
                    double yy = baseline + _inner.Descent + _room * 0.5;
                    dc.DrawLine(_pen, new Point(l, yy), new Point(r, yy));
                    break;
                }
                case "hat" or "widehat":
                {
                    double m = (l + r) / 2, hw = Math.Min((r - l) / 2, s * (_kind == "hat" ? 0.18 : 2));
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(new Point(m - hw, y + s * 0.08), false, false);
                        g.LineTo(new Point(m, y - s * 0.08), true, true);
                        g.LineTo(new Point(m + hw, y + s * 0.08), true, true);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, _pen, geo);
                    break;
                }
                case "tilde" or "widetilde":
                {
                    double m = (l + r) / 2, hw = Math.Min((r - l) / 2, s * (_kind == "tilde" ? 0.2 : 2));
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(new Point(m - hw, y + s * 0.04), false, false);
                        g.BezierTo(new Point(m - hw / 2, y - s * 0.12), new Point(m + hw / 2, y + s * 0.12), new Point(m + hw, y - s * 0.04), true, true);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, _pen, geo);
                    break;
                }
                case "dot":
                    dc.DrawEllipse(_brush, null, new Point((l + r) / 2, y), s * 0.05, s * 0.05);
                    break;
                case "ddot":
                    dc.DrawEllipse(_brush, null, new Point((l + r) / 2 - s * 0.09, y), s * 0.05, s * 0.05);
                    dc.DrawEllipse(_brush, null, new Point((l + r) / 2 + s * 0.09, y), s * 0.05, s * 0.05);
                    break;
                case "overbrace" or "underbrace":
                {
                    bool over = _kind == "overbrace";
                    double yy = over ? baseline - _inner.Ascent - _room * 0.2 : baseline + _inner.Descent + _room * 0.2;
                    double tipY = over ? yy - _room * 0.6 : yy + _room * 0.6, mid = (l + r) / 2, half = (tipY + yy) / 2;
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        g.BeginFigure(new Point(l, yy), false, false);
                        g.QuadraticBezierTo(new Point(l, half), new Point(l + s * 0.2, half), true, true);
                        g.LineTo(new Point(mid - s * 0.2, half), true, true);
                        g.QuadraticBezierTo(new Point(mid, half), new Point(mid, tipY), true, true);
                        g.QuadraticBezierTo(new Point(mid, half), new Point(mid + s * 0.2, half), true, true);
                        g.LineTo(new Point(r - s * 0.2, half), true, true);
                        g.QuadraticBezierTo(new Point(r, half), new Point(r, yy), true, true);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, _pen, geo);
                    break;
                }
                case "not":
                {
                    double m = x + Width / 2;
                    dc.DrawLine(_pen, new Point(m + s * 0.15, baseline - s * 0.7), new Point(m - s * 0.15, baseline + s * 0.2));
                    break;
                }
            }
        }
    }

    sealed class FrameBox : MathBox
    {
        readonly MathBox _inner;
        readonly double _pad;
        readonly Pen _pen;

        public FrameBox(MathBox inner, double size, Brush brush)
        {
            _inner = inner;
            _pad = size * 0.2;
            _pen = new Pen(brush, Math.Max(1, size * 0.05));
            _pen.Freeze();
            Width = inner.Width + 2 * _pad;
            Ascent = inner.Ascent + _pad;
            Descent = inner.Descent + _pad;
        }

        public override void Draw(DrawingContext dc, double x, double baseline)
        {
            dc.DrawRectangle(null, _pen, new Rect(x + _pen.Thickness / 2, baseline - Ascent, Width - _pen.Thickness, Height));
            _inner.Draw(dc, x + _pad, baseline);
        }
    }
}
