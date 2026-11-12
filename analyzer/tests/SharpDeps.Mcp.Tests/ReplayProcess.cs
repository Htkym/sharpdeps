namespace SharpDeps.Mcp.Tests;

using System.Diagnostics;
using System.Text;

internal sealed record ReplayCapture(byte[] Stdout, byte[] Stderr, int ExitCode, double WallMilliseconds,
    double? CpuMilliseconds, int ProcessId);

internal sealed class ReplayProcess : IAsyncDisposable
{
    private const int MaximumOutputBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(3);
    private readonly Process process;
    private readonly CancellationTokenSource reads = new();
    private readonly Task<byte[]> stderr;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly byte[] buffer = new byte[8192];
    private int offset, available;
    private bool closed;

    internal ReplayProcess(string workingDirectory, string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        process = Process.Start(start) ?? throw new InvalidOperationException("Replay process did not start.");
        stderr = ReadBoundedAsync(process.StandardError.BaseStream, reads.Token);
        ObserveFault(stderr);
    }

    internal int Id => process.Id;
    internal double? CpuMilliseconds => ReadCpuMilliseconds(process);

    internal static double? ReadCpuMilliseconds(Process target)
    {
        try
        {
            target.Refresh();
            return target.TotalProcessorTime.TotalMilliseconds;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    internal async Task SendAsync(byte[] bytes, CancellationToken token)
    {
        await WaitForIoOrStderrFailureAsync(process.StandardInput.BaseStream.WriteAsync(bytes, token).AsTask(), token);
        await WaitForIoOrStderrFailureAsync(process.StandardInput.BaseStream.FlushAsync(token), token);
    }

    internal async Task<byte[]> ReadLineAsync(CancellationToken token)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (offset == available)
            {
                var read = process.StandardOutput.BaseStream.ReadAsync(buffer, token).AsTask();
                await WaitForIoOrStderrFailureAsync(read, token);
                available = await read;
                offset = 0;
                if (available == 0)
                {
                    throw new EndOfStreamException("MCP stdout ended before the expected JSON-RPC response.");
                }
            }
            var newline = Array.IndexOf(buffer, (byte)'\n', offset, available - offset);
            var end = newline < 0 ? available : newline + 1;
            line.Write(buffer, offset, end - offset);
            offset = end;
            if (line.Length > MaximumOutputBytes)
            {
                throw new InvalidDataException("MCP response exceeded the replay capture limit.");
            }
            if (newline >= 0)
            {
                return line.ToArray();
            }
        }
    }

    internal async Task<ReplayCapture> FinishAsync(CancellationToken token)
    {
        process.StandardInput.Close();
        var trailing = ReadBoundedAsync(process.StandardOutput.BaseStream, reads.Token);
        ObserveFault(trailing);
        await WaitForExitOrReadFailureAsync(process, token, trailing, stderr);
        closed = true;
        var output = await trailing.WaitAsync(CleanupTimeout, token);
        if (offset < available)
        {
            output = buffer[offset..available].Concat(output).ToArray();
        }
        var error = await stderr.WaitAsync(CleanupTimeout, token);
        return new(output, error, process.ExitCode, clock.Elapsed.TotalMilliseconds, CpuMilliseconds, Id);
    }

    internal static async Task<ReplayCapture> RunAsync(string root, string executable,
        IEnumerable<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var child = new ReplayProcess(root, executable, arguments);
        var stdout = ReadBoundedAsync(child.process.StandardOutput.BaseStream, child.reads.Token);
        ObserveFault(stdout);
        child.process.StandardInput.Close();
        await WaitForExitOrReadFailureAsync(child.process, token, stdout, child.stderr);
        child.closed = true;
        var output = await stdout.WaitAsync(CleanupTimeout, token);
        var error = await child.stderr.WaitAsync(CleanupTimeout, token);
        return new(output, error, child.process.ExitCode, child.clock.Elapsed.TotalMilliseconds,
            child.CpuMilliseconds, child.Id);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!closed && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
                var exited = process.WaitForExitAsync();
                ObserveFault(exited);
                await exited.WaitAsync(CleanupTimeout);
            }
        }
        finally
        {
            reads.Cancel();
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            try
            {
                await stderr.WaitAsync(CleanupTimeout);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException or InvalidDataException or TimeoutException)
            {
            }
            reads.Dispose();
            process.Dispose();
        }
    }

    private async Task WaitForIoOrStderrFailureAsync(Task operation, CancellationToken token)
    {
        ObserveFault(operation);
        var completed = await Task.WhenAny(operation, stderr).WaitAsync(token);
        if (completed == stderr)
        {
            await stderr;
        }
        await operation;
    }

    private static async Task WaitForExitOrReadFailureAsync(Process child, CancellationToken token, params Task[] readers)
    {
        var exit = child.WaitForExitAsync(token);
        ObserveFault(exit);
        var pending = readers.Append(exit).ToList();
        while (true)
        {
            var completed = await Task.WhenAny(pending).WaitAsync(token);
            await completed;
            if (completed == exit)
            {
                return;
            }
            pending.Remove(completed);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, token);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > MaximumOutputBytes)
            {
                throw new InvalidDataException("Process output exceeded the replay capture limit.");
            }
            output.Write(bytes, 0, read);
        }
    }

    private static void ObserveFault(Task task)
        => _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
        }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
