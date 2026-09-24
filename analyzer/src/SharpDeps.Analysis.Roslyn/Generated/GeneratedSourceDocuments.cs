// Source-generated documents the loader obtained (SD-011).
//
// A generator that registers sources in memory writes no file itself, yet its output
// is part of the compilation the collectors read. The loader keeps the content and hash
// next to the analysis result so the UI can open the document read-only, and the
// document registry marks evidence from it as generatedSource.

namespace SharpDeps.Analysis.Roslyn;

/// <summary>One generated document as the workspace reported it.</summary>
/// <param name="Text">The document content, or null when it exceeded the retention budget.</param>
/// <param name="TextTruncated">True when the content was obtained but not retained.</param>
public sealed record GeneratedSourceDocumentInfo(
    string VariantKey,
    string ProjectName,
    string HintName,
    string? FilePath,
    string ContentHash,
    long ByteLength,
    string? Text,
    bool TextTruncated);
