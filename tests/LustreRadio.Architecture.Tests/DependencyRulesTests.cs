using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace LustreRadio.Architecture.Tests;

/// <summary>
/// Pravila zavisnosti iz specifikacije (2.5) i garancija "offline" (deo 13).
/// Test cita prevedene .dll fajlove i gleda sta svaki stvarno koristi.
/// Zahteva da je cela solucija prevedena pre pokretanja testova.
/// </summary>
public class DependencyRulesTests
{
    private const string Prefix = "LustreRadio.";

    /// <summary>Projekat -> projekti koje sme da koristi.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["Core"] = [],
        ["Audio"] = ["Core"],
        ["Analysis"] = ["Core", "Audio"],
        ["Data"] = ["Core"],
        ["Library"] = ["Core", "Audio"],
        ["Ipc"] = ["Core"],
        ["Voice"] = ["Core"],
        ["Playout"] = ["Core", "Audio", "Analysis", "Data", "Library", "Ipc"],
        ["Engine"] = ["Core", "Audio", "Analysis", "Data", "Library", "Ipc", "Playout"],
        ["Proba"] = ["Core", "Audio", "Data", "Library", "Playout"],
        ["UI"] = ["Core", "Ipc", "Voice", "Data", "Audio"],
        ["Watchdog"] = ["Core", "Ipc"],
        ["Analyzer"] = ["Core", "Audio", "Analysis"],
        ["Bench"] = ["Core", "Audio"],
    };

    /// <summary>Jedini projekat koji sme da koristi mrezu.</summary>
    private const string NetworkProject = "Voice";

    private static readonly string[] NetworkAssemblies =
    [
        "System.Net.Http", "System.Net.Http.Json", "System.Net.Sockets", "System.Net.Requests",
        "System.Net.WebClient", "System.Net.WebSockets", "System.Net.WebSockets.Client",
        "System.Net.Security", "System.Net.NameResolution", "System.Net.Quic",
        "System.Net.HttpListener", "System.Net.Mail", "System.Net.Ping", "System.Net.NetworkInformation",
    ];

    public static IEnumerable<object[]> Projects => Allowed.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Projects))]
    public void Project_UsesOnlyAllowedProjects(string project)
    {
        var used = ReferencesOf(FindAssembly(project))
            .Where(r => r.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(r => r[Prefix.Length..]);

        Assert.Empty(used.Except(Allowed[project]));
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Project_HasNoNetworkCode_ExceptVoice(string project)
    {
        if (project == NetworkProject)
        {
            return;
        }

        Assert.Empty(ReferencesOf(FindAssembly(project)).Intersect(NetworkAssemblies));
    }

    [Fact]
    public void Engine_And_Watchdog_NeverUseVoice()
    {
        Assert.DoesNotContain(Prefix + "Voice", ReferencesOf(FindAssembly("Engine")));
        Assert.DoesNotContain(Prefix + "Voice", ReferencesOf(FindAssembly("Watchdog")));
        Assert.DoesNotContain(Prefix + "Voice", ReferencesOf(FindAssembly("Playout")));
    }

    /// <summary>Dokaz da citanje referenci zaista radi (da testovi iznad ne prolaze "na prazno").</summary>
    [Fact]
    public void Reader_SeesRealReferences()
    {
        var own = ReferencesOf(typeof(DependencyRulesTests).Assembly.Location);
        Assert.Contains("xunit.core", own);

        Assert.Contains(Prefix + "Core", ReferencesOf(FindAssembly("Engine")));
        Assert.Contains("Serilog", ReferencesOf(FindAssembly("Engine")));
    }

    private static string[] ReferencesOf(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.AssemblyReferences
            .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
            .ToArray();
    }

    private static string FindAssembly(string project)
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = baseDir.Parent!.Name; // .../bin/<Configuration>/<tfm>

        var root = baseDir;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "LustreRadio.slnx")))
        {
            root = root.Parent;
        }

        Assert.True(root is not null, "Nije pronadjen koren repozitorijuma (LustreRadio.slnx).");

        var bin = Path.Combine(root!.FullName, "src", Prefix + project, "bin", configuration);
        Assert.True(Directory.Exists(bin), $"Projekat {project} nije preveden ({bin}).");

        var refSegment = Path.DirectorySeparatorChar + "ref" + Path.DirectorySeparatorChar;
        var files = Directory
            .GetFiles(bin, Prefix + project + ".dll", SearchOption.AllDirectories)
            .Where(f => !f.Contains(refSegment, StringComparison.Ordinal))
            .ToArray();

        Assert.True(files.Length > 0, $"Nije pronadjen {Prefix}{project}.dll u {bin}.");
        return files[0];
    }
}
