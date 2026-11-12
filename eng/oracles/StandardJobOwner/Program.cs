using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
namespace SharpDeps.StandardJobOwner;

internal static class Program
{
    internal static void RequestCancellation(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        // A callback captured before unsubscribe can arrive after final disposal.
        catch (ObjectDisposedException)
        {
        }
    }
    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--self-check"])
            {
                PureChecks.Run();
                return 0;
            }
            bool execute = args is ["--execute", _];
            string file = args switch
            {
                [var path] => path,
                ["--plan", var path] => path,
                ["--execute", var path] => path,
                _ => throw new ArgumentException("Usage: StandardJobOwner [--plan|--execute] plan.json; --self-check is process-free.")
            };
            if (new FileInfo(file).Length > 1048576)
                throw new ArgumentException("Plan exceeds 1 MiB.");
            byte[] bytes = await File.ReadAllBytesAsync(file);
            LaunchPlan plan = JsonSerializer.Deserialize<LaunchPlan>(bytes, LaunchPlan.Json) ??
                throw new ArgumentException("Null plan.");
            plan.Validate();
            if (!execute)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Mode = "PLAN_ONLY_NO_CHILD",
                    PlanSha256 = LaunchPlan.Sha(bytes),
                    CommandSha256 = LaunchPlan.Sha(Encoding.Unicode.GetBytes(LaunchPlan.CommandLine(plan.Application, plan.Arguments))),
                    EnvironmentSha256 = LaunchPlan.Sha(Encoding.Unicode.GetBytes(LaunchPlan.EnvironmentBlock(plan.Environment))),
                    plan.RunDirectory,
                    TestAcceptance = "NOT_EVALUATED_STANDARD_TRX_ONLY"
                }, LaunchPlan.Json));
                return 0;
            }
            return await new Owner(plan, bytes).RunAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }
}

internal sealed record Failure(string Operation, int? NativeError, string Message, DateTimeOffset Utc, double ElapsedSeconds);
internal sealed class LogRecord(string path)
{
    public string Path { get; } = path;
    public long Bytes
    {
        get; set;
    }
    public long ReadBytes
    {
        get; set;
    }
    public bool Eof
    {
        get; set;
    }
    public bool Flushed
    {
        get; set;
    }
    public string? Sha256
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }
    public bool DrainTaskCompleted
    {
        get; set;
    }
}
internal sealed class Receipt
{
    public string Schema { get; } = "sharpdeps-standard-job-owner-receipt";
    public int SchemaVersion { get; } = 1;
    public Guid RunId { get; } = Guid.NewGuid();
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedUtc
    {
        get; set;
    }
    public string? PlanSha256
    {
        get; set;
    }
    public string OwnerOutcome { get; set; } = "INCOMPLETE";
    public string TestAcceptance { get; } = "NOT_EVALUATED_STANDARD_TRX_ONLY";
    public bool OldRunClosureEstablished { get; } = false;
    public string CreationStrategy { get; } = "JOB_LIST+CREATE_SUSPENDED";
    public bool LaunchQueueSealed { get; } = true;
    public bool BreakawayAllowed { get; } = false;
    public uint? RootPidFromCreate
    {
        get; set;
    }
    public string StartTokenKind { get; } = "run-local-create-return-not-OS-birth-time";
    public bool JobCreated
    {
        get; set;
    }
    public bool LimitsVerified
    {
        get; set;
    }
    public bool MembershipVerifiedBeforeResume
    {
        get; set;
    }
    public bool CreationReceiptVerified
    {
        get; set;
    }
    public OwnedStopBinding? OwnedStopBinding
    {
        get; set;
    }
    public string OwnedStopStatus { get; set; } = "DISABLED";
    public string? OwnedStopReason
    {
        get; set;
    }
    public string? OwnedStopRequestSha256
    {
        get; set;
    }
    public bool RootResumed
    {
        get; set;
    }
    public uint? RootWaitResult
    {
        get; set;
    }
    public uint? RootExitCode
    {
        get; set;
    }
    public bool RootTerminalRecorded
    {
        get; set;
    }
    public bool RootTerminalReceiptVerified
    {
        get; set;
    }
    public bool TerminationRequested
    {
        get; set;
    }
    public bool AccountingQuerySucceeded
    {
        get; set;
    }
    public uint? ActiveProcesses
    {
        get; set;
    }
    public DateTimeOffset? AccountingUtc
    {
        get; set;
    }
    public bool? OwnedProcessesZero
    {
        get; set;
    }
    public bool CleanupVerified
    {
        get; set;
    }
    public bool JobClosed
    {
        get; set;
    }
    public bool LogsComplete
    {
        get; set;
    }
    public string[] PendingLocalIo { get; set; } = [];
    public LogRecord? Stdout
    {
        get; set;
    }
    public LogRecord? Stderr
    {
        get; set;
    }
    public InputPin[] PreInputs { get; set; } = [];
    public InputPin[] PostInputs { get; set; } = [];
    public InputPin? StandardTrxPin
    {
        get; set;
    }
    public Failure? PrimaryFailure
    {
        get; set;
    }
    public List<Failure> CleanupFailures { get; } = [];
    public List<Failure> PersistenceFailures { get; } = [];
}

internal sealed class PipeLog : IDisposable
{
    internal readonly LogRecord Record;
    internal readonly NamedPipeServerStream Reader;
    internal readonly NamedPipeClientStream Writer;
    internal readonly FileStream Log;
    internal Task? PumpTask, FinishTask;
    internal bool Settled => PumpTask?.IsCompleted == true && FinishTask?.IsCompleted == true;

    internal PipeLog(string directory, string leaf)
    {
        Record = new(leaf);
        string name = "sharpdeps-owned-" + Guid.NewGuid().ToString("N");
        Reader = new(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 0);
        try
        {
            Writer = new(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly,
                TokenImpersonationLevel.Identification, HandleInheritability.Inheritable);
            try
            {
                Log = new(Path.Combine(directory, leaf), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
            }
            catch
            {
                Writer.Dispose();
                throw;
            }
        }
        catch
        {
            Reader.Dispose();
            throw;
        }
    }

    internal async Task ConnectAsync(CancellationToken token)
    {
        Task wait = Reader.WaitForConnectionAsync(token);
        await Writer.ConnectAsync(token);
        await wait;
    }

    internal async Task PumpAsync(long cap, CancellationToken stop)
    {
        try
        {
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await Reader.ReadAsync(buffer, stop);
                if (read == 0)
                {
                    Record.Eof = true;
                    break;
                }
                Record.ReadBytes += read;
                int keep = (int)Math.Min(read, cap - Record.Bytes);
                if (keep > 0)
                {
                    await Log.WriteAsync(buffer.AsMemory(0, keep), stop);
                    Record.Bytes += keep;
                }
                if (keep != read)
                    throw new IOException("Log cap exceeded; saved prefix only.");
            }
        }
        catch (Exception ex)
        {
            Record.Error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            using var flushStop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            async Task Finish(CancellationToken token)
            {
                try
                {
                    await Log.FlushAsync(token);
                    Record.Flushed = true;
                }
                finally
                {
                    Log.Dispose();
                }
            }
            FinishTask = Finish(flushStop.Token);
            try
            {
                await FinishTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                flushStop.Cancel();
                Record.Error ??= "Flush/close: " + ex.Message;
            }
        }
    }

    public void Dispose()
    {
        Writer.Dispose();
        Reader.Dispose();
        if (PumpTask is null)
            Log.Dispose();
    }
}

internal sealed class Owner(LaunchPlan plan, byte[] planBytes)
{
    private readonly Receipt receipt = new() { PlanSha256 = LaunchPlan.Sha(planBytes) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource cancel = new(), drainStop = new();
    private KernelHandle? job, process, thread, stdin;
    private PipeLog? stdout, stderr;
    private OwnedStopBinding? ownedStop;
    private bool rootObserveFailed, accountingFailed, runDirectoryCreated, cleaningUp;
    private readonly List<(string Operation, Task Task)> pendingIo = [];
    private double rootExitAt = double.PositiveInfinity, cleanupEnd;
    private string stage = "setup";
    private double WorkEnd => plan.WorkSeconds;
    private double HardEnd => plan.WorkSeconds + plan.CleanupSeconds;
    private double Now => clock.Elapsed.TotalSeconds;

    internal async Task<int> RunAsync()
    {
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            Program.RequestCancellation(cancel);
        };
        Console.CancelKeyPress += handler;
        try
        {
            stage = "CreateDirectoryW(fresh run)";
            LaunchPlan.NoReparseParents(Path.GetDirectoryName(plan.RunDirectory)!);
            Native.Require(Native.CreateDirectoryW(plan.RunDirectory, 0), stage);
            runDirectoryCreated = true;
            if (plan.EnableOwnedStop)
            {
                ownedStop = OwnedStop.CreateBinding(plan.RunDirectory);
                receipt.OwnedStopBinding = ownedStop;
                receipt.OwnedStopStatus = "BOUND_NO_REQUEST";
                stage = "persist owned-stop binding before root creation";
                await WriteObjectAsync("owned-stop.binding.json", ownedStop, WorkEnd);
            }
            stage = "persist plan";
            await WriteCheckedAsync("plan.json", planBytes, WorkEnd);
            stage = "pre-input pins";
            receipt.PreInputs = await CheckInputsAsync(WorkEnd);
            stage = "pipe setup";
            stdout = new(plan.RunDirectory, "stdout.bin");
            receipt.Stdout = stdout.Record;
            stderr = new(plan.RunDirectory, "stderr.bin");
            receipt.Stderr = stderr.Record;
            await AwaitOwnedIoAsync(async token =>
            {
                await Task.WhenAll(stdout.ConnectAsync(token), stderr.ConnectAsync(token));
                return true;
            }, Math.Min(WorkEnd, Now + 5), "connect owned pipes");
            stdout.PumpTask = stdout.PumpAsync(plan.LogCapBytes, drainStop.Token);
            stderr.PumpTask = stderr.PumpAsync(plan.LogCapBytes, drainStop.Token);
            stage = "CreateJobObjectW(anonymous)";
            job = Native.CreateJobObjectW(0, null);
            Native.Require(!job.IsInvalid, stage);
            receipt.JobCreated = true;
            Native.Require(Native.GetHandleInformation(job, out uint jobFlags), "GetHandleInformation(ownJob)");
            if ((jobFlags & 1) != 0)
                throw new InvalidOperationException("ownJob must not be inheritable.");
            var limits = new Native.ExtendedLimits { Basic = new() { Flags = Native.KillOnClose } };
            Native.Require(Native.SetInformationJobObject(job, Native.ExtendedLimitsClass, ref limits,
                (uint)Marshal.SizeOf<Native.ExtendedLimits>()), "SetInformationJobObject(KILL_ON_CLOSE)");
            Native.Require(Native.QueryLimits(job, Native.ExtendedLimitsClass, out var verified,
                (uint)Marshal.SizeOf<Native.ExtendedLimits>(), 0), "QueryInformationJobObject(limits)");
            if (verified.Basic.Flags != Native.KillOnClose)
                throw new InvalidOperationException("Unexpected owned Job limit flags.");
            receipt.LimitsVerified = true;
            var sa = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), Inherit = 1 };
            stdin = Native.CreateFileW("NUL", 0x80000000, 3, ref sa, 3, 0, 0);
            Native.Require(!stdin.IsInvalid, "CreateFileW(NUL stdin)");
            using (var attrs = new Native.Attributes(job, stdin.DangerousGetHandle(),
                stdout.Writer.SafePipeHandle.DangerousGetHandle(), stderr.Writer.SafePipeHandle.DangerousGetHandle(),
                LaunchPlan.EnvironmentBlock(plan.Environment.Concat(ownedStop is null ? [] : OwnedStop.Environment(ownedStop)))))
            {
                var startup = new Native.StartupInfoEx
                {
                    Startup = new()
                    {
                        Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(),
                        Flags = 0x100,
                        Stdin = stdin.DangerousGetHandle(),
                        Stdout = stdout.Writer.SafePipeHandle.DangerousGetHandle(),
                        Stderr = stderr.Writer.SafePipeHandle.DangerousGetHandle()
                    },
                    Attributes = attrs.Pointer
                };
                _ = Remaining(WorkEnd);
                cancel.Token.ThrowIfCancellationRequested();
                CheckLogFailure();
                stage = "CreateProcessW(JOB_LIST+SUSPENDED)";
                Native.Require(Native.CreateProcessW(plan.Application, new StringBuilder(LaunchPlan.CommandLine(plan.Application, plan.Arguments)),
                    0, 0, true, Native.Suspended | Native.UnicodeEnvironment | Native.ExtendedStartup | Native.NoWindow,
                    attrs.Environment, plan.WorkingDirectory, ref startup, out var created), stage);
                process = new(created.Process);
                thread = new(created.Thread);
                receipt.RootPidFromCreate = created.Pid;
            }
            stdout.Writer.Dispose();
            stderr.Writer.Dispose();
            Close(ref stdin, "CloseHandle(stdin)");
            Native.Require(Native.IsProcessInJob(process!, job, out bool member), "IsProcessInJob(created root, ownJob)");
            if (!member)
                throw new InvalidOperationException("Created root is not in the owned Job; do not resume.");
            receipt.MembershipVerifiedBeforeResume = true;
            stage = "persist creation receipt before resume";
            await WriteObjectAsync("creation.json", receipt, WorkEnd);
            receipt.CreationReceiptVerified = true;
            _ = Remaining(WorkEnd);
            cancel.Token.ThrowIfCancellationRequested();
            CheckLogFailure();
            stage = "ResumeThread(created primary)";
            uint previous = Native.ResumeThread(thread!);
            if (previous == uint.MaxValue)
                throw new NativeFailure(stage, Marshal.GetLastPInvokeError());
            if (previous != 1)
                throw new InvalidOperationException("Unexpected previous suspend count: " + previous);
            receipt.RootResumed = true;
            stage = "monitor work/descendant/output deadlines";
            while (true)
            {
                cancel.Token.ThrowIfCancellationRequested();
                _ = Remaining(WorkEnd);
                await CheckOwnedStopAsync(WorkEnd);
                await ObserveRootAsync(WorkEnd);
                QueryJob();
                CheckLogFailure();
                if (Closed())
                {
                    await CheckOwnedStopAsync(WorkEnd);
                    break;
                }
                if (Now - rootExitAt >= plan.DescendantGraceSeconds)
                    throw new TimeoutException("Root exited but owned descendants or output did not close within grace.");
                await Task.Delay(10);
            }
        }
        catch (Exception ex)
        {
            receipt.PrimaryFailure ??= Error(stage, ex);
        }
        finally
        {
            cleaningUp = true;
            cleanupEnd = Math.Min(HardEnd, Now + plan.CleanupSeconds);
            await CleanupAsync();
            Console.CancelKeyPress -= handler;
            cancel.Dispose();
            drainStop.Dispose();
        }

        if (!runDirectoryCreated)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(receipt.PrimaryFailure, LaunchPlan.Json));
            return 2; // Never write into a pre-existing or uncreated run directory.
        }
        receipt.PendingLocalIo = PendingOperations();
        receipt.EndedUtc = DateTimeOffset.UtcNow;
        receipt.OwnerOutcome = receipt.PrimaryFailure is not null || receipt.CleanupFailures.Count != 0 ||
            receipt.PersistenceFailures.Count != 0 || receipt.PendingLocalIo.Length != 0 || !receipt.CleanupVerified || !receipt.LogsComplete || !receipt.JobClosed
            ? "INFRASTRUCTURE_FAILURE"
            : receipt.RootExitCode == 0 ? "OWNER_CLOSED_TEST_NOT_EVALUATED" : "WORKLOAD_EXIT_NONZERO";
        try
        {
            string hash = await WriteObjectAsync("receipt.final.json", receipt, cleanupEnd);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                receipt.OwnerOutcome,
                FinalReceiptSha256 = hash,
                ReceiptPath = Path.Combine(plan.RunDirectory, "receipt.final.json"),
                receipt.TestAcceptance
            }, LaunchPlan.Json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Final receipt persistence failed: " + ex);
            return 2;
        }
        return receipt.OwnerOutcome == "OWNER_CLOSED_TEST_NOT_EVALUATED" ? 0 :
            receipt.OwnerOutcome == "WORKLOAD_EXIT_NONZERO" ? 1 : 2;
    }

    private async Task ObserveRootAsync(double end)
    {
        if (process is null || rootObserveFailed)
            return;
        try
        {
            uint wait = Native.WaitForSingleObject(process, 0);
            receipt.RootWaitResult = wait;
            if (wait == Native.Timeout)
                return;
            if (wait == Native.Failed)
                throw new NativeFailure("WaitForSingleObject(created root)", Marshal.GetLastPInvokeError());
            if (wait != Native.Signaled)
                throw new InvalidOperationException("Unexpected root wait result: " + wait);
            Native.Require(Native.GetExitCodeProcess(process, out uint code), "GetExitCodeProcess(signaled creation handle)");
            receipt.RootExitCode = code;
            receipt.RootTerminalRecorded = true;
            rootExitAt = Now;
            await WriteObjectAsync("root-terminal.json", new
            {
                receipt.RunId,
                receipt.RootPidFromCreate,
                receipt.RootWaitResult,
                receipt.RootExitCode,
                Utc = DateTimeOffset.UtcNow
            }, end);
            receipt.RootTerminalReceiptVerified = true;
            Close(ref thread, "CloseHandle(root thread)");
            Close(ref process, "CloseHandle(terminal root)");
        }
        catch
        {
            rootObserveFailed = true;
            throw;
        }
    }

    private void QueryJob()
    {
        if (job is null || accountingFailed)
            return;
        bool ok = Native.QueryAccounting(job, Native.AccountingClass, out var info, (uint)Marshal.SizeOf<Native.Accounting>(), 0);
        if (!ok)
        {
            int error = Marshal.GetLastPInvokeError();
            accountingFailed = true;
            receipt.AccountingQuerySucceeded = false;
            receipt.ActiveProcesses = null;
            receipt.OwnedProcessesZero = null;
            throw new NativeFailure("QueryInformationJobObject(ownJob accounting)", error);
        }
        receipt.AccountingQuerySucceeded = true;
        receipt.ActiveProcesses = info.ActiveProcesses;
        receipt.OwnedProcessesZero = info.ActiveProcesses == 0;
        receipt.AccountingUtc = DateTimeOffset.UtcNow;
    }

    private bool Closed() => receipt.RootTerminalRecorded && receipt.OwnedProcessesZero == true &&
        stdout?.Settled == true && stderr?.Settled == true && pendingIo.All(item => item.Task.IsCompleted);

    private async Task CheckOwnedStopAsync(double end)
    {
        var binding = ownedStop;
        if (binding is null)
            return;
        byte[]? bytes = await AwaitOwnedIoAsync(token => OwnedStop.ReadAsync(binding, token), end,
            "read predetermined owned-stop request");
        if (bytes is null)
            return;
        receipt.OwnedStopRequestSha256 = bytes.Length <= OwnedStop.MaxRequestBytes ? LaunchPlan.Sha(bytes) : null;
        try
        {
            receipt.OwnedStopReason = OwnedStop.Parse(bytes, binding.Nonce);
            receipt.OwnedStopStatus = "VALID_REQUEST";
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            receipt.OwnedStopStatus = "INVALID_REQUEST";
            throw new InvalidDataException("Invalid owned-stop request; infrastructure failure and ownJob cleanup only.", ex);
        }
        // A request never asserts a test result; only CleanupAsync can terminate the retained ownJob.
        throw new IOException("Owned stop requested: " + receipt.OwnedStopReason);
    }

    private void CheckLogFailure()
    {
        foreach (PipeLog? pipe in new[] { stdout, stderr })
            if (pipe?.PumpTask?.IsCompleted == true && (pipe.PumpTask.IsFaulted || pipe.Record.Error is not null))
                throw new IOException(pipe.Record.Path + ": " + (pipe.Record.Error ?? pipe.PumpTask.Exception?.ToString()));
    }

    private async Task CleanupAsync()
    {
        try
        {
            stdout?.Writer.Dispose();
            stderr?.Writer.Dispose();
            Close(ref stdin, "CloseHandle(stdin)");
            if (receipt.RootPidFromCreate is not null && !Closed())
            {
                receipt.TerminationRequested = true;
                Try("TerminateJobObject(ownJob)", () => Native.Require(Native.TerminateJobObject(job!, 0xE0010001), "TerminateJobObject(ownJob)"));
                if (!receipt.MembershipVerifiedBeforeResume && process is not null)
                    Try("TerminateProcess(unresumed creation handle)", () =>
                        Native.Require(Native.TerminateProcess(process, 0xE0010002), "TerminateProcess(unresumed creation handle)"));
            }
            while (Now < cleanupEnd - 3 && receipt.RootPidFromCreate is not null)
            {
                try
                {
                    await ObserveRootAsync(cleanupEnd - 2);
                }
                catch (Exception ex)
                {
                    receipt.CleanupFailures.Add(Error("observe retained root", ex));
                }
                if (rootObserveFailed)
                {
                    Close(ref thread, "CloseHandle(failed-observe thread)");
                    Close(ref process, "CloseHandle(failed-observe root)");
                }
                if (!accountingFailed)
                    Try("query owned Job", QueryJob);
                if (Closed())
                    break;
                await Task.Delay(10);
            }
            drainStop.Cancel();
            stdout?.Reader.Dispose();
            stderr?.Reader.Dispose();
            var pumps = new[] { stdout?.PumpTask, stderr?.PumpTask }.OfType<Task>().ToArray();
            if (pumps.Length != 0)
            {
                try
                {
                    await Task.WhenAll(pumps).WaitAsync(Remaining(cleanupEnd - 2));
                }
                catch (Exception ex)
                {
                    receipt.CleanupFailures.Add(Error("bounded drain cancellation/completion", ex));
                }
            }
            var unsettled = pendingIo.Select(item => item.Task)
                .Concat(new[] { stdout?.FinishTask, stderr?.FinishTask }.OfType<Task>()).ToArray();
            if (unsettled.Length != 0)
            {
                try
                {
                    await Task.WhenAll(unsettled).WaitAsync(Remaining(cleanupEnd - 2));
                }
                catch (Exception ex)
                {
                    receipt.CleanupFailures.Add(Error("bounded local I/O completion", ex));
                }
            }
            foreach (PipeLog? pipe in new[] { stdout, stderr })
            {
                if (pipe is null)
                    continue;
                pipe.Record.DrainTaskCompleted = pipe.Settled;
                if (pipe.Record.DrainTaskCompleted && !pipe.PumpTask!.IsFaulted)
                {
                    try
                    {
                        pipe.Record.Sha256 = await HashAsync(Path.Combine(plan.RunDirectory, pipe.Record.Path), cleanupEnd - 1);
                    }
                    catch (Exception ex)
                    {
                        receipt.PersistenceFailures.Add(Error("pin " + pipe.Record.Path, ex));
                    }
                }
                if (pipe.Record.Error is not null)
                    receipt.CleanupFailures.Add(Error("drain " + pipe.Record.Path, new IOException(pipe.Record.Error)));
            }
            receipt.LogsComplete = stdout is not null && stderr is not null &&
                new[] { stdout.Record, stderr.Record }.All(log => log.DrainTaskCompleted && log.Eof && log.Flushed && log.Error is null && log.Sha256 is not null);
            if (receipt.RootPidFromCreate is not null && !accountingFailed)
                Try("final owned Job accounting", QueryJob);
            receipt.CleanupVerified = receipt.RootTerminalRecorded && receipt.RootTerminalReceiptVerified &&
                receipt.AccountingQuerySucceeded && receipt.OwnedProcessesZero == true;
            if (!receipt.CleanupVerified && receipt.RootPidFromCreate is not null)
                receipt.CleanupFailures.Add(Error("owned closure", new IOException("Root terminal and same retained Job ActiveProcesses=0 were not both verified.")));
            try
            {
                receipt.PostInputs = await CheckInputsAsync(cleanupEnd - 1);
                if (plan.StandardTrxPath is not null && File.Exists(plan.StandardTrxPath) && receipt.CleanupVerified)
                    receipt.StandardTrxPin = new(plan.StandardTrxPath, await HashAsync(plan.StandardTrxPath, cleanupEnd - 1));
                if (job is not null)
                    await WriteObjectAsync("receipt.before-job-close.json", receipt, cleanupEnd - 1);
            }
            catch (Exception ex)
            {
                receipt.PersistenceFailures.Add(Error("post-input/TRX/pre-close persistence", ex));
            }
        }
        catch (Exception ex)
        {
            receipt.CleanupFailures.Add(Error("cleanup", ex));
        }
        finally
        {
            Close(ref thread, "CloseHandle(final thread)");
            Close(ref process, "CloseHandle(final root)");
            Close(ref stdin, "CloseHandle(final stdin)");
            // Without prior zero proof this is emergency kill-on-close, never cleanup verification.
            if (job is not null)
            {
                var owned = job;
                Close(ref job, "CloseHandle(ownJob)");
                receipt.JobClosed = receipt.JobCreated && owned.CloseError is null;
            }
            Try("dispose owned stdout pipe", () => stdout?.Dispose());
            Try("dispose owned stderr pipe", () => stderr?.Dispose());
        }
    }

    private void Try(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            receipt.CleanupFailures.Add(Error(operation, ex));
        }
    }

    private void Close(ref KernelHandle? handle, string operation)
    {
        if (handle is null)
            return;
        var closing = handle;
        handle = null;
        closing.Dispose();
        if (closing.CloseError is int code)
            receipt.CleanupFailures.Add(Error(operation, new NativeFailure(operation, code)));
    }

    private Failure Error(string operation, Exception ex) => new(
        ex is NativeFailure native ? native.Operation : operation,
        ex is NativeFailure failed ? failed.NativeErrorCode : null,
        ex.GetType().Name + ": " + ex.Message, DateTimeOffset.UtcNow, Now);

    private TimeSpan Remaining(double end)
    {
        double remaining = end - Now;
        return remaining > 0 ? TimeSpan.FromSeconds(remaining) :
            throw new TimeoutException("Monotonic phase deadline exceeded.");
    }

    private async Task<T> AwaitOwnedIoAsync<T>(Func<CancellationToken, Task<T>> operation, double end, string label)
    {
        using var stop = cleaningUp ? new CancellationTokenSource() :
            CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        stop.CancelAfter(Remaining(end));
        Task<T> task = operation(stop.Token);
        try
        {
            return await task.WaitAsync(Remaining(end));
        }
        catch
        {
            stop.Cancel(); // WaitAsync only limits the wait; the actual operation must be canceled separately.
            if (!task.IsCompleted)
                pendingIo.Add((label, task));
            throw;
        }
    }

    private string[] PendingOperations() => pendingIo.Where(item => !item.Task.IsCompleted)
        .Select(item => item.Operation)
        .Concat(new[] { stdout, stderr }.OfType<PipeLog>().Where(pipe => pipe.PumpTask is not null && !pipe.Settled)
            .Select(pipe => "unsettled " + pipe.Record.Path + " read/write/flush/close"))
        .ToArray();

    private Task<string> HashAsync(string path, double end) => AwaitOwnedIoAsync(async token =>
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }, end, "hash " + path);

    private async Task<InputPin[]> CheckInputsAsync(double end)
    {
        var pins = new[] { new InputPin(plan.Application, plan.ApplicationSha256) }.Concat(plan.Inputs).ToArray();
        foreach (InputPin pin in pins)
            if (!string.Equals(await HashAsync(pin.Path, end), pin.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Input SHA256 mismatch: " + pin.Path);
        return pins;
    }

    private Task<string> WriteObjectAsync(string leaf, object value, double end) =>
        WriteCheckedAsync(leaf, JsonSerializer.SerializeToUtf8Bytes(value, LaunchPlan.Json), end);

    private async Task<string> WriteCheckedAsync(string leaf, byte[] bytes, double end)
    {
        try
        {
            return await AwaitOwnedIoAsync(async token =>
            {
                string path = Path.Combine(plan.RunDirectory, leaf);
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await file.WriteAsync(bytes, token);
                    await file.FlushAsync(token);
                }
                byte[] saved = await File.ReadAllBytesAsync(path, token);
                if (!bytes.AsSpan().SequenceEqual(saved))
                    throw new IOException("Receipt readback mismatch: " + leaf);
                return LaunchPlan.Sha(saved);
            }, end, "persist/readback " + leaf);
        }
        catch (Exception ex)
        {
            receipt.PersistenceFailures.Add(Error("persist/readback " + leaf, ex));
            throw;
        }
    }
}