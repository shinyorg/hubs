using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shiny.UniversalHubs.SourceGenerators;

namespace Shiny.UniversalHubs.Tests;

public class GeneratorTests
{
    static (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Sources) Run(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(x => MetadataReference.CreateFromFile(x))
            .Concat([
                MetadataReference.CreateFromFile(typeof(UniversalHubContractAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(UniversalHub<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(HubProxy).Assembly.Location)
            ]);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "GenTest",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        var driver = CSharpGeneratorDriver.Create([new UniversalHubGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var all = diagnostics.Concat(output.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error)).ToList();
        var sources = driver.GetRunResult().GeneratedTrees.Select(x => x.ToString()).ToList();
        return (all, sources);
    }


    const string Usings = "using System; using System.Threading; using System.Threading.Tasks; using System.Collections.Generic; using Shiny.UniversalHubs;\n";


    [Fact]
    public void ValidHubCompilesClean()
    {
        var (diagnostics, sources) = Run(Usings + """
            namespace Demo;
            [UniversalHubContract] public interface IChat { Task<int> Send(string text); IAsyncEnumerable<string> Feed(CancellationToken ct); event Action<string> Said; }
            public class ChatHub : UniversalHub<IChat>
            {
                public Task<int> Send(string text) => Task.FromResult(text.Length);
                public IAsyncEnumerable<string> Feed(CancellationToken ct) => throw new NotImplementedException();
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Contains(sources, x => x.Contains("class ChatClient"));
        Assert.Contains(sources, x => x.Contains("class ChatPushExtensions"));
        Assert.Contains(sources, x => x.Contains("Demo_ChatHub_HubDispatcher"));
    }


    [Fact]
    public void MissingHubMethod()
    {
        var (diagnostics, _) = Run(Usings + """
            [UniversalHubContract] public interface IChat { Task<int> Send(string text); }
            public class ChatHub : UniversalHub<IChat> { public Task<int> Send(int wrong) => Task.FromResult(1); }
            """);
        Assert.Contains(diagnostics, x => x.Id == "SUH001");
    }


    [Fact]
    public void UnsupportedReturnType()
    {
        var (diagnostics, _) = Run(Usings + "[UniversalHubContract] public interface IChat { int Send(string text); }");
        Assert.Contains(diagnostics, x => x.Id == "SUH002");
    }


    [Fact]
    public void OverloadsAreRejected()
    {
        var (diagnostics, _) = Run(Usings + "[UniversalHubContract] public interface IChat { Task Send(string text); Task Send(int n); }");
        Assert.Contains(diagnostics, x => x.Id == "SUH003");
    }


    [Fact]
    public void EventsMustBeActions()
    {
        var (diagnostics, _) = Run(Usings + "[UniversalHubContract] public interface IChat { event EventHandler Said; }");
        Assert.Contains(diagnostics, x => x.Id == "SUH004");
    }


    [Fact]
    public void ContractMustBeMarked()
    {
        var (diagnostics, _) = Run(Usings + """
            public interface IChat { Task Send(string text); }
            public class ChatHub : UniversalHub<IChat> { public Task Send(string text) => Task.CompletedTask; }
            """);
        Assert.Contains(diagnostics, x => x.Id == "SUH005");
    }


    [Fact]
    public void RefParametersAreRejected()
    {
        var (diagnostics, _) = Run(Usings + "[UniversalHubContract] public interface IChat { Task Send(ref int n); }");
        Assert.Contains(diagnostics, x => x.Id == "SUH006");
    }
}
