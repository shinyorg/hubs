using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Shiny.BluetoothLE.Hubs.SourceGenerators;

[Generator(LanguageNames.CSharp)]
public sealed class BleHubGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var references = context.CompilationProvider.Select(static (c, _) => new ReferenceInfo(
            c.GetTypeByMetadataName(ModelBuilder.ClientBaseName) != null,
            c.GetTypeByMetadataName(ModelBuilder.HubBaseName) != null,
            c.GetTypeByMetadataName(ModelBuilder.ClientBaseName) == null
                ? System.Array.Empty<ContractModel>()
                : ModelBuilder.FindReferencedContracts(c).Select(x => ModelBuilder.BuildContract(x, false)).ToArray()
        ));

        var contracts = context.SyntaxProvider.ForAttributeWithMetadataName(
            ModelBuilder.AttributeName,
            static (node, _) => node is InterfaceDeclarationSyntax,
            static (ctx, _) => ModelBuilder.BuildContract((INamedTypeSymbol)ctx.TargetSymbol, true)
        );

        var hubs = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
            static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node) is INamedTypeSymbol symbol
                ? ModelBuilder.BuildHub(symbol, ctx.SemanticModel.Compilation)
                : null
        ).Where(static x => x != null);

        // local contracts - proxies (client referenced) and push senders (host referenced)
        context.RegisterSourceOutput(contracts.Combine(references), static (spc, pair) =>
        {
            var (contract, refs) = pair;
            foreach (var d in contract.Diagnostics)
                spc.ReportDiagnostic(d.ToDiagnostic());

            if (contract.Diagnostics.Count > 0)
                return;

            if (refs.HasClient)
                spc.AddSource(HintName(contract.Namespace, contract.ProxyName), Emitter.ClientProxy(contract));

            if (refs.HasHost)
                spc.AddSource(HintName(contract.Namespace, contract.PushExtensionsName), Emitter.PushExtensions(contract));
        });

        // contracts declared in referenced assemblies get internal proxies
        context.RegisterSourceOutput(references, static (spc, refs) =>
        {
            if (!refs.HasClient)
                return;

            foreach (var contract in refs.ExternalContracts.Where(x => x.Diagnostics.Count == 0))
                spc.AddSource(HintName(contract.Namespace, contract.ProxyName), Emitter.ClientProxy(contract));
        });

        // hubs - dispatchers, IHubContext<THub>.Clients, and push senders for external contracts
        context.RegisterSourceOutput(hubs.Collect(), static (spc, all) =>
        {
            var externalPushes = new HashSet<string>();
            foreach (var hub in all.Distinct())
            {
                foreach (var d in hub!.Diagnostics)
                    spc.ReportDiagnostic(d.ToDiagnostic());

                if (hub.Diagnostics.Count > 0 || hub.Contract.Diagnostics.Count > 0)
                    continue;

                spc.AddSource(HintName("Shiny.BluetoothLE.Hubs.Generated", hub.MetadataSafeName + "_BleHubDispatcher"), Emitter.HubDispatcher(hub));

                if (!hub.Contract.IsLocal && externalPushes.Add(hub.Contract.FullName))
                    spc.AddSource(HintName(hub.Contract.Namespace, hub.Contract.PushExtensionsName), Emitter.PushExtensions(hub.Contract));
            }
        });
    }


    static string HintName(string? ns, string name) => $"{(ns == null ? "" : ns + ".")}{name}.g.cs";
}


sealed record ReferenceInfo(bool HasClient, bool HasHost, EquatableArray<ContractModel> ExternalContracts);
