using AwesomeAssertions;

using Opportunity.Benchmarks.Bundles;
using Opportunity.Benchmarks.Capture;
using Opportunity.Benchmarks.Infrastructure;

namespace Opportunity.Benchmarks.Tests;

public class HostCaptureTests
{
    [Fact]
    public void Reads_cpu_memory_disks_and_kernel_from_a_proc_tree()
    {
        string root = FakeRoot();

        HostInfo host = new HostCapture(root).Capture(new HostOverrides { Role = "load-generator", IncludeHostname = false },
            new ContainerRuntimeInfo { Name = "docker", RootDir = "/var/lib/docker" });

        host.Role.Should().Be("load-generator");
        host.Hostname.Should().BeNull();
        host.Cpu.Model.Should().Be("AMD EPYC 9454P 48-Core Processor");
        host.Cpu.Vendor.Should().Be("AuthenticAMD");
        host.Cpu.LogicalCores.Should().Be(4);
        host.Cpu.PhysicalCores.Should().Be(2);
        host.Cpu.Sockets.Should().Be(1);
        host.Cpu.MaxMhz.Should().Be(3800);
        host.Cpu.Hypervisor.Should().BeFalse();
        host.Cpu.Flags.Should().Equal("sse4_2", "avx", "avx2", "avx512f");
        host.Cpu.ScalingGovernor.Should().Be("performance");
        host.Memory.TotalBytes.Should().Be(131_072_000L * 1024);
        host.Memory.TransparentHugePages.Should().Be("madvise");
        host.Os.Kernel.Should().Be("6.8.0-45-generic");
        host.Os.Distribution.Should().Be("Ubuntu 24.04.1 LTS");
        host.Os.MaxMapCount.Should().Be(262_144);
        host.Platform.Kind.Should().Be(PlatformKind.BareMetal);
        host.Platform.Vendor.Should().Be("Supermicro");

        host.Disks.Select(d => d.Name).Should().Equal("nvme0n1", "sda");
        DiskInfo nvme = host.Disks[0];
        nvme.Type.Should().Be(DiskType.Nvme);
        nvme.SizeBytes.Should().Be(3_750_748_848L * 512);
        nvme.Model.Should().Be("SAMSUNG MZQL23T8HCLS");
        nvme.Scheduler.Should().Be("none");
        nvme.Backs.Should().Equal("/var/lib/docker");
        host.Disks[1].Type.Should().Be(DiskType.Hdd);
        host.Disks[1].Backs.Should().BeNull();
    }

    [Theory]
    [InlineData("nvme1n1", false, DiskType.Nvme)]
    [InlineData("sdb", false, DiskType.Ssd)]
    [InlineData("sdc", true, DiskType.Hdd)]
    [InlineData("vda", true, DiskType.Virtual)]
    [InlineData("xvdf", false, DiskType.Virtual)]
    [InlineData("sdd", null, DiskType.Unknown)]
    public void Classifies_disks(string name, bool? rotational, DiskType expected) =>
        HostCapture.ClassifyDisk(name, rotational).Should().Be(expected);

    [Fact]
    public void Cloud_vendor_and_shape_are_recorded()
    {
        string root = FakeRoot();
        File.WriteAllText(Path.Combine(root, "sys/class/dmi/id/sys_vendor"), "Amazon EC2\n");
        File.WriteAllText(Path.Combine(root, "sys/class/dmi/id/product_name"), "r7i.4xlarge\n");

        PlatformInfo platform = new HostCapture(root).Capture(new HostOverrides { Region = "eu-central-1" }).Platform;

        platform.Kind.Should().Be(PlatformKind.Cloud);
        platform.CloudProvider.Should().Be("aws");
        platform.InstanceType.Should().Be("r7i.4xlarge");
        platform.Region.Should().Be("eu-central-1");
    }

    [Fact]
    public async Task Captured_manifest_from_a_fake_host_validates_against_the_environment_schema()
    {
        string versions = VersionsEnvFile.Locate()!;
        var capturer = new EnvironmentCapturer(new NoDocker());

        EnvironmentManifest manifest = await capturer.CaptureAsync(new CaptureOptions
        {
            Profile = BenchmarkProfile.DeveloperRegression,
            SystemRoot = FakeRoot(),
            VersionsEnvPath = versions,
        }, TestContext.Current.CancellationToken);

        manifest.Software.VersionsEnv.Sha256.Should().Be(BenchJson.Sha256OfFile(versions));
        manifest.Software.Images.Should().Contain(i => i.Key == "POSTGRES" && i.Digest != null);
        manifest.Durability.Mode.Should().Be(DurabilityMode.Production);
        BundleSchemas.ValidateEnvironment(BundleSchemas.ToNode(manifest)).Should().BeEmpty();
    }

    private sealed class NoDocker : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(1, string.Empty, "Cannot connect to the Docker daemon"));
    }

    private static string FakeRoot()
    {
        string root = SampleBundle.NewDirectory();
        void Put(string relative, string content)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        string Processor(int index, int core) => $"""
            processor	: {index}
            vendor_id	: AuthenticAMD
            model name	: AMD EPYC 9454P 48-Core Processor
            cpu MHz		: 2750.000
            physical id	: 0
            core id		: {core}
            flags		: fpu sse4_2 avx avx2 avx512f

            """;

        Put("proc/cpuinfo", Processor(0, 0) + Processor(1, 1) + Processor(2, 0) + Processor(3, 1));
        Put("proc/meminfo", "MemTotal:       131072000 kB\nMemFree:        1000 kB\nSwapTotal:             0 kB\n");
        Put("proc/sys/kernel/osrelease", "6.8.0-45-generic\n");
        Put("proc/sys/vm/max_map_count", "262144\n");
        Put("proc/sys/vm/swappiness", "1\n");
        Put("etc/os-release", "NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\n");
        Put("sys/kernel/mm/transparent_hugepage/enabled", "always [madvise] never\n");
        Put("sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq", "3800000\n");
        Put("sys/devices/system/cpu/cpu0/cpufreq/scaling_governor", "performance\n");
        Put("sys/class/dmi/id/sys_vendor", "Supermicro\n");
        Put("sys/class/dmi/id/product_name", "AS -1115SV-WTNRT\n");
        Put("sys/block/nvme0n1/size", "3750748848\n");
        Put("sys/block/nvme0n1/queue/rotational", "0\n");
        Put("sys/block/nvme0n1/queue/scheduler", "[none] mq-deadline\n");
        Put("sys/block/nvme0n1/device/model", "SAMSUNG MZQL23T8HCLS\n");
        Put("sys/block/sda/size", "1000\n");
        Put("sys/block/sda/queue/rotational", "1\n");
        Put("sys/block/loop0/size", "8\n");
        Put("proc/self/mounts", "/dev/nvme0n1p2 / ext4 rw 0 0\n/dev/sda1 /backup xfs rw 0 0\noverlay /var/lib/docker/overlay2/x/merged overlay rw 0 0\n");
        return root;
    }
}
