namespace PathForge.Core.Projects;

/// <summary>
/// Undo/redo history of project snapshots (serialized project text). A snapshot is committed after each
/// settled change; undo returns the previous one. The oldest steps are dropped when the step or size limit is reached.
/// </summary>
public sealed class UndoHistory
{
    private readonly LinkedList<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly int _maxSteps;
    private readonly long _maxChars;
    private long _undoChars;

    public UndoHistory(int maxSteps = 100, long maxChars = 100_000_000)
    {
        _maxSteps = Math.Max(1, maxSteps);
        _maxChars = Math.Max(1, maxChars);
    }

    /// <summary>State the project is in now (after the last commit, undo or redo).</summary>
    public string? Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int UndoCount => _undo.Count;

    /// <summary>Forgets everything (new or opened project).</summary>
    public void Reset(string state)
    {
        _undo.Clear();
        _redo.Clear();
        _undoChars = 0;
        Current = state;
    }

    /// <summary>Records a new state. Returns false when nothing changed.</summary>
    public bool Commit(string state)
    {
        if (Current is null)
        {
            Current = state;
            return false;
        }

        if (string.Equals(state, Current, StringComparison.Ordinal))
        {
            return false;
        }

        _undo.AddLast(Current);
        _undoChars += Current.Length;
        Current = state;
        _redo.Clear();
        while (_undo.Count > 1 && (_undo.Count > _maxSteps || _undoChars > _maxChars))
        {
            _undoChars -= _undo.First!.Value.Length;
            _undo.RemoveFirst();
        }

        return true;
    }

    /// <summary>Replaces the current state without making an undo step (e.g. the same state written again).</summary>
    public void ReplaceCurrent(string state) => Current = state;

    /// <summary>The previous state, or null when there is none.</summary>
    public string? Undo()
    {
        if (_undo.Count == 0 || Current is null)
        {
            return null;
        }

        _redo.Push(Current);
        Current = _undo.Last!.Value;
        _undoChars -= Current.Length;
        _undo.RemoveLast();
        return Current;
    }

    /// <summary>The state that was undone last, or null.</summary>
    public string? Redo()
    {
        if (_redo.Count == 0 || Current is null)
        {
            return null;
        }

        _undo.AddLast(Current);
        _undoChars += Current.Length;
        Current = _redo.Pop();
        return Current;
    }
}
