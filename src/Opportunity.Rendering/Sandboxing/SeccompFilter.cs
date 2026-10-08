using System.Runtime.InteropServices;

namespace Opportunity.Rendering.Sandboxing;

/// <summary>
/// The seccomp-BPF program the render sandbox installs on itself (E11-T03). It is a deny list on top of the container
/// runtime's default profile: no sockets of any family (so no network, no Unix-socket services), no new programs
/// (<c>execve</c>), no new processes (only threads), no namespaces or mounts, no tracing or reading other processes'
/// memory, no signals to other processes, no kernel keyring, BPF, io_uring, perf or module loading. A wrong
/// architecture (e.g. the x32 ABI) kills the process. Denied calls fail with <c>EPERM</c>; <c>clone3</c> fails with
/// <c>ENOSYS</c> so the C library falls back to <c>clone</c>, whose flags can be inspected.
/// </summary>
internal static class SeccompFilter
{
    // linux/audit.h
    public const uint AuditArchX8664 = 0xC000003E;
    public const uint AuditArchAarch64 = 0xC00000B7;

    // linux/filter.h, linux/seccomp.h
    private const ushort LdWAbs = 0x20;
    private const ushort JeqK = 0x15;
    private const ushort JgeK = 0x35;
    private const ushort JsetK = 0x45;
    private const ushort RetK = 0x06;
    private const uint RetKillProcess = 0x80000000;
    private const uint RetErrno = 0x00050000;
    private const uint RetAllow = 0x7FFF0000;
    private const int Eperm = 1;
    private const int Enosys = 38;

    // struct seccomp_data: nr at 0, arch at 4, args[0] (low word, little endian) at 16.
    private const uint OffsetNr = 0;
    private const uint OffsetArch = 4;
    private const uint OffsetArg0Low = 16;

    private const uint CloneThread = 0x00010000;

    /// <summary>CLONE_NEWNS | NEWCGROUP | NEWUTS | NEWIPC | NEWUSER | NEWPID | NEWNET.</summary>
    private const uint CloneNamespaces = 0x00020000 | 0x02000000 | 0x04000000 | 0x08000000 | 0x10000000 | 0x20000000 | 0x40000000;

    /// <summary>The per-architecture system call numbers the filter needs.</summary>
    internal sealed record SyscallTable(
        uint AuditArch,
        bool HasX32,
        int[] Denied,
        int CloneNr,
        int Clone3,
        int[] SignalSelfOnly);

    /// <summary>x86_64 (asm/unistd_64.h).</summary>
    public static readonly SyscallTable X64 = new(
        AuditArchX8664,
        HasX32: true,
        Denied:
        [
            41, // socket
            42, // connect
            53, // socketpair
            59, // execve
            322, // execveat
            57, // fork
            58, // vfork
            101, // ptrace
            310, // process_vm_readv
            311, // process_vm_writev
            165, // mount
            166, // umount2
            155, // pivot_root
            161, // chroot
            272, // unshare
            308, // setns
            321, // bpf
            298, // perf_event_open
            248, // add_key
            249, // request_key
            250, // keyctl
            246, // kexec_load
            320, // kexec_file_load
            175, // init_module
            313, // finit_module
            176, // delete_module
            323, // userfaultfd
            425, // io_uring_setup
            426, // io_uring_enter
            427, // io_uring_register
            135, // personality
            303, // name_to_handle_at
            304, // open_by_handle_at
            200, // tkill
            424, // pidfd_send_signal
            434, // pidfd_open
            438, // pidfd_getfd
        ],
        CloneNr: 56,
        Clone3: 435,
        SignalSelfOnly: [62 /* kill */, 234 /* tgkill */, 129 /* rt_sigqueueinfo */, 297 /* rt_tgsigqueueinfo */]);

    /// <summary>aarch64 (asm-generic/unistd.h; there is no fork or vfork).</summary>
    public static readonly SyscallTable Arm64 = new(
        AuditArchAarch64,
        HasX32: false,
        Denied:
        [
            198, // socket
            203, // connect
            199, // socketpair
            221, // execve
            281, // execveat
            117, // ptrace
            270, // process_vm_readv
            271, // process_vm_writev
            40, // mount
            39, // umount2
            41, // pivot_root
            51, // chroot
            97, // unshare
            268, // setns
            280, // bpf
            241, // perf_event_open
            217, // add_key
            218, // request_key
            219, // keyctl
            104, // kexec_load
            294, // kexec_file_load
            105, // init_module
            273, // finit_module
            106, // delete_module
            282, // userfaultfd
            425, // io_uring_setup
            426, // io_uring_enter
            427, // io_uring_register
            92, // personality
            264, // name_to_handle_at
            265, // open_by_handle_at
            130, // tkill
            424, // pidfd_send_signal
            434, // pidfd_open
            438, // pidfd_getfd
        ],
        CloneNr: 220,
        Clone3: 435,
        SignalSelfOnly: [129 /* kill */, 131 /* tgkill */, 138 /* rt_sigqueueinfo */, 240 /* rt_tgsigqueueinfo */]);

    /// <summary>The table of the running process's architecture, or null where the filter is not available.</summary>
    public static SyscallTable? ForCurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => X64,
        Architecture.Arm64 => Arm64,
        _ => null,
    };

    /// <summary>Builds the program; signals are allowed only to <paramref name="selfPid"/> (the runtime signals its own threads).</summary>
    public static SockFilter[] Build(SyscallTable table, int selfPid)
    {
        ArgumentNullException.ThrowIfNull(table);
        var program = new List<SockFilter>
        {
            Load(OffsetArch),
            Jump(JeqK, table.AuditArch, 1, 0),
            Return(RetKillProcess),
            Load(OffsetNr),
        };

        if (table.HasX32)
        {
            // x32 system calls (__X32_SYSCALL_BIT) bypass a filter written for x86_64 numbers.
            program.Add(Jump(JgeK, 0x40000000, 0, 1));
            program.Add(Return(RetErrno | Eperm));
        }

        foreach (var nr in table.Denied)
        {
            program.Add(Jump(JeqK, (uint)nr, 0, 1));
            program.Add(Return(RetErrno | Eperm));
        }

        program.Add(Jump(JeqK, (uint)table.Clone3, 0, 1));
        program.Add(Return(RetErrno | Enosys));

        // clone: threads only (CLONE_THREAD set), never new namespaces.
        program.Add(Jump(JeqK, (uint)table.CloneNr, 0, 6));
        program.Add(Load(OffsetArg0Low));
        program.Add(Jump(JsetK, CloneNamespaces, 0, 1));
        program.Add(Return(RetErrno | Eperm));
        program.Add(Jump(JsetK, CloneThread, 1, 0));
        program.Add(Return(RetErrno | Eperm));
        program.Add(Return(RetAllow));

        // Signals: the first argument (pid / thread group) must be this process. The accumulator still holds nr here.
        foreach (var nr in table.SignalSelfOnly)
        {
            program.Add(Jump(JeqK, (uint)nr, 0, 4));
            program.Add(Load(OffsetArg0Low));
            program.Add(Jump(JeqK, (uint)selfPid, 0, 1));
            program.Add(Return(RetAllow));
            program.Add(Return(RetErrno | Eperm));
        }

        program.Add(Return(RetAllow));
        return [.. program];
    }

    private static SockFilter Load(uint offset) => new(LdWAbs, 0, 0, offset);

    private static SockFilter Jump(ushort code, uint k, byte jt, byte jf) => new(code, jt, jf, k);

    private static SockFilter Return(uint k) => new(RetK, 0, 0, k);

    /// <summary>struct sock_filter.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct SockFilter(ushort Code, byte Jt, byte Jf, uint K);
}
