using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shiny.UniversalHubs.SourceGenerators;

namespace Shiny.UniversalHubs.Tests;

public class GeneratorFixesTests
{
    const string Usings = "using System; using System.Threading; using System.Threading.Tasks; using System.Collections.Generic; using Shiny.UniversalHubs;\n";

    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);


    static IEnumerable<MetadataReference> BaseReferences() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(x => (MetadataReference)MetadataReference.CreateFromFile(x))
        .Concat([
            MetadataReference.CreateFromFile(typeof(UniversalHubContractAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(UniversalHub<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(HubProxy).Assembly.Location)
        ]);


    static (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Sources, Compilation Output) Run(string assemblyName, string source, params MetadataReference[] extra)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, ParseOptions)],
            BaseReferences().Concat(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        var driver = CSharpGeneratorDriver.Create([new UniversalHubGenerator().AsSourceGenerator()], parseOptions: ParseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var all = diagnostics.Concat(output.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error)).ToList();
        var sources = driver.GetRunResult().GeneratedTrees.Select(x => x.ToString()).ToList();
        return (all, sources, output);
    }


    static MetadataReference Emit(Compilation compilation)
    {
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(ms.ToArray());
    }


    [Fact]
    public void HostOverReferencedContractsLibraryUsesItsPushExtensions()
    {
        var (libDiagnostics, libSources, libOutput) = Run("Contracts", Usings + """
            namespace Demo.Contracts;
            [UniversalHubContract] public interface IChat { Task Send(string text); event Action<string> Said; }
            """);
        Assert.Empty(libDiagnostics);
        Assert.Contains(libSources, x => x.Contains("public static class ChatPushExtensions"));

        var (diagnostics, sources, _) = Run("HostApp", Usings + """
            using Demo.Contracts;
            namespace Demo.Host;
            public class ChatHub : UniversalHub<IChat>
            {
                public Task Send(string text) => this.Clients.All.Said(text);
            }
            """, Emit(libOutput));

        Assert.Empty(diagnostics);
        Assert.DoesNotContain(sources, x => x.Contains("class ChatPushExtensions"));
        Assert.Contains(sources, x => x.Contains("Demo_Host_ChatHub_HubDispatcher"));
    }


    [Fact]
    public void HostOverContractsWithoutPushExtensionsStillGetsThem()
    {
        // a contracts assembly built without the generator - the host emits internal push senders for it
        var lib = CSharpCompilation.Create(
            "PlainContracts",
            [CSharpSyntaxTree.ParseText(Usings + "namespace Demo.Contracts; [UniversalHubContract] public interface IChat { Task Send(string text); event Action<string> Said; }", ParseOptions)],
            BaseReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var (diagnostics, sources, _) = Run("HostApp", Usings + """
            using Demo.Contracts;
            namespace Demo.Host;
            public class ChatHub : UniversalHub<IChat>
            {
                public Task Send(string text) => this.Clients.All.Said(text);
            }
            """, Emit(lib));

        Assert.Empty(diagnostics);
        Assert.Contains(sources, x => x.Contains("internal static class ChatPushExtensions"));
    }


    [Theory]
    [InlineData("string[] GetNames();")]
    [InlineData("dynamic GetValue();")]
    [InlineData("unsafe int* GetPointer();")]
    public void NonNamedReturnTypesAreReported(string member)
    {
        var (diagnostics, _, _) = Run("GenTest", Usings + "[UniversalHubContract] public interface IChat { " + member + " }");
        Assert.Contains(diagnostics, x => x.Id == "SUH002");
        Assert.DoesNotContain(diagnostics, x => x.Id == "CS8785");
    }


    [Fact]
    public void TypeParameterReturnOnGenericContractIsReported()
    {
        var (diagnostics, _, _) = Run("GenTest", Usings + "[UniversalHubContract] public interface IChat<T> { T Get(); }");
        Assert.Contains(diagnostics, x => x.Id == "SUH002");
        Assert.Contains(diagnostics, x => x.Id == "SUH008");
        Assert.DoesNotContain(diagnostics, x => x.Id == "CS8785");
    }


    [Fact]
    public void PointerParametersAreRejected()
    {
        var (diagnostics, _, _) = Run("GenTest", Usings + "[UniversalHubContract] public unsafe interface IChat { Task Send(int* p); }");
        Assert.Contains(diagnostics, x => x.Id == "SUH006");
        Assert.DoesNotContain(diagnostics, x => x.Id == "CS8785");
    }


    [Theory]
    [InlineData("string Name { get; }")]
    [InlineData("int this[int i] { get; }")]
    public void PropertiesAndIndexersAreRejected(string member)
    {
        var (diagnostics, _, _) = Run("GenTest", Usings + "[UniversalHubContract] public interface IChat { Task Send(string text); " + member + " }");
        Assert.Contains(diagnostics, x => x.Id == "SUH007");
        Assert.DoesNotContain(diagnostics, x => x.Id == "CS0535");
    }


    [Fact]
    public void GenericContractsAreRejected()
    {
        var (diagnostics, _, _) = Run("GenTest", Usings + "[UniversalHubContract] public interface IChat<T> { Task Send(T value); }");
        Assert.Contains(diagnostics, x => x.Id == "SUH008");
        Assert.DoesNotContain(diagnostics, x => x.Id == "CS0246");
    }


    [Fact]
    public void ContractsNestedInGenericTypesAreRejected()
    {
        var (diagnostics, _, _) = Run("GenTest", Usings + "public class Outer<T> { [UniversalHubContract] public interface IChat { Task Send(T value); } }");
        Assert.Contains(diagnostics, x => x.Id == "SUH008");
    }


    [Theory]
    [InlineData("Task Hub();")]
    [InlineData("Task OnPush(string name);")]
    [InlineData("Task RegisterHubProxy();")]
    [InlineData("Task CreateArguments();")]
    [InlineData("Task ChatClient();")]
    [InlineData("event Action Hub;")]
    public void ReservedProxyMemberNamesAreRejected(string member)
    {
        var (diagnostics, _, _) = Run("GenTest", Usings + "[UniversalHubContract] public interface IChat { " + member + " }");
        Assert.Contains(diagnostics, x => x.Id == "SUH009");
        Assert.DoesNotContain(diagnostics, x => x.Id.StartsWith("CS"));
    }


    [Fact]
    public void MethodAndEventWithTheSameNameAreRejected()
    {
        var (diagnostics, _, _) = Run("GenTest", Usings + """
            public interface IBase { Task Said(); }
            [UniversalHubContract] public interface IChat : IBase { event Action Said; }
            """);
        Assert.Contains(diagnostics, x => x.Id == "SUH009");
    }


    [Fact]
    public void GenericHubMethodsDoNotMatchContractMethods()
    {
        var (diagnostics, _, _) = Run("GenTest", Usings + """
            [UniversalHubContract] public interface IChat { Task Foo(int a); }
            public class ChatHub : UniversalHub<IChat> { public Task Foo<T>(int a) => Task.CompletedTask; }
            """);
        Assert.Contains(diagnostics, x => x.Id == "SUH001");
        Assert.DoesNotContain(diagnostics, x => x.Id == "CS0411");
    }


    [Fact]
    public void NonGenericOverloadBesideGenericHubMethodStillMatches()
    {
        var (diagnostics, _, _) = Run("GenTest", Usings + """
            [UniversalHubContract] public interface IChat { Task Foo(int a); }
            public class ChatHub : UniversalHub<IChat>
            {
                public Task Foo<T>(int a) => Task.CompletedTask;
                public Task Foo(int a) => Task.CompletedTask;
            }
            """);
        Assert.Empty(diagnostics);
    }
}
