using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SharpDeps.StandardJobOwner;

internal sealed class NativeFailure(string operation, int error) : Win32Exception(error, operation)
{
    internal string Operation { get; } = operation;
}

internal sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public KernelHandle() : base(true) { }
    internal KernelHandle(nint value) : base(true) => SetHandle(value);
    internal int? CloseError { get; private set; }
    protected override bool ReleaseHandle()
    {
        bool ok = Native.CloseHandle(handle);
        if (!ok)
            CloseError = Marshal.GetLastPInvokeError();
        return ok;
    }
}

internal static class Native
{
    internal const uint KillOnClose = 0x2000, Suspended = 4, UnicodeEnvironment = 0x400,
        ExtendedStartup = 0x80000, NoWindow = 0x08000000;
    internal const uint Signaled = 0, Timeout = 258, Failed = 0xffffffff;
    internal const int ExtendedLimitsClass = 9, AccountingClass = 1;

    internal static void Require(bool ok, string operation)
    {
        if (!ok)
            throw new NativeFailure(operation, Marshal.GetLastPInvokeError());
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes { public uint Length; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public uint Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize; public nint ReservedBytes, Stdin, Stdout, Stderr;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation { public nint Process, Thread; public uint Pid, Tid; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct BasicLimits
    {
        public long ProcessUserTime, JobUserTime; public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit;
        public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Accounting
    {
        public long User, Kernel, PeriodUser, PeriodKernel;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern KernelHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(KernelHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryLimits(KernelHandle job, int infoClass, out ExtendedLimits info, uint length, nint returned);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryAccounting(KernelHandle job, int infoClass, out Accounting info, uint length, nint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessInJob(KernelHandle process, KernelHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetHandleInformation(KernelHandle handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(nint attributes, int count, uint flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(nint attributes, uint flags, nuint key, nint value, nuint bytes, nint previous, nint returned);
    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(nint attributes);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags,
        nint environment, string cwd, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(KernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(KernelHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(KernelHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(KernelHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(KernelHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern KernelHandle CreateFileW(string name, uint access, uint share,
        ref SecurityAttributes attributes, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateDirectoryW(string path, nint attributes);

    internal sealed class Attributes : IDisposable
    {
        internal nint Pointer { get; private set; }
        private nint jobs, handles, environment;
        private bool initialized;
        internal nint Environment => environment;

        internal Attributes(KernelHandle job, nint stdin, nint stdout, nint stderr, string environmentBlock)
        {
            try
            {
                nuint bytes = 0;
                bool sizeResult = InitializeProcThreadAttributeList(0, 2, 0, ref bytes);
                int sizeError = Marshal.GetLastPInvokeError();
                if (sizeResult || sizeError != 122 || bytes == 0)
                    throw new NativeFailure("InitializeProcThreadAttributeList(size)", sizeError);
                Pointer = Marshal.AllocHGlobal(checked((int)bytes));
                Require(InitializeProcThreadAttributeList(Pointer, 2, 0, ref bytes), "InitializeProcThreadAttributeList");
                initialized = true;
                jobs = Marshal.AllocHGlobal(nint.Size);
                Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
                handles = Marshal.AllocHGlobal(nint.Size * 3);
                Marshal.WriteIntPtr(handles, 0, stdin);
                Marshal.WriteIntPtr(handles, nint.Size, stdout);
                Marshal.WriteIntPtr(handles, nint.Size * 2, stderr);
                environment = Marshal.StringToHGlobalUni(environmentBlock);
                Require(UpdateProcThreadAttribute(Pointer, 0, 0x2000D, jobs, (nuint)nint.Size, 0, 0),
                    "UpdateProcThreadAttribute(JOB_LIST)");
                Require(UpdateProcThreadAttribute(Pointer, 0, 0x20002, handles, (nuint)(nint.Size * 3), 0, 0),
                    "UpdateProcThreadAttribute(HANDLE_LIST)");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(Pointer);
                initialized = false;
            }
            if (Pointer != 0)
            {
                Marshal.FreeHGlobal(Pointer);
                Pointer = 0;
            }
            if (jobs != 0)
            {
                Marshal.FreeHGlobal(jobs);
                jobs = 0;
            }
            if (handles != 0)
            {
                Marshal.FreeHGlobal(handles);
                handles = 0;
            }
            if (environment != 0)
            {
                Marshal.FreeHGlobal(environment);
                environment = 0;
            }
        }
    }
}
