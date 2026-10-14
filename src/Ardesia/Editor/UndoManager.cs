using System.Windows.Ink;
using Ardesia.Models;

namespace Ardesia.Editor;

public sealed record PageSnapshot(StrokeCollection Strokes, List<ElementData> Elements);

/// <summary>Annulla/Ripeti basato su istantanee dell'intera pagina: semplice e robusto.</summary>
public sealed class UndoManager
{
    const int Limit = 100;
    readonly LinkedList<PageSnapshot> _undo = new();
    readonly Stack<PageSnapshot> _redo = new();
    PageSnapshot? _current;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Reset(PageSnapshot current)
    {
        _undo.Clear();
        _redo.Clear();
        _current = current;
    }

    public void Push(PageSnapshot next)
    {
        if (_current != null)
        {
            _undo.AddLast(_current);
            if (_undo.Count > Limit) _undo.RemoveFirst();
        }
        _current = next;
        _redo.Clear();
    }

    public PageSnapshot? Undo()
    {
        if (_undo.Count == 0 || _current == null) return null;
        _redo.Push(_current);
        _current = _undo.Last!.Value;
        _undo.RemoveLast();
        return _current;
    }

    public PageSnapshot? Redo()
    {
        if (_redo.Count == 0 || _current == null) return null;
        _undo.AddLast(_current);
        _current = _redo.Pop();
        return _current;
    }
}
