using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;

namespace Sample.Calls;

/// <summary>
/// Places and takes 1:1 calls over the app's hubs - one call at a time. Resolve it at startup: incoming calls are only
/// answered once it exists (Calls.Maui does that for you).
/// </summary>
public interface IHubCalls
{
    /// <summary>
    /// The call in progress (ringing, connecting or active), if any
    /// </summary>
    IHubCall? Current { get; }

    /// <summary>
    /// What this device's video can do - None makes every call audio only
    /// </summary>
    CallVideoCodecs VideoCodecs { get; }

    /// <summary>
    /// Someone is calling. The call is ringing: Accept or Decline it. While a call is in progress, new callers hear Busy.
    /// Raised on a background thread.
    /// </summary>
    event EventHandler<IHubCall>? Incoming;

    /// <summary>
    /// Calls the host this <typeparamref name="TContract"/> client is connected to. Returns once the invite is sent.
    /// </summary>
    Task<IHubCall> CallHost<TContract>(CallOptions? options = null, CancellationToken cancellationToken = default) where TContract : class;

    /// <summary>
    /// Calls another client of the same host, which relays the call. <paramref name="connectionId"/> is that client's
    /// connection id on the host - the app shares it however suits (a hub method, a push).
    /// </summary>
    Task<IHubCall> CallPeer<TContract>(string connectionId, CallOptions? options = null, CancellationToken cancellationToken = default) where TContract : class;

    /// <summary>
    /// The host calls one of <typeparamref name="THub"/>'s clients
    /// </summary>
    Task<IHubCall> CallClient<THub>(string connectionId, CallOptions? options = null, CancellationToken cancellationToken = default) where THub : class;
}


internal interface ICallEndpoint;


internal sealed class HubCalls : IHubCalls
{
    readonly IServiceProvider services;
    readonly ConcurrentDictionary<Guid, HubCall> calls = new();
    readonly Dictionary<Type, ICallEndpoint> endpoints = new();
    readonly object gate = new();


    public HubCalls(
        IServiceProvider services,
        HubCallsOptions options,
        HubCallsRegistrations registrations,
        ICallAudio? audio = null,
        ICallVideoCapabilities? video = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        this.services = services;
        this.Options = options;
        this.Audio = audio;
        this.supportedCodecs = video?.Supported ?? CallVideoCodecs.None;
        this.LoggerFactory = loggerFactory;
        this.Logger = loggerFactory?.CreateLogger<HubCalls>();

        foreach (var (type, create) in registrations.Endpoints)
            this.endpoints[type] = create(services, this);
    }


    internal HubCallsOptions Options { get; }
    internal ICallAudio? Audio { get; }
    internal ILogger? Logger { get; }
    internal ILoggerFactory? LoggerFactory { get; }

    readonly CallVideoCodecs supportedCodecs;
    public CallVideoCodecs VideoCodecs => this.supportedCodecs & this.Options.AllowedVideoCodecs;
    public event EventHandler<IHubCall>? Incoming;

    public IHubCall? Current => this.calls.Values.FirstOrDefault(x => x.State != CallState.Ended);


    public Task<IHubCall> CallHost<TContract>(CallOptions? options = null, CancellationToken cancellationToken = default) where TContract : class
        => this.CallThroughClient<TContract>("", options, cancellationToken);


    public Task<IHubCall> CallPeer<TContract>(string connectionId, CallOptions? options = null, CancellationToken cancellationToken = default) where TContract : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return this.CallThroughClient<TContract>(connectionId, options, cancellationToken);
    }


    async Task<IHubCall> CallThroughClient<TContract>(string to, CallOptions? options, CancellationToken cancellationToken) where TContract : class
    {
        var endpoint = this.Endpoint<ClientCallEndpoint<TContract>>($"Client<{typeof(TContract).Name}>()");
        var link = await endpoint.GetLink(cancellationToken).ConfigureAwait(false);
        return this.Place(link, to, typeof(TContract), options ?? new CallOptions(), endpoint.ClientName);
    }


    public Task<IHubCall> CallClient<THub>(string connectionId, CallOptions? options = null, CancellationToken cancellationToken = default) where THub : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        var router = this.Endpoint<HostCallRouter<THub>>($"Host<{typeof(THub).Name}>()");
        var link = router.Find(connectionId) ?? throw new BleHubException($"{connectionId} is not connected to {typeof(THub).Name} with calls");
        return Task.FromResult(this.Place(link, "", typeof(THub), options ?? new CallOptions(), router.HostName));
    }


    internal IHubCall Place(MediaLink link, string to, Type hub, CallOptions options, string? defaultName)
    {
        HubCall call;
        lock (this.gate)
        {
            if (this.Current != null)
                throw new InvalidOperationException("Already on a call - hang it up first");

            // host → client: the invite goes straight down that client's link, so it is addressed to "" (whoever is there)
            call = HubCall.Outgoing(this, link, to, hub, options, this.VideoCodecs);
            this.calls[call.Id] = call;
        }
        call.Dial(options.DisplayName ?? defaultName);
        this.Logger?.LogInformation("Calling {To} ({Video})", to.Length == 0 ? "the host" : to, call.IsVideo ? "video" : "audio");
        return call;
    }


    T Endpoint<T>(string registration) where T : class, ICallEndpoint
    {
        foreach (var endpoint in this.endpoints.Values)
        {
            if (endpoint is T match)
                return match;
        }
        throw new InvalidOperationException($"Calls aren't set up for that hub - add {registration} in AddHubCalls");
    }


    /// <summary>
    /// A packet for one of this device's own calls. The router has already taken the relayed ones.
    /// </summary>
    internal async Task OnPacket(MediaLink link, Type hub, ReadOnlyMemory<byte> packet)
    {
        if (!CallProtocol.TryReadHeader(packet.Span, out var version, out var type, out var callId))
            return;

        if (type == CallPacketType.Invite)
        {
            this.OnInvite(link, hub, version, callId, packet);
            return;
        }

        if (this.calls.TryGetValue(callId, out var call) && call.Link == link)
            await call.OnPacket(type, packet).ConfigureAwait(false);
    }


    void OnInvite(MediaLink link, Type hub, byte version, Guid callId, ReadOnlyMemory<byte> packet)
    {
        if (version != CallProtocol.Version)
        {
            link.Send(CallProtocol.Decline(callId, CallEndReason.Unsupported, $"Call protocol {version} isn't supported (this device speaks {CallProtocol.Version})"));
            return;
        }

        var invite = CallProtocol.ReadInvite(packet.Span);
        HubCall call;
        lock (this.gate)
        {
            if (this.calls.ContainsKey(callId))
                return;

            if (this.Current != null)
            {
                link.Send(CallProtocol.Decline(callId, CallEndReason.Busy, null));
                return;
            }
            call = HubCall.Incoming(this, link, callId, invite, hub, this.VideoCodecs);
            this.calls[callId] = call;
        }

        link.Send(CallProtocol.Ringing(callId));
        call.StartIncomingRingTimer();
        this.Logger?.LogInformation("Incoming {Video} call from {From}", call.IsVideo ? "video" : "audio", call.RemoteName ?? call.RemoteConnectionId);

        try
        {
            this.Incoming?.Invoke(this, call);
        }
        catch (Exception ex)
        {
            this.Logger?.LogWarning(ex, "Incoming handler failed");
        }
    }


    internal void OnKeyFrameNeeded(Guid callId)
    {
        if (this.calls.TryGetValue(callId, out var call))
            call.RequestKeyFrame();
    }


    internal Task OnLinkClosed(MediaLink link)
        => Task.WhenAll(this.calls.Values.Where(x => x.Link == link).Select(x => x.OnLinkLost()));


    internal void OnEnded(HubCall call)
    {
        this.calls.TryRemove(call.Id, out _);
        this.Logger?.LogInformation("Call {Id} ended: {Reason}", call.Id, call.End?.Reason);
    }
}


internal sealed class HubCallsRegistrations
{
    public List<(Type Type, Func<IServiceProvider, HubCalls, ICallEndpoint> Create)> Endpoints { get; } = new();
}


public sealed class HubCallsBuilder
{
    readonly IServiceCollection services;
    readonly HubCallsRegistrations registrations;
    readonly HubCallsOptions options;


    internal HubCallsBuilder(IServiceCollection services, HubCallsRegistrations registrations, HubCallsOptions options)
    {
        this.services = services;
        this.registrations = registrations;
        this.options = options;
    }


    /// <summary>
    /// Calls on this host's <typeparamref name="THub"/>: it takes calls from its clients, calls them
    /// (<see cref="IHubCalls.CallClient{THub}"/>), and relays calls between them. The host needs media channels
    /// (BleHubHostOptions.EnableMediaChannels).
    /// </summary>
    public HubCallsBuilder Host<THub>() where THub : class
    {
        this.Add(typeof(HostCallRouter<THub>), (sp, calls) => new HostCallRouter<THub>(
            sp.GetRequiredService<IHubContext<THub>>(),
            sp.GetService<BleHubHostOptions>(),
            calls
        ));
        return this;
    }


    /// <summary>
    /// Calls through this app's <typeparamref name="TContract"/> client: it opens a media channel whenever it connects,
    /// so it can call (<see cref="IHubCalls.CallHost{TContract}"/>, <see cref="IHubCalls.CallPeer{TContract}"/>) and be called
    /// </summary>
    public HubCallsBuilder Client<TContract>() where TContract : class
    {
        this.Add(typeof(ClientCallEndpoint<TContract>), (sp, calls) => new ClientCallEndpoint<TContract>(
            sp.GetRequiredService<IBleHubClient<TContract>>(),
            calls
        ));
        return this;
    }


    public HubCallsBuilder Options(Action<HubCallsOptions> configure)
    {
        configure(this.options);
        return this;
    }


    void Add(Type type, Func<IServiceProvider, HubCalls, ICallEndpoint> create)
    {
        if (this.registrations.Endpoints.Any(x => x.Type == type))
            throw new InvalidOperationException($"{type.GetGenericArguments()[0].Name} already has calls");

        this.registrations.Endpoints.Add((type, create));
    }
}


public static class HubCallsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IHubCalls"/>. Pick the hubs this app hosts calls on (<see cref="HubCallsBuilder.Host{THub}"/>)
    /// and the clients it calls through (<see cref="HubCallsBuilder.Client{TContract}"/>). The microphone and camera come
    /// from <see cref="ICallAudio"/> / <see cref="ICallVideoCapabilities"/>: AddHubCallsMaui
    /// registers them.
    /// </summary>
    public static IServiceCollection AddHubCalls(this IServiceCollection services, Action<HubCallsBuilder> configure)
    {
        if (services.Any(x => x.ServiceType == typeof(HubCallsRegistrations)))
            throw new InvalidOperationException("AddHubCalls can only be called once");

        var registrations = new HubCallsRegistrations();
        var options = new HubCallsOptions();
        configure(new HubCallsBuilder(services, registrations, options));
        if (registrations.Endpoints.Count == 0)
            throw new InvalidOperationException("AddHubCalls needs at least one Host<THub>() or Client<TContract>()");

        services.AddSingleton(registrations);
        services.AddSingleton(options);
        services.AddSingleton<HubCalls>();
        services.AddSingleton<IHubCalls>(sp => sp.GetRequiredService<HubCalls>());
        return services;
    }
}
