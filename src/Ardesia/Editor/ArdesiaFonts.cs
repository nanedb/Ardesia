using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ardesia.Editor;

/// <summary>
/// Latin Modern, il carattere di LaTeX, incorporato in Ardesia (Assets/Fonts, licenza GUST/LPPL accanto
/// ai file: sulle LIM non è installato). È il carattere predefinito dei testi, delle formule e delle legende dei grafici.
/// Latin Modern Roman non ha il greco né i simboli matematici: quelli (∑, ∫, ≤, α…) vengono da Latin Modern Math,
/// e solo quel che manca anche lì da Cambria Math, che c'è su ogni Windows.
/// </summary>
public static class ArdesiaFonts
{
    /// <summary>Nome nell'elenco dei caratteri (scheda Home).</summary>
    public const string LatinModernName = "Latin Modern";

    const string Source = "./Assets/Fonts/#Latin Modern Roman 10, ./Assets/Fonts/#Latin Modern Math, Cambria Math, Cambria, Segoe UI Symbol";
    static readonly Uri BaseUri = new("pack://application:,,,/");

    public static FontFamily LatinModern { get; } = new(BaseUri, Source);

    /// <summary>
    /// Le lettere delle formule nel corsivo matematico di Unicode (𝑥, 𝛼…), che Latin Modern Math disegna come il corsivo
    /// di TeX (più largo e spaziato di quello del testo). Le maiuscole greche restano dritte, come in LaTeX.
    /// </summary>
    public static string MathItalic(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length * 2);
        foreach (var ch in s)
        {
            int cp = ch switch
            {
                'h' => 0x210E, // ℎ: nel blocco matematico il suo posto è vuoto
                >= 'a' and <= 'z' => 0x1D44E + (ch - 'a'),
                >= 'A' and <= 'Z' => 0x1D434 + (ch - 'A'),
                >= 'α' and <= 'ω' => 0x1D6FC + (ch - 'α'),
                'ϵ' => 0x1D716,
                'ϑ' => 0x1D717,
                'ϕ' => 0x1D719,
                'ϱ' => 0x1D71A,
                'ϖ' => 0x1D71B,
                _ => 0,
            };
            if (cp == 0) sb.Append(ch);
            else sb.Append(char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }

    /// <summary>Il carattere per un nome dell'elenco: Latin Modern dalle risorse, gli altri dal sistema.</summary>
    public static FontFamily ByName(string name) => name == LatinModernName ? LatinModern : new FontFamily(name);

    /// <summary>Il nome dell'elenco per un carattere (null se non è nell'elenco).</summary>
    public static string NameOf(FontFamily family) => IsLatinModern(family) ? LatinModernName : family.Source;

    static bool IsLatinModern(FontFamily f) => f.Source.Contains("#Latin Modern", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dopo aver letto il XAML di una casella: il carattere vi è scritto come percorso relativo alle risorse
    /// («./Assets/Fonts/#…»), che fuori da Ardesia non vuol dire niente; qui torna il carattere incorporato.
    /// </summary>
    public static void FixAfterLoad(FlowDocument doc)
    {
        if (IsLatinModern(doc.FontFamily)) doc.FontFamily = LatinModern;
        for (var p = doc.ContentStart; p != null && p.CompareTo(doc.ContentEnd) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.ElementStart) continue;
            if (p.GetAdjacentElement(LogicalDirection.Forward) is TextElement te &&
                te.ReadLocalValue(TextElement.FontFamilyProperty) is FontFamily f && IsLatinModern(f))
                te.FontFamily = LatinModern;
        }
    }
}
