using System.Runtime.InteropServices;

namespace Opportunity.Rendering.Sandboxing;

/// <summary>
/// Kernel controls the render sandbox child applies to itself before it reads any input (E11-T03), in this order:
/// leave root for an unprivileged user (only when started as root), resource limits, <c>no_new_privs</c>, Landlock
/// (file system confined to the runtime, the application and the document's work directory; TCP denied; signals and
/// abstract Unix sockets scoped to the process) and the seccomp filter (<see cref="SeccompFilter"/>). Each step reports
/// what it achieved; nothing here needs a capability.
/// </summary>
internal static partial class LinuxSandbox
{
    private const string Libc = "libc";

    private const int PrSetNoNewPrivs = 38;
    private const int PrSetDumpable = 4;
    private const int SeccompSetModeFilter = 1;
    private const uint SeccompFilterFlagTsync = 1;

    private const int RlimitCpu = 0;
    private const int RlimitFsize = 1;
    private const int RlimitData = 2;
    private const int RlimitCore = 4;
    private const int RlimitNofile = 7;

    private const int OPath = 0x200000;
    private const int OCloexec = 0x80000;

    private const long SysLandlockCreateRuleset = 444;
    private const long SysLandlockAddRule = 445;
    private const long SysLandlockRestrictSelf = 446;
    private const uint LandlockCreateRulesetVersion = 1;
    private const int LandlockRulePathBeneath = 1;

    // Landlock file system rights (linux/landlock.h).
    private const ulong FsExecute = 1UL << 0;
    private const ulong FsWriteFile = 1UL << 1;
    private const ulong FsReadFile = 1UL << 2;
    private const ulong FsReadDir = 1UL << 3;
    private const ulong FsRemoveDir = 1UL << 4;
    private const ulong FsRemoveFile = 1UL << 5;
    private const ulong FsMakeChar = 1UL << 6;
    private const ulong FsMakeDir = 1UL << 7;
    private const ulong FsMakeReg = 1UL << 8;
    private const ulong FsMakeSock = 1UL << 9;
    private const ulong FsMakeFifo = 1UL << 10;
    private const ulong FsMakeBlock = 1UL << 11;
    private const ulong FsMakeSym = 1UL << 12;
    private const ulong FsRefer = 1UL << 13;
    private const ulong FsTruncate = 1UL << 14;
    private const ulong FsIoctlDev = 1UL << 15;
    private const ulong NetBindTcp = 1UL << 0;
    private const ulong NetConnectTcp = 1UL << 1;
    private const ulong ScopeAbstractUnixSocket = 1UL << 0;
    private const ulong ScopeSignal = 1UL << 1;

    private const ulong FileRights = FsExecute | FsWriteFile | FsReadFile | FsTruncate | FsIoctlDev;

    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>Effective user ID of this process.</summary>
    public static uint EffectiveUserId => IsSupported ? geteuid() : uint.MaxValue;

    public static int ParentProcessId => getppid();

    /// <summary>Whether a (null) signal to <paramref name="pid"/> is permitted.</summary>
    public static bool CanSignal(int pid) => kill(pid, 0) == 0;

    /// <summary>Sends SIGSTOP and waits until every thread of <paramref name="pid"/> is stopped; false on timeout.</summary>
    public static bool StopAndWait(int pid, TimeSpan timeout)
    {
        const int SigStop = 19;
        if (kill(pid, SigStop) != 0)
        {
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (AllThreadsStopped(pid))
            {
                return true;
            }

            Thread.Sleep(1);
        }

        return false;
    }

    public static void Continue(int pid)
    {
        const int SigCont = 18;
        _ = kill(pid, SigCont);
    }

    private static bool AllThreadsStopped(int pid)
    {
        try
        {
            foreach (var task in Directory.EnumerateDirectories($"/proc/{pid}/task"))
            {
                var stat = File.ReadAllText(Path.Combine(task, "stat"));
                var end = stat.LastIndexOf(')');
                if (end < 0 || end + 2 >= stat.Length || stat[end + 2] is not ('T' or 't'))
                {
                    return false;
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Drops every group and switches real, effective and saved IDs (all threads: glibc broadcasts the change).</summary>
    public static void SwitchUser(uint uid, uint gid)
    {
        Check(setgroups(0, 0), "setgroups");
        Check(setresgid(gid, gid, gid), "setresgid");
        Check(setresuid(uid, uid, uid), "setresuid");
    }

    public static void SetLimit(SandboxLimit limit, ulong value)
    {
        var resource = limit switch
        {
            SandboxLimit.CpuSeconds => RlimitCpu,
            SandboxLimit.FileBytes => RlimitFsize,
            SandboxLimit.DataBytes => RlimitData,
            SandboxLimit.CoreBytes => RlimitCore,
            SandboxLimit.OpenFiles => RlimitNofile,
            _ => throw new ArgumentOutOfRangeException(nameof(limit)),
        };

        // Soft limit below the hard one where the kernel signals first (SIGXCPU before SIGKILL).
        var rlimit = new RLimit(value, limit == SandboxLimit.CpuSeconds ? value + 2 : value);
        Check(setrlimit(resource, in rlimit), "setrlimit");
    }

    public static void SetNoNewPrivileges() => Check(prctl(PrSetNoNewPrivs, 1, 0, 0, 0), "prctl(PR_SET_NO_NEW_PRIVS)");

    /// <summary>
    /// Makes this process non-dumpable: its <c>/proc/&lt;pid&gt;</c> files (environment, memory) become root-owned and
    /// it cannot be traced by processes of the same user. The parent worker does this when Landlock is unavailable.
    /// </summary>
    public static void SetNotDumpable() => Check(prctl(PrSetDumpable, 0, 0, 0, 0), "prctl(PR_SET_DUMPABLE)");

    /// <summary>The Landlock ABI version, or 0 when the kernel has none (or the container profile denies it).</summary>
    public static int LandlockAbi()
    {
        if (!IsSupported || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return 0;
        }

        var abi = syscall(SysLandlockCreateRuleset, 0, 0, (nint)LandlockCreateRulesetVersion, 0);
        return abi > 0 ? (int)abi : 0;
    }

    /// <summary>
    /// Restricts the calling thread (and what it executes) with Landlock: read and execute only beneath
    /// <paramref name="executable"/>, read only beneath <paramref name="readable"/>, read and write only beneath
    /// <paramref name="writable"/>; with ABI 4+ no TCP bind or connect; with ABI 6+ no signals or abstract Unix sockets
    /// outside the domain. Paths that do not exist are skipped. Requires no_new_privs. Returns the ABI applied, or 0
    /// when Landlock is unavailable. Landlock binds only the calling thread: follow with <see cref="ExecSelf"/>.
    /// </summary>
    public static unsafe int RestrictFileSystem(IEnumerable<string> executable, IEnumerable<string> readable, IEnumerable<string> writable)
    {
        var abi = LandlockAbi();
        if (abi == 0)
        {
            return 0;
        }

        var handledFs = FsExecute | FsWriteFile | FsReadFile | FsReadDir | FsRemoveDir | FsRemoveFile | FsMakeChar | FsMakeDir | FsMakeReg
            | FsMakeSock | FsMakeFifo | FsMakeBlock | FsMakeSym;
        if (abi >= 2)
        {
            handledFs |= FsRefer;
        }

        if (abi >= 3)
        {
            handledFs |= FsTruncate;
        }

        if (abi >= 5)
        {
            handledFs |= FsIoctlDev;
        }

        var attr = new RulesetAttr(handledFs, abi >= 4 ? NetBindTcp | NetConnectTcp : 0, abi >= 6 ? ScopeAbstractUnixSocket | ScopeSignal : 0);
        var size = abi >= 6 ? 24 : abi >= 4 ? 16 : 8;
        var ruleset = (int)syscall(SysLandlockCreateRuleset, (nint)(&attr), size, 0, 0);
        if (ruleset < 0)
        {
            throw new SandboxSetupException($"landlock_create_ruleset failed (errno {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            var read = (FsReadFile | FsReadDir) & handledFs;
            var write = (FsReadFile | FsReadDir | FsWriteFile | FsRemoveDir | FsRemoveFile | FsMakeDir | FsMakeReg | FsTruncate) & handledFs;
            foreach (var path in executable)
            {
                AddRule(ruleset, path, read | FsExecute);
            }

            foreach (var path in readable)
            {
                AddRule(ruleset, path, read);
            }

            foreach (var path in writable)
            {
                AddRule(ruleset, path, write);
            }

            if (syscall(SysLandlockRestrictSelf, ruleset, 0, 0, 0) != 0)
            {
                throw new SandboxSetupException($"landlock_restrict_self failed (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            _ = close(ruleset);
        }

        return abi;
    }

    /// <summary>Installs <see cref="SeccompFilter"/> on every thread (TSYNC). Requires no_new_privs.</summary>
    public static unsafe bool InstallSeccompFilter()
    {
        var table = SeccompFilter.ForCurrentArchitecture();
        if (!IsSupported || table is null)
        {
            return false;
        }

        var filter = SeccompFilter.Build(table, Environment.ProcessId);
        fixed (SeccompFilter.SockFilter* instructions = filter)
        {
            var program = new SockFprog((ushort)filter.Length, (nint)instructions);
            var nr = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 317 : 277;
            if (syscall(nr, SeccompSetModeFilter, (nint)SeccompFilterFlagTsync, (nint)(&program), 0) != 0)
            {
                throw new SandboxSetupException($"seccomp(SECCOMP_SET_MODE_FILTER) failed (errno {Marshal.GetLastPInvokeError()}).");
            }
        }

        return true;
    }

    /// <summary>
    /// Replaces this process image with a new run of the same application (dotnet host, entry assembly) and
    /// <paramref name="arguments"/>, keeping the environment and open standard streams. Returns only on failure.
    /// </summary>
    public static unsafe void ExecSelf(IReadOnlyList<string> arguments)
    {
        var host = Environment.ProcessPath ?? throw new SandboxSetupException("The process path is unknown.");
        var entry = Environment.GetCommandLineArgs()[0];
        var argv = new List<string> { host };
        if (!string.Equals(Path.GetFullPath(entry), Path.GetFullPath(host), StringComparison.Ordinal))
        {
            argv.Add(entry);
        }

        argv.AddRange(arguments);
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => $"{e.Key}={e.Value}")
            .ToList();
        var argvPointers = ToNative(argv);
        var envPointers = ToNative(environment);
        try
        {
            fixed (nint* argvBlock = argvPointers)
            fixed (nint* envBlock = envPointers)
            {
                _ = execve(host, (nint)argvBlock, (nint)envBlock);
            }

            throw new SandboxSetupException($"execve failed (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally
        {
            foreach (var pointer in argvPointers.Concat(envPointers).Where(p => p != 0))
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    private static nint[] ToNative(List<string> values)
    {
        var pointers = new nint[values.Count + 1];
        for (var i = 0; i < values.Count; i++)
        {
            pointers[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
        }

        return pointers;
    }

    /// <summary>Changes the owner of a path (the parent hands the work directory to the sandbox user).</summary>
    public static void ChangeOwner(string path, uint uid, uint gid) => Check(chown(path, uid, gid), $"chown");

    private static unsafe void AddRule(int ruleset, string path, ulong access)
    {
        var fd = open(path, OPath | OCloexec);
        if (fd < 0)
        {
            return;
        }

        try
        {
            var isDirectory = Directory.Exists(path);
            var rule = new PathBeneathAttr(isDirectory ? access : access & FileRights, fd);
            if (syscall(SysLandlockAddRule, ruleset, LandlockRulePathBeneath, (nint)(&rule), 0) != 0)
            {
                throw new SandboxSetupException($"landlock_add_rule failed for a sandbox path (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            _ = close(fd);
        }
    }

    private static void Check(int result, string call)
    {
        if (result != 0)
        {
            throw new SandboxSetupException($"{call} failed (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RLimit(ulong Current, ulong Maximum);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SockFprog(ushort Length, nint Filter);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RulesetAttr(ulong HandledAccessFs, ulong HandledAccessNet, ulong Scoped);

    /// <summary>struct landlock_path_beneath_attr is packed: 12 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct PathBeneathAttr(ulong AllowedAccess, int ParentFd);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int setrlimit(int resource, in RLimit limit);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int setgroups(nuint size, nint list);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int setresuid(uint ruid, uint euid, uint suid);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int setresgid(uint rgid, uint egid, uint sgid);

    [LibraryImport(Libc)]
    private static partial uint geteuid();

    [LibraryImport(Libc)]
    private static partial int getppid();

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int kill(int pid, int signal);

    [LibraryImport(Libc, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int chown(string path, uint owner, uint group);

    [LibraryImport(Libc, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport(Libc, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int execve(string path, nint argv, nint envp);

    /// <summary>glibc's syscall(2) is an assembly stub that only moves registers, so a fixed-arity import is safe.</summary>
    [LibraryImport(Libc, SetLastError = true)]
    private static partial long syscall(long number, nint arg1, nint arg2, nint arg3, nint arg4);
}

internal enum SandboxLimit
{
    CpuSeconds,
    FileBytes,
    DataBytes,
    CoreBytes,
    OpenFiles,
}

/// <summary>A sandbox control could not be applied; the child reports it and exits without reading the input.</summary>
public sealed class SandboxSetupException : Exception
{
    public SandboxSetupException()
    {
    }

    public SandboxSetupException(string message)
        : base(message)
    {
    }

    public SandboxSetupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
