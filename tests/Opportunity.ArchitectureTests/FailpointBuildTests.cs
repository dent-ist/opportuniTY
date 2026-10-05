using System.Diagnostics;
using System.Text.RegularExpressions;

using AwesomeAssertions;

namespace Opportunity.ArchitectureTests;

/// <summary>
/// E06-T05: <c>IFaultInjector</c> failpoints (and the E17-T07 unversioned-write switch) are compiled into test builds only. Release artifacts are produced by
/// <c>dotnet publish -c Release</c> (deploy/docker/dotnet.Dockerfile), for which Directory.Build.props leaves
/// <c>OPPORTUNITY_FAILPOINTS</c> undefined; every mention of the failpoint API in production code must sit inside
/// <c>#if OPPORTUNITY_FAILPOINTS</c>, so a release build contains neither the hooks nor their call sites.
/// </summary>
public partial class FailpointBuildTests
{
    private const string Symbol = "OPPORTUNITY_FAILPOINTS";

    private static readonly string Jobs = Path.Combine("src", "Opportunity.Jobs", "Opportunity.Jobs.csproj");

    [Fact]
    public async Task Release_publish_builds_do_not_define_the_failpoint_symbol()
    {
        (await DefineConstantsAsync("-p:Configuration=Release", "-p:_IsPublishing=true")).Should().NotContain(Symbol);
        (await DefineConstantsAsync("-p:Configuration=Debug", "-p:OpportunityFailpoints=false")).Should().NotContain(Symbol);
    }

    [Fact]
    public async Task Test_builds_define_the_failpoint_symbol()
    {
        (await DefineConstantsAsync("-p:Configuration=Release")).Should().Contain(Symbol, "CI builds and tests in Release without publishing");
        (await DefineConstantsAsync("-p:Configuration=Debug")).Should().Contain(Symbol);
    }

#if OPPORTUNITY_FAILPOINTS
    [Fact]
    public void This_test_build_contains_the_failpoint_hooks()
    {
        typeof(Application.AssemblyMarker).Assembly.GetType("Opportunity.Application.Faults.IFaultInjector").Should().NotBeNull();
    }
#else
    [Fact]
    public void This_build_contains_no_failpoint_hooks()
    {
        typeof(Application.AssemblyMarker).Assembly.GetTypes()
            .Where(t => t.Namespace == "Opportunity.Application.Faults").Should().BeEmpty();
    }
#endif

    [Fact]
    public void Production_code_uses_the_failpoint_api_only_inside_the_conditional_block()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var guarded = new Stack<bool>();
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("#if ", StringComparison.Ordinal))
                {
                    guarded.Push(line[4..].Trim() == Symbol);
                }
                else if (line.StartsWith("#else", StringComparison.Ordinal) || line.StartsWith("#elif", StringComparison.Ordinal))
                {
                    // The other branch of a conditional is never guarded by the failpoint symbol.
                    if (guarded.Count > 0)
                    {
                        guarded.Pop();
                    }

                    guarded.Push(false);
                }
                else if (line.StartsWith("#endif", StringComparison.Ordinal))
                {
                    guarded.Pop();
                }
                else if (FailpointApi().IsMatch(line) && !guarded.Contains(true))
                {
                    offenders.Add($"{Path.GetRelativePath(root, path)}:{i + 1}");
                }
            }
        }

        offenders.Should().BeEmpty("failpoint hooks must be compiled out of release builds");
    }

    private static async Task<string> DefineConstantsAsync(params string[] properties)
    {
        var root = FindRepositoryRoot();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in (string[])["msbuild", Jobs, "-getProperty:DefineConstants", "-nologo", .. properties])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        process.ExitCode.Should().Be(0, await error);
        return (await output).Trim();
    }

    [GeneratedRegex(@"\b(IFaultInjector|FailpointContext|Failpoints\.|FaultFlags\.|SimulatedCrashException|Opportunity\.Application\.Faults)\b", RegexOptions.CultureInvariant)]
    private static partial Regex FailpointApi();

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Opportunity.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Opportunity.slnx) not found.");
    }
}
