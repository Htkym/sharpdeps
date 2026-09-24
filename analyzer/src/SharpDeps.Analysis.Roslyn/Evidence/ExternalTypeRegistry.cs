// External type registry (SD-011).
//
// Evidence may point at types the solution does not declare (BCL, packages). The v2
// model materializes those as external type nodes, so the collectors record their id
// and display name here while they work. Assembly identity stays an internal
// resolution detail; the display name is what the UI shows.

namespace SharpDeps.Analysis.Roslyn.Evidence;

using System.Collections.Concurrent;

public sealed class ExternalTypeRegistry
{
    private readonly ConcurrentDictionary<string, string> _displayNameById = new(StringComparer.Ordinal);

    public void Register(string typeId, string displayName)
        => _displayNameById.TryAdd(typeId, displayName);

    public IReadOnlyDictionary<string, string> Entries
        => _displayNameById.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}
