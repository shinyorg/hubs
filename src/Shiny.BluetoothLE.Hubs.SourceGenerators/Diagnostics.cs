using Microsoft.CodeAnalysis;

namespace Shiny.BluetoothLE.Hubs.SourceGenerators;

static class Diagnostics
{
    const string Category = "Shiny.BluetoothLE.Hubs";

    // every descriptor takes the fully formatted message as {0} so DiagnosticInfo can rebuild it
    public static readonly DiagnosticDescriptor HubMethodMismatch = new(
        "SBH001", "Hub does not implement contract method", "{0}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedReturnType = new(
        "SBH002", "Unsupported hub method return type", "{0}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor OverloadedMethod = new(
        "SBH003", "Hub methods cannot be overloaded", "{0}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedEvent = new(
        "SBH004", "Unsupported hub event", "{0}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor ContractNotMarked = new(
        "SBH005", "Hub contract is not marked [BleHubClient]", "{0}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedParameter = new(
        "SBH006", "Unsupported hub method signature", "{0}", Category, DiagnosticSeverity.Error, true);


    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "SBH001" => HubMethodMismatch,
        "SBH002" => UnsupportedReturnType,
        "SBH003" => OverloadedMethod,
        "SBH004" => UnsupportedEvent,
        "SBH005" => ContractNotMarked,
        _ => UnsupportedParameter
    };
}
