namespace SharpDeps.Cli;

using System.Text.RegularExpressions;
using SharpDeps.Analysis.Roslyn;

internal sealed record CliIndexDiagnostic(string Stage, string? LastCompletedStage = null, string? Reason = null,
    long? Limit = null, long? Observed = null, string? SdkVersion = null, string? Project = null,
    string? TargetFramework = null, string? RepresentativeDiagnostic = null, long? RemainingDiagnostics = null);

internal static class CliIndexDiagnostics
{
    internal static T Attach<T>(T error, CliIndexDiagnostic diagnostic) where T : Exception
    {
        error.Data[typeof(CliIndexDiagnostic)] = diagnostic;
        return error;
    }

    internal static CliIndexDiagnostic? Get(Exception error) => error.Data[typeof(CliIndexDiagnostic)] as CliIndexDiagnostic;

    internal static CliIndexException Budget(string reason, long limit, long observed)
        => Attach(new CliIndexException("CLI_INPUT_BUDGET_EXCEEDED"), new("input-scan", Reason: reason,
            Limit: limit, Observed: observed));

    internal static CliIndexException Semantic(string code, SemanticProbeReport report, ProjectVariantInfo? variant,
        string? requestedPath = null, string? requestedFramework = null)
    {
        var failures = report.Diagnostics.Where(d => d.Kind == "Failure").ToArray();
        var loadFailure = report.Limitations.FirstOrDefault(l => l.Code == "semantic.loadFailed");
        var count = failures.LongLength + (loadFailure is null ? 0 : 1)
            + report.Variants.Sum(v => (long)v.ErrorDiagnosticCount);
        var message = variant?.FailureReason ?? failures.FirstOrDefault()?.Message ?? loadFailure?.Message;
        if (variant?.FailureReason is { } reason && !failures.Any(d => d.Message == reason)) count++;
        var project = Path.GetFileName((variant?.ProjectPath ?? requestedPath)?.Replace('\\', '/'));
        var diagnostic = variant?.FirstCompilerErrorCode is { } compiler && Regex.IsMatch(compiler, @"^CS[0-9]{4,5}$")
            ? compiler : message is null ? null : DescribeFailure(message);
        return Attach(new CliIndexException(code), new("semantic-load", "input-scan",
            Reason: variant?.LoadState == "failed" ? "project-load-failed" :
                variant?.GeneratedDocumentError is not null ? "generated-input-unavailable" :
                variant is { ErrorDiagnosticCount: > 0 } ? "compilation-errors" :
                loadFailure is not null ? "project-load-failed" :
                variant is { CompilationObtained: false } ? "compilation-unavailable" : "variant-unavailable",
            SdkVersion: Identifier(report.Environment.SdkVersion), Project: Identifier(project),
            TargetFramework: Identifier(variant?.TargetFramework ?? requestedFramework),
            RepresentativeDiagnostic: diagnostic, RemainingDiagnostics: Math.Max(0, count - (diagnostic is null ? 0 : 1))));
    }

    // MSBuild diagnostics contain arbitrary task output, absolute paths and source fragments.
    private static string DescribeFailure(string message)
    {
        var match = Regex.Match(message, @"\b(?:MSB|NETSDK|NU|CS)[0-9]{4,5}\b",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        if (match.Success) return match.Value;
        if (message.Contains("and has not been tested with it", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not support targeting", StringComparison.OrdinalIgnoreCase))
            return "unsupported-target";
        return "workspace-load-failure";
    }

    private static string? Identifier(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or ' ') ? value : null;
}
