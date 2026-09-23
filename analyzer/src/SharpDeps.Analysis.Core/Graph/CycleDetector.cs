namespace SharpDeps.Analysis.Core.Graph;

using SharpDeps.Analysis.Contracts;

public static class CycleDetector
{
    public static CycleResult Analyze(
        IEnumerable<string> nodeKeys,
        IReadOnlyList<CodeMapEdge> edges,
        IReadOnlyDictionary<string, string> nameByKey)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var key in nodeKeys)
        {
            if (!adjacency.ContainsKey(key))
            {
                adjacency[key] = [];
            }
        }

        foreach (var edge in edges)
        {
            if (!adjacency.TryGetValue(edge.SourceKey, out var targets))
            {
                targets = [];
                adjacency[edge.SourceKey] = targets;
            }

            if (!adjacency.ContainsKey(edge.TargetKey))
            {
                adjacency[edge.TargetKey] = [];
            }

            targets.Add(edge.TargetKey);
        }

        var components = ComputeStronglyConnectedComponents(adjacency);

        var componentIdByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var nonTrivialComponents = new HashSet<int>();
        for (var componentId = 0; componentId < components.Count; componentId++)
        {
            var members = components[componentId];
            foreach (var member in members)
            {
                componentIdByKey[member] = componentId;
            }

            var isCycle = members.Count > 1
                || (members.Count == 1 && adjacency[members[0]].Contains(members[0]));
            if (isCycle)
            {
                nonTrivialComponents.Add(componentId);
            }
        }

        var cycleNodeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var componentId in nonTrivialComponents)
        {
            foreach (var member in components[componentId])
            {
                cycleNodeKeys.Add(member);
            }
        }

        var cycleEdgeKeys = new HashSet<(string Source, string Target)>();
        foreach (var edge in edges)
        {
            if (componentIdByKey.TryGetValue(edge.SourceKey, out var sourceComponent)
                && componentIdByKey.TryGetValue(edge.TargetKey, out var targetComponent)
                && sourceComponent == targetComponent
                && nonTrivialComponents.Contains(sourceComponent))
            {
                cycleEdgeKeys.Add((edge.SourceKey, edge.TargetKey));
            }
        }

        var componentNames = nonTrivialComponents
            .Select(componentId => (IReadOnlyList<string>)components[componentId]
                .Select(key => nameByKey.TryGetValue(key, out var name) ? name : key)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray())
            .ToArray();

        return new CycleResult(cycleNodeKeys, cycleEdgeKeys, componentNames);
    }

    private static List<List<string>> ComputeStronglyConnectedComponents(
        IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var tarjanStack = new Stack<string>();
        var components = new List<List<string>>();
        var nextIndex = 0;

        foreach (var start in adjacency.Keys)
        {
            if (index.ContainsKey(start))
            {
                continue;
            }

            var callStack = new Stack<(string Node, int ChildIndex)>();
            callStack.Push((start, 0));

            while (callStack.Count > 0)
            {
                var (node, childIndex) = callStack.Pop();

                if (childIndex == 0)
                {
                    index[node] = nextIndex;
                    low[node] = nextIndex;
                    nextIndex++;
                    tarjanStack.Push(node);
                    onStack.Add(node);
                }
                else
                {
                    var finishedChild = adjacency[node][childIndex - 1];
                    low[node] = Math.Min(low[node], low[finishedChild]);
                }

                var neighbors = adjacency[node];
                var pushedChild = false;
                for (var i = childIndex; i < neighbors.Count; i++)
                {
                    var next = neighbors[i];
                    if (!index.ContainsKey(next))
                    {
                        callStack.Push((node, i + 1));
                        callStack.Push((next, 0));
                        pushedChild = true;
                        break;
                    }

                    if (onStack.Contains(next))
                    {
                        low[node] = Math.Min(low[node], index[next]);
                    }
                }

                if (pushedChild)
                {
                    continue;
                }

                if (low[node] == index[node])
                {
                    var component = new List<string>();
                    string popped;
                    do
                    {
                        popped = tarjanStack.Pop();
                        onStack.Remove(popped);
                        component.Add(popped);
                    }
                    while (!string.Equals(popped, node, StringComparison.Ordinal));

                    components.Add(component);
                }
            }
        }

        return components;
    }
}

public sealed class CycleResult
{
    public CycleResult(
        HashSet<string> cycleNodeKeys,
        HashSet<(string Source, string Target)> cycleEdgeKeys,
        IReadOnlyList<IReadOnlyList<string>> nonTrivialComponentNames)
    {
        CycleNodeKeys = cycleNodeKeys;
        CycleEdgeKeys = cycleEdgeKeys;
        NonTrivialComponentNames = nonTrivialComponentNames;
    }

    public HashSet<string> CycleNodeKeys { get; }

    public HashSet<(string Source, string Target)> CycleEdgeKeys { get; }

    public IReadOnlyList<IReadOnlyList<string>> NonTrivialComponentNames { get; }

    public IReadOnlyList<DependencyCycle> ToCycles(string scope)
        => NonTrivialComponentNames
            .Select(names => new DependencyCycle(scope, names, names.Count))
            .OrderByDescending(cycle => cycle.Length)
            .ThenBy(cycle => cycle.Nodes.Count > 0 ? cycle.Nodes[0] : string.Empty, StringComparer.Ordinal)
            .ToArray();
}
