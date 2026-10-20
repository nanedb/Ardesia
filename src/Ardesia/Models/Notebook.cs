using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Ardesia.Models;

public enum NodeKind { Section, Page }

public enum PageBackground { Blank, LinesNarrow, LinesWide, GridSmall, GridLarge, Dots, GridXLarge }

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Un blocco appunti: una gerarchia libera di sezioni (con sottosezioni) e pagine (con sottopagine).</summary>
public class Notebook : ObservableObject
{
    public const int CurrentFormatVersion = 1;

    string _title = "Nuovo blocco appunti";

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Title { get => _title; set => Set(ref _title, value); }
    public ObservableCollection<NotebookNode> Sections { get; set; } = new();

    /// <summary>PDF per la classe spento a mano. Di regola è attivo per ogni blocco appunti.</summary>
    public bool ClassPdfDisabled { get; set; }

    /// <summary>Dove scrivere il PDF per la classe; null = accanto al file del blocco appunti, con lo stesso nome.</summary>
    public string? ClassPdfPath { get; set; }

    /// <summary>Il file del PDF per la classe per un blocco appunti salvato in <paramref name="notebookPath"/>, o null se è spento.</summary>
    public string? ClassPdfFile(string? notebookPath) =>
        ClassPdfDisabled ? null : ClassPdfPath ?? (notebookPath != null ? Path.ChangeExtension(notebookPath, ".pdf") : null);

    /// <summary>Sezioni escluse dal PDF per la classe (con le loro sottosezioni), es. le verifiche.</summary>
    public List<Guid> ClassPdfExcluded { get; set; } = new();

    /// <summary>Le pagine che vanno nel PDF per la classe.</summary>
    public IEnumerable<NotebookNode> ClassPdfPages() =>
        AllPages().Where(p =>
        {
            for (var n = p.Parent; n != null; n = n.Parent)
                if (n.Kind == NodeKind.Section && ClassPdfExcluded.Contains(n.Id)) return false;
            return true;
        });

    /// <summary>Immagini incorporate, indicizzate per id.</summary>
    [JsonIgnore] public Dictionary<string, byte[]> Media { get; } = new();

    public IEnumerable<NotebookNode> AllNodes() => Sections.SelectMany(s => s.DescendantsAndSelf());
    public IEnumerable<NotebookNode> AllPages() => AllNodes().Where(n => n.Kind == NodeKind.Page);
    public IEnumerable<NotebookNode> AllSections() => AllNodes().Where(n => n.Kind == NodeKind.Section);

    public void FixParents()
    {
        foreach (var s in Sections) s.FixParents(null);
    }

    public ObservableCollection<NotebookNode> SiblingsOf(NotebookNode n) =>
        n.Parent == null ? Sections : n.Kind == NodeKind.Section ? n.Parent.Sections : n.Parent.Pages;

    public void Insert(NotebookNode node, NotebookNode? parent, int index = -1)
    {
        if (node.Kind == NodeKind.Page && parent == null)
            throw new InvalidOperationException("Una pagina deve stare in una sezione o in un'altra pagina.");
        node.Parent = parent;
        var list = node.Kind == NodeKind.Section ? parent?.Sections ?? Sections : parent!.Pages;
        if (index < 0 || index > list.Count) list.Add(node); else list.Insert(index, node);
    }

    public void Detach(NotebookNode node)
    {
        SiblingsOf(node).Remove(node);
        node.Parent = null;
    }

    public bool MoveUp(NotebookNode n)
    {
        var list = SiblingsOf(n);
        int i = list.IndexOf(n);
        if (i <= 0) return false;
        list.Move(i, i - 1);
        return true;
    }

    public bool MoveDown(NotebookNode n)
    {
        var list = SiblingsOf(n);
        int i = list.IndexOf(n);
        if (i < 0 || i >= list.Count - 1) return false;
        list.Move(i, i + 1);
        return true;
    }

    /// <summary>Rende il nodo figlio del fratello precedente (sottosezione / sottopagina).</summary>
    public bool Indent(NotebookNode n)
    {
        var list = SiblingsOf(n);
        int i = list.IndexOf(n);
        if (i <= 0) return false;
        var prev = list[i - 1];
        Detach(n);
        Insert(n, prev);
        prev.IsExpanded = true;
        return true;
    }

    /// <summary>Porta il nodo al livello del genitore, subito dopo di esso.</summary>
    public bool Outdent(NotebookNode n)
    {
        var parent = n.Parent;
        if (parent == null) return false;
        if (n.Kind == NodeKind.Page && parent.Kind == NodeKind.Section) return false;
        int pi = SiblingsOf(parent).IndexOf(parent);
        Detach(n);
        Insert(n, parent.Parent, pi + 1);
        return true;
    }

    public bool CanMoveAfter(NotebookNode n, NotebookNode target) =>
        n != target && n.Kind == target.Kind && !n.IsAncestorOf(target);

    public void MoveAfter(NotebookNode n, NotebookNode target)
    {
        if (!CanMoveAfter(n, target)) return;
        Detach(n);
        Insert(n, target.Parent, SiblingsOf(target).IndexOf(target) + 1);
    }

    public bool CanMoveInto(NotebookNode n, NotebookNode newParent) =>
        n != newParent && !n.IsAncestorOf(newParent) &&
        (n.Kind == NodeKind.Page || newParent.Kind == NodeKind.Section);

    public void MoveInto(NotebookNode n, NotebookNode newParent)
    {
        if (!CanMoveInto(n, newParent)) return;
        Detach(n);
        Insert(n, newParent);
        newParent.IsExpanded = true;
    }
}

public class NotebookNode : ObservableObject
{
    string _title = "";
    string _color = "#1E5A68";
    bool _isExpanded = true, _isSelected, _isEditing;

    public Guid Id { get; set; } = Guid.NewGuid();
    public NodeKind Kind { get; set; }

    public string Title
    {
        get => _title;
        set { if (Set(ref _title, value)) OnPropertyChanged(nameof(DisplayTitle)); }
    }

    [JsonIgnore]
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title
        : Kind == NodeKind.Page ? "Pagina senza titolo" : "Sezione senza titolo";

    public string Color { get => _color; set => Set(ref _color, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    [JsonIgnore] public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    [JsonIgnore] public bool IsEditing { get => _isEditing; set => Set(ref _isEditing, value); }

    /// <summary>Sottosezioni (solo per le sezioni).</summary>
    public ObservableCollection<NotebookNode> Sections { get; set; } = new();

    /// <summary>Pagine di una sezione, oppure sottopagine di una pagina.</summary>
    public ObservableCollection<NotebookNode> Pages { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PageData? Page { get; set; }

    [JsonIgnore] public NotebookNode? Parent { get; set; }
    [JsonIgnore] public NotebookNode? LastOpenedPage { get; set; }

    public static NotebookNode NewSection(string title, string color) =>
        new() { Kind = NodeKind.Section, Title = title, Color = color };

    public static NotebookNode NewPage(string title = "") =>
        new() { Kind = NodeKind.Page, Title = title, Page = new PageData() };

    /// <summary>Il nodo, poi le sue pagine (ricorsivamente), poi le sue sottosezioni.</summary>
    public IEnumerable<NotebookNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var p in Pages)
            foreach (var d in p.DescendantsAndSelf()) yield return d;
        foreach (var s in Sections)
            foreach (var d in s.DescendantsAndSelf()) yield return d;
    }

    public bool IsAncestorOf(NotebookNode? n)
    {
        for (var p = n?.Parent; p != null; p = p.Parent)
            if (p == this) return true;
        return false;
    }

    /// <summary>La sezione che contiene questa pagina (o il nodo stesso se è una sezione).</summary>
    [JsonIgnore]
    public NotebookNode? OwningSection
    {
        get
        {
            var n = this;
            while (n != null && n.Kind != NodeKind.Section) n = n.Parent;
            return n;
        }
    }

    internal void FixParents(NotebookNode? parent)
    {
        Parent = parent;
        if (Kind == NodeKind.Page) Page ??= new PageData();
        foreach (var s in Sections) s.FixParents(this);
        foreach (var p in Pages) p.FixParents(this);
    }
}

public class PageData
{
    public DateTime Created { get; set; } = DateTime.Now;
    public PageBackground Background { get; set; } = PageBackground.GridLarge;
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public List<ElementData> Elements { get; set; } = new();

    /// <summary>Tratti di penna in formato Ink Serialized Format (salvati a parte nel file).</summary>
    [JsonIgnore] public byte[]? Ink { get; set; }

    public PageData CloneData() => new()
    {
        Created = DateTime.Now,
        Background = Background,
        BackgroundColor = BackgroundColor,
        Elements = Elements.Select(e => e with { }).ToList(),
        Ink = Ink?.ToArray(),
    };
}

/// <summary>Un oggetto posizionato sulla pagina (casella di testo, immagine o grafico GeoGebra).</summary>
public record ElementData
{
    public string Type { get; set; } = "text";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    /// <summary>Per le caselle di testo: contenuto in formato XAML (FlowDocument).
    /// Per i grafici GeoGebra: la costruzione (.ggb in Base64).</summary>
    public string? Content { get; set; }

    /// <summary>Per immagini e grafici: id nel dizionario Media del blocco appunti.</summary>
    public string? MediaId { get; set; }
}
