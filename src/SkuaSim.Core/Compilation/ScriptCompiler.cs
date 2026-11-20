using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using SkuaSim.Core.Hosting;

namespace SkuaSim.Core.Compilation;

public sealed record CompileDiagnostic(string File, int Line, int Column, string Id, string Message)
{
    public override string ToString() => $"{Path.GetFileName(File)}({Line},{Column}): {Id}: {Message}";
}

public sealed record SynthesizedMonsterHint(string Map, string Cell, string MonsterName);
public sealed record SynthesizedShopHint(int ShopId, string ItemName);

/// <summary>
/// One compiled source file. Like Skua, each //cs_include file is compiled into its own assembly.
/// </summary>
public sealed class CompiledUnit
{
    public required string Path { get; init; }
    public required string AssemblyName { get; init; }
    public required byte[] Image { get; init; }
    public byte[]? Pdb { get; init; }
    public required MetadataReference Reference { get; init; }
    public required IReadOnlyList<CompiledUnit> Dependencies { get; init; }
    public int TimeHookRewrites { get; init; }
    public IReadOnlyList<string> ScriptMainClasses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SynthesizedMonsterHint> MonsterHints { get; init; } = Array.Empty<SynthesizedMonsterHint>();
    public IReadOnlyList<SynthesizedShopHint> ShopHints { get; init; } = Array.Empty<SynthesizedShopHint>();
}

public sealed class ScriptCompileResult
{
    public required string MainPath { get; init; }
    public bool Success => Errors.Count == 0 && Main != null;
    public CompiledUnit? Main { get; set; }
    public List<CompiledUnit> Units { get; } = new();
    public List<CompileDiagnostic> Errors { get; } = new();
    public List<string> Notes { get; } = new();
    public int WarningCount { get; set; }
    public TimeSpan Elapsed { get; set; }
    public IReadOnlyList<string> IncludedFiles => Units.Where(u => u != Main).Select(u => u.Path).ToList();
}

public sealed class CompilerSettings
{
    /// <summary>Rewrite Thread.Sleep/Task.Delay/DateTime.Now/Stopwatch to the virtual clock.</summary>
    public bool UseVirtualTime { get; init; } = true;
    public IReadOnlyList<string> IncludeRoots { get; init; } = Array.Empty<string>();
    public LanguageVersion LanguageVersion { get; init; } = LanguageVersion.Latest;
}

/// <summary>
/// Compiles Skua scripts with Roslyn against the real Skua assemblies, resolving //cs_include recursively.
/// Thread-safe; compiled include units are cached and shared between scripts (e.g. CoreBots.cs compiles once).
/// </summary>
public sealed class ScriptCompiler
{
    // Namespaces Westwind.Scripting and Skua.Core.Compiler prepend to every script.
    private static readonly string[] DefaultNamespaces =
    {
        "System", "System.Text", "System.Reflection", "System.IO", "System.Net", "System.Net.Http",
        "System.Collections", "System.Collections.Generic", "System.Collections.Concurrent",
        "System.Text.RegularExpressions", "System.Threading", "System.Threading.Tasks", "System.Linq",
        "Skua.Core.Interfaces", "Skua.Core.Models", "Skua.Core.Models.Items", "Skua.Core.Models.Monsters",
        "Skua.Core.Models.Quests", "Skua.Core.Models.Shops", "Skua.Core.Models.Players",
        "Skua.Core.Models.Servers", "Skua.Core.Models.Skills", "Skua.Core.Models.Auras",
        "Skua.Core.Options", "Skua.Core.Utils"
    };

    private readonly SkuaInstallation _installation;
    private readonly CompilerSettings _settings;
    private readonly IncludeResolver _resolver;
    private readonly ReferenceCatalog _references;
    private readonly CSharpParseOptions _parseOptions;
    private readonly CSharpCompilationOptions _compilationOptions;
    private readonly SyntaxTree _globalUsingsTree;
    private readonly ConcurrentDictionary<string, Lazy<UnitBuild>> _unitCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime Stamp, string Text)> _sourceCache = new(StringComparer.OrdinalIgnoreCase);

    public ScriptCompiler(SkuaInstallation installation, CompilerSettings? settings = null)
    {
        _installation = installation;
        _settings = settings ?? new CompilerSettings();
        _resolver = new IncludeResolver(installation.UserDataDirectory, installation.ScriptsDirectory, _settings.IncludeRoots);
        _references = ReferenceCatalog.GetShared(installation.AssembliesDirectory, installation.ApplicationDirectory);
        _parseOptions = new CSharpParseOptions(_settings.LanguageVersion, DocumentationMode.None);
        _compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,
            allowUnsafe: true,
            concurrentBuild: true,
            nullableContextOptions: NullableContextOptions.Disable);

        var usings = string.Join(Environment.NewLine, DefaultNamespaces.Select(n => $"global using {n};"));
        _globalUsingsTree = CSharpSyntaxTree.ParseText(SourceText.From(usings, Encoding.UTF8), _parseOptions, path: "SkuaSim.GlobalUsings.g.cs");
    }

    public ScriptCompileResult Compile(string mainScriptPath)
    {
        var sw = Stopwatch.StartNew();
        var mainPath = Path.GetFullPath(mainScriptPath);
        var result = new ScriptCompileResult { MainPath = mainPath };

        try
        {
            if (!File.Exists(mainPath))
            {
                result.Errors.Add(new CompileDiagnostic(mainPath, 0, 0, "SIM001", "Script file not found."));
                return result;
            }

            // 1. Discover the include graph.
            var nodes = new Dictionary<string, SourceNode>(StringComparer.OrdinalIgnoreCase);
            Discover(mainPath, nodes, result);
            if (result.Errors.Count > 0)
                return result;

            // 2. Topological order (dependencies first).
            var order = new List<SourceNode>();
            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Visit(nodes[mainPath], nodes, state, order, result);

            // 3. Compile includes, then the main script.
            var compiled = new Dictionary<string, CompiledUnit>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in order)
            {
                var isMain = string.Equals(node.Path, mainPath, StringComparison.OrdinalIgnoreCase);
                List<CompiledUnit> refs;
                if (isMain)
                {
                    refs = compiled.Values.ToList();
                }
                else
                {
                    refs = TransitiveIncludes(node, nodes).Where(compiled.ContainsKey).Select(p => compiled[p]).ToList();
                }

                var build = CompileUnitCached(node, refs);
                if (!build.Success && !isMain && compiled.Count > refs.Count)
                {
                    // Like Skua's shared reference set: retry with every include compiled so far.
                    var retry = CompileUnitCached(node, compiled.Values.ToList());
                    if (retry.Success)
                    {
                        result.Notes.Add($"{Path.GetFileName(node.Path)} needed types from includes it does not declare itself.");
                        build = retry;
                    }
                }

                result.WarningCount += build.WarningCount;
                if (!build.Success)
                {
                    result.Errors.AddRange(build.Errors);
                    return result;
                }

                compiled[node.Path] = build.Unit!;
                result.Units.Add(build.Unit!);
                if (isMain)
                    result.Main = build.Unit;
            }

            if (result.Main != null && result.Main.ScriptMainClasses.Count == 0)
                result.Notes.Add("No class with a ScriptMain method was found in the main script.");

            return result;
        }
        catch (Exception ex)
        {
            result.Errors.Add(new CompileDiagnostic(mainPath, 0, 0, "SIM999", "Internal compiler error: " + ex.Message));
            return result;
        }
        finally
        {
            result.Elapsed = sw.Elapsed;
        }
    }

    private sealed class SourceNode
    {
        public required string Path { get; init; }
        public required string Source { get; init; }
        public List<string> Includes { get; } = new();
        public List<string> CsRefs { get; } = new();
    }

    private sealed class UnitBuild
    {
        public CompiledUnit? Unit { get; init; }
        public List<CompileDiagnostic> Errors { get; } = new();
        public int WarningCount { get; init; }
        public bool Success => Unit != null && Errors.Count == 0;
    }

    private void Discover(string path, Dictionary<string, SourceNode> nodes, ScriptCompileResult result)
    {
        if (nodes.ContainsKey(path))
            return;

        var node = new SourceNode { Path = path, Source = ReadSource(path) };
        nodes[path] = node;

        foreach (var directive in ScriptDirectiveParser.Parse(node.Source))
        {
            if (directive.Kind == "ref")
            {
                node.CsRefs.Add(directive.Value);
                continue;
            }

            var resolved = _resolver.Resolve(path, directive.Value);
            if (resolved == null)
            {
                result.Errors.Add(new CompileDiagnostic(path, directive.Line, 1, "SIM002", $"Included file not found: {directive.Value}"));
                continue;
            }

            if (!node.Includes.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                node.Includes.Add(resolved);
            Discover(resolved, nodes, result);
        }
    }

    private static void Visit(SourceNode node, Dictionary<string, SourceNode> nodes, Dictionary<string, int> state, List<SourceNode> order, ScriptCompileResult result)
    {
        state[node.Path] = 1;
        foreach (var inc in node.Includes)
        {
            state.TryGetValue(inc, out var s);
            if (s == 1)
            {
                result.Notes.Add($"Circular include ignored: {Path.GetFileName(node.Path)} -> {Path.GetFileName(inc)}");
                continue;
            }
            if (s == 0)
                Visit(nodes[inc], nodes, state, order, result);
        }
        state[node.Path] = 2;
        order.Add(node);
    }

    private static IEnumerable<string> TransitiveIncludes(SourceNode node, Dictionary<string, SourceNode> nodes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(node.Includes);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!seen.Add(p))
                continue;
            foreach (var child in nodes[p].Includes)
                stack.Push(child);
        }
        seen.Remove(node.Path);
        return seen;
    }

    private string ReadSource(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_sourceCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Text;
        var text = File.ReadAllText(path);
        _sourceCache[path] = (stamp, text);
        return text;
    }

    private UnitBuild CompileUnitCached(SourceNode node, List<CompiledUnit> refs)
    {
        var refKey = string.Join(",", refs.Select(r => r.AssemblyName).OrderBy(n => n, StringComparer.Ordinal));
        var key = $"{node.Path}|{Hash(node.Source)}|{refKey}|{_settings.UseVirtualTime}";
        var lazy = _unitCache.GetOrAdd(key, _ => new Lazy<UnitBuild>(() => CompileUnit(node, refs), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private UnitBuild CompileUnit(SourceNode node, List<CompiledUnit> refs)
    {
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(node.Source, Encoding.UTF8), _parseOptions, path: node.Path);
        var rewrites = 0;
        if (_settings.UseVirtualTime)
            tree = TimeHookRewriter.Rewrite(tree, out rewrites);

        var metadata = new List<MetadataReference>(_references.References);
        metadata.AddRange(refs.Select(r => r.Reference));
        foreach (var csRef in node.CsRefs)
        {
            var refPath = ResolveCsRef(node.Path, csRef);
            if (refPath != null)
                metadata.Add(MetadataReference.CreateFromFile(refPath));
        }

        var asmName = $"SkuaSimScript_{Sanitize(System.IO.Path.GetFileNameWithoutExtension(node.Path))}_{Hash(node.Source + string.Join(",", refs.Select(r => r.AssemblyName)))[..8]}";
        var compilation = CSharpCompilation.Create(asmName, new[] { _globalUsingsTree, tree }, metadata, _compilationOptions);

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var emit = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

        var errors = emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(ToDiagnostic)
            .ToList();
        var warnings = emit.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        if (!emit.Success || errors.Count > 0)
        {
            var failed = new UnitBuild { WarningCount = warnings };
            failed.Errors.AddRange(errors.Count > 0 ? errors : new List<CompileDiagnostic> { new(node.Path, 0, 0, "SIM003", "Emit failed.") });
            return failed;
        }

        var image = pe.ToArray();
        var scriptMainClasses = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == "ScriptMain" && m.ParameterList.Parameters.Count == 1)
            .Select(m => m.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText)
            .Where(n => n != null)
            .Select(n => n!)
            .Distinct()
            .ToList();

        var (monsterHints, shopHints) = ExtractAstHints(tree.GetRoot());

        return new UnitBuild
        {
            WarningCount = warnings,
            Unit = new CompiledUnit
            {
                Path = node.Path,
                AssemblyName = asmName,
                Image = image,
                Pdb = pdb.ToArray(),
                Reference = MetadataReference.CreateFromImage(image),
                Dependencies = refs,
                TimeHookRewrites = rewrites,
                ScriptMainClasses = scriptMainClasses,
                MonsterHints = monsterHints,
                ShopHints = shopHints
            }
        };
    }

    private static (List<SynthesizedMonsterHint> Monsters, List<SynthesizedShopHint> Shops) ExtractAstHints(SyntaxNode root)
    {
        var monsters = new List<SynthesizedMonsterHint>();
        var shops = new List<SynthesizedShopHint>();
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                IdentifierNameSyntax id => id.Identifier.ValueText,
                _ => null
            };
            if (methodName is not ("KillMonster" or "HuntMonster" or "KillQuest" or "BuyItem"))
                continue;

            var args = inv.ArgumentList.Arguments;
            if (methodName == "KillQuest" && args.Count >= 3)
            {
                // CoreStory.KillQuest(int QuestID, string MapName, string MonsterName | string[] MonsterNames, ...)
                var mapExpr = GetArgument(args, 1, "MapName");
                var monExpr = GetArgument(args, 2, "MonsterName") ?? GetArgument(args, 2, "MonsterNames");
                var map = mapExpr == null ? null : GetStringLiteral(mapExpr);
                var names = monExpr == null ? new List<string>() : GetStringLiterals(monExpr);
                if (!string.IsNullOrWhiteSpace(map) && names.Count > 0)
                    monsters.Add(new SynthesizedMonsterHint(map.Trim(), "r1", string.Join("|", names.Select(n => n.Trim()))));
            }
            else if (methodName == "KillMonster" && args.Count >= 4)
            {
                var map = GetStringLiteral(args[0].Expression);
                var cell = GetStringLiteral(args[1].Expression);
                var mon = GetStringLiteral(args[3].Expression);
                if (!string.IsNullOrWhiteSpace(map) && !string.IsNullOrWhiteSpace(mon))
                    monsters.Add(new SynthesizedMonsterHint(map.Trim(), string.IsNullOrWhiteSpace(cell) ? "Enter" : cell.Trim(), mon.Trim()));
            }
            else if (methodName == "HuntMonster" && args.Count >= 2)
            {
                var map = GetStringLiteral(args[0].Expression);
                var mon = GetStringLiteral(args[1].Expression);
                if (!string.IsNullOrWhiteSpace(map) && !string.IsNullOrWhiteSpace(mon))
                    monsters.Add(new SynthesizedMonsterHint(map.Trim(), "r1", mon.Trim()));
            }
            else if (methodName == "BuyItem" && args.Count >= 3)
            {
                var shopId = GetIntLiteral(args[1].Expression);
                var itemName = GetStringLiteral(args[2].Expression);
                if (shopId is > 0 && !string.IsNullOrWhiteSpace(itemName))
                    shops.Add(new SynthesizedShopHint(shopId.Value, itemName.Trim()));
            }
        }
        return (monsters, shops);
    }

    private static string? GetStringLiteral(ExpressionSyntax expr) =>
        expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
            ? lit.Token.ValueText
            : null;

    /// <summary>Returns the argument named <paramref name="name"/>, or the positional one at <paramref name="index"/>.</summary>
    private static ExpressionSyntax? GetArgument(SeparatedSyntaxList<ArgumentSyntax> args, int index, string name)
    {
        var named = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name);
        if (named != null)
            return named.Expression;
        return index < args.Count && args[index].NameColon == null ? args[index].Expression : null;
    }

    /// <summary>
    /// String literals from a single literal, <c>new[] { "a", "b" }</c>, <c>new string[] { ... }</c>
    /// or a collection expression <c>["a", "b"]</c>. Non-literal elements are skipped.
    /// </summary>
    private static List<string> GetStringLiterals(ExpressionSyntax expr)
    {
        IEnumerable<ExpressionSyntax> elements = expr switch
        {
            ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer.Expressions,
            ArrayCreationExpressionSyntax array when array.Initializer != null => array.Initializer.Expressions,
            CollectionExpressionSyntax collection => collection.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression),
            _ => new[] { expr }
        };
        return elements
            .Select(GetStringLiteral)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
    }

    private static int? GetIntLiteral(ExpressionSyntax expr) =>
        expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NumericLiteralExpression) && lit.Token.Value is int v
            ? v
            : null;

    private string? ResolveCsRef(string owner, string spec)
    {
        var candidates = new[]
        {
            spec,
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(owner) ?? "", spec),
            System.IO.Path.Combine(_installation.UserDataDirectory, spec),
            System.IO.Path.Combine(_installation.AssembliesDirectory, spec),
            System.IO.Path.Combine(_installation.AssembliesDirectory, spec + ".dll")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static CompileDiagnostic ToDiagnostic(Diagnostic d)
    {
        var span = d.Location.GetMappedLineSpan();
        var file = string.IsNullOrEmpty(span.Path) ? "<unknown>" : span.Path;
        return new CompileDiagnostic(file, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, d.Id, d.GetMessage());
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
