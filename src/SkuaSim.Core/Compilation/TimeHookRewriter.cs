using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SkuaSim.Core.Compilation;

/// <summary>
/// Rewrites real-time APIs to the simulator's virtual clock hooks (SkuaSim.Runtime.SimHooks / SimStopwatch).
/// Purely syntactic and line-preserving, so compiler diagnostics and stack traces keep the original line numbers.
/// </summary>
public sealed class TimeHookRewriter : CSharpSyntaxRewriter
{
    private const string Hooks = "global::SkuaSim.Runtime.SimHooks";
    private const string StopwatchReplacement = "global::SkuaSim.Runtime.SimStopwatch";

    private static readonly HashSet<string> ThreadNames = new(StringComparer.Ordinal)
        { "Thread", "System.Threading.Thread", "global::System.Threading.Thread" };
    private static readonly HashSet<string> TaskNames = new(StringComparer.Ordinal)
        { "Task", "System.Threading.Tasks.Task", "global::System.Threading.Tasks.Task" };
    private static readonly HashSet<string> RandomNames = new(StringComparer.Ordinal)
        { "Random", "System.Random", "global::System.Random" };
    private static readonly HashSet<string> DateTimeNames =new(StringComparer.Ordinal)
        { "DateTime", "System.DateTime", "global::System.DateTime" };
    private static readonly HashSet<string> DateTimeOffsetNames = new(StringComparer.Ordinal)
        { "DateTimeOffset", "System.DateTimeOffset", "global::System.DateTimeOffset" };
    private static readonly HashSet<string> EnvironmentNames = new(StringComparer.Ordinal)
        { "Environment", "System.Environment", "global::System.Environment" };
    private static readonly HashSet<string> StopwatchQualifiedNames = new(StringComparer.Ordinal)
        { "System.Diagnostics.Stopwatch", "global::System.Diagnostics.Stopwatch" };

    public int RewriteCount { get; private set; }

    public static SyntaxTree Rewrite(SyntaxTree tree, out int rewrites)
    {
        var rewriter = new TimeHookRewriter();
        var newRoot = rewriter.Visit(tree.GetRoot());
        rewrites = rewriter.RewriteCount;
        return rewrites == 0 ? tree : tree.WithRootAndOptions(newRoot, tree.Options);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (node.Expression is MemberAccessExpressionSyntax member)
        {
            var target = member.Expression.ToString();
            var name = member.Name.Identifier.ValueText;
            if ((name == "Sleep" && ThreadNames.Contains(target)) || (name is "Delay" or "Run" or "WaitAll" or "WaitAny" && TaskNames.Contains(target)))
            {
                RewriteCount++;
                var newArgs = (ArgumentListSyntax)Visit(node.ArgumentList)!;
                var replacement = SyntaxFactory.ParseExpression($"{Hooks}.{name}").WithTriviaFrom(member);
                return node.WithExpression(replacement).WithArgumentList(newArgs);
            }
        }
        return base.VisitInvocationExpression(node);
    }

    // "new Random()" → SimHooks.NewRandom() (seeded from the run seed). A seeded "new Random(n)" is left alone.
    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (RandomNames.Contains(node.Type.ToString()) && (node.ArgumentList == null || node.ArgumentList.Arguments.Count == 0) && node.Initializer == null)
        {
            RewriteCount++;
            return SyntaxFactory.ParseExpression($"{Hooks}.NewRandom()").WithTriviaFrom(node);
        }
        return base.VisitObjectCreationExpression(node);
    }

    // "Random rng = new();" (target-typed, as in CoreBots' quest watchdog) and "private Random r = new();".
    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        if (node.ArgumentList.Arguments.Count == 0 && node.Initializer == null &&
            node.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax decl } } &&
            RandomNames.Contains(decl.Type.ToString()))
        {
            RewriteCount++;
            return SyntaxFactory.ParseExpression($"{Hooks}.NewRandom()").WithTriviaFrom(node);
        }
        return base.VisitImplicitObjectCreationExpression(node);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var target = node.Expression.ToString();
        var name = node.Name.Identifier.ValueText;

        string? hook = null;
        if (DateTimeNames.Contains(target))
            hook = name switch { "Now" => "Now", "UtcNow" => "UtcNow", "Today" => "Today", _ => null };
        else if (DateTimeOffsetNames.Contains(target))
            hook = name switch { "Now" => "OffsetNow", "UtcNow" => "OffsetUtcNow", _ => null };
        else if (EnvironmentNames.Contains(target))
            hook = name switch { "TickCount" => "TickCount", "TickCount64" => "TickCount64", _ => null };

        if (hook != null)
        {
            RewriteCount++;
            return SyntaxFactory.ParseExpression($"{Hooks}.{hook}").WithTriviaFrom(node);
        }

        if (StopwatchQualifiedNames.Contains(target))
        {
            RewriteCount++;
            var replacementTarget = SyntaxFactory.ParseExpression(StopwatchReplacement).WithTriviaFrom(node.Expression);
            return node.WithExpression(replacementTarget);
        }

        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
    {
        if (StopwatchQualifiedNames.Contains(node.ToString()) && node.Parent is not UsingDirectiveSyntax)
        {
            RewriteCount++;
            return SyntaxFactory.ParseTypeName(StopwatchReplacement).WithTriviaFrom(node);
        }
        return base.VisitQualifiedName(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (node.Identifier.ValueText != "Stopwatch")
            return base.VisitIdentifierName(node);

        // Skip "x.Stopwatch" member names, qualified-name right parts and using directives.
        if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node)
            return node;
        if (node.Parent is QualifiedNameSyntax qn && qn.Right == node)
            return node;
        if (node.Ancestors().Any(a => a is UsingDirectiveSyntax))
            return node;

        RewriteCount++;
        return SyntaxFactory.ParseTypeName(StopwatchReplacement).WithTriviaFrom(node);
    }
}
