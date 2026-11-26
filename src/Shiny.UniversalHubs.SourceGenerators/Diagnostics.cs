using Microsoft.CodeAnalysis;

namespace Shiny.UniversalHubs.SourceGenerators;

static class Diagnostics
{
    const string Category = "Shiny.UniversalHubs";

    // every descriptor takes the fully formatted message as {0} so DiagnosticInfo can rebuild it
    public static readonly DiagnosticDescriptor HubMethodMismatch = new(
        "SUH001", "Hub does not implement contract method", "{0}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedReturnType = new(
        "SUH002", "Unsupported hub method return type", "{0}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor OverloadedMethod = new(
        "SUH003", "Hub methods cannot be overloaded", "{0}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedEvent = new(
        "SUH004", "Unsupported hub event", "{0}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor ContractNotMarked = new(
        "SUH005", "Hub contract is not marked [UniversalHubContract]", "{0}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedParameter = new(
        "SUH006", "Unsupported hub method signature", "{0}", Category, DiagnosticSeverity.Error, true);


    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "SUH001" => HubMethodMismatch,
        "SUH002" => UnsupportedReturnType,
        "SUH003" => OverloadedMethod,
        "SUH004" => UnsupportedEvent,
        "SUH005" => ContractNotMarked,
        _ => UnsupportedParameter
    };
}
