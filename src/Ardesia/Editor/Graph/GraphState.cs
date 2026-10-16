using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ardesia.Editor.Graph;

/// <summary>Tutto ciò che serve per ridisegnare un grafico di Ardesia: salvato come JSON in <c>ElementData.Content</c>.</summary>
public sealed class GraphState
{
    public int V { get; set; } = 1;
    public List<GraphFunctionData> Functions { get; set; } = new();
    public GraphView View { get; set; } = new();
    public List<GraphParameter> Params { get; set; } = new();
    public GraphOptions Show { get; set; } = new();

    /// <summary>Colori delle curve, uno dopo l'altro: ben visibili sulla LIM.</summary>
    public static readonly string[] Palette = { "#1E5AC8", "#C62828", "#2E7D32", "#EF6C00", "#6A1B9A", "#00838F" };

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static GraphState FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new GraphState();
        try
        {
            return JsonSerializer.Deserialize<GraphState>(json, Json) ?? new GraphState();
        }
        catch (JsonException)
        {
            return new GraphState();
        }
    }

    public GraphState Clone() => FromJson(ToJson());

    /// <summary>Sostituisce le funzioni con le righe del testo, tenendo i colori già scelti per le righe rimaste uguali.</summary>
    public void SetFunctions(IEnumerable<string> lines)
    {
        var list = lines.ToList();
        var old = Functions;
        // colori delle righe che restano: non vanno dati alle righe nuove
        var reserved = old.Where(o => list.Contains(o.Text)).Select(o => o.Color).ToHashSet();
        Functions = new List<GraphFunctionData>();
        foreach (var line in list)
        {
            bool Free(string c) => Functions.All(f => f.Color != c);
            var color = old.FirstOrDefault(o => o.Text == line && Free(o.Color))?.Color
                        ?? Palette.FirstOrDefault(c => Free(c) && !reserved.Contains(c))
                        ?? Palette.FirstOrDefault(Free)
                        ?? Palette[Functions.Count % Palette.Length];
            Functions.Add(new GraphFunctionData { Text = line, Color = color });
        }
    }

    public string FunctionsText => string.Join(Environment.NewLine, Functions.Select(f => f.Text));
}

public sealed class GraphFunctionData
{
    /// <summary>La riga come l'ha scritta il docente (con x², virgola decimale…): si mostra così nella legenda.</summary>
    public string Text { get; set; } = "";
    public string Color { get; set; } = GraphState.Palette[0];
}

/// <summary>La parte di piano visibile.</summary>
public sealed class GraphView
{
    public double Xmin { get; set; } = -10;
    public double Xmax { get; set; } = 10;
    public double Ymin { get; set; } = -7.5;
    public double Ymax { get; set; } = 7.5;
}

/// <summary>Un parametro con cursore (a, b, k…).</summary>
public sealed class GraphParameter
{
    public string Name { get; set; } = "a";
    public double Value { get; set; } = 1;
    public double Min { get; set; } = -5;
    public double Max { get; set; } = 5;
    public double Step { get; set; } = 0.1;
}

/// <summary>Le caselle da spuntare: tutto spento all'inizio, tranne la griglia.</summary>
public sealed class GraphOptions
{
    public bool Zeros { get; set; }
    public bool Intersections { get; set; }
    public bool Extrema { get; set; }
    public bool Derivative { get; set; }
    public bool Sign { get; set; }
    public bool Grid { get; set; } = true;
    public bool Pi { get; set; }
}
