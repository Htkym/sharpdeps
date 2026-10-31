namespace SharpDeps.Cli;

/// <summary>Opt-in stdio cancellation for a single VS Code mutation caller.</summary>
public static class CliMutationEntry
{
    public static async Task<int> RunAsync(string[] arguments, Stream input, TextWriter output,
        TextWriter error, CancellationToken token = default)
    {
        if (arguments is not ["index" or "update", ..])
        {
            await error.WriteLineAsync("VSCODE_MUTATION_ARGUMENT_INVALID");
            return 2;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Do not delay normal exit waiting for a parent that keeps stdin open.
        _ = CancelOnInputAsync();
        try
        {
            return await CliApplication.RunAsync(arguments, output, error, stop.Token);
        }
        finally
        {
            stop.Cancel();
        }

        async Task CancelOnInputAsync()
        {
            try
            {
                await input.ReadExactlyAsync(new byte[1], stop.Token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException) { }
        }
    }
}
