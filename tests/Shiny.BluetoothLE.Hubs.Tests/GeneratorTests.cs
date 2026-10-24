using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shiny.BluetoothLE.Hubs.SourceGenerators;

namespace Shiny.BluetoothLE.Hubs.Tests;

public class GeneratorTests
{
    static (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Sources) Run(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(x => MetadataReference.CreateFromFile(x))
            .Concat([
                MetadataReference.CreateFromFile(typeof(BleHubClientAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(BleHub<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(BleHubClient).Assembly.Location)
            ]);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "GenTest",
            [CSharpSyntaxTree.ParseText(source, parseOptions)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        var driver = CSharpGeneratorDriver.Create([new BleHubGenerator().AsSourceGenerator()], parseOptions: parseOptions)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var all = diagnostics.Concat(output.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error)).ToList();
        var sources = driver.GetRunResult().GeneratedTrees.Select(x => x.ToString()).ToList();
        return (all, sources);
    }


    const string Usings = "using System; using System.Threading; using System.Threading.Tasks; using System.Collections.Generic; using Shiny.BluetoothLE.Hubs;\n";


    [Fact]
    public void ValidHubCompilesClean()
    {
        var (diagnostics, sources) = Run(Usings + """
            namespace Demo;
            [BleHubClient] public interface IChat { Task<int> Send(string text); IAsyncEnumerable<string> Feed(CancellationToken ct); event Action<string> Said; }
            public class ChatHub : BleHub<IChat>
            {
                public Task<int> Send(string text) => Task.FromResult(text.Length);
                public IAsyncEnumerable<string> Feed(CancellationToken ct) => throw new NotImplementedException();
            }
            """);

        Assert.Empty(diagnostics);
        Assert.Contains(sources, x => x.Contains("class ChatClient"));
        Assert.Contains(sources, x => x.Contains("class ChatPushExtensions"));
        Assert.Contains(sources, x => x.Contains("Demo_ChatHub_BleHubDispatcher"));
    }


    [Fact]
    public void MissingHubMethod()
    {
        var (diagnostics, _) = Run(Usings + """
            [BleHubClient] public interface IChat { Task<int> Send(string text); }
            public class ChatHub : BleHub<IChat> { public Task<int> Send(int wrong) => Task.FromResult(1); }
            """);
        Assert.Contains(diagnostics, x => x.Id == "SBH001");
    }


    [Fact]
    public void UnsupportedReturnType()
    {
        var (diagnostics, _) = Run(Usings + "[BleHubClient] public interface IChat { int Send(string text); }");
        Assert.Contains(diagnostics, x => x.Id == "SBH002");
    }


    [Fact]
    public void OverloadsAreRejected()
    {
        var (diagnostics, _) = Run(Usings + "[BleHubClient] public interface IChat { Task Send(string text); Task Send(int n); }");
        Assert.Contains(diagnostics, x => x.Id == "SBH003");
    }


    [Fact]
    public void EventsMustBeActions()
    {
        var (diagnostics, _) = Run(Usings + "[BleHubClient] public interface IChat { event EventHandler Said; }");
        Assert.Contains(diagnostics, x => x.Id == "SBH004");
    }


    [Fact]
    public void ContractMustBeMarked()
    {
        var (diagnostics, _) = Run(Usings + """
            public interface IChat { Task Send(string text); }
            public class ChatHub : BleHub<IChat> { public Task Send(string text) => Task.CompletedTask; }
            """);
        Assert.Contains(diagnostics, x => x.Id == "SBH005");
    }


    [Fact]
    public void RefParametersAreRejected()
    {
        var (diagnostics, _) = Run(Usings + "[BleHubClient] public interface IChat { Task Send(ref int n); }");
        Assert.Contains(diagnostics, x => x.Id == "SBH006");
    }
}
