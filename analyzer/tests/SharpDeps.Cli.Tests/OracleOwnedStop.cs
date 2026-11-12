namespace SharpDeps.Cli.Tests;

using System.Text.Json;

internal sealed record OracleOwnedStopBinding(string RequestPath, string Nonce);

internal static class OracleOwnedStop
{
    internal const string PathKey = "SHARPDEPS_OWNED_STOP_PATH";
    internal const string NonceKey = "SHARPDEPS_OWNED_STOP_NONCE";
    internal const string RequestLeaf = "owned-stop.request.json";
    internal const string Schema = "sharpdeps-owned-stop-v1";

    internal static OracleOwnedStopBinding RequireBinding(string? path, string? nonce, string? fixtureRoot = null)
    {
        if (path is null && nonce is null)
            throw new InvalidOperationException("Owned Fact2 requires an owner-injected stop binding before fixture creation.");
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(nonce)
            || nonce.Length != 64 || !nonce.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))
            throw new ArgumentException("Owned-stop path and 64-character nonce must be supplied together.");
        string ownerRoot = OracleChildEnvironmentPlanner.ValidateRunRoot(Path.GetDirectoryName(path));
        if (!string.Equals(path, ownerRoot + "\\" + RequestLeaf, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ownerRoot, fixtureRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Owned-stop request must use the fixed leaf in a distinct owner root.");
        return new(path, nonce);
    }

    internal static bool TerminalAndDrained(bool terminalWaitSucceeded, bool drainsCompleted)
        => terminalWaitSucceeded && drainsCompleted;

    internal static void Publish(OracleOwnedStopBinding binding, string reason)
    {
        _ = RequireBinding(binding.RequestPath, binding.Nonce);
        if (reason is not ("cancelled" or "child-uncertain"))
            throw new ArgumentException("Owned-stop reason is outside the fixed contract.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = Schema,
            nonce = binding.Nonce,
            reason
        });
        string temporary = binding.RequestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1, FileOptions.WriteThrough))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, binding.RequestPath); // No overwrite: the owner reads only this fixed, closed file.
    }

    internal static async Task RequestAndWaitAsync(OracleOwnedStopBinding binding, string reason)
    {
        try
        {
            Publish(binding, reason);
        }
        catch (Exception)
        {
            // Publication failure must also stay inside the Job until the owner's independent hard bound.
        }
        // Never unwind into fixture mutation restore while any owned-child state is uncertain.
        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        throw new InvalidOperationException("The noncancellable owned-stop wait unexpectedly returned.");
    }
}
