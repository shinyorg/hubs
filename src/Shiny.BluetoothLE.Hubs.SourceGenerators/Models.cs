using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Shiny.BluetoothLE.Hubs.SourceGenerators;

enum ReturnKind
{
    Task,
    TaskOfT,
    AsyncEnumerable
}


sealed record ParameterModel(string Name, string Type, bool IsCancellationToken) : IEquatable<ParameterModel>;

sealed record MethodModel(
    string Name,
    ReturnKind ReturnKind,
    string ReturnType,       // full return type as declared (Task<X>, IAsyncEnumerable<X>...)
    string? ResultType,      // X for Task<X> / IAsyncEnumerable<X>
    EquatableArray<ParameterModel> Parameters
) : IEquatable<MethodModel>;

sealed record EventModel(
    string Name,
    string DelegateType,
    EquatableArray<string> ArgumentTypes,
    EquatableArray<string> ArgumentNames
) : IEquatable<EventModel>;

sealed record ContractModel(
    string FullName,          // global::Ns.IGameHub
    string? Namespace,
    string Name,              // IGameHub
    string ProxyName,         // GameHubClient
    string PushExtensionsName,// GameHubPushExtensions
    bool IsPublic,
    bool IsLocal,
    EquatableArray<MethodModel> Methods,
    EquatableArray<EventModel> Events,
    EquatableArray<DiagnosticInfo> Diagnostics
) : IEquatable<ContractModel>;

sealed record HubMethodBinding(string Name, bool PassCancellationToken) : IEquatable<HubMethodBinding>;

sealed record HubModel(
    string FullName,          // global::Ns.GameHub
    string? Namespace,
    string Name,
    string MetadataSafeName,  // Ns_GameHub
    bool IsPublic,
    ContractModel Contract,
    EquatableArray<HubMethodBinding> Bindings,
    EquatableArray<DiagnosticInfo> Diagnostics
) : IEquatable<HubModel>;


/// <summary>
/// Locations are not equatable across compilations - capture what is needed to rebuild one
/// </summary>
sealed record DiagnosticInfo(
    string Id,
    string Message,
    string? FilePath,
    TextSpan Span,
    LinePositionSpan LineSpan
) : IEquatable<DiagnosticInfo>
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location? location, params object[] args)
    {
        var message = string.Format(descriptor.MessageFormat.ToString(), args);
        var lineSpan = location?.GetLineSpan();
        return new DiagnosticInfo(
            descriptor.Id,
            message,
            location?.IsInSource == true ? location.SourceTree?.FilePath : null,
            location?.SourceSpan ?? default,
            lineSpan?.Span ?? default
        );
    }

    public Diagnostic ToDiagnostic()
    {
        var descriptor = Diagnostics.ById(this.Id);
        var location = this.FilePath == null ? Location.None : Location.Create(this.FilePath, this.Span, this.LineSpan);
        return Diagnostic.Create(descriptor, location, this.Message);
    }
}
