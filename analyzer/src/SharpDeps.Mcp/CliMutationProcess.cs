namespace SharpDeps.Mcp;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

/// <summary>Contains trusted build/generator console output in an explicitly owned CLI process.</summary>
internal static class CliMutationProcess
{
    private const int MaximumOutputChars = 256 * 1024;

    public static async Task<(int Exit, string Json)> RunAsync(string[] arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var assembly = typeof(McpQueryAdapter).Assembly.Location;
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw Failure())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(assembly);
        foreach (var argument in new[] { "--cli-mutation-helper" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        Process started;
        try
        {
            started = Process.Start(start) ?? throw Failure();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw Failure();
        }
        using var process = started;
        using var cancellation = token.Register(CancelHelper);
        var stdout = CaptureAsync(process.StandardOutput, CancelHelper);
        var stderr = CaptureAsync(process.StandardError, CancelHelper);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync());
            token.ThrowIfCancellationRequested();
            if ((await stdout).Truncated || (await stderr).Truncated)
                throw Failure();
            // SDK discovery can leave non-JSON console lines even after the CLI reply.
            foreach (var line in (await stdout).Text.Split('\n').Reverse())
            {
                try
                {
                    using var reply = JsonDocument.Parse(line);
                    if (reply.RootElement.ValueKind == JsonValueKind.Object
                        && reply.RootElement.TryGetProperty("apiVersion", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == "1"
                        && reply.RootElement.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String && command.GetString() == arguments[0]
                        && reply.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                        return (process.ExitCode, line.Trim());
                }
                catch (JsonException) { }
            }
            throw Failure();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw Failure();
        }
        finally
        {
            CancelHelper();
            // The helper can own the shared lease: preserve admitted clients through CLI DrainAsync.
            await process.WaitForExitAsync();
            try
            {
                await Task.WhenAll(stdout, stderr);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or McpProtocolException) { }
        }

        void CancelHelper()
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private static async Task<(string Text, bool Truncated)> CaptureAsync(StreamReader reader, Action cancel)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory()) is var count && count != 0)
            {
                var retained = Math.Min(count, MaximumOutputChars - text.Length);
                text.Append(buffer, 0, retained);
                if (retained != count && !truncated)
                {
                    truncated = true;
                    cancel();
                }
                // Continue draining discarded output so a shared owner can finish other clients.
            }
        }
        catch
        {
            cancel();
            throw;
        }
        return (text.ToString(), truncated);
    }

    private static McpProtocolException Failure()
        => new("MCP_MUTATION_FAILED_CHECK_SAVED_STATUS", McpErrorCode.InternalError);
}
