# Costruisce la guida PDF di Ardesia (docs/wiki/Ardesia-guida.pdf) dalle pagine Markdown della wiki.
# Non fa parte di Ardesia: serve Python con i pacchetti «markdown» e «pymupdf» (per esempio in un venv)
# e Microsoft Edge, che stampa l'HTML in PDF senza aprire finestre.
#
# Uso: python tools/guida-pdf.py [cartella di lavoro]
# La versione si legge da src/Ardesia/Ardesia.csproj.
import re, sys, subprocess, pathlib, datetime, tempfile, time
import markdown, pymupdf

root = pathlib.Path(__file__).resolve().parent.parent
wiki = root / "docs" / "wiki"
out_pdf = wiki / "Ardesia-guida.pdf"
work = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else pathlib.Path(tempfile.gettempdir()) / "ardesia-guida-pdf"
work.mkdir(parents=True, exist_ok=True)
version = re.search(r"<Version>([^<]+)</Version>", (root / "src" / "Ardesia" / "Ardesia.csproj").read_text(encoding="utf-8")).group(1)

ORDER = [
    "Installazione", "Primo-avvio", "Blocchi-appunti-sezioni-e-pagine", "La-finestra-e-le-barre",
    "Scrivere-e-disegnare", "Righello-goniometro-e-compasso", "Testo-immagini-e-appunti",
    "Zoom-foglio-e-sfondi", "Grafico-di-funzione", "Scrittura-a-mano-delle-funzioni", "GeoGebra",
    "PDF", "Impostazioni", "Uso-sulle-LIM", "Google-Drive", "Scorciatoie-da-tastiera", "Domande-frequenti",
]
MONTHS = ["gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre",
          "ottobre", "novembre", "dicembre"]


def slug(p):
    return "cap-" + p.lower()


def convert(page, text):
    # collegamenti fra le pagine: [testo](Pagina.md) o [testo](Pagina.md#ancora) → capitolo del PDF
    def link(m):
        label, target = m.group(1), m.group(2)
        if target.startswith("#"):
            return f"[{label}](#{slug(page)})"
        name = target.split("#")[0]
        if target.startswith("http") or not name.endswith(".md"):
            return m.group(0)
        name = name[:-3]
        if name == "Home":
            return label
        return f"[{label}](#{slug(name)})"
    text = re.sub(r"(?<!!)\[([^\]]+)\]\(([^)]+)\)", link, text)
    # immagini: percorso assoluto
    text = re.sub(r"!\[([^\]]*)\]\((immagini/[^)]+)\)",
                  lambda m: f"![{m.group(1)}]({(wiki / m.group(2)).as_uri()})", text)
    html = markdown.markdown(text, extensions=["tables", "fenced_code", "sane_lists"])
    html = html.replace("<h1>", f'<h1 id="{slug(page)}">', 1)
    return html


home = (wiki / "Home.md").read_text(encoding="utf-8")
# dalla Home si tengono l'introduzione e le parti dopo l'indice, senza titolo, indice e riga della versione
intro = home.split("## Indice della guida")[0]
intro = re.sub(r"^# .*\n", "", intro)
intro = re.sub(r"^> \*\*Versione descritta:\*\*.*\n", "", intro, flags=re.M)
intro_after = home.split("## Che cosa serve")[1]
intro_html = convert("Home", intro + "\n## Che cosa serve" + intro_after)

titles = []
chapters = []
for i, p in enumerate(ORDER, 1):
    md = (wiki / f"{p}.md").read_text(encoding="utf-8")
    title = re.match(r"# (.*)", md).group(1).strip()
    titles.append(title)
    md = re.sub(r"^# (.*)", lambda m: f"# <span class='num'>{i}</span> {m.group(1)}", md, count=1)
    chapters.append(f'<section class="chapter">{convert(p, md)}</section>')

toc = "".join(f'<li><a href="#{slug(p)}"><span class="n">{i}</span>{t}</a></li>'
              for i, (p, t) in enumerate(zip(ORDER, titles), 1))
today = datetime.date.today()
when = f"{MONTHS[today.month - 1]} {today.year}"

css = """
@page { size: A4; margin: 20mm 18mm 22mm 18mm; }
:root { --acc: #1E5A68; --acc2: #2b7f93; --soft: #eef5f7; --line: #d6e2e6; }
* { box-sizing: border-box; }
body { font-family: 'Segoe UI', Calibri, sans-serif; font-size: 10.5pt; line-height: 1.5; color: #222; margin: 0; }
h1 { color: var(--acc); font-size: 22pt; font-weight: 600; margin: 0 0 14pt; padding-bottom: 6pt; border-bottom: 2.5pt solid var(--acc); }
h1 .num { display: inline-block; background: var(--acc); color: #fff; border-radius: 4pt; min-width: 26pt; text-align: center; margin-right: 8pt; font-size: 17pt; padding: 1pt 5pt; vertical-align: 2pt; }
h2 { color: var(--acc); font-size: 14.5pt; font-weight: 600; margin: 18pt 0 6pt; break-after: avoid; }
h3 { color: #333; font-size: 12pt; font-weight: 600; margin: 14pt 0 4pt; break-after: avoid; }
p { margin: 0 0 7pt; }
ul, ol { margin: 0 0 8pt; padding-left: 18pt; }
li { margin: 2pt 0; }
a { color: var(--acc2); text-decoration: none; }
code { font-family: Consolas, monospace; background: #f1f3f4; padding: 0 3pt; border-radius: 3pt; font-size: 9.5pt; }
table { border-collapse: collapse; width: 100%; margin: 6pt 0 10pt; font-size: 9.8pt; break-inside: auto; }
th { background: var(--acc); color: #fff; text-align: left; font-weight: 600; }
th, td { padding: 4pt 7pt; border: 0.6pt solid var(--line); vertical-align: top; }
tr { break-inside: avoid; }
tr:nth-child(even) td { background: #f7fafb; }
img { display: block; max-width: 100%; max-height: 120mm; margin: 8pt auto 12pt; border: 0.6pt solid #ccd; border-radius: 3pt; box-shadow: 0 1pt 4pt rgba(0,0,0,.12); break-inside: avoid; }
blockquote { margin: 8pt 0 10pt; padding: 6pt 10pt; background: var(--soft); border-left: 3.5pt solid var(--acc); border-radius: 0 4pt 4pt 0; break-inside: avoid; }
blockquote p:last-child { margin-bottom: 0; }
.chapter { break-before: page; }
.cover { height: 250mm; display: flex; flex-direction: column; justify-content: center; }
.cover .logo { width: 70pt; height: 70pt; background: #2F3B3A; color: #fff; font-family: Georgia, serif; font-weight: bold; font-size: 48pt; border-radius: 12pt; display: flex; align-items: center; justify-content: center; margin-bottom: 18pt; }
.cover h1.t { font-size: 42pt; border: none; margin: 0; padding: 0; color: var(--acc); }
.cover .sub { font-size: 16pt; color: #444; margin: 4pt 0 30pt; }
.cover .band { height: 6pt; background: var(--acc); width: 120pt; margin-bottom: 26pt; }
.cover .meta { font-size: 11pt; color: #555; line-height: 1.7; }
.toc { break-before: page; }
.toc ol { list-style: none; padding: 0; columns: 1; }
.toc li { border-bottom: 0.6pt dotted #bbb; padding: 5pt 0; font-size: 12pt; }
.toc .n { display: inline-block; width: 26pt; color: var(--acc); font-weight: 600; }
.intro { break-before: page; }
"""

html = f"""<!doctype html><html lang="it"><head><meta charset="utf-8"><title>Ardesia – guida per i docenti</title>
<style>{css}</style></head><body>
<div class="cover">
  <div class="logo">A</div>
  <h1 class="t">Ardesia</h1>
  <div class="sub">Il blocco appunti per la lavagna interattiva<br>Guida per i docenti</div>
  <div class="band"></div>
  <div class="meta">Versione {version} · {when}<br>A cura di Giovanni Dal Bo</div>
</div>
<div class="toc"><h1>Indice</h1><ol>
<li><a href="#cap-intro"><span class="n">·</span>Che cos'è Ardesia</a></li>{toc}</ol></div>
<section class="intro"><h1 id="cap-intro">Che cos'è Ardesia</h1>{intro_html}</section>
{''.join(chapters)}
</body></html>"""

html_path = work / "guida.html"
html_path.write_text(html, encoding="utf-8")
tmp_pdf = work / "guida-edge.pdf"
tmp_pdf.unlink(missing_ok=True)
edge = next(p for p in [pathlib.Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
                        pathlib.Path(r"C:\Program Files\Microsoft\Edge\Application\msedge.exe")] if p.exists())
subprocess.run([str(edge), "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
                f"--user-data-dir={work / 'edge-profile'}",
                f"--print-to-pdf={tmp_pdf}", html_path.as_uri()], check=True, timeout=180)
# msedge.exe può tornare prima che il PDF sia scritto: si aspetta che il file smetta di crescere
last = -1
for _ in range(240):
    time.sleep(0.5)
    size = tmp_pdf.stat().st_size if tmp_pdf.exists() else -1
    if size > 0 and size == last:
        break
    last = size

# numeri di pagina, segnalibri, metadati
doc = pymupdf.open(tmp_pdf)
toc_entries = [[1, "Copertina", 1], [1, "Indice", 2]]
search = [("Che cos'è Ardesia", "Che cos'è Ardesia")] + [(t, f"{i} {t}") for i, t in enumerate(titles, 1)]
start = 2
for title, label in search:
    for pno in range(start, len(doc)):
        first = "\n".join(doc[pno].get_text("text").splitlines()[:4])
        if title in first:
            toc_entries.append([1, label, pno + 1])
            start = pno
            break
doc.set_toc(toc_entries)
for pno in range(1, len(doc)):
    page = doc[pno]
    r = page.rect
    page.insert_text((r.width / 2 - 6, r.height - 28), str(pno + 1), fontsize=9, color=(0.35, 0.35, 0.35))
    page.insert_text((51, r.height - 28), f"Ardesia {version} – guida per i docenti", fontsize=8, color=(0.55, 0.55, 0.55))
doc.set_metadata({"title": "Ardesia – guida per i docenti", "author": "Giovanni Dal Bo", "subject": f"Ardesia {version}"})
doc.save(out_pdf, garbage=3, deflate=True)
print("pagine:", len(doc), "segnalibri:", len(toc_entries), "->", out_pdf)
