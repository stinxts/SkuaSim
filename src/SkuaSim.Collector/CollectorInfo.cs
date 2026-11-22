using System.Reflection;

namespace SkuaSim.Collector;

/// <summary>
/// Version of this collector build, written into every <c>gamedata.json</c> flush
/// (<see cref="Models.GameDatabase.CollectorVersions"/>) so data merged from several testers shows which builds produced
/// it (e.g. drop quantities are only reliable from 1.1.0). Bump <c>&lt;Version&gt;</c> in the .csproj when the
/// collected data changes; the build adds the git commit (<c>1.1.0+19e923d</c>).
/// </summary>
public static class CollectorInfo
{
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = typeof(CollectorInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            return asm.GetName().Version?.ToString(3) ?? "unknown";
        // "1.1.0+<40-char commit>" -> "1.1.0+<7 chars>"
        var plus = info.IndexOf('+');
        return plus > 0 && info.Length > plus + 8 ? info[..(plus + 8)] : info;
    }
}
