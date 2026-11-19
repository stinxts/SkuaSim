using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Text;
using SkuaSim.Core.Runtime;

namespace SkuaSim.Core.Proxies;

/// <summary>
/// Optional interface for API handlers that supply default values for properties they do not implement explicitly.
/// </summary>
public interface IPropertyDefaults
{
    bool TryGetDefault(string propertyName, out object? value);
}

public sealed class ProxyState
{
    public required Type Interface { get; init; }
    public required string Label { get; init; }
    public object? Handler { get; init; }
    public ConcurrentDictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, Delegate?> Events { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, object?> Children { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// DispatchProxy base used for every simulated Skua interface. Must be public and non-sealed.
/// </summary>
public class SimProxy : DispatchProxy
{
    internal ProxyState State = null!;
    internal ProxyHub Hub = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        Hub.Dispatch(State, targetMethod!, args ?? Array.Empty<object?>());
}

/// <summary>
/// Creates proxies for Skua interfaces and dispatches calls to handler objects. Members without a handler fall back to
/// (1) the interface's default implementation, (2) a property store, or (3) a safe default value, and are recorded as
/// "unimplemented" so the report shows exactly which parts of the API a script touched that are not simulated yet.
/// </summary>
public sealed class ProxyHub
{
    private const int TimelineFullDetailCalls = 200;
    private const int TimelineSampleEvery = 1000;

    private readonly ConcurrentDictionary<(Type HandlerType, MethodInfo Target), Resolution> _cache = new();

    public ProxyHub(SimContext context) => Context = context;

    public SimContext Context { get; }

    public T Create<T>(object? handler, string label) where T : class => (T)Create(typeof(T), handler, label);

    public object Create(Type interfaceType, object? handler, string label)
    {
        var proxy = (SimProxy)DispatchProxy.Create(interfaceType, typeof(SimProxy));
        proxy.State = new ProxyState { Interface = interfaceType, Handler = handler, Label = label };
        proxy.Hub = this;
        _proxyByState[proxy.State] = proxy;
        if (handler is IProxyAware aware)
            aware.Attach(proxy, proxy.State);
        return proxy;
    }

    public static ProxyState? GetState(object? proxy) => (proxy as SimProxy)?.State;

    internal object? Dispatch(ProxyState state, MethodInfo method, object?[] args)
    {
        var trace = Context.Trace;
        Context.Clock.NoteApiCall();
        Context.NoteApiActivity();

        var resolution = _cache.GetOrAdd((state.Handler?.GetType() ?? typeof(void), method), key => Resolve(key.HandlerType, method));
        var member = $"{state.Label}.{resolution.MemberName}";
        Context.NotePostStopApiCall(member);
        var count = trace.CountCall(member);

        object? result;
        switch (resolution.Kind)
        {
            case ResolutionKind.HandlerGet:
                return resolution.Property!.GetValue(state.Handler);

            case ResolutionKind.StoreGet:
            {
                if (state.Values.TryGetValue(resolution.MemberName, out var stored))
                    return stored;
                if (state.Handler is IPropertyDefaults defaults && defaults.TryGetDefault(resolution.MemberName, out var def))
                    return def;
                trace.NoteUnimplemented("get " + member);
                return DefaultValues.For(method.ReturnType, this, state, resolution.MemberName);
            }

            case ResolutionKind.HandlerSet:
                resolution.Property!.SetValue(state.Handler, args[0]);
                LogCall(member, count, $"{ArgFormatter.Format(args[0])}", null, isSetter: true);
                return null;

            case ResolutionKind.StoreSet:
                state.Values[resolution.MemberName] = args[0];
                LogCall(member, count, $"{ArgFormatter.Format(args[0])}", null, isSetter: true);
                return null;

            case ResolutionKind.EventAdd:
                state.Events.AddOrUpdate(resolution.MemberName, (Delegate?)args[0], (_, d) => Delegate.Combine(d, (Delegate?)args[0]));
                return null;

            case ResolutionKind.EventRemove:
                state.Events.AddOrUpdate(resolution.MemberName, _ => null, (_, d) => Delegate.Remove(d, (Delegate?)args[0]));
                return null;

            case ResolutionKind.HandlerMethod:
            {
                var target = resolution.Method!;
                if (target.IsGenericMethodDefinition)
                    target = target.MakeGenericMethod(method.GetGenericArguments());
                try
                {
                    result = target.Invoke(state.Handler, args);
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                    throw;
                }
                LogCall(member, count, ArgFormatter.FormatArgs(args), method.ReturnType == typeof(void) ? null : ArgFormatter.Format(result), false);
                return result;
            }

            case ResolutionKind.DefaultBody:
                result = resolution.DefaultBody!(GetProxyFor(state), args);
                LogCall(member, count, ArgFormatter.FormatArgs(args), method.ReturnType == typeof(void) ? null : ArgFormatter.Format(result), false);
                return result;

            default:
                trace.NoteUnimplemented(member + ArgFormatter.Signature(method));
                result = DefaultValues.For(method.ReturnType, this, state, resolution.MemberName);
                LogCall(member, count, ArgFormatter.FormatArgs(args), method.ReturnType == typeof(void) ? "(not simulated)" : ArgFormatter.Format(result) + " (not simulated)", false);
                return result;
        }
    }

    private readonly ConcurrentDictionary<ProxyState, object> _proxyByState = new();

    internal void Register(object proxy)
    {
        if (GetState(proxy) is { } st)
            _proxyByState[st] = proxy;
    }

    private object GetProxyFor(ProxyState state) =>
        _proxyByState.TryGetValue(state, out var p) ? p : throw new InvalidOperationException($"Proxy for {state.Label} not registered.");

    private void LogCall(string member, long count, string args, string? result, bool isSetter)
    {
        if (count <= TimelineFullDetailCalls || count % TimelineSampleEvery == 0)
        {
            var suffix = count > TimelineFullDetailCalls ? $" [call #{count}]" : "";
            if (isSetter)
                Context.Trace.Api(member + " =", args + suffix, null);
            else
                Context.Trace.Api(member, args, result == null ? (suffix.Length > 0 ? suffix.Trim() : null) : result + suffix);
        }
    }

    private enum ResolutionKind { HandlerGet, HandlerSet, StoreGet, StoreSet, EventAdd, EventRemove, HandlerMethod, DefaultBody, Unimplemented }

    private sealed class Resolution
    {
        public required ResolutionKind Kind { get; init; }
        public required string MemberName { get; init; }
        public PropertyInfo? Property { get; init; }
        public MethodInfo? Method { get; init; }
        public Func<object, object?[], object?>? DefaultBody { get; init; }
    }

    private static Resolution Resolve(Type handlerType, MethodInfo method)
    {
        var name = method.Name;
        var hasHandler = handlerType != typeof(void);
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;

        if (method.IsSpecialName)
        {
            if (name.StartsWith("get_", StringComparison.Ordinal) && method.GetParameters().Length == 0)
            {
                var prop = name[4..];
                var hp = hasHandler ? handlerType.GetProperty(prop, Flags) : null;
                if (hp is { CanRead: true } && method.ReturnType.IsAssignableFrom(hp.PropertyType))
                    return new Resolution { Kind = ResolutionKind.HandlerGet, MemberName = prop, Property = hp };
                return new Resolution { Kind = ResolutionKind.StoreGet, MemberName = prop };
            }
            if (name.StartsWith("set_", StringComparison.Ordinal) && method.GetParameters().Length == 1)
            {
                var prop = name[4..];
                var hp = hasHandler ? handlerType.GetProperty(prop, Flags) : null;
                if (hp is { CanWrite: true } && hp.SetMethod!.IsPublic)
                    return new Resolution { Kind = ResolutionKind.HandlerSet, MemberName = prop, Property = hp };
                return new Resolution { Kind = ResolutionKind.StoreSet, MemberName = prop };
            }
            if (name.StartsWith("add_", StringComparison.Ordinal))
                return new Resolution { Kind = ResolutionKind.EventAdd, MemberName = name[4..] };
            if (name.StartsWith("remove_", StringComparison.Ordinal))
                return new Resolution { Kind = ResolutionKind.EventRemove, MemberName = name[7..] };
        }

        if (hasHandler)
        {
            var handlerMethod = FindHandlerMethod(handlerType, method);
            if (handlerMethod != null)
                return new Resolution { Kind = ResolutionKind.HandlerMethod, MemberName = name, Method = handlerMethod };
        }

        if (!method.IsAbstract && !method.IsStatic)
            return new Resolution { Kind = ResolutionKind.DefaultBody, MemberName = name, DefaultBody = NonVirtualInvoker.Create(method) };

        return new Resolution { Kind = ResolutionKind.Unimplemented, MemberName = name };
    }

    private static MethodInfo? FindHandlerMethod(Type handlerType, MethodInfo target)
    {
        var targetParams = target.GetParameters();
        var candidates = handlerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == target.Name && m.GetParameters().Length == targetParams.Length && m.DeclaringType != typeof(object))
            .ToList();

        MethodInfo? best = null;
        var bestScore = -1;
        foreach (var candidate in candidates)
        {
            if (target.IsGenericMethod != candidate.IsGenericMethodDefinition)
                continue;
            if (target.IsGenericMethod && candidate.GetGenericArguments().Length != target.GetGenericArguments().Length)
                continue;

            var cParams = candidate.GetParameters();
            var score = 0;
            var ok = true;
            for (var i = 0; i < targetParams.Length; i++)
            {
                var tp = targetParams[i].ParameterType;
                var cp = cParams[i].ParameterType;
                if (tp.IsByRef != cp.IsByRef) { ok = false; break; }
                if (tp.IsByRef) { tp = tp.GetElementType()!; cp = cp.GetElementType()!; }

                if (tp == cp || SameGenericShape(tp, cp))
                    score += 2;
                else if (!tp.ContainsGenericParameters && !cp.ContainsGenericParameters && cp.IsAssignableFrom(tp))
                    score += 1;
                else { ok = false; break; }
            }
            if (ok && score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        return best;
    }

    private static bool SameGenericShape(Type a, Type b)
    {
        if (a.IsGenericParameter && b.IsGenericParameter)
            return a.GenericParameterPosition == b.GenericParameterPosition;
        if (a.IsGenericType && b.IsGenericType && a.GetGenericTypeDefinition() == b.GetGenericTypeDefinition())
            return a.GetGenericArguments().Zip(b.GetGenericArguments()).All(p => p.First == p.Second || SameGenericShape(p.First, p.Second));
        if (a.IsArray && b.IsArray)
            return SameGenericShape(a.GetElementType()!, b.GetElementType()!) || a.GetElementType() == b.GetElementType();
        return false;
    }
}

/// <summary>
/// Handlers implementing this receive their own proxy (e.g. to read events registered on it).
/// </summary>
public interface IProxyAware
{
    void Attach(object proxy, ProxyState state);
}

/// <summary>
/// Calls an interface's default method implementation non-virtually (a virtual call would loop back into the proxy).
/// </summary>
internal static class NonVirtualInvoker
{
    public static Func<object, object?[], object?> Create(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var dm = new DynamicMethod("SkuaSimDefault_" + method.Name, typeof(object), new[] { typeof(object), typeof(object[]) },
            typeof(NonVirtualInvoker).Module, skipVisibility: true);
        var il = dm.GetILGenerator();
        var locals = new LocalBuilder?[parameters.Length];

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, method.DeclaringType!);
        for (var i = 0; i < parameters.Length; i++)
        {
            var pt = parameters[i].ParameterType;
            if (pt.IsByRef)
            {
                var et = pt.GetElementType()!;
                locals[i] = il.DeclareLocal(et);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Unbox_Any, et);
                il.Emit(OpCodes.Stloc, locals[i]!);
                il.Emit(OpCodes.Ldloca, locals[i]!);
            }
            else
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Unbox_Any, pt);
            }
        }
        il.Emit(OpCodes.Call, method);

        var resultLocal = method.ReturnType == typeof(void) ? null : il.DeclareLocal(method.ReturnType);
        if (resultLocal != null)
            il.Emit(OpCodes.Stloc, resultLocal);

        for (var i = 0; i < parameters.Length; i++)
        {
            if (locals[i] == null)
                continue;
            var et = parameters[i].ParameterType.GetElementType()!;
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldloc, locals[i]!);
            if (et.IsValueType)
                il.Emit(OpCodes.Box, et);
            il.Emit(OpCodes.Stelem_Ref);
        }

        if (resultLocal == null)
        {
            il.Emit(OpCodes.Ldnull);
        }
        else
        {
            il.Emit(OpCodes.Ldloc, resultLocal);
            if (method.ReturnType.IsValueType)
                il.Emit(OpCodes.Box, method.ReturnType);
        }
        il.Emit(OpCodes.Ret);

        var invoker = (Func<object, object?[], object?>)dm.CreateDelegate(typeof(Func<object, object?[], object?>));
        return (target, args) =>
        {
            // Value-type out/ref parameters may arrive as null; give them a default so unboxing succeeds.
            for (var i = 0; i < parameters.Length; i++)
            {
                var pt = parameters[i].ParameterType;
                var et = pt.IsByRef ? pt.GetElementType()! : pt;
                if (args[i] == null && et.IsValueType && Nullable.GetUnderlyingType(et) == null)
                    args[i] = Activator.CreateInstance(et);
            }
            return invoker(target, args);
        };
    }
}

/// <summary>
/// Safe default values for members the simulator does not implement.
/// </summary>
public static class DefaultValues
{
    public static object? For(Type type, ProxyHub? hub = null, ProxyState? owner = null, string? memberName = null)
    {
        if (type == typeof(void))
            return null;
        if (type == typeof(string))
            return string.Empty;
        if (Nullable.GetUnderlyingType(type) != null)
            return null;
        if (type == typeof(Task))
            return Task.CompletedTask;
        if (type == typeof(ValueTask))
            return default(ValueTask);
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (def == typeof(Task<>))
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(args[0]).Invoke(null, new[] { For(args[0], hub, owner, memberName) });
            if (def == typeof(ValueTask<>))
                return Activator.CreateInstance(type, For(args[0], hub, owner, memberName));
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(ICollection<>) || def == typeof(IEnumerable<>)
                || def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
                return Activator.CreateInstance(typeof(List<>).MakeGenericType(args[0]));
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
                return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args));
            if (def == typeof(HashSet<>) || def == typeof(ISet<>))
                return Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(args[0]));
            if (def == typeof(ImmutableDictionary<,>))
                return type.GetField("Empty", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (def == typeof(ImmutableList<>))
                return type.GetField("Empty", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }
        if (type.IsArray)
            return Array.CreateInstance(type.GetElementType()!, 0);
        if (type.IsValueType)
            return Activator.CreateInstance(type);
        if (type.IsInterface && hub != null && owner != null && memberName != null && type.Namespace?.StartsWith("Skua", StringComparison.Ordinal) == true)
        {
            return owner.Children.GetOrAdd(memberName + "|" + type.FullName, _ =>
            {
                var child = hub.Create(type, null, $"{owner.Label}.{memberName}");
                hub.Register(child);
                return child;
            });
        }
        if (type == typeof(IEnumerable) || type == typeof(IList))
            return new List<object>();
        return null;
    }
}

/// <summary>
/// Compact, human-readable formatting of API arguments and results for the trace.
/// </summary>
public static class ArgFormatter
{
    public static string FormatArgs(object?[] args) => string.Join(", ", args.Select(a => Format(a)));

    public static string Signature(MethodInfo m) =>
        "(" + string.Join(", ", m.GetParameters().Select(p => TypeName(p.ParameterType))) + ")";

    public static string TypeName(Type t)
    {
        if (t.IsByRef)
            return "out " + TypeName(t.GetElementType()!);
        if (t.IsGenericType)
            return t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
        return t.Name switch
        {
            "String" => "string", "Int32" => "int", "Boolean" => "bool", "Object" => "object", "Int64" => "long", "Double" => "double", "Single" => "float",
            _ => t.Name
        };
    }

    public static string Format(object? value) => Format(value, 0);

    private static string Format(object? value, int depth)
    {
        switch (value)
        {
            case null:
                return "null";
            case string s:
                return "\"" + (s.Length > 80 ? s[..77] + "..." : s) + "\"";
            case bool b:
                return b ? "true" : "false";
            case IFormattable f when value.GetType().IsPrimitive || value is decimal:
                return f.ToString(null, CultureInfo.InvariantCulture);
            case Enum e:
                return e.ToString();
            case Delegate:
                return "λ";
            case Task t:
                return t.IsCompleted ? "Task(done)" : "Task";
            case SimProxy p:
                return "<" + p.State.Label + ">";
        }

        var type = value.GetType();
        if (value is IEnumerable enumerable && type != typeof(string))
        {
            if (depth > 1)
                return "[...]";
            var items = new List<string>();
            var total = 0;
            foreach (var item in enumerable)
            {
                if (items.Count < 6)
                    items.Add(Format(item, depth + 1));
                total++;
            }
            var sb = new StringBuilder("[").Append(string.Join(", ", items));
            if (total > items.Count)
                sb.Append($", ... ({total} total)");
            return sb.Append(']').ToString();
        }

        var nameProp = type.GetProperty("Name");
        var idProp = type.GetProperty("ID");
        if (nameProp != null && nameProp.PropertyType == typeof(string))
        {
            var name = nameProp.GetValue(value) as string;
            var id = idProp?.GetValue(value);
            var qtyProp = type.GetProperty("Quantity");
            var qty = qtyProp?.GetValue(value);
            return id != null
                ? $"{type.Name}(\"{name}\" #{id}{(qty is int q && q != 0 ? $" x{q}" : "")})"
                : $"{type.Name}(\"{name}\")";
        }

        if (type.IsGenericType && type.FullName?.StartsWith("System.ValueTuple", StringComparison.Ordinal) == true)
            return value.ToString() ?? type.Name;

        return type.Name;
    }
}
