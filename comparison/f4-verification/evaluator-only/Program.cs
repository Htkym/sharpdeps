using F4Fixture;
int cases = 0;
foreach (bool op in new[] { false, true })
foreach (bool first in new[] { false, true })
foreach (bool last in new[] { false, true })
{
    Check(ReleaseFlow.Run, op, first, last, last ? "cleanup" : op ? "operation" : null,
        new[] { "operation" }.Concat(op ? new[] { "rollback" } : Array.Empty<string>()).Concat(new[] { "release" }).Concat(first ? new[] { "release-failed" } : Array.Empty<string>()).Concat(new[] { "cleanup" }).ToArray());
    Check(NestedCleanup.Run, op, first, last, last ? "second-cleanup" : first ? "first-cleanup" : op ? "operation" : null,
        new[] { "operation", "first-cleanup", "second-cleanup" });
}
Console.WriteLine($"PASS {cases} deterministic source cases; no model calls");
void Check(Action<bool, bool, bool, List<string>> run, bool a, bool b, bool c, string? expected, string[] expectedTrace)
{
    var trace = new List<string>(); string? observed = null;
    try { run(a, b, c, trace); } catch (InvalidOperationException ex) { observed = ex.Message; }
    if (observed != expected || !trace.SequenceEqual(expectedTrace)) throw new Exception("Fixture expectation mismatch");
    cases++;
}
