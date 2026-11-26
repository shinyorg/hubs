# Shiny.UniversalHubs

**SignalR-style hubs between nearby devices, over any transport.** Write a hub once and serve it over Bluetooth LE,
HTTP (Wi-Fi, LAN or the internet through a tunnel), Apple's MultipeerConnectivity and Google's Nearby Connections at the
same time - or over a transport of your own. Every client shares the same hub, groups and pushes, whichever way it
connected: players on a plane connect over BLE or Nearby, at home over Wi-Fi.

- A contract interface, a source-generated client proxy, typed host-to-client pushes, groups, streaming.
- Each hub picks its transports, its own serializer, and (over BLE) its own GATT service.
- Clients find hosts over every transport in one list and connect over the first that works, in an order you choose.
- Resume after a dropped link, file transfers and media channels (live audio/video packet pipes) on every transport.
- Hubs defined while the app runs (dynamic hubs).
- AOT and trim safe - no reflection.

## Packages

Every package targets .NET 11 (`net11.0`), with platform builds where they need one.

| Package | NuGet | Platforms | What it is |
|---|---|---|---|
| `Shiny.UniversalHubs` | [![NuGet](https://img.shields.io/nuget/v/Shiny.UniversalHubs.svg)](https://www.nuget.org/packages/Shiny.UniversalHubs) | .NET | Hubs, the host (`AddUniversalServer`, `IUniversalHubHost`, `IHubContext<THub>`), the client (`AddUniversalHubClient`, `IUniversalHubClient<T>`), the transport seams (`IHubTransport`, `IHubClientTransport`), the wire protocols and the source generator. |
| `Shiny.UniversalHubs.BluetoothLE` | [![NuGet](https://img.shields.io/nuget/v/Shiny.UniversalHubs.BluetoothLE.svg)](https://www.nuget.org/packages/Shiny.UniversalHubs.BluetoothLE) | .NET, Android, iOS, Mac Catalyst | Hubs as GATT characteristics, files and media channels over L2CAP: `UseBluetoothLE(...)`. Registers the platform BLE stacks. |
| `Shiny.UniversalHubs.Http` | [![NuGet](https://img.shields.io/nuget/v/Shiny.UniversalHubs.Http.svg)](https://www.nuget.org/packages/Shiny.UniversalHubs.Http) | .NET, Android, iOS, Mac Catalyst | Hubs as WebSockets on [Shiny.Net.HttpServer](https://github.com/shinyorg/httpserver), found over mDNS, reachable through tunnels: `UseHttp(...)`. |
| `Shiny.UniversalHubs.Multipeer` | [![NuGet](https://img.shields.io/nuget/v/Shiny.UniversalHubs.Multipeer.svg)](https://www.nuget.org/packages/Shiny.UniversalHubs.Multipeer) | iOS, Mac Catalyst, macOS | Apple devices with no shared network, over MultipeerConnectivity: `UseMultipeer(...)`. |
| `Shiny.UniversalHubs.Nearby` | [![NuGet](https://img.shields.io/nuget/v/Shiny.UniversalHubs.Nearby.svg)](https://www.nuget.org/packages/Shiny.UniversalHubs.Nearby) | Android, iOS, macOS | Android, iPhones and Macs with no shared network, over Google's Nearby Connections: `UseNearby(...)`. |

Add the core and the transports you use. The transport packages also build for plain .NET, where a transport the
platform doesn't have reports itself unsupported and is skipped, so one registration works in a cross-platform app.

> Coming from `Shiny.BluetoothLE.Hubs`? It is replaced by `Shiny.UniversalHubs` + `Shiny.UniversalHubs.BluetoothLE`.
> See [Upgrading from Shiny.BluetoothLE.Hubs](#upgrading-from-shinybluetoothlehubs).

## A hub

```csharp
// the contract - shared by host and client
[UniversalHubContract]
public interface IGameHub
{
    Task<bool> Move(int cell);
    IAsyncEnumerable<Score> Scores(CancellationToken cancellationToken);
    Task JoinTeam(string team);

    event Action<GameState> StateChanged;      // pushed by the host
}

// the host's implementation - a new instance, in its own DI scope, for every call
public class GameHub(IGameStore store) : UniversalHub<IGameHub>
{
    public async Task<bool> Move(int cell)
    {
        var state = await store.Apply(this.Context.ConnectionId, cell);
        await this.Clients.All.StateChanged(state);          // a generated, typed push
        return true;
    }

    public async IAsyncEnumerable<Score> Scores([EnumeratorCancellation] CancellationToken cancellationToken) { ... }

    public Task JoinTeam(string team) => this.Groups.AddToGroupAsync(this.Context.ConnectionId, team);

    public override Task OnConnectedAsync(HubConnection connection) => ...;   // which transport: connection.Transport
    public override Task OnDisconnectedAsync(HubConnection connection, HubDisconnect disconnect) => ...;
}
```

Push from outside a hub with `IHubContext<GameHub>`: `context.Clients.Group("red").StateChanged(state)`,
`ConnectedClients`, `Disconnect(id)`, `MediaChannelOpened`, `Start()` / `Stop()`.

## Host

```csharp
services.AddUniversalServer(server => server
    .Configure(o =>
    {
        o.LocalName = "Allan's iPad";                              // advertised by every transport that advertises
        o.EnableFileTransfers(filesDir, ft => ft.MaxUploadSize = 25 * 1024 * 1024);
        o.AuthorizeFile = req => req.FileName.StartsWith("avatar-");   // every transport
        o.EnableMediaChannels();
    })
    .Multipeer("mygame-mc")                       // transport-wide settings, where a transport has them
    .Nearby("com.mycompany.mygame")
    .AddHub<GameHub>("game", hub => hub
        .MaxClients(6)                            // counted across every transport
        .ValidateClient(info => info.Name == null ? "A name is required" : null)
        .UseHttp("_mygame._tcp")
        .UseMultipeer()
        .UseNearby()
        .UseBluetoothLE(serviceUuid: "180D", characteristicUuid: "6e400002-b5a3-f393-e0a9-e50e24dcca9e"))
    .AddHub<ChatHub>("chat", hub => hub.UseHttp("_mychat._tcp", allowTunnel: true)));

var host = services.GetRequiredService<IUniversalHubHost>();
await host.Start();                                 // every hub on every transport it picked
await host.Stop<BleHostTransport>();                // one transport off; the others keep their clients
await host.StartHub<GameHub, NearbyHostTransport>();
```

- **A hub's name** is how clients ask for it on every transport: 1-64 letters, digits, `.`, `_`, `-`.
- **Registration mistakes throw at registration:** a hub with no transport, a duplicate name, the same transport twice,
  a transport set up that no hub uses, BLE services that don't fit in the advertisement.
- **A transport that can't start** (Bluetooth off, no network, not this platform) is skipped as long as another starts;
  `Start` returns the transports that did. `RunningTransports`, `GetRunningTransports<THub>()` and `RunningChanged` say
  what is running.
- **In a hub,** `connection.Transport is BleHostTransport` says how a client is connected, and transport details come
  from extensions: `connection.Peripheral()` (BLE), `connection.HttpContext()` (the WebSocket's request: user, headers),
  `connection.MultipeerPeer()`, `connection.NearbyPeer()`.

### Bluetooth LE

```csharp
hub.UseBluetoothLE(serviceUuid, characteristicUuid, advertise: true);
server.BluetoothLE(o => { o.SecureFileTransfers = true; o.ClientSweepInterval = TimeSpan.FromSeconds(10); });
```

- **Each hub is a characteristic in a GATT service of its choosing.** Hubs may share a service or have their own.
- **The advertisement holds 31 bytes.** The advertised services of every hub, running or not, must fit, or registration
  throws: one 128-bit UUID fits beside a short name; two don't. Use 16-bit or 32-bit UUIDs (`"180D"`, `"ABCD1234"`,
  expanded onto the Bluetooth base UUID, 2 or 4 bytes on air), share a service, or set `advertise: false`. Short UUIDs the
  Bluetooth SIG hasn't assigned to you work, but aren't compliant.
- **A running hub's service is advertised;** a stopped hub's service leaves the advertisement unless another running hub
  shares it.
- **A hub that isn't advertised** is found by scanning for another service the host does advertise (the client's
  `scanFor`), or over another transport. The host logs this once per hub.
- Files and media channels go over L2CAP. On plain .NET (and the macOS AppKit head) register `IBleManager` /
  `IBleHostingManager` yourself.

### HTTP

```csharp
hub.UseHttp(mdnsServiceType: "_mygame._tcp", allowTunnel: false);
server.Http(o => { o.BasePath = "/hubs"; o.Advertise = true; o.ManageHttpServer = true; });
server.HttpServer(http => http.AddQuickTunnel());   // the HttpServer itself: port, TLS, middleware, tunnels
```

- **Each hub connection is a WebSocket** at `{BasePath}/{hub name}`, carrying the same hub session as Multipeer and Nearby:
  calls, streams, pushes, resume, files and media channels, all in-band.
- **mDNS** advertises each service type once, listing the hubs under it. A TXT record fits about six hub names.
- **Tunnels:** through a tunnel (the public internet), a hub that doesn't allow it answers 404. Put authentication in front
  of anything you share - `connection.HttpContext()` has the request's user.
- Clients connect to an address you have with `HttpHubHost.FromUrl(new Uri("https://abc.a.pinggy.link"))`.

### Multipeer (Apple devices, no network)

```csharp
server.Multipeer("mygame-mc");   hub.UseMultipeer();              // host
o.UseMultipeer("mygame-mc");                                      // client
```

MultipeerConnectivity picks the radio: the Wi-Fi network, peer-to-peer Wi-Fi (AWDL) or Bluetooth. Two iPhones on a plane
get Wi-Fi bandwidth. One session per hub connection, so its 8-peer limit never applies. The service type is 1-15
lowercase letters, digits and hyphens, and must not be the name in a hub's mDNS type. Info.plist needs
`_mygame-mc._tcp` and `_mygame-mc._udp` in `NSBonjourServices`, and `NSLocalNetworkUsageDescription`. Elsewhere it
reports itself unsupported.

### Nearby (Android, iOS and macOS, no network)

```csharp
server.Nearby("com.mycompany.mygame");   hub.UseNearby();         // host
o.UseNearby("com.mycompany.mygame");                              // client
```

Nearby Connections picks the medium: Bluetooth, BLE, Wi-Fi LAN, Wi-Fi Direct, a hotspot. One service id per app (one
advertisement and one discovery per app), one connection per pair of devices with every hub session a channel inside it.
Android needs Google Play services; on iOS / macOS the package carries Google's NearbyConnections as a native framework.
Info.plist needs `NearbyProtocol.BonjourServices(serviceId)` in `NSBonjourServices`, `NSLocalNetworkUsageDescription`
and `NSBluetoothAlwaysUsageDescription`. Building from source: run `native/ShinyNearbyBridge/build.sh` first.

### A transport of your own

A transport is a class, and its type is its identity:

```csharp
public sealed class LoraHostTransport : IHubTransport
{
    public string DisplayName => "LoRa";
    public bool IsSupported => true;
    public bool IsRunning(IHubBinding hub) => ...;
    public Task Start(IReadOnlyList<IHubBinding> hubs, CancellationToken ct) => ...;   // hub.GetSettings<LoraSettings>()
    public Task Stop(IReadOnlyList<IHubBinding> hubs, HubDisconnect disconnect) => ...;
}

server.AddHub<GameHub>("game", hub => hub.UseTransport<LoraHostTransport>(new LoraSettings(channel: 7)));
```

It reaches each hub through `IHubBinding.Endpoint` (`IHubEndpoint`: `Connect`, `Invoke`, `Stream`, `Disconnected`,
`Resume`), handing it an `IHubPeerChannel` per client. The client side is an `IHubClientTransport`
(`Discover`, `Connect` through `HubClientContext.Connect`), added with `o.UseTransport(sp => new LoraClientTransport(...))`.

### Serializers

Every hub uses the app's `IHubSerializer` (`JsonHubSerializer` unless you register another), or its own:

```csharp
hub.Serializer(new MessagePackHubSerializer());        // host
o.Serializer(new MessagePackHubSerializer());          // client
```

The serializer's `Name` travels in the handshake, and a hub refuses a client whose serializer has another name. Values
travel as the serializer wrote them, on every transport. Dynamic hubs are JSON.

### Dynamic hubs

Hubs defined while the app runs - by a downloaded web app, say:

```csharp
server.AllowDynamicHubs(o => o.MaxHubs = 6)
      .Http(o => o.DynamicServiceTypes.Add("_mygame._tcp"));

var hub = dynamicHubs.Add(
    new DynamicHubDefinition("trivia") { MaxClients = 8 }
        .UseHttp("_mygame._tcp")
        .UseBluetoothLE("6e400010-b5a3-f393-e0a9-e50e24dcca9e"),
    handler                                              // IDynamicHubHandler: methods by name, values as JSON
);
await hub.Start();
await hub.Clients.All.Send("Question", [json]);
```

Each transport decides whether it serves one (`IDynamicHubTransport`): BLE only when given a characteristic, HTTP with
its defaults either way, Multipeer and Nearby always. Clients without a compiled contract use
`services.AddDynamicHubClients(o => o.UseHttp())` and `dynamicClients.Create("trivia", o => o.UseHttp("_mygame._tcp"))`.

## Client

```csharp
services.AddUniversalHubClient<IGameHub>("game", o => o
    .UseHttp("_mygame._tcp", h => h.ConnectTimeout = TimeSpan.FromSeconds(4))
    .UseMultipeer("mygame-mc")
    .UseNearby("com.mycompany.mygame")
    .UseBluetoothLE("180D", "6e400002-b5a3-f393-e0a9-e50e24dcca9e")
    .Priority<NearbyClientTransport, HttpClientTransport>());   // optional - the Use… order otherwise

var client = services.GetRequiredService<IUniversalHubClient<IGameHub>>();
using var scan = client.DiscoverAll().Subscribe(host => ...);   // one list, merged across transports
await client.Connect(host, new HubConnectOptions("Allan"));
await client.Hub.Move(4);
client.Hub.StateChanged += state => ...;
```

- **Discovery:** a host seen several ways is one `HubHostInfo`. `host.Transports` lists the client transport types it is
  seen on, in priority order; `host.Endpoint<BleHubEndpoint>()?.Rssi` and friends have each transport's details. A host
  with no transports left has gone.
- **Connecting** tries the transports the host is seen on, in priority order, falling back to the next when one fails.
  A refusal (full, `ValidateClient`, serializer mismatch) is not retried. Per connect:
  `new HubConnectOptions("Allan").Prefer<BleClientTransport>()`, `.Allow<HttpClientTransport>()`, `.Only<NearbyClientTransport>()`.
- **While the app runs:** `client.Disable<BleClientTransport>()`, `Enable<T>()`, `Solo<T>()`, `IsEnabled<T>()` and
  `EnabledChanged`. A disabled transport leaves running discoveries and is never connected over; disabling the one a
  connection is on disconnects it. `client.Transport` is the transport carrying the connection.
- **Everything else** - the proxy, events, `Status`, `Disconnected`, `Rename`, `UploadFile` / `DownloadFile`,
  `OpenMediaChannel` - works the same on every transport.

## Dropped links and resume

A client whose link drops comes back as the same connection on every transport: same connection id, groups and
`Properties`, with the pushes it missed replayed. The host holds a dropped client for `UniversalServerOptions.ResumeWindow`
(30 seconds) and keeps up to `ResumeBufferSize` (128) pushes for it; the client reconnects by itself and its status is
`Reconnecting` until it is back. `OnDisconnectedAsync` runs (`ClientTimeout`) only if the window runs out. Calls in flight
fail (`HubDisconnectedException`), and media channels and file transfers close. A client resumes on the transport it
dropped from.

## Disconnect reasons

Every disconnect carries a `HubDisconnect`: a `HubDisconnectReason` (`ClientDisconnect`, `ClientTimeout`,
`ServerDisconnect`, `ServerShutdown`, `ConnectionFailed`) and an optional message, the same on every transport and on both
sides. The host sees it in `OnDisconnectedAsync` and `IHubContext.ClientDisconnected`; the client in `Disconnected`,
`StatusChanged` and `HubDisconnectedException.Disconnect`.

## Media channels

An ordered, two-way packet pipe beside a hub connection, for live audio and video - L2CAP over BLE, the hub session itself
over HTTP, Multipeer and Nearby:

```csharp
server.Configure(o => o.EnableMediaChannels());                    // host, off by default
hubContext.MediaChannelOpened += (_, e) => Serve(e.Client, e.Channel);

var channel = await client.OpenMediaChannel();                     // client
await channel.Send(packet);
await foreach (var p in channel.Receive()) { ... }
```

## Platform setup

| | Host | Client |
|---|---|---|
| iOS / macOS | `NSBluetoothAlwaysUsageDescription`, `NSLocalNetworkUsageDescription`, `NSBonjourServices` = your hubs' mDNS service types, `_{type}._tcp` / `_{type}._udp` for Multipeer, `NearbyProtocol.BonjourServices(serviceId)` for Nearby | the same, plus `NSAppTransportSecurity` / `NSAllowsLocalNetworking` (plain HTTP on the LAN) |
| Android | BLE permissions, `INTERNET`, `ACCESS_NETWORK_STATE`, `ACCESS_WIFI_STATE`, `CHANGE_WIFI_MULTICAST_STATE`; Nearby adds its own | the same, plus `android:usesCleartextTraffic="true"` |

## Things to know

- **Plane, hotel and public Wi-Fi usually isolate clients from each other** - mDNS and device-to-device traffic are
  blocked. BLE, Multipeer and Nearby work there.
- Hubs are foreground only: no background BLE, no state restoration.
- Four versioned wires: BLE frames (`FrameCodec.ProtocolVersion`), the peer-link session over HTTP, Multipeer and Nearby
  (`PeerProtocol.Version`), the HTTP routes and mDNS TXT keys (`UniversalHubProtocol.Version`). The handshake refuses a
  mismatch.

## Upgrading from Shiny.BluetoothLE.Hubs

| Before | Now |
|---|---|
| `Shiny.BluetoothLE.Hubs`, `.Host`, `.Client`, `Shiny.UniversalHubs.Host`, `.Client` | `Shiny.UniversalHubs` + `Shiny.UniversalHubs.BluetoothLE` (+ `.Http`, `.Multipeer`, `.Nearby`) |
| `namespace Shiny.BluetoothLE.Hubs` | `namespace Shiny.UniversalHubs` |
| `[BleHubClient]`, `BleHub<T>` | `[UniversalHubContract]`, `UniversalHub<T>` (`OnConnectedAsync(HubConnection)`) |
| `AddBleHubServer`, `AddHub<THub>(characteristic, mdnsType, o => ...)` | `AddUniversalServer(s => s.AddHub<THub>("name", h => h.UseBluetoothLE(service, characteristic).UseHttp(type)))` |
| `AddBleHubClient<T>`, `AddUniversalHubClient<T>(characteristic, mdnsType, ...)` | `AddUniversalHubClient<T>("name", o => o.UseBluetoothLE(service, characteristic).UseHttp(type))` |
| `IBleHubClient<T>`, `IBleHubConnection`, `IBleHubSerializer`, `BleHubException`, `BleHubRemoteException`, `IBleHubMediaChannel` | `IUniversalHubClient<T>`, `IHubConnection`, `IHubSerializer`, `HubException`, `HubRemoteException`, `IHubMediaChannel` |
| `HubTransports` flags | transport types: `host.Start<BleHostTransport>()`, `HubConnectOptions.Prefer<HttpClientTransport>()` |
| `HubHostInfo.Ble` / `.Http` / `.Multipeer` / `.Nearby` | `host.Endpoint<BleHubEndpoint>()`, `host.IsSeenOver<HttpClientTransport>()` |
| `connection.Ble` / `.Http` / `.Multipeer` / `.Nearby` | `connection.Peripheral()`, `connection.HttpContext()`, `connection.MultipeerPeer()`, `connection.NearbyPeer()` |
| one BLE service for every hub | a service per hub, `advertise:` flag, 16/32-bit UUIDs |
| HTTP over a Switchboard (SSE + POST, file and media routes) | a WebSocket per hub connection, everything in-band |
| `SBH0xx` diagnostics | `SUH0xx` |

## Sample

`samples/Sample` (MAUI: Android, iOS) and `samples/Sample.MacOS` (AppKit) are a tic-tac-toe game and an always-on chat
with calls, hosting and joining over every transport. The **Transports** page turns each transport on and off for hosting
and connecting, and **Solo** runs one alone both ways, so each can be tested on its own.

```
dotnet build samples/Sample -f net11.0-android -t:Run
dotnet build samples/Sample -f net11.0-ios -t:Run
dotnet build samples/Sample.MacOS -t:Run
```

## Commercial Support

Shipping this to production? [ShinySoft Technologies](https://shinysoft.biz), the company that builds and maintains the Shiny libraries, offers priority issue response, support agreements, architecture reviews and private office hours. See [Commercial Support](https://shinylib.net/foundation/support/?utm_source=github&utm_medium=readme#commercial-support) or [get in touch](https://shinysoft.biz/contact?utm_source=github&utm_medium=readme). Community support through GitHub issues and discussions stays free.
