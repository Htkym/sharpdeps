namespace SharpDeps.Mcp;

internal static class McpAgentGuidance
{
    internal const string ExplorationWorkflow = "Use the smallest evidence source that answers the question. Read known relevant files/ranges directly when they suffice, including across files. For indexed evidence still missing, default to find_symbols for discovery, read_symbols for exact declarations, and relations for one-hop edges. Do not automatically escalate to explore; it remains available for explicit user requests or manual investigation. Before submitting, check the answer against evidence already in context: concrete method names, decisive branch conditions, failure paths and limits. For exception-sensitive behavior, trace try/catch/finally, cleanup/disposal and release failures; distinguish logging, rethrow and an exception replacing the one observed by the caller. Read only specific missing evidence with typed tools or source reads; this check does not require another call when evidence is sufficient. Preserve uncertainty and stop when sufficient. Use ordinary read/search for literal text, dirty/unindexed files, unavailable indexes or user opt-out. ";
}
