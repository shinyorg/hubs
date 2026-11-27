using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.DependencyInjection;
using Shiny.UniversalHubs;

namespace Shiny.UniversalHubs.Tests;

/// <summary>
/// Client transports written outside the library: the order a client tries them in, Prefer / Allow / Only, unsupported
/// transports, and discovery merged across them
/// </summary>
public class ClientTransportPriorityTests : IDisposable
{
    readonly List<string> attempts = new();
    readonly List<ServiceProvider> containers = new();


    public void Dispose()
    {
        foreach (var c in this.containers)
            c.Dispose();
    }


    IUniversalHubClient<ITestHub> Client(Action<UniversalHubClientOptions> configure)
    {
        var sp = new ServiceCollection()
            .AddLogging()
            .AddUniversalHubClient<ITestHub>("test", configure)
            .BuildServiceProvider();
        this.containers.Add(sp);
        return sp.GetRequiredService<IUniversalHubClient<ITestHub>>();
    }

    static HubHostInfo SeenOn(params Type[] transports) => transports.Aggregate(
        new HubHostInfo("Host", "Host", null),
        (host, t) => host.With(t, new FakeEndpoint(t.Name), [])
    );


    async Task<IReadOnlyList<string>> Attempt(IUniversalHubClient<ITestHub> client, HubHostInfo host, HubConnectOptions? options = null)
    {
        this.attempts.Clear();
        await Assert.ThrowsAnyAsync<Exception>(() => client.Connect(host, options));
        return this.attempts.ToList();
    }


    [Fact]
    public async Task TransportsAreTriedInTheOrderTheyWereAdded()
    {
        var client = this.Client(o => o
            .UseTransport(_ => new Beta(this.attempts))
            .UseTransport(_ => new Alpha(this.attempts))
            .UseTransport(_ => new Gamma(this.attempts)));

        Assert.Equal(["Beta", "Alpha", "Gamma"], client.Transports.Select(x => x.DisplayName));
        Assert.Equal(["Beta", "Alpha", "Gamma"], await this.Attempt(client, SeenOn(typeof(Alpha), typeof(Beta), typeof(Gamma))));

        // only what the host is seen on
        Assert.Equal(["Beta", "Gamma"], await this.Attempt(client, SeenOn(typeof(Gamma), typeof(Beta))));
    }


    [Fact]
    public async Task PriorityOverridesTheOrderTheyWereAdded()
    {
        var client = this.Client(o => o
            .UseTransport(_ => new Alpha(this.attempts))
            .UseTransport(_ => new Beta(this.attempts))
            .UseTransport(_ => new Gamma(this.attempts))
            .Priority<Gamma, Alpha>());

        // the ones left out follow, as they were added
        Assert.Equal(["Gamma", "Alpha", "Beta"], await this.Attempt(client, SeenOn(typeof(Alpha), typeof(Beta), typeof(Gamma))));
    }


    [Fact]
    public async Task PreferredTransportsGoFirstAndAllowedOnesAreTheOnlyOnesTried()
    {
        var client = this.Client(o => o
            .UseTransport(_ => new Alpha(this.attempts))
            .UseTransport(_ => new Beta(this.attempts))
            .UseTransport(_ => new Gamma(this.attempts)));
        var host = SeenOn(typeof(Alpha), typeof(Beta), typeof(Gamma));

        Assert.Equal(["Gamma", "Beta", "Alpha"], await this.Attempt(client, host, new HubConnectOptions().Prefer<Gamma>().Prefer<Beta>()));
        Assert.Equal(["Alpha", "Gamma"], await this.Attempt(client, host, new HubConnectOptions().Allow<Gamma>().Allow<Alpha>()));
        Assert.Equal(["Beta"], await this.Attempt(client, host, new HubConnectOptions().Only<Beta>()));
    }


    [Fact]
    public async Task NothingToTryNamesWhatTheHostWasSeenOn()
    {
        var client = this.Client(o => o
            .UseTransport(_ => new Alpha(this.attempts))
            .UseTransport(_ => new Beta(this.attempts)));

        var ex = await Assert.ThrowsAsync<HubException>(() => client.Connect(SeenOn(typeof(Beta)), new HubConnectOptions().Only<Alpha>(), TestContext.Current.CancellationToken));
        Assert.Contains("seen over Beta", ex.Message);
        Assert.Empty(this.attempts);
    }


    [Fact]
    public async Task AnUnsupportedTransportIsNeverTried()
    {
        var client = this.Client(o => o
            .UseTransport(_ => new Unsupported(this.attempts))
            .UseTransport(_ => new Alpha(this.attempts)));

        Assert.Equal(["Alpha"], await this.Attempt(client, SeenOn(typeof(Unsupported), typeof(Alpha))));
    }


    [Fact]
    public async Task MistakesAboutTransportsThrow()
    {
        Assert.Throws<InvalidOperationException>(() => this.Client(o => o
            .UseTransport(_ => new Alpha(this.attempts))
            .UseTransport(_ => new Alpha(this.attempts))));

        // a priority naming a transport the client doesn't have
        Assert.Throws<InvalidOperationException>(() => this.Client(o => o
            .UseTransport(_ => new Alpha(this.attempts))
            .Priority<Alpha, Beta>()));

        Assert.Throws<ArgumentException>(() => new UniversalHubClientOptions().Priority(typeof(Alpha), typeof(Alpha)));
        Assert.Throws<InvalidOperationException>(() => this.Client(_ => { }));

        var client = this.Client(o => o.UseTransport(_ => new Alpha(this.attempts)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Connect(SeenOn(typeof(Alpha)), new HubConnectOptions().Prefer<Beta>(), TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => client.DiscoverAll([typeof(Beta)]));
    }


    [Fact]
    public async Task ADisabledTransportIsNotTriedUntilItIsEnabledAgain()
    {
        var client = this.Client(o => o
            .UseTransport(_ => new Alpha(this.attempts))
            .UseTransport(_ => new Beta(this.attempts))
            .UseTransport(_ => new Gamma(this.attempts)));
        var host = SeenOn(typeof(Alpha), typeof(Beta), typeof(Gamma));
        var changes = 0;
        client.EnabledChanged += (_, _) => changes++;

        await client.Disable<Beta>();
        Assert.False(client.IsEnabled<Beta>());
        Assert.Equal(["Alpha", "Gamma"], await this.Attempt(client, host));

        await client.Solo<Gamma>();
        Assert.Equal(["Gamma"], await this.Attempt(client, host));
        Assert.Equal(["Gamma"], client.Transports.Where(x => client.IsEnabled(x.GetType())).Select(x => x.DisplayName));

        await client.Enable<Beta>();
        await client.Enable<Beta>();   // already on - no change
        Assert.Equal(["Beta", "Gamma"], await this.Attempt(client, host));
        Assert.Equal(3, changes);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetEnabled(typeof(Unsupported), false));
    }


    [Fact]
    public async Task DisablingATransportTakesItOutOfRunningDiscoveries()
    {
        var alpha = new Alpha(this.attempts);
        var beta = new Beta(this.attempts);
        var client = this.Client(o => o
            .UseTransport(_ => alpha)
            .UseTransport(_ => beta));

        var seen = new List<HubHostInfo>();
        using var watch = client.DiscoverAll().Subscribe(seen.Add);
        alpha.Sightings.OnNext(new HubHostSighting("Host", null, new FakeEndpoint("alpha")));
        beta.Sightings.OnNext(new HubHostSighting("Host", null, new FakeEndpoint("beta")));
        Assert.Equal([typeof(Alpha), typeof(Beta)], seen.Last().Transports);

        await client.Disable<Alpha>();
        Assert.False(alpha.Discovering);
        Assert.Equal([typeof(Beta)], seen.Last().Transports);

        // not listening any more
        var count = seen.Count;
        alpha.Sightings.OnNext(new HubHostSighting("Host", null, new FakeEndpoint("alpha")));
        Assert.Equal(count, seen.Count);

        await client.Enable<Alpha>();
        Assert.True(alpha.Discovering);
        alpha.Sightings.OnNext(new HubHostSighting("Host", null, new FakeEndpoint("alpha")));
        Assert.Equal([typeof(Alpha), typeof(Beta)], seen.Last().Transports);

        watch.Dispose();
        Assert.False(alpha.Discovering);
        Assert.False(beta.Discovering);
    }


    [Fact]
    public async Task DiscoveryMergesTransportsInPriorityOrder()
    {
        var alpha = new Alpha(this.attempts);
        var beta = new Beta(this.attempts);
        var unsupported = new Unsupported(this.attempts);
        var client = this.Client(o => o
            .UseTransport(_ => unsupported)
            .UseTransport(_ => alpha)
            .UseTransport(_ => beta));

        var seen = new List<HubHostInfo>();
        using var watch = client.DiscoverAll().Subscribe(x =>
        {
            lock (seen)
                seen.Add(x);
        });

        beta.Sightings.OnNext(new HubHostSighting("Host", null, new FakeEndpoint("beta")));
        alpha.Sightings.OnNext(new HubHostSighting("Host", "host-1", new FakeEndpoint("alpha")));

        var both = seen.Last();
        Assert.Equal([typeof(Alpha), typeof(Beta)], both.Transports);
        Assert.Equal("host-1", both.HostId);
        Assert.True(both.IsSeenOver<Beta>());
        Assert.Equal("alpha", ((FakeEndpoint)both.Endpoints[typeof(Alpha)]).Value);
        Assert.False(unsupported.Discovering);

        alpha.Sightings.OnNext(new HubHostSighting("Host", null, null));
        Assert.Equal([typeof(Beta)], seen.Last().Transports);
        Assert.Equal("host-1", seen.Last().HostId);

        beta.Sightings.OnNext(new HubHostSighting("Host", null, null));
        Assert.Empty(seen.Last().Transports);

        // one transport only
        var betaSeen = new List<HubHostInfo>();
        using var betaOnly = client.DiscoverAll([typeof(Beta)]).Subscribe(betaSeen.Add);
        alpha.Sightings.OnNext(new HubHostSighting("Other", null, new FakeEndpoint("alpha")));
        beta.Sightings.OnNext(new HubHostSighting("Other", null, new FakeEndpoint("beta")));
        Assert.Equal([typeof(Beta)], Assert.Single(betaSeen).Transports);
    }
}


public sealed record FakeEndpoint(string Value) : HubEndpoint;


/// <summary>
/// Records connect attempts and fails them, so the order transports are tried in shows; its sightings come from the test
/// </summary>
public abstract class RecordingClientTransport(List<string> attempts) : IHubClientTransport
{
    public Subject<HubHostSighting> Sightings { get; } = new();
    public bool Discovering { get; private set; }

    public virtual string DisplayName => this.GetType().Name;
    public virtual bool IsSupported => true;

    public IObservable<HubHostSighting> Discover(HubClientContext client) => Observable.Create<HubHostSighting>(observer =>
    {
        this.Discovering = true;
        var sub = this.Sightings.Subscribe(observer);
        return () =>
        {
            this.Discovering = false;
            sub.Dispose();
        };
    });

    public Task Connect(HubClientContext client, HubHostInfo host, CancellationToken cancellationToken)
    {
        lock (attempts)
            attempts.Add(this.DisplayName);
        throw new InvalidOperationException($"{this.DisplayName} is not reachable");
    }
}

public sealed class Alpha(List<string> attempts) : RecordingClientTransport(attempts);
public sealed class Beta(List<string> attempts) : RecordingClientTransport(attempts);
public sealed class Gamma(List<string> attempts) : RecordingClientTransport(attempts);

public sealed class Unsupported(List<string> attempts) : RecordingClientTransport(attempts)
{
    public override bool IsSupported => false;
}
