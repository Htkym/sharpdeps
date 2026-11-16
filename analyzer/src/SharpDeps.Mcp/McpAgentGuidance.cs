namespace SharpDeps.Mcp;

internal static class McpAgentGuidance
{
    internal const string ExplorationWorkflow = "For missing indexed .NET caller/callee, dependency, implementation or cross-project structure, use focused SharpDeps explore before broad reconstruction. A known file suffices only for single-file/literal lookups, not missing cross-file evidence. Query focused names/paths and reuse returned evidence; do not retrieve complete unchanged ranges already in context. Follow exact selectors only for specific evidence still missing or changed and needed for the task. Omissions do not require exhaustive exploration; stop when sufficient evidence is available. Use ordinary read/search for simple literal text or documentation-only lookups, dirty/unindexed/out-of-scope files, unavailable tools/indexes or user opt-out. Report blockers without claiming unverified evidence. No repeat call is required for evidence already in context. ";
}
