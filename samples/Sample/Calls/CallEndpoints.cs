using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Shiny.BluetoothLE.Hubs;
using Shiny.UniversalHubs;

namespace Sample.Calls;

/// <summary>
/// The client side: opens a media channel to the host each time the client connects, and hands its packets to
/// <see cref="HubCalls"/>
/// </summary>
internal sealed class ClientCallEndpoint<TContract> : ICallEndpoint where TContract : class
{
    readonly IBleHubClient<TContract> client;
    readonly HubCalls calls;
    readonly ILogger? logger;
    readonly SemaphoreSlim opening = new(1, 1);
    MediaLink? link;


    public ClientCallEndpoint(IBleHubClient<TContract> client, HubCalls calls)
    {
        this.client = client;
        this.calls = calls;
        this.logger = calls.LoggerFactory?.CreateLogger<ClientCallEndpoint<TContract>>();

        client.Connected += (_, _) => _ = this.TryOpen();
        if (client.Status == BleHubClientStatus.Connected)
            _ = this.TryOpen();
    }


    public string? ClientName => this.client.ClientName;


    /// <summary>
    /// The open link, opening one if the client is connected but has none yet
    /// </summary>
    public async Task<MediaLink> GetLink(CancellationToken cancellationToken)
    {
        if (this.client.Status != BleHubClientStatus.Connected)
            throw new BleHubException($"Connect the {typeof(TContract).Name} client before calling");
        if (!this.client.CanOpenMediaChannel)
            throw new BleHubException("This host doesn't take calls - it has no media channels");

        return await this.Open(cancellationToken).ConfigureAwait(false);
    }


    async Task TryOpen()
    {
        try
        {
            if (this.client.CanOpenMediaChannel)
                await this.Open(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // the hub still works without calls
            this.logger?.LogInformation(ex, "Could not open a media channel to {Host}", this.client.HostName);
        }
    }


    async Task<MediaLink> Open(CancellationToken cancellationToken)
    {
        await this.opening.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.link is { } open)
                return open;

            var channel = await this.client.OpenMediaChannel(cancellationToken).ConfigureAwait(false);
            var profile = ((BleHubClient)(object)this.client).ExternalTransport != null ? CallProfile.Wifi : CallProfile.Ble;
            var link = new MediaLink(channel, "", profile, this.logger);
            link.KeyFrameNeeded += (_, callId) => this.calls.OnKeyFrameNeeded(callId);
            this.link = link;
            link.Start(
                (l, packet) => this.calls.OnPacket(l, typeof(TContract), packet),
                this.OnClosed,
                this.logger
            );
            return link;
        }
        finally
        {
            this.opening.Release();
        }
    }


    async Task OnClosed(MediaLink closed)
    {
        await this.opening.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.link == closed)
                this.link = null;
        }
        finally
        {
            this.opening.Release();
        }
        await this.calls.OnLinkClosed(closed).ConfigureAwait(false);
        await closed.DisposeAsync().ConfigureAwait(false);
    }
}


/// <summary>
/// The host side of one hub: takes a link per client media channel, keeps the host's own calls on it, and relays
/// calls between clients (PLAN.md §9.3)
/// </summary>
internal sealed class HostCallRouter<THub> : ICallEndpoint where THub : class
{
    readonly HubCalls calls;
    readonly BleHubHostOptions? hostOptions;
    readonly ILogger? logger;
    readonly ConcurrentDictionary<string, MediaLink> links = new();
    readonly ConcurrentDictionary<Guid, (MediaLink A, MediaLink B)> routes = new();


    public HostCallRouter(IHubContext<THub> hub, BleHubHostOptions? hostOptions, HubCalls calls)
    {
        this.calls = calls;
        this.hostOptions = hostOptions;
        this.logger = calls.LoggerFactory?.CreateLogger<HostCallRouter<THub>>();

        if (hostOptions?.MediaChannels == null)
            this.logger?.LogWarning("Calls need media channels - call EnableMediaChannels() in AddUniversalServer's Ble(...)");
        hub.MediaChannelOpened += (_, e) => this.Attach(e.Client.Id, e.Channel);
    }


    public string? HostName => this.hostOptions?.LocalName;

    public MediaLink? Find(string connectionId) => this.links.GetValueOrDefault(connectionId);

    /// <summary>
    /// Calls being relayed right now
    /// </summary>
    internal int RelayedCalls => this.routes.Count;


    void Attach(string connectionId, IBleHubMediaChannel channel)
    {
        // Multipeer has Wi-Fi bandwidth, so only BLE gets the low-bandwidth profile
        var profile = HubConnection.GetTransport(connectionId) == HubTransports.Ble ? CallProfile.Ble : CallProfile.Wifi;
        var link = new MediaLink(channel, connectionId, profile, this.logger);
        link.KeyFrameNeeded += this.OnKeyFrameNeeded;

        if (this.links.TryGetValue(connectionId, out var replaced))
            _ = this.Detach(replaced);

        this.links[connectionId] = link;
        link.Start(this.OnPacket, this.Detach, this.logger);
        this.logger?.LogDebug("Media channel from {Client} over {Profile}", connectionId, profile);
    }


    async Task OnPacket(MediaLink from, ReadOnlyMemory<byte> packet)
    {
        if (!CallProtocol.TryReadHeader(packet.Span, out var version, out var type, out var callId))
            return;

        if (this.routes.TryGetValue(callId, out var route))
        {
            if (from != route.A && from != route.B)
                return;   // someone else's call

            var other = from == route.A ? route.B : route.A;
            other.Send(packet.ToArray());
            if (type is CallPacketType.HangUp or CallPacketType.Decline)
                this.routes.TryRemove(callId, out _);
            return;
        }

        if (type == CallPacketType.Invite && version == CallProtocol.Version)
        {
            var invite = CallProtocol.ReadInvite(packet.Span);
            if (invite.To.Length > 0)
            {
                this.Relay(from, callId, invite);
                return;
            }

            // for the host - the caller is whoever owns this link, whatever it claims
            packet = CallProtocol.Invite(callId, invite with { From = from.Id });
        }

        await this.calls.OnPacket(from, typeof(THub), packet).ConfigureAwait(false);
    }


    void Relay(MediaLink from, Guid callId, CallInvite invite)
    {
        if (invite.To == from.Id || !this.links.TryGetValue(invite.To, out var target))
        {
            from.Send(CallProtocol.Decline(callId, CallEndReason.Unreachable, $"{invite.To} isn't connected"));
            return;
        }

        if (!this.routes.TryAdd(callId, (from, target)))
            return;

        // the callee is asked for the slower leg's quality, and answers with it
        var profile = invite.Profile == CallProfile.Ble || from.Profile == CallProfile.Ble || target.Profile == CallProfile.Ble
            ? CallProfile.Ble
            : CallProfile.Wifi;

        target.Send(CallProtocol.Invite(callId, invite with { To = "", From = from.Id, Profile = profile }));
        this.logger?.LogInformation("Relaying call {Call} from {From} to {To}", callId, from.Id, invite.To);
    }


    // a call's video was dropped on its way down this link: ask whoever is sending it for a key frame
    void OnKeyFrameNeeded(MediaLink link, Guid callId)
    {
        if (this.routes.TryGetValue(callId, out var route))
            (link == route.A ? route.B : route.A).Send(CallProtocol.KeyFrameRequest(callId));
        else
            this.calls.OnKeyFrameNeeded(callId);
    }


    async Task Detach(MediaLink link)
    {
        if (this.links.TryGetValue(link.Id, out var current) && current == link)
            this.links.TryRemove(link.Id, out _);

        foreach (var (callId, route) in this.routes)
        {
            if (route.A != link && route.B != link)
                continue;

            if (this.routes.TryRemove(callId, out _))
                (route.A == link ? route.B : route.A).Send(CallProtocol.HangUp(callId, CallEndReason.Dropped, "The other side lost its connection"));
        }

        await this.calls.OnLinkClosed(link).ConfigureAwait(false);
        await link.DisposeAsync().ConfigureAwait(false);
    }
}
