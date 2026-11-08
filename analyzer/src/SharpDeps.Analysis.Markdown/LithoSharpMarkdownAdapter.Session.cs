namespace SharpDeps.Analysis.Markdown;

using SharpDeps.Analysis.Core.Diagnostics;
using Syntamark;

public sealed partial class LithoSharpMarkdownAdapter
{
    internal MarkdownPreparedInput PrepareSession(MarkdownGraphRequest request, MarkdownParseOptions options,
        MarkdownPreparedInput? previous, MarkdownParseSession? previousSession, out MarkdownParseSession? session,
        CancellationToken cancellationToken)
    {
        MarkdownRuntimePin.RequireLoaded(request.ExpectedParserVersion);
        MarkdownParseSession next;
        if (previousSession is null)
            next = MarkdownParser.CreateSession(request.RawText, request.ScopeId, request.SourceId,
                request.SourceVersion, options, cancellationToken);
        else
        {
            if (previous is null || previousSession.Document.TextHash != previous.Facts.TextHash
                || previousSession.Document.SourceVersion != previous.Facts.SourceVersion
                || previousSession.Document.ScopeId != request.ScopeId || previousSession.Document.SourceId != request.SourceId
                || previousSession.Document.OptionsHash != previous.Facts.OptionsHash)
                throw new InvalidOperationException("The previous Markdown session does not match its captured input.");
            MarkdownRuntimePin.RequireFacts(previousSession.Document);
            next = previousSession.ApplyEdits(previous.Facts.TextHash,
                Edits(previous.RawText, request.RawText, cancellationToken), request.SourceVersion, options, cancellationToken);
        }
        if (next.Mode == MarkdownSessionMode.Full)
            PhaseMetrics.Decision("markdown.session.fallback", next.Fallback switch
            {
                MarkdownSessionFallback.None => "none",
                MarkdownSessionFallback.InitialParse => "initial-parse",
                MarkdownSessionFallback.PreviousNotComplete => "previous-not-complete",
                MarkdownSessionFallback.OptionsChanged => "options-changed",
                MarkdownSessionFallback.UnsupportedSyntax => "unsupported-syntax",
                MarkdownSessionFallback.UnsupportedEdit => "unsupported-edit",
                MarkdownSessionFallback.NoReusableBlocks => "no-reusable-blocks",
                MarkdownSessionFallback.ResourceBoundary => "resource-boundary",
                _ => "unknown"
            });
        var facts = CanonicalFacts(request, options, next.Document, cancellationToken);
        session = ReferenceEquals(facts, next.Document) ? next : null;
        return PrepareParsed(request, options, facts, next.Root, cancellationToken);
    }

    private static IReadOnlyList<MarkdownTextEdit> Edits(string previous, string current, CancellationToken token)
    {
        var start = 0;
        while (start < previous.Length && start < current.Length && previous[start] == current[start])
        {
            if ((start & 127) == 0)
                token.ThrowIfCancellationRequested();
            start++;
        }
        var oldEnd = previous.Length;
        var newEnd = current.Length;
        while (oldEnd > start && newEnd > start && previous[oldEnd - 1] == current[newEnd - 1])
        {
            if ((oldEnd & 127) == 0)
                token.ThrowIfCancellationRequested();
            oldEnd--;
            newEnd--;
        }
        token.ThrowIfCancellationRequested();
        return start == oldEnd && start == newEnd ? []
            : [new(new RawSpan(start, oldEnd - start), current[start..newEnd])];
    }
}
