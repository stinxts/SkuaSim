using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace SkuaSim.Core.Hosting;

/// <summary>
/// Resolves Skua/VibeSkua assemblies from the installed client at runtime, plus in-memory compiled script assemblies.
/// Must be registered before any code touching Skua types is JIT-compiled.
/// This type must not reference any Skua type.
/// </summary>
public static class SkuaAssemblyResolver
{
    private static readonly object Gate = new();
    private static readonly List<string> ProbeDirectories = new();
    private static readonly ConcurrentDictionary<string, Assembly> InMemoryAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private static bool _registered;

    public static void Register(IEnumerable<string> probeDirectories)
    {
        lock (Gate)
        {
            foreach (var dir in probeDirectories)
            {
                if (!ProbeDirectories.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    ProbeDirectories.Add(dir);
            }

            if (_registered)
                return;

            AssemblyLoadContext.Default.Resolving += OnResolving;
            _registered = true;
        }
    }

    /// <summary>
    /// Registers an assembly loaded from memory (compiled script units) so other units can bind to it by name.
    /// </summary>
    public static void RegisterInMemory(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (!string.IsNullOrEmpty(name))
            InMemoryAssemblies[name] = assembly;
    }

    private static Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is null)
            return null;

        if (InMemoryAssemblies.TryGetValue(name.Name, out var inMemory))
            return inMemory;

        string[] dirs;
        lock (Gate)
            dirs = ProbeDirectories.ToArray();

        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, name.Name + ".dll");
            if (File.Exists(candidate))
                return context.LoadFromAssemblyPath(candidate);
        }

        return null;
    }
}
