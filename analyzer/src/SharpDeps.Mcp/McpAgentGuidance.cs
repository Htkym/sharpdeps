namespace SharpDeps.Mcp;

internal static class McpAgentGuidance
{
    internal const string ExplorationWorkflow = "Use the smallest evidence source that answers the question. Read known relevant files/ranges directly when they suffice, including across files. Use focused SharpDeps explore for specific indexed .NET caller/callee, dependency, implementation or cross-project evidence still missing, not to reconfirm source already read. For an unfamiliar broad type/file, prefer overview to choose a member; use evidence for the needed declaration and detail only for specific missing related bodies. Do not add an overview step when a focused member or sufficient source is already known. Reuse complete unchanged ranges already in context; omissions do not require exhaustive exploration. Stop when sufficient evidence is available, preserving uncertainty and stating decisive branch conditions. Use ordinary read/search for literal text, documentation, dirty/unindexed/out-of-scope files, unavailable tools/indexes or user opt-out. Report blockers without claiming unverified evidence. No repeat call is required for evidence already in context. ";
}
