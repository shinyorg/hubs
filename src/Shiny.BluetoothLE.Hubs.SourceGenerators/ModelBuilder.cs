using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Shiny.BluetoothLE.Hubs.SourceGenerators;

static class ModelBuilder
{
    public const string AttributeName = "Shiny.BluetoothLE.Hubs.BleHubClientAttribute";
    public const string HubBaseName = "Shiny.BluetoothLE.Hubs.BleHub`1";
    public const string ClientBaseName = "Shiny.BluetoothLE.Hubs.BleHubClient";

    static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);


    public static ContractModel BuildContract(INamedTypeSymbol iface, bool isLocal)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var methods = new List<MethodModel>();
        var events = new List<EventModel>();
        var location = iface.Locations.FirstOrDefault();

        var members = new[] { iface }.Concat(iface.AllInterfaces).SelectMany(x => x.GetMembers()).ToList();

        foreach (var method in members.OfType<IMethodSymbol>().Where(x => x.MethodKind == MethodKind.Ordinary && !x.IsStatic))
        {
            var model = BuildMethod(method, iface, diagnostics);
            if (model != null)
                methods.Add(model);
        }

        foreach (var group in methods.GroupBy(x => x.Name).Where(x => x.Count() > 1))
        {
            diagnostics.Add(DiagnosticInfo.Create(
                Diagnostics.OverloadedMethod,
                location,
                $"'{iface.Name}.{group.Key}' is overloaded - hub methods are called by name so each name must be unique"
            ));
        }

        foreach (var ev in members.OfType<IEventSymbol>().Where(x => !x.IsStatic))
        {
            var model = BuildEvent(ev, iface, diagnostics);
            if (model != null)
                events.Add(model);
        }

        foreach (var group in events.GroupBy(x => x.Name).Where(x => x.Count() > 1))
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnsupportedEvent, location, $"'{iface.Name}.{group.Key}' is declared more than once"));

        var baseName = iface.Name.Length > 1 && iface.Name[0] == 'I' && char.IsUpper(iface.Name[1])
            ? iface.Name.Substring(1)
            : iface.Name;

        var attr = iface.GetAttributes().FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == AttributeName);
        var proxyName = attr?.NamedArguments.FirstOrDefault(x => x.Key == "ProxyName").Value.Value as string ?? baseName + "Client";

        return new ContractModel(
            iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            iface.ContainingNamespace.IsGlobalNamespace ? null : iface.ContainingNamespace.ToDisplayString(),
            iface.Name,
            proxyName,
            baseName + "PushExtensions",
            IsEffectivelyPublic(iface),
            isLocal,
            methods.ToArray(),
            events.ToArray(),
            diagnostics.ToArray()
        );
    }


    static MethodModel? BuildMethod(IMethodSymbol method, INamedTypeSymbol iface, List<DiagnosticInfo> diagnostics)
    {
        var location = method.Locations.FirstOrDefault() ?? iface.Locations.FirstOrDefault();
        var signature = $"{iface.Name}.{method.Name}";

        if (method.IsGenericMethod)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnsupportedParameter, location, $"'{signature}' is generic - hub methods must have concrete types"));
            return null;
        }

        var returnType = (INamedTypeSymbol)method.ReturnType;
        ReturnKind kind;
        string? resultType = null;
        var original = returnType.OriginalDefinition.ToDisplayString();

        if (original == "System.Threading.Tasks.Task")
        {
            kind = ReturnKind.Task;
        }
        else if (original == "System.Threading.Tasks.Task<TResult>")
        {
            kind = ReturnKind.TaskOfT;
            resultType = returnType.TypeArguments[0].ToDisplayString(TypeFormat);
        }
        else if (original == "System.Collections.Generic.IAsyncEnumerable<T>")
        {
            kind = ReturnKind.AsyncEnumerable;
            resultType = returnType.TypeArguments[0].ToDisplayString(TypeFormat);
        }
        else
        {
            diagnostics.Add(DiagnosticInfo.Create(
                Diagnostics.UnsupportedReturnType,
                location,
                $"'{signature}' returns {method.ReturnType.ToDisplayString()} - hub methods must return Task, Task<T> or IAsyncEnumerable<T>"
            ));
            return null;
        }

        var parameters = new List<ParameterModel>();
        foreach (var p in method.Parameters)
        {
            if (p.RefKind != RefKind.None)
            {
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnsupportedParameter, location, $"'{signature}' parameter '{p.Name}' is ref/out/in - hub parameters are passed by value"));
                return null;
            }
            parameters.Add(new ParameterModel(
                EscapeIdentifier(p.Name),
                p.Type.ToDisplayString(TypeFormat),
                IsCancellationToken(p.Type)
            ));
        }

        if (parameters.Count(x => !x.IsCancellationToken) > 255)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnsupportedParameter, location, $"'{signature}' has more than 255 parameters"));
            return null;
        }

        if (parameters.Count(x => x.IsCancellationToken) > 1)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnsupportedParameter, location, $"'{signature}' has more than one CancellationToken parameter"));
            return null;
        }

        return new MethodModel(method.Name, kind, method.ReturnType.ToDisplayString(TypeFormat), resultType, parameters.ToArray());
    }


    static EventModel? BuildEvent(IEventSymbol ev, INamedTypeSymbol iface, List<DiagnosticInfo> diagnostics)
    {
        var location = ev.Locations.FirstOrDefault() ?? iface.Locations.FirstOrDefault();
        if (ev.Type is not INamedTypeSymbol type || type.ContainingNamespace?.ToDisplayString() != "System" || type.Name != "Action" || type.TypeArguments.Length > 4)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                Diagnostics.UnsupportedEvent,
                location,
                $"'{iface.Name}.{ev.Name}' is a {ev.Type.ToDisplayString()} - hub events must be Action or Action<T1..T4>"
            ));
            return null;
        }

        var args = type.TypeArguments.Select(x => x.ToDisplayString(TypeFormat)).ToArray();
        var names = new List<string>();
        for (var i = 0; i < type.TypeArguments.Length; i++)
        {
            var simple = type.TypeArguments[i].Name;
            var name = simple.Length == 0 ? "arg" + i : char.ToLowerInvariant(simple[0]) + simple.Substring(1);
            if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None || names.Contains(name) || name is "push" or "cancellationToken")
                name = "arg" + (i + 1);
            names.Add(name);
        }

        return new EventModel(ev.Name, ev.Type.ToDisplayString(TypeFormat), args, names.ToArray());
    }


    public static HubModel? BuildHub(INamedTypeSymbol hub, Compilation compilation)
    {
        if (hub.IsAbstract || hub.IsGenericType || hub.TypeKind != TypeKind.Class)
            return null;

        INamedTypeSymbol? contract = null;
        for (var t = hub.BaseType; t != null; t = t.BaseType)
        {
            if (t.IsGenericType && t.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Shiny.BluetoothLE.Hubs.BleHub<TContract>")
            {
                contract = t.TypeArguments[0] as INamedTypeSymbol;
                break;
            }
        }
        if (contract == null)
            return null;

        var location = hub.Locations.FirstOrDefault();
        var diagnostics = new List<DiagnosticInfo>();
        var hasAttribute = contract.GetAttributes().Any(x => x.AttributeClass?.ToDisplayString() == AttributeName);
        var isLocal = SymbolEqualityComparer.Default.Equals(contract.ContainingAssembly, compilation.Assembly);
        var contractModel = BuildContract(contract, isLocal);

        if (contract.TypeKind != TypeKind.Interface || !hasAttribute)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                Diagnostics.ContractNotMarked,
                location,
                $"'{hub.Name}' derives from BleHub<{contract.Name}> but {contract.Name} is not an interface marked [BleHubClient]"
            ));
        }

        var bindings = new List<HubMethodBinding>();
        var contractMethods = new[] { contract }.Concat(contract.AllInterfaces)
            .SelectMany(x => x.GetMembers())
            .OfType<IMethodSymbol>()
            .Where(x => x.MethodKind == MethodKind.Ordinary && !x.IsStatic)
            .ToList();

        foreach (var cm in contractMethods)
        {
            var wireParams = cm.Parameters.Where(x => !IsCancellationToken(x.Type)).Select(x => x.Type).ToList();
            var match = AllMembers(hub)
                .OfType<IMethodSymbol>()
                .Where(x => x.Name == cm.Name && x.MethodKind == MethodKind.Ordinary && !x.IsStatic && x.DeclaredAccessibility == Accessibility.Public)
                .Select(x => (Method: x, PassToken: Matches(x, wireParams, cm.ReturnType)))
                .FirstOrDefault(x => x.PassToken != null);

            if (match.Method == null)
            {
                var sig = $"{cm.ReturnType.ToDisplayString()} {cm.Name}({string.Join(", ", wireParams.Select(x => x.ToDisplayString()))})";
                diagnostics.Add(DiagnosticInfo.Create(
                    Diagnostics.HubMethodMismatch,
                    location,
                    $"'{hub.Name}' does not implement '{sig}' from {contract.Name} (a trailing CancellationToken parameter is optional)"
                ));
                continue;
            }
            bindings.Add(new HubMethodBinding(cm.Name, match.PassToken!.Value));
        }

        var ns = hub.ContainingNamespace.IsGlobalNamespace ? null : hub.ContainingNamespace.ToDisplayString();
        return new HubModel(
            hub.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ns,
            hub.Name,
            hub.ToDisplayString().Replace('.', '_').Replace('+', '_'),
            IsEffectivelyPublic(hub),
            contractModel,
            bindings.ToArray(),
            diagnostics.ToArray()
        );
    }


    /// <summary>
    /// Returns whether to pass the invocation token (null = no match)
    /// </summary>
    static bool? Matches(IMethodSymbol candidate, List<ITypeSymbol> wireParams, ITypeSymbol returnType)
    {
        if (!SymbolEqualityComparer.Default.Equals(candidate.ReturnType, returnType))
            return null;

        var ps = candidate.Parameters;
        var passToken = ps.Length > 0 && IsCancellationToken(ps[ps.Length - 1].Type);
        var wire = passToken ? ps.Take(ps.Length - 1).ToList() : ps.ToList();

        if (wire.Count != wireParams.Count || wire.Any(x => x.RefKind != RefKind.None || IsCancellationToken(x.Type)))
            return null;

        for (var i = 0; i < wire.Count; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(wire[i].Type, wireParams[i]))
                return null;
        }
        return passToken;
    }


    static IEnumerable<ISymbol> AllMembers(INamedTypeSymbol type)
    {
        for (var t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            foreach (var m in t.GetMembers())
                yield return m;
    }


    static bool IsCancellationToken(ITypeSymbol type) => type.ToDisplayString() == "System.Threading.CancellationToken";


    static bool IsEffectivelyPublic(ISymbol symbol)
    {
        for (var s = symbol; s != null && s is not INamespaceSymbol; s = s.ContainingSymbol)
        {
            if (s.DeclaredAccessibility != Accessibility.Public)
                return false;
        }
        return true;
    }


    static string EscapeIdentifier(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;


    /// <summary>
    /// Finds [BleHubClient] interfaces in referenced assemblies that themselves reference Shiny.BluetoothLE.Hubs (keeps the scan cheap)
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> FindReferencedContracts(Compilation compilation)
    {
        var attribute = compilation.GetTypeByMetadataName(AttributeName);
        if (attribute == null)
            yield break;

        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (!assembly.Modules.Any(m => m.ReferencedAssemblies.Any(r => r.Name == "Shiny.BluetoothLE.Hubs")))
                continue;

            foreach (var type in GetTypes(assembly.GlobalNamespace))
            {
                if (type.TypeKind == TypeKind.Interface
                    && IsEffectivelyPublic(type)
                    && type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute)))
                    yield return type;
            }
        }
    }


    static IEnumerable<INamedTypeSymbol> GetTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in type.GetTypeMembers())
                yield return nested;
        }
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in GetTypes(child))
                yield return type;
    }
}
