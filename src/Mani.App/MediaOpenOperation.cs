namespace Mani_App;

// UI-thread ownership of an in-flight open, independent of source-list revisions.
internal sealed class MediaOpenOperation
{
    private long _sequence, _owner;
    public bool IsActive => _owner != 0;
    public long Begin() => _owner = ++_sequence;
    public bool Complete(long owner)
    {
        if (_owner != owner) return false;
        _owner = 0; return true;
    }
    public void Cancel() => _owner = 0;
}
