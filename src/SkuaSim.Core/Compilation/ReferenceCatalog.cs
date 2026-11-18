using System.Reflection;
using Microsoft.CodeAnalysis;

namespace SkuaSim.Core.Compilation;

/// <summary>
/// Metadata references available to scripts: the .NET shared frameworks of this process (including
/// Windows Desktop for System.Windows.Forms), every managed assembly shipped with the Skua client, and
/// SkuaSim.Core itself (for the virtual-clock hooks).
/// </summary>
public sealed class ReferenceCatalog
{
    private static readonly object Gate = new();
    private static ReferenceCatalog? _shared;

    public IReadOnlyList<MetadataReference> References { get; }
    public IReadOnlyList<string> ReferencePaths { get; }

    private ReferenceCatalog(IReadOnlyList<string> paths)
    {
        ReferencePaths = paths;
        References = paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    }

    public static ReferenceCatalog GetShared(string skuaAssembliesDirectory, string? skuaApplicationDirectory = null)
    {
        lock (Gate)
            return _shared ??= Build(skuaAssembliesDirectory, skuaApplicationDirectory);
    }

    private static ReferenceCatalog Build(string skuaAssembliesDirectory, string? skuaApplicationDirectory)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Skua client assemblies first (they define the API surface scripts compile against).
        foreach (var dir in new[] { skuaAssembliesDirectory, skuaApplicationDirectory }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
        {
            foreach (var dll in Directory.EnumerateFiles(dir!, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                if (byName.ContainsKey(name) || !IsManagedAssembly(dll))
                    continue;
                byName[name] = dll;
            }
        }

        // 2. .NET shared framework(s) loaded by this process (Microsoft.NETCore.App + Microsoft.WindowsDesktop.App).
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.StartsWith("SkuaSim.", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!byName.ContainsKey(name))
                    byName[name] = path;
            }
        }

        if (Hosting.SkuaInstallation.FindWindowsDesktopSharedDirectory() is { } desktopDir && Directory.Exists(desktopDir))
        {
            foreach (var dll in Directory.EnumerateFiles(desktopDir, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                if (byName.ContainsKey(name) || !IsManagedAssembly(dll))
                    continue;
                byName[name] = dll;
            }
        }

        // 3. SkuaSim.Core (virtual clock hooks used by rewritten scripts).
        byName["SkuaSim.Core"] = typeof(SkuaSim.Runtime.SimHooks).Assembly.Location;

        return new ReferenceCatalog(byName.Values.ToList());
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
