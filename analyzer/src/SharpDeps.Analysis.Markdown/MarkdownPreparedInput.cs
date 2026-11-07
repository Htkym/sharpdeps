namespace SharpDeps.Analysis.Markdown;

using Syntamark;

/// <summary>Owned immutable parse output bound to one workspace, source and option set.</summary>
public sealed class MarkdownPreparedInput
{
    private readonly Guid workspaceUuid;
    private readonly string expectedParserVersion;
    private readonly MarkdownParseOptions options;

    internal MarkdownPreparedInput(MarkdownGraphRequest request, MarkdownParseOptions options, MarkdownDocument facts,
        IEnumerable<MarkdownMetadataValue> attributes, IEnumerable<string> ignoredMetadataKeys, MarkdownSyntaxNode? syntaxRoot)
    {
        workspaceUuid = request.WorkspaceUuid;
        expectedParserVersion = request.ExpectedParserVersion;
        this.options = options;
        RawText = request.RawText;
        Facts = facts;
        Attributes = Array.AsReadOnly(attributes.ToArray());
        IgnoredMetadataKeys = Array.AsReadOnly(ignoredMetadataKeys.ToArray());
        SyntaxRoot = syntaxRoot;
    }

    public MarkdownDocument Facts { get; }
    public IReadOnlyList<MarkdownMetadataValue> Attributes { get; }
    public IReadOnlyList<string> IgnoredMetadataKeys { get; }
    /// <summary>Auxiliary positive syntax evidence only; a missing node or range never proves absence.</summary>
    public MarkdownSyntaxNode? SyntaxRoot { get; }
    internal string RawText { get; }
    internal Guid WorkspaceUuid => workspaceUuid;

    internal void RequireMatches(MarkdownGraphRequest request, MarkdownParseOptions requestedOptions)
    {
        if (request.ExpectedParserVersion != MarkdownRuntimePin.ParserVersion
            || request.ExpectedParserVersion != expectedParserVersion)
            throw new InvalidOperationException("The requested Markdown parser stamp differs from the prepared input.");
        MarkdownRuntimePin.RequireFacts(Facts);
        if (request.WorkspaceUuid != workspaceUuid || request.RawText != RawText
            || request.ScopeId != Facts.ScopeId || request.SourceId != Facts.SourceId || request.SourceVersion != Facts.SourceVersion
            || requestedOptions.MaxInputUtf16 != options.MaxInputUtf16 || requestedOptions.MaxOutputItems != options.MaxOutputItems
            || requestedOptions.MaxNestingDepth != options.MaxNestingDepth || requestedOptions.MaxScanUnits != options.MaxScanUnits
            || requestedOptions.ProfileId != options.ProfileId || requestedOptions.OptionsSchemaVersion != options.OptionsSchemaVersion
            || requestedOptions.FrontMatterRecovery != options.FrontMatterRecovery || requestedOptions.InputKind != options.InputKind)
            throw new ArgumentException("Prepared Markdown input must match its parsed source and options.", nameof(request));
    }
}
