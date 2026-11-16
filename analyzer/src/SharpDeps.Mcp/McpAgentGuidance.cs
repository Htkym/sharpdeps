namespace SharpDeps.Mcp;

internal static class McpAgentGuidance
{
    internal const string ExplorationWorkflow = "MUST start indexed .NET symbol, dependency, caller/callee and cross-file implementation investigation with SharpDeps explore, before broad search or opening source to discover code. Query focused names/paths; use returned source as primary evidence. Do not reread complete ranges already in context with the same hash and span, or make a token call before repeating broad reads. Follow exact selectors for omitted members; read only specific evidence still missing or changed. Skip for simple literal text or documentation-only lookups, dirty/unindexed/out-of-scope files, unavailable tools/indexes or user opt-out. Report blockers; use permitted targeted fallback without claiming unverified evidence. No repeat call is required for evidence already in context. ";
}
