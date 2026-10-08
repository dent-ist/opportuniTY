using AwesomeAssertions;

using Opportunity.Rendering.Sandboxing;

namespace Opportunity.UnitTests.Rendering.Sandbox;

/// <summary>
/// The render sandbox's seccomp program (E11-T03), run through a small classic-BPF interpreter for both architectures:
/// what it denies, what it lets through, and that a foreign architecture is killed.
/// </summary>
public sealed class SeccompFilterTests
{
    private const uint Allow = 0x7FFF0000;
    private const uint Eperm = 0x00050001;
    private const uint Enosys = 0x00050026;
    private const uint Kill = 0x80000000;
    private const int Self = 4242;

    public static TheoryData<string> Architectures => new() { "x64", "arm64" };

    [Theory]
    [MemberData(nameof(Architectures))]
    public void Network_exec_tracing_and_namespaces_are_denied(string architecture)
    {
        var (table, names) = Table(architecture);
        var program = SeccompFilter.Build(table, Self);

        foreach (var name in new[] { "socket", "connect", "socketpair", "execve", "execveat", "ptrace", "process_vm_readv", "unshare", "setns", "mount", "bpf", "io_uring_setup", "tkill", "pidfd_open" })
        {
            Run(program, table.AuditArch, names[name]).Should().Be(Eperm, name);
        }
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    public void Ordinary_calls_threads_and_signals_to_itself_are_allowed(string architecture)
    {
        var (table, names) = Table(architecture);
        var program = SeccompFilter.Build(table, Self);
        const ulong ThreadFlags = 0x003D0F00; // glibc pthread_create: VM|FS|FILES|SIGHAND|THREAD|SYSVSEM|SETTLS|PARENT_SETTID|CHILD_CLEARTID

        Run(program, table.AuditArch, names["read"]).Should().Be(Allow);
        Run(program, table.AuditArch, names["openat"]).Should().Be(Allow);
        Run(program, table.AuditArch, table.CloneNr, ThreadFlags).Should().Be(Allow);
        Run(program, table.AuditArch, names["kill"], Self).Should().Be(Allow);
        Run(program, table.AuditArch, names["tgkill"], Self).Should().Be(Allow);
    }

    [Theory]
    [MemberData(nameof(Architectures))]
    public void New_processes_namespaces_and_signals_to_others_are_denied(string architecture)
    {
        var (table, names) = Table(architecture);
        var program = SeccompFilter.Build(table, Self);

        Run(program, table.AuditArch, table.CloneNr, 17 /* SIGCHLD: fork */).Should().Be(Eperm);
        Run(program, table.AuditArch, table.CloneNr, 0x003D0F00 | 0x40000000 /* CLONE_NEWNET */).Should().Be(Eperm);
        Run(program, table.AuditArch, table.Clone3).Should().Be(Enosys, "the C library then falls back to clone, whose flags are checked");
        Run(program, table.AuditArch, names["kill"], 1).Should().Be(Eperm);
        Run(program, table.AuditArch, names["kill"], 0).Should().Be(Eperm, "0 is the whole process group");
        Run(program, table.AuditArch, names["tgkill"], Self + 1).Should().Be(Eperm);
    }

    [Fact]
    public void A_foreign_architecture_or_the_x32_ABI_does_not_pass()
    {
        var program = SeccompFilter.Build(SeccompFilter.X64, Self);

        Run(program, SeccompFilter.AuditArchAarch64, 0).Should().Be(Kill);
        Run(program, 0x40000003 /* i386 */, 0).Should().Be(Kill);
        Run(program, SeccompFilter.AuditArchX8664, 0x40000000 + 41).Should().Be(Eperm);
    }

    private static (SeccompFilter.SyscallTable Table, Dictionary<string, int> Names) Table(string architecture) => architecture == "x64"
        ? (SeccompFilter.X64, new Dictionary<string, int>
        {
            ["read"] = 0,
            ["openat"] = 257,
            ["socket"] = 41,
            ["connect"] = 42,
            ["socketpair"] = 53,
            ["execve"] = 59,
            ["execveat"] = 322,
            ["ptrace"] = 101,
            ["process_vm_readv"] = 310,
            ["unshare"] = 272,
            ["setns"] = 308,
            ["mount"] = 165,
            ["bpf"] = 321,
            ["io_uring_setup"] = 425,
            ["tkill"] = 200,
            ["pidfd_open"] = 434,
            ["kill"] = 62,
            ["tgkill"] = 234,
        })
        : (SeccompFilter.Arm64, new Dictionary<string, int>
        {
            ["read"] = 63,
            ["openat"] = 56,
            ["socket"] = 198,
            ["connect"] = 203,
            ["socketpair"] = 199,
            ["execve"] = 221,
            ["execveat"] = 281,
            ["ptrace"] = 117,
            ["process_vm_readv"] = 270,
            ["unshare"] = 97,
            ["setns"] = 268,
            ["mount"] = 40,
            ["bpf"] = 280,
            ["io_uring_setup"] = 425,
            ["tkill"] = 130,
            ["pidfd_open"] = 434,
            ["kill"] = 129,
            ["tgkill"] = 131,
        });

    /// <summary>Classic BPF over struct seccomp_data (only the instructions the filter uses).</summary>
    private static uint Run(SeccompFilter.SockFilter[] program, uint arch, int nr, ulong arg0 = 0)
    {
        uint accumulator = 0;
        var pc = 0;
        for (var steps = 0; steps < 10_000; steps++)
        {
            var i = program[pc];
            switch (i.Code)
            {
                case 0x20:
                    accumulator = i.K switch
                    {
                        0 => (uint)nr,
                        4 => arch,
                        16 => (uint)(arg0 & 0xFFFFFFFF),
                        20 => (uint)(arg0 >> 32),
                        _ => throw new InvalidOperationException($"Unexpected load offset {i.K}."),
                    };
                    pc++;
                    break;
                case 0x15:
                    pc += 1 + (accumulator == i.K ? i.Jt : i.Jf);
                    break;
                case 0x35:
                    pc += 1 + (accumulator >= i.K ? i.Jt : i.Jf);
                    break;
                case 0x45:
                    pc += 1 + ((accumulator & i.K) != 0 ? i.Jt : i.Jf);
                    break;
                case 0x06:
                    return i.K;
                default:
                    throw new InvalidOperationException($"Unexpected opcode {i.Code:X}.");
            }
        }

        throw new InvalidOperationException("The program does not terminate.");
    }
}
