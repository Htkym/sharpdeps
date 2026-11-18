namespace SharpDeps.Mcp;

internal static class McpAgentGuidance
{
    internal const string ExplorationWorkflow = "Use the smallest evidence source that answers the question. Read known relevant files/ranges directly when they suffice, including across files. For indexed evidence still missing, default to find_symbols for discovery, read_symbols for exact declarations, and relations for one-hop edges. Escalate to explore for multi-step context, unresolved identity or stalled targeted investigation; do not require preliminary calls. Reuse complete unchanged ranges already in context. Stop when sufficient, preserving uncertainty and stating decisive branch conditions; retain concrete symbol names and applicable failure paths from evidence already read without extra retrieval. Use ordinary read/search for literal text, dirty/unindexed files, unavailable indexes or user opt-out. ";
}
