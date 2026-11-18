namespace SkuaSim.Core.Compilation;

/// <summary>
/// Parses Skua script directives (//cs_include and //cs_ref) exactly like Skua's ScriptManager:
/// lines are scanned from the top and scanning stops at the first line that starts with "using".
/// </summary>
public static class ScriptDirectiveParser
{
    public sealed record Directive(string Kind, string Value, int Line);

    public static IReadOnlyList<Directive> Parse(string source)
    {
        var result = new List<Directive>();
        using var reader = new StringReader(source);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.StartsWith("using", StringComparison.Ordinal))
                break;
            if (!trimmed.StartsWith("//cs_", StringComparison.Ordinal))
                continue;

            var body = trimmed[5..];
            var space = body.IndexOfAny(new[] { ' ', '\t' });
            if (space <= 0)
                continue;

            var kind = body[..space].Trim();
            var value = body[(space + 1)..].Trim();
            if ((kind == "include" || kind == "ref") && value.Length > 0)
                result.Add(new Directive(kind, value, lineNumber));
        }
        return result;
    }
}

/// <summary>
/// Resolves //cs_include paths. Order: explicit include roots, ancestors of the including file
/// (so a script inside a Git checkout prefers that checkout's Core files), then the Skua user data folder.
/// </summary>
public sealed class IncludeResolver
{
    private readonly IReadOnlyList<string> _includeRoots;
    private readonly string _userDataDirectory;
    private readonly string _scriptsDirectory;

    public IncludeResolver(string userDataDirectory, string scriptsDirectory, IEnumerable<string>? includeRoots = null)
    {
        _userDataDirectory = userDataDirectory;
        _scriptsDirectory = scriptsDirectory;
        _includeRoots = (includeRoots ?? Array.Empty<string>()).Where(Directory.Exists).Select(Path.GetFullPath).ToList();
    }

    public string? Resolve(string ownerFile, string specifier)
    {
        var spec = specifier.Trim().Trim('"').Replace('\\', '/');
        if (Path.IsPathRooted(spec))
            return File.Exists(spec) ? Path.GetFullPath(spec) : null;

        var withoutScriptsPrefix = spec.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase) ? spec[8..] : spec;

        foreach (var root in _includeRoots)
        {
            if (TryCombine(root, spec, out var hit) || TryCombine(root, withoutScriptsPrefix, out hit))
                return hit;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(ownerFile));
        while (!string.IsNullOrEmpty(dir))
        {
            if (TryCombine(dir, spec, out var hit))
                return hit;
            dir = Path.GetDirectoryName(dir);
        }

        if (TryCombine(_userDataDirectory, spec, out var userHit))
            return userHit;
        if (TryCombine(_scriptsDirectory, withoutScriptsPrefix, out var scriptsHit))
            return scriptsHit;

        return null;
    }

    private static bool TryCombine(string root, string relative, out string path)
    {
        path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(path);
    }
}
