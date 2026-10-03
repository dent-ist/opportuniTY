using System.Globalization;
using System.Runtime.InteropServices;

namespace Opportunity.Benchmarks.Capture;

/// <summary>Optional facts the host cannot reveal about itself without a cloud metadata call.</summary>
public sealed record HostOverrides
{
    public string Role { get; init; } = "all-in-one";

    public bool IncludeHostname { get; init; } = true;

    public string? CloudProvider { get; init; }

    public string? InstanceType { get; init; }

    public string? Region { get; init; }
}

/// <summary>
/// Hardware and OS facts from Linux <c>/proc</c> and <c>/sys</c>. <see cref="Root"/> lets tests point it at a fake tree.
/// </summary>
public sealed class HostCapture(string root = "/")
{
    private static readonly string[] InterestingFlags = ["sse4_2", "avx", "avx2", "avx512f", "aes", "sha_ni", "asimd"];
    private static readonly string[] IgnoredBlockPrefixes = ["loop", "ram", "zram", "sr", "nbd", "fd", "dm-", "md"];
    private static readonly string[] VirtualVendors = ["QEMU", "KVM", "VMware", "innotek", "Xen", "Parallels", "Red Hat", "Microsoft Corporation", "BHYVE"];

    public string Root { get; } = root;

    /// <summary>Captures the host. <paramref name="runtime"/> comes from the container runtime and names its data root.</summary>
    public HostInfo Capture(HostOverrides overrides, ContainerRuntimeInfo? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        CpuInfo cpu = CaptureCpu();
        Dictionary<string, string> meminfo = ReadKeyValues("proc/meminfo", ':');
        List<string> backs = [];
        if (runtime?.RootDir is { Length: > 0 } rootDir)
        {
            backs.Add(rootDir);
        }

        return new HostInfo
        {
            Role = overrides.Role,
            Hostname = overrides.IncludeHostname ? Environment.MachineName : null,
            Cpu = cpu,
            Memory = new MemoryInfo
            {
                TotalBytes = KiloBytes(meminfo, "MemTotal") ?? throw new InvalidOperationException("MemTotal missing from /proc/meminfo."),
                SwapTotalBytes = KiloBytes(meminfo, "SwapTotal"),
                TransparentHugePages = Bracketed(Read("sys/kernel/mm/transparent_hugepage/enabled")),
            },
            Disks = CaptureDisks(backs),
            Os = new OsInfo
            {
                Kernel = Read("proc/sys/kernel/osrelease") ?? RuntimeInformation.OSDescription,
                Distribution = OsRelease(),
                MaxMapCount = ParseLong(Read("proc/sys/vm/max_map_count")),
                Swappiness = (int?)ParseLong(Read("proc/sys/vm/swappiness")),
            },
            ContainerRuntime = runtime,
            Platform = CapturePlatform(cpu.Hypervisor == true, overrides),
        };
    }

    public CpuInfo CaptureCpu()
    {
        string text = Read("proc/cpuinfo") ?? throw new InvalidOperationException($"Cannot read {Path("proc/cpuinfo")}; host capture needs Linux /proc.");
        var processors = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                current = null;
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (key.Equals("processor", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                processors.Add(current);
            }

            (current ?? global).TryAdd(key, value);
        }

        if (processors.Count == 0)
        {
            throw new InvalidOperationException("No processors found in /proc/cpuinfo.");
        }

        Dictionary<string, string> first = processors[0];
        string model = Value(first, "model name") ?? Value(global, "Model") ?? Value(global, "Hardware") ?? Value(first, "CPU part") ?? "unknown";
        string[] flags = (Value(first, "flags") ?? Value(first, "Features") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int sockets = processors.Select(p => Value(p, "physical id") ?? "0").Distinct(StringComparer.Ordinal).Count();
        int physical = processors.Any(p => Value(p, "core id") is not null)
            ? processors.Select(p => (Value(p, "physical id") ?? "0") + "/" + Value(p, "core id")).Distinct(StringComparer.Ordinal).Count()
            : processors.Count;

        double? maxMhz = ParseLong(Read("sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq")) is { } khz
            ? khz / 1000.0
            : double.TryParse(Value(first, "cpu MHz"), NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz) ? Math.Round(mhz, 1) : null;

        return new CpuInfo
        {
            Model = model,
            Vendor = Value(first, "vendor_id") ?? Value(first, "CPU implementer"),
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToUpperInvariant() switch
            {
                "X64" => "x86_64",
                "ARM64" => "aarch64",
                var other => other,
            },
            LogicalCores = processors.Count,
            PhysicalCores = physical,
            Sockets = Math.Max(1, sockets),
            MaxMhz = maxMhz,
            Hypervisor = flags.Contains("hypervisor", StringComparer.Ordinal),
            ScalingGovernor = Read("sys/devices/system/cpu/cpu0/cpufreq/scaling_governor"),
            Flags = [.. InterestingFlags.Where(f => flags.Contains(f, StringComparer.Ordinal))],
        };
    }

    public IReadOnlyList<DiskInfo> CaptureDisks(IReadOnlyList<string> importantPaths)
    {
        ArgumentNullException.ThrowIfNull(importantPaths);
        string blockRoot = Path("sys/block");
        if (!Directory.Exists(blockRoot))
        {
            return [];
        }

        string[] devices = [.. Directory.EnumerateFileSystemEntries(blockRoot)
            .Select(System.IO.Path.GetFileName)
            .OfType<string>()
            .Where(name => !IgnoredBlockPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)];

        Dictionary<string, List<string>> backs = MapPathsToDevices(importantPaths, devices);
        return [.. devices.Select(name =>
        {
            bool? rotational = Read($"sys/block/{name}/queue/rotational") switch
            {
                "1" => true,
                "0" => false,
                _ => null,
            };
            return new DiskInfo
            {
                Name = name,
                Model = Read($"sys/block/{name}/device/model"),
                Type = ClassifyDisk(name, rotational),
                Rotational = rotational,
                SizeBytes = (ParseLong(Read($"sys/block/{name}/size")) ?? 0) * 512,
                Scheduler = Bracketed(Read($"sys/block/{name}/queue/scheduler")),
                Backs = backs.TryGetValue(name, out List<string>? paths) ? paths : null,
            };
        })];
    }

    public static DiskType ClassifyDisk(string name, bool? rotational)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.StartsWith("nvme", StringComparison.Ordinal))
        {
            return DiskType.Nvme;
        }

        // virtio/Xen block devices report whatever the hypervisor says; the media behind them is not knowable here.
        if (name.StartsWith("vd", StringComparison.Ordinal) || name.StartsWith("xvd", StringComparison.Ordinal))
        {
            return DiskType.Virtual;
        }

        return rotational switch
        {
            true => DiskType.Hdd,
            false => DiskType.Ssd,
            null => DiskType.Unknown,
        };
    }

    private PlatformInfo CapturePlatform(bool hypervisor, HostOverrides overrides)
    {
        string? vendor = Read("sys/class/dmi/id/sys_vendor");
        string? product = Read("sys/class/dmi/id/product_name");
        string? cloud = overrides.CloudProvider ?? DetectCloud(vendor, product);
        PlatformKind kind = cloud is not null
            ? PlatformKind.Cloud
            : hypervisor || (vendor is not null && VirtualVendors.Any(v => vendor.Contains(v, StringComparison.OrdinalIgnoreCase)))
                ? PlatformKind.Vm
                : vendor is null ? PlatformKind.Unknown : PlatformKind.BareMetal;
        return new PlatformInfo
        {
            Kind = kind,
            Vendor = vendor,
            Product = product,
            CloudProvider = cloud,
            InstanceType = overrides.InstanceType ?? (cloud == "aws" ? product : null),
            Region = overrides.Region,
        };
    }

    private static string? DetectCloud(string? vendor, string? product)
    {
        if (vendor is null)
        {
            return null;
        }

        if (vendor.Contains("Amazon", StringComparison.OrdinalIgnoreCase))
        {
            return "aws";
        }

        if (vendor.Contains("Google", StringComparison.OrdinalIgnoreCase))
        {
            return "gcp";
        }

        return product?.Contains("Hetzner", StringComparison.OrdinalIgnoreCase) == true || vendor.Contains("Hetzner", StringComparison.OrdinalIgnoreCase)
            ? "hetzner"
            : null;
    }

    private Dictionary<string, List<string>> MapPathsToDevices(IReadOnlyList<string> paths, string[] devices)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? mounts = Read("proc/self/mounts");
        if (mounts is null)
        {
            return result;
        }

        var table = mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(' '))
            .Where(parts => parts.Length >= 2)
            .Select(parts => (Source: parts[0], MountPoint: parts[1].Replace("\\040", " ", StringComparison.Ordinal)))
            .ToList();

        foreach (string path in paths)
        {
            var best = table
                .Where(m => path.StartsWith(m.MountPoint, StringComparison.Ordinal)
                    && (path.Length == m.MountPoint.Length || m.MountPoint.EndsWith('/') || path[m.MountPoint.Length] == '/'))
                .OrderByDescending(m => m.MountPoint.Length)
                .FirstOrDefault();
            if (best.Source is null || !best.Source.StartsWith("/dev/", StringComparison.Ordinal))
            {
                continue;
            }

            string partition = best.Source["/dev/".Length..];
            string? device = devices.Where(d => partition.StartsWith(d, StringComparison.Ordinal)).OrderByDescending(d => d.Length).FirstOrDefault();
            if (device is not null)
            {
                if (!result.TryGetValue(device, out List<string>? list))
                {
                    result[device] = list = [];
                }

                list.Add(path);
            }
        }

        return result;
    }

    private string? OsRelease()
    {
        Dictionary<string, string> values = ReadKeyValues("etc/os-release", '=');
        return values.TryGetValue("PRETTY_NAME", out string? pretty) ? pretty.Trim('"') : null;
    }

    private Dictionary<string, string> ReadKeyValues(string relative, char separator)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in (Read(relative) ?? string.Empty).Split('\n'))
        {
            int index = line.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                values.TryAdd(line[..index].Trim(), line[(index + 1)..].Trim());
            }
        }

        return values;
    }

    private string Path(string relative) => System.IO.Path.Combine(Root, relative);

    private string? Read(string relative)
    {
        try
        {
            string path = Path(relative);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Value(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;

    private static long? KiloBytes(Dictionary<string, string> meminfo, string key) =>
        meminfo.TryGetValue(key, out string? value) && ParseLong(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]) is { } kb ? kb * 1024 : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? result : null;

    /// <summary>"always [madvise] never" → "madvise"; plain values pass through.</summary>
    private static string? Bracketed(string? value)
    {
        if (value is null)
        {
            return null;
        }

        int open = value.IndexOf('[', StringComparison.Ordinal);
        int close = value.IndexOf(']', StringComparison.Ordinal);
        return open >= 0 && close > open ? value[(open + 1)..close] : value;
    }
}
