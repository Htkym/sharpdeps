namespace SharpDeps.Mcp;

internal static class McpAgentGuidance
{
    internal const string ExplorationWorkflow = "Use SharpDeps explore when indexed .NET symbol, dependency, caller/callee or cross-file structure would help resolve missing evidence. Tool use is optional: use targeted source read/search when the relevant location is already known. Query focused names/paths and reuse returned evidence; do not retrieve complete unchanged ranges already in context. Follow exact selectors only for specific evidence still missing or changed and needed for the task. Omissions do not require exhaustive exploration; stop when sufficient evidence is available. Use ordinary read/search for simple literal text or documentation-only lookups, dirty/unindexed/out-of-scope files, unavailable tools/indexes or user opt-out. Report blockers without claiming unverified evidence. No repeat call is required for evidence already in context. ";
}
