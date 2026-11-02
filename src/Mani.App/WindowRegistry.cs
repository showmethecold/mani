// IINA PlayerCore.playerCores/newPlayerCore/findIdlePlayerCore, C# platform port.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
namespace Mani_App;

// Accessed on the UI thread, like IINA's main-thread player registry.
internal sealed class WindowRegistry<T> where T : class
{
    private readonly List<T> _windows = [];
    public IReadOnlyList<T> Windows => _windows;
    public bool IsQuitting { get; private set; }
    public T? GetOrCreate(Func<T, bool> isIdle, Func<T> create)
    {
        if (IsQuitting) return null;
        var idle = _windows.FirstOrDefault(isIdle);
        if (idle is not null) return idle;
        var window = create();
        _windows.Add(window);
        return window;
    }
    public void Remove(T window) => _windows.Remove(window);
    public T[] BeginQuit()
    {
        if (IsQuitting) return [];
        IsQuitting = true;
        return _windows.ToArray();
    }
}
