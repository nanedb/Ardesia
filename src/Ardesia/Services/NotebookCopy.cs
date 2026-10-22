using System.IO;
using System.Text;
using Ardesia.Models;

namespace Ardesia.Services;

/// <summary>
/// «Copia in un altro blocco appunti…»: tutto il blocco, una sezione o una pagina (con le sottopagine) vanno in un
/// altro blocco, aggiunti in fondo oppure al posto di tutto il suo contenuto. Le copie hanno id nuovi (si possono
/// copiare più volte), le date delle pagine restano quelle di origine, le immagini e i grafici vanno con le pagine.
/// </summary>
public static class NotebookCopy
{
    /// <summary>Che cosa si copia, per i messaggi: «il blocco appunti «Fisica 2BO»», «la sezione «Algebra»»…</summary>
    public static string Describe(Notebook source, NotebookNode? node) => node switch
    {
        null => $"tutto il blocco appunti «{source.Title}»",
        { Kind: NodeKind.Section } => $"la sezione «{node.DisplayTitle}»",
        _ => node.Pages.Count > 0 ? $"la pagina «{node.DisplayTitle}» con le sue sottopagine" : $"la pagina «{node.DisplayTitle}»",
    };

    /// <summary>
    /// Copia <paramref name="node"/> (null = tutto il blocco) di <paramref name="source"/> in <paramref name="target"/>.
    /// Aggiungendo, le sezioni vanno in fondo; una pagina va nella sezione con lo stesso nome della sua (se manca, la
    /// si crea in fondo). Sostituendo, il contenuto di prima sparisce; nome e PDF per la classe restano quelli del
    /// blocco di destinazione. Restituisce la sezione o la pagina da mostrare per prima.
    /// </summary>
    public static NotebookNode Copy(Notebook source, NotebookNode? node, Notebook target, bool replace)
    {
        if (replace)
        {
            target.Sections.Clear();
            target.ClassPdfExcluded.Clear();
            target.Media.Clear();
        }

        NotebookNode first;
        if (node is { Kind: NodeKind.Page })
        {
            var from = node.OwningSection;
            var section = replace ? null
                : target.AllSections().FirstOrDefault(s => string.Equals(s.Title.Trim(), from?.Title.Trim(), StringComparison.CurrentCultureIgnoreCase));
            if (section == null)
            {
                section = NotebookNode.NewSection(from?.Title ?? "Sezione 1", from?.Color ?? MainWindow.SectionColors[0]);
                target.Insert(section, null);
            }
            first = Clone(node);
            target.Insert(first, section);
        }
        else
        {
            var sections = node == null ? source.Sections.ToList() : new List<NotebookNode> { node };
            first = null!;
            foreach (var s in sections)
            {
                var copy = Clone(s);
                target.Insert(copy, null);
                first ??= copy;
            }
            if (first == null)
            {
                // blocco vuoto: almeno una sezione con una pagina, come ogni blocco nuovo
                first = NotebookNode.NewSection("Sezione 1", MainWindow.SectionColors[0]);
                target.Insert(first, null);
                target.Insert(NotebookNode.NewPage(), first);
            }
        }
        target.FixParents();

        // immagini e grafici delle pagine copiate
        var copied = (node == null ? source.AllPages() : node.DescendantsAndSelf().Where(n => n.Kind == NodeKind.Page));
        foreach (var e in copied.SelectMany(p => p.Page?.Elements ?? new List<ElementData>()))
            if (e.MediaId != null && source.Media.TryGetValue(e.MediaId, out var bytes))
                target.Media[e.MediaId] = bytes;
        return first;
    }

    /// <summary>Copia di un nodo e di tutto quello che contiene, con id nuovi.</summary>
    public static NotebookNode Clone(NotebookNode n)
    {
        var c = new NotebookNode { Kind = n.Kind, Title = n.Title, Color = n.Color, IsExpanded = n.IsExpanded };
        if (n.Page is { } p)
            c.Page = new PageData
            {
                Created = p.Created,
                Background = p.Background,
                BackgroundColor = p.BackgroundColor,
                Elements = p.Elements.Select(e => e with { }).ToList(),
                Ink = p.Ink?.ToArray(),
            };
        foreach (var s in n.Sections) c.Sections.Add(Clone(s));
        foreach (var q in n.Pages) c.Pages.Add(Clone(q));
        return c;
    }

    /// <summary>Prova senza finestre (<c>--prova-copia</c>): scrive il registro; restituisce gli errori.</summary>
    public static int SelfTest(string log)
    {
        var sb = new StringBuilder();
        int failures = 0;
        void Check(bool ok, string what)
        {
            sb.AppendLine((ok ? "ok      " : "ERRORE  ") + what);
            if (!ok) failures++;
        }

        Notebook Sample(string title, params string[] sections)
        {
            var nb = new Notebook { Title = title };
            foreach (var name in sections)
            {
                var s = NotebookNode.NewSection(name, MainWindow.SectionColors[0]);
                nb.Insert(s, null);
                var page = NotebookNode.NewPage(name + " – pagina 1");
                page.Page!.Created = new DateTime(2026, 9, 15, 8, 0, 0);
                page.Page.Ink = new byte[] { 1, 2, 3 };
                var mediaId = Guid.NewGuid().ToString("N");
                nb.Media[mediaId] = new byte[] { 9, 9 };
                page.Page.Elements.Add(new ElementData { Type = "image", MediaId = mediaId, Width = 10, Height = 10 });
                nb.Insert(page, s);
                nb.Insert(NotebookNode.NewPage(name + " – sottopagina"), page);
            }
            return nb;
        }

        var src = Sample("Origine", "Algebra", "Geometria");

        var all = Sample("Destinazione", "Fisica");
        Copy(src, null, all, replace: false);
        Check(all.Sections.Select(s => s.Title).SequenceEqual(new[] { "Fisica", "Algebra", "Geometria" }), "Tutto il blocco aggiunto in fondo");
        Check(all.AllPages().Count() == 6 && all.Media.Count == 3, "Pagine, sottopagine e immagini copiate");
        Check(all.AllNodes().Select(n => n.Id).Distinct().Count() == all.AllNodes().Count()
              && !all.AllNodes().Select(n => n.Id).Intersect(src.AllNodes().Select(n => n.Id)).Any(), "Id nuovi per le copie");
        Check(all.AllPages().First(p => p.Title == "Algebra – pagina 1").Page!.Created == new DateTime(2026, 9, 15, 8, 0, 0),
            "Data della pagina conservata");
        Check(all.AllPages().First(p => p.Title == "Algebra – sottopagina").Parent?.Title == "Algebra – pagina 1", "Sottopagine al loro posto");

        var replaced = Sample("Destinazione", "Fisica");
        replaced.ClassPdfExcluded.Add(replaced.Sections[0].Id);
        Copy(src, src.Sections[1], replaced, replace: true);
        Check(replaced.Title == "Destinazione" && replaced.Sections.Count == 1 && replaced.Sections[0].Title == "Geometria"
              && replaced.Media.Count == 1 && replaced.ClassPdfExcluded.Count == 0, "Sezione al posto di tutto il contenuto");

        var page = Sample("Destinazione", "Fisica", "algebra ");
        var algebraPage = src.Sections[0].Pages[0];
        var shown = Copy(src, algebraPage, page, replace: false);
        Check(page.Sections.Count == 2 && page.Sections[1].Pages.Count == 2 && shown.Parent == page.Sections[1]
              && shown.Pages.Count == 1, "Pagina nella sezione con lo stesso nome (maiuscole e spazi a parte)");
        var page2 = Sample("Destinazione", "Fisica");
        Copy(src, algebraPage, page2, replace: false);
        Check(page2.Sections.Select(s => s.Title).SequenceEqual(new[] { "Fisica", "Algebra" }), "Pagina in una sezione nuova se manca");
        Copy(src, algebraPage, page2, replace: false);
        Check(page2.Sections[1].Pages.Count == 2, "Copiata due volte: due pagine");

        // su file: salvataggio e riapertura
        var dir = Path.Combine(Path.GetTempPath(), "ardesia-prova-copia-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "destinazione.ardesia");
            NotebookStore.Save(Sample("Su file", "Chimica"), file);
            var loaded = NotebookStore.Load(file);
            Copy(src, null, loaded, replace: false);
            NotebookStore.Save(loaded, file);
            var again = NotebookStore.Load(file);
            Check(again.AllPages().Count() == 6 && again.Media.Count == 3
                  && again.AllPages().First(p => p.Title == "Geometria – pagina 1").Page!.Ink is { Length: 3 }, "Copia salvata e riaperta dal file");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* resta nella cartella temporanea */ }
        }

        sb.AppendLine(failures == 0 ? "Tutto bene." : $"{failures} prove non riuscite.");
        File.WriteAllText(log, sb.ToString());
        return failures;
    }
}
