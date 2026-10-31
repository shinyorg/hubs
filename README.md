# Shiny.UniversalHubs

**[Shiny.BluetoothLE.Hubs](README.BluetoothLE.Hubs.md) over Wi-Fi as well as Bluetooth LE.** Both libraries live in this
repo and release together. Write the hub
once. Players on a plane connect over BLE. At home they connect over Wi-Fi, which has more range and bandwidth and no MTU.
Both kinds of client share the same hub, groups and pushes.

- **Host**: one `UniversalHub<T>` is served over GATT and over a [Shiny.Net.HttpServer](https://github.com/shinyorg/httpserver)
  Switchboard (SSE + POST, with resume), and advertised over mDNS under your app's service type.
- **Client**: one discovery list merges BLE scanning and mDNS browsing. Connect prefers Wi-Fi and falls back to BLE. The
  generated proxy, events, `Disconnected` and file methods work the same on either transport.
- **Files**: L2CAP over BLE, plain HTTP over Wi-Fi, behind the same `UploadFile` / `DownloadFile`.
- **Media channels**: blehubs' ordered packet pipe beside each hub connection works over Wi-Fi too (a WebSocket), so live
  audio and video run on either transport. The sample uses it for FaceTime-style calls.
- **Multipeer** (optional, Apple only): add `Shiny.UniversalHubs.Multipeer` and iPhones, iPads and Macs also reach each
  other over Apple's MultipeerConnectivity, at Wi-Fi speed with no shared network at all. See [Multipeer](#multipeer-apple-devices-no-network).

| Package | What it is |
|---|---|
| `Shiny.UniversalHubs.Host` | `AddUniversalHubs().AddHub<THub>()`, `IUniversalHubHost` |
| `Shiny.UniversalHubs.Client` | `AddUniversalHubClient<TContract>()`, `IUniversalHubClient<TContract>` |
| `Shiny.UniversalHubs` | The shared Wi-Fi wire protocol. Comes with both. |
| `Shiny.UniversalHubs.Multipeer` | `server.Multipeer(...)` and `o.Multipeer(...)`: the MultipeerConnectivity transport for iOS, Mac Catalyst and macOS |

## Host

A **universal server** serves its hubs over BLE and HTTP at once. Set up both sides in one call and add each hub inside it,
along with the mDNS service type it's advertised under:

```csharp
services.AddUniversalServer(server => server
    .ServiceUuid(ServiceUuid)                                                    // optional - the one BLE service; clients use the same
    .Ble(o => o.EnableFileTransfers(filesDir))                                   // BleHubHostOptions - L2CAP and HTTP both use it
    .Http(o => o.AuthorizeFile = req => req.FileName.StartsWith("avatar-"))     // UniversalHubHostOptions
    .HttpServer(http => http.Options.Port = 8080)                                // optional - the HttpServer itself
    .AddHub<GameHub>(CharacteristicUuid, "_mygame._tcp", o => o.MaxClients = 6)   // MaxClients/ValidateClient cover both transports
);
```

- **One BLE service:** every hub is a characteristic inside one service: `server.ServiceUuid(...)`, or blehubs'
  `BleHubProtocolOptions.DefaultServiceUuid` unless you set your own. Clients set the same one in their options. That's the only UUID a host advertises, because two 128-bit UUIDs don't fit in a
  BLE advertisement. A BLE scan therefore can't tell which hubs a host is running: joining one that's stopped is refused
  at the handshake. mDNS has no such limit, since each advertisement lists its running hubs.

- **mDNS service type** (`_mygame._tcp`): set per hub. It's what the hub is advertised under and what its clients browse
  for. Hubs that share a type share one advertisement; each distinct type gets its own. iOS hosts list every type in
  `NSBonjourServices`.
- **`server.Endpoint`**: the HTTP base path, `/hubs` by default. Hubs are served at `{Endpoint}/{characteristicUuid}`
  and files at `{Endpoint}/files`. mDNS advertises it, so clients need no setting.
- **The HttpServer** binds every interface on a free port, and mDNS tells clients which port. If you already registered
  the server with `AddShinyHttpServer`, its address and port are left alone.
- **BLE stack:** on Android, iOS and Mac Catalyst, blehubs adds the platform BLE stacks for you: `AddBluetoothLeHosting`
  for the server and `AddBluetoothLE` for clients. Calling them yourself as well is harmless. On Apple, the client's
  `AppleBleConfiguration` turns off iOS's background alerts (`NotifyOnConnection`, `NotifyOnDisconnection`,
  `NotifyOnNotification`), because hubs are used in the foreground. Call `AddBluetoothLE(appleConfig)` *before* the client
  registration to use your own, because the first registration wins. On plain `net10.0` (Linux, tests), register an
  `IBleHostingManager` / `IBleManager` yourself.
- **Registration rules:** `AddUniversalServer` can only be called once and needs at least one hub. It doesn't start
  anything; `IUniversalHubHost` starts and stops both transports.
- **Lower-level API:** blehubs' `AddBleHubServer` + `AddShinyHttpServer(http => http.AddUniversalHubs().AddHub<THub>())`
  still works, and it's what the universal server calls.

```csharp
public class Lobby(IUniversalHubHost host)
{
    // BLE, Wi-Fi or both - whichever this device can do right now
    public Task<HubTransports> Open() => host.Start(HubTransports.All);
    public Task Close() => host.Stop("The host ended the game");
}
```

Derive the hub from `UniversalHub<TContract>`. Its lifecycle hooks get a `HubConnection`, which gives one connection id
for both transports and says which transport the client is on:

```csharp
public class GameHub : UniversalHub<IGameHub>
{
    public override Task OnConnectedAsync(HubConnection connection)
    {
        // connection.Id: the peripheral UUID over BLE, sb:{lineId} over Wi-Fi
        // connection.Transport: HubTransports.Ble or HubTransports.Http
        // connection.Client: name, app version, properties, Items
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(HubConnection connection, HubDisconnect disconnect)
    {
        // disconnect.Reason: ClientDisconnect, ClientTimeout, ServerDisconnect, ServerShutdown or ConnectionFailed
        // disconnect.Message: the text given to Context.Abort, Disconnect or Stop, if any
        return Task.CompletedTask;
    }

    public Task MakeMove(int cell) => /* this.Connection is the caller, with its transport */ Task.CompletedTask;
}
```

`HubConnection` also carries what each transport knows about the client. Each property is null when it doesn't apply:

- **`connection.Properties`:** your own `string`→`object` state for the connection, the same bag on both transports
  (blehubs' per-connection `Items`). The client's handshake values are separate, in `connection.Client.Properties`.
- **`connection.Ble`:** the BLE central (`Shiny.BluetoothLE.Hosting.IPeripheral`). Null over HTTP.
- **`connection.Http`:** the Switchboard `CallerContext`.
  - It has the line id, `User`, the line's own `Items`, and `HttpContext` for the request being handled (the connect
    request in `OnConnectedAsync`, the call's request in a hub method).
  - Null over BLE, and in `OnDisconnectedAsync`, because the line's requests are over by then.

`Clients.All`, groups, `IHubContext<THub>`, `MaxClients`, `ValidateClient` and `Context.Abort` cover BLE and Wi-Fi
clients together. `connection.Id` is the same id that `Context.ConnectionId`, `Clients.Client(id)` and `Groups` use, and
`HubConnection.GetTransport(id)` maps any connection id to its transport. A plain `BleHub<T>` still works, but its hooks
can't tell the transports apart.

### One hub at a time

`Start`/`Stop` run every hub. `StartHub<THub>()`/`StopHub<THub>()` run one, on both transports, so an always-on hub can
sit beside one that only runs on demand:

```csharp
await host.StartHub<ChatHub>();                 // at app start, and left running
await host.StartHub<GameHub>();                 // when the player taps Host
await host.StopHub<GameHub>("Game over");       // its clients get ServerShutdown; the chat carries on
var over = host.GetRunningTransports<ChatHub>();
```

- **The HTTP server:** it starts with the first hub hosted over HTTP and stops with the last.
- **mDNS:** it only advertises the hubs that are running.
- **BLE:** it advertises the services of running hubs (blehubs' per-hub start). Give an always-on hub its own service
  UUID if clients scan for the other one, so a scan never lists a host that is only running the always-on hub.

### Tunnels (the internet)

A tunnel exposes the whole HTTP server to the internet, so each hub opts in:

```csharp
services.AddUniversalServer(server => server
    .HttpServer(http => http.AddQuickTunnel())            // Shiny.Net.HttpServer.Ssh - start it from a button
    .AddHub<GameHub>(..., "_mygame._tcp")                  // not reachable through the tunnel
    .AddHub<ChatHub>(..., "_mychat._tcp", o => o.AllowTunnel = true));
```

- **Hubs that don't allow it:** through a tunnel (`ctx.Connection.IsTunneled`), their routes answer 404. Their LAN and
  BLE clients aren't affected.
- **File routes:** open through a tunnel when any hub allows it. The file token still ties each request to a client.
- **Reaching it:** the tunnel's public URL has no mDNS, so a remote client connects by address with
  `client.Connect(HubHostInfo.FromUrl(new Uri("https://abc.a.pinggy.link")))`. `FromUrl` also works for typed addresses
  and QR codes.
- **Authentication:** put it in front of anything you share on the internet.

### Disconnect reasons

Every disconnect carries a `HubDisconnect`: a `HubDisconnectReason` you can switch on, plus an optional message. It
means the same thing on every transport, and the host and the client see the same value:

| Reason | BLE | HTTP | Multipeer |
|---|---|---|---|
| `ClientDisconnect` | the client called `Disconnect()` (it says goodbye before unsubscribing) | the client closed its line | the client said goodbye, then ended its session |
| `ClientTimeout` | an unsubscribe without a goodbye, or the client sweep | the line dropped and its resume window ran out | the session ended without a goodbye |
| `ServerDisconnect` | `Context.Abort` / `IHubContext.Disconnect` | the same | the same |
| `ServerShutdown` | `IUniversalHubHost.Stop`, `IHubContext.Stop` | the same | the same |
| `ConnectionFailed` | the link failed | the line faulted | the connect failed |

- **Host:** `UniversalHub.OnDisconnectedAsync(connection, disconnect)` and `IHubContext.ClientDisconnected` carry it.
- **Client:** `Disconnected`, `StatusChanged` (`e.Disconnect`) and `BleHubDisconnectedException.Disconnect` carry it.
- **Short drops:** a brief HTTP drop that resumes isn't a disconnect at all. Multipeer has no resume: a dropped session
  is a `ClientTimeout`.

## Client

```csharp
services.AddUniversalHubClient<IGameHub>(CharacteristicUuid, "_mygame._tcp",        // the hub's mDNS service type
    o => o.ServiceUuid = ServiceUuid);                                           // the host's BLE service, if it set one
```

```csharp
public class JoinViewModel(IUniversalHubClient<IGameHub> client)
{
    // a host seen over BLE and Wi-Fi is one HubHostInfo; Transports == None means it has gone
    IDisposable Scan() => client.DiscoverAll().Subscribe(host => /* host.Name, host.Transports, host.Rssi */);

    async Task Join(HubHostInfo host)
    {
        await client.Connect(host, new HubConnectOptions("Allan") { Prefer = HubTransports.Http });
        // client.Transport is Http, Multipeer or Ble
        await client.Hub.MakeMove(4);
    }
}
```

- `Prefer` is tried first when the host is reachable more than one way. The rest follow in the order Wi-Fi, Multipeer,
  BLE. `Allowed` limits which transports may be used at all.
- A refusal (host full, `ValidateClient`, not running) is not retried on another transport.
- A host that restarted listens on a new port, and one that was killed never sent mDNS its goodbye, so discovery can
  still hold its old address. When no address answers, the client looks the host up again by name
  (`UniversalHubClientOptions.HttpRediscoverTimeout`, 3 seconds) and retries where it is now, before falling back to BLE.
- `IUniversalHubClient<T>` is an `IBleHubClient<T>`. `Discover()` and `Connect(BleHubHostInfo)` still give BLE-only behavior.
- A short Wi-Fi drop is resumed: the line keeps its id and groups, and missed pushes are replayed. The hub sees no
  disconnect. Turn this off with `UniversalHubClientOptions.AutomaticReconnect = false`.

## Media channels

A media channel is [BluetoothLE Hubs'](README.BluetoothLE.Hubs.md) ordered, two-way packet pipe beside a hub
connection, for live audio and video. Over BLE it's an L2CAP channel. Over Wi-Fi this library carries it as a WebSocket
at `{Endpoint}/media`. The API is blehubs' own, and it works the same on either transport:

```csharp
// host
server.Ble(o => o.EnableMediaChannels());                     // off by default
hubContext.MediaChannelOpened += (_, e) => Serve(e.Client, e.Channel);

// client
if (client.CanOpenMediaChannel)
{
    var channel = await client.OpenMediaChannel();
    await channel.Send(packet);                               // whole packets, in order, up to MaxPacketSize
    await foreach (var p in channel.Receive()) { ... }
}
```

- The handshake reply carries a per-connection media token, and the WebSocket presents it. Nobody else can open
  another client's channel.
- The channel closes when the client disconnects, the hub stops or either side disposes it.
- Through a tunnel, a hub's channels open only if that hub allows tunnels.
- On devices, the WebSocket uses its own sockets, because the platform HTTP handlers can't upgrade.

## Multipeer (Apple devices, no network)

`Shiny.UniversalHubs.Multipeer` adds Apple's MultipeerConnectivity as a third transport. MultipeerConnectivity picks the
radio itself: the Wi-Fi network if both devices are on one, peer-to-peer Wi-Fi (AWDL) if not, Bluetooth if Wi-Fi is off.
Two iPhones on a plane get Wi-Fi bandwidth instead of BLE's, and no MTU. Android can't use it: Android players keep using
Wi-Fi or BLE against the same host, in the same hub.

```csharp
// host - one Multipeer service type per host, like the BLE service UUID
services.AddUniversalServer(server => server
    .Multipeer("mygame-mc")
    .AddHub<GameHub>(CharacteristicUuid, "_mygame._tcp"));

// client
services.AddUniversalHubClient<IGameHub>(CharacteristicUuid, "_mygame._tcp", o => o.Multipeer("mygame-mc"));
```

- **The service type** is 1-15 lowercase letters, digits and hyphens, and must not be the name in a hub's mDNS type
  (`mygame` for `_mygame._tcp`): Multipeer advertises over Bonjour too. Every running hub is listed in the one
  advertisement, with the same keys as the mDNS TXT record.
- **Info.plist** needs `_mygame-mc._tcp` and `_mygame-mc._udp` in `NSBonjourServices`, and an
  `NSLocalNetworkUsageDescription`. Without them iOS finds nothing and says nothing. The library logs a warning at start
  when either is missing.
- **`HubTransports.All` includes Multipeer.** `Start()` hosts over it when the package is set up and skips it where
  MultipeerConnectivity doesn't exist (Android, Windows, Linux), so the same registration works in a cross-platform app.
- **Discovery and connect:** `DiscoverAll` merges Multipeer hosts with BLE and mDNS ones by name
  (`HubHostInfo.Multipeer`). `Connect` tries Multipeer after Wi-Fi and before BLE.
- **One session per hub connection.** The host accepts each invitation into its own `MCSession`, so MultipeerConnectivity's
  8-peer limit never applies and clients never connect to each other. Sessions are always encrypted.
- **What the hub sees:** connection ids are `mc:{id}`, `connection.Transport` is `Multipeer`, and `connection.Multipeer`
  has the peer's display name (`MultipeerClientOptions.DisplayName`).
- **Values travel as binary**, exactly as `IBleHubSerializer` wrote them, like BLE. Any serializer works, not only JSON.
- **Renames work** (`client.Rename`, `HostRenamed`), as they do over BLE.
- **Files** move as Multipeer resources, under the same `EnableFileTransfers` and `AuthorizeFile` rules as HTTP.
  **Media channels** ride the session as reliable, ordered messages.
- **No resume.** MultipeerConnectivity sessions end when the app goes to the background. A dropped session is a
  disconnect (`ClientTimeout`); reconnect as you would over BLE.

## Platform setup

| | Host | Client |
|---|---|---|
| iOS / macOS | `NSBluetoothAlwaysUsageDescription`, `NSLocalNetworkUsageDescription`, `NSBonjourServices` = your hubs' mDNS service types, plus `_{type}._tcp` and `_{type}._udp` for the Multipeer service type | the same, plus `NSAppTransportSecurity` / `NSAllowsLocalNetworking` (games are plain HTTP on the LAN) |
| Android | BLE permissions, `INTERNET`, `ACCESS_NETWORK_STATE`, `ACCESS_WIFI_STATE`, `CHANGE_WIFI_MULTICAST_STATE` | the same, plus `android:usesCleartextTraffic="true"` |

## Things to know

- **Plane, hotel and public Wi-Fi usually isolate clients from each other.** mDNS and device-to-device traffic are
  blocked there, which is why BLE is the fallback. Between Apple devices, Multipeer works there too.
- Hosting runs while the app is in the foreground, the same as BLE hosting.
- Hub values are carried as JSON over HTTP. They are re-framed, never re-serialized, so `IBleHubSerializer` must produce
  JSON (the default does). BLE and Multipeer carry them as-is.
- The host still needs a BLE hosting stack registered (`AddBluetoothLeHosting`) even if Bluetooth is off. Wi-Fi-only
  hosting works on a device whose Bluetooth is off or unavailable.
- An mDNS TXT record fits about six hubs per host.

## Sample

`samples/Sample` is the Shiny.BluetoothLE.Hubs tic-tac-toe game, hosting over every transport (BLE, Wi-Fi, and
Multipeer between iPhones and Macs), plus an always-on chat:

- **Game:** each hosted game is its own Shiny.Actors `GameActor`. `GameHub` runs only while hosting
  (`StartHub`/`StopHub<GameHub>`). The join list shows how each game is reachable, and the game screen says which
  transport is in use.
- **Chat:**
  - `ChatHub` starts with the app and stays open, so anyone nearby, on the network or (through the tunnel) on the
    internet can message the device's owner.
  - Each conversation is a `ConversationActor` kept on disk. New messages arrive on its actor stream and in Shiny.Maui.Controls' `ChatView`.
  - Incoming messages raise Shiny.Notifications local notifications.
  - Photos (ChatView's attach button, opened in its image viewer when tapped) and files ("📎 Send a file") move over the
    hub's file transfers: L2CAP over BLE, HTTP over Wi-Fi. A visitor uploads to the chat's owner; the owner's files are
    downloaded by the visitor.
  - "Share on the internet" starts a quick tunnel. Only `ChatHub` allows it.
- **People:** one list of everyone you can reach (nearby, on the network, visiting you, past chats), each with 💬 / 📞 / 📹.
  Tapping one connects first if it has to.
- **Calls:** 📞 / 📹 FaceTime the other person, over Wi-Fi or Bluetooth (`samples/Sample/Calls`). It's app code
  built on the libraries:
  - media channels carry the call. The host relays calls between its clients.
  - Shiny.Audio's `IAudioDuplex` plays and captures echo-cancelled voice.
  - Shiny Controls' `CameraView` + `VideoEncoder` film, and `RemoteVideoView` shows the other side. Video is H.264 when
    both devices have it, otherwise JPEG. People → "More options" → "Video codec for calls" forces H.264 or JPEG, so you can
    try both. On the call screen, 📷 picks the camera and 🎤 the microphone, before answering or mid-call.
  - Quality follows the link: 640 px at 15 fps with PCM16 over Wi-Fi; 160 px at 3 fps with IMA ADPCM over Bluetooth.
- **`samples/Sample.MacOS`:** the same app for macOS (AppKit), on .NET MAUI's AppKit backend from dotnet/maui-labs.
  - It links the whole phone app (XAML app, Shell, pages, hubs, game and services), so it is the same full host and
    client. It hosts its chat and games over BLE and Wi-Fi, and joins anyone else's. Only its startup is its own.
  - blehubs doesn't register BLE stacks on macOS, so the app registers Shiny's macOS builds itself (`AddBluetoothLE`,
    `AddBluetoothLeHosting`). It starts Shiny with `UseShiny()`, like the phones.

```
dotnet build samples/Sample -f net10.0-android -t:Run
dotnet build samples/Sample -f net10.0-ios -t:Run
dotnet build samples/Sample.MacOS -t:Run
```

> Shiny.Actors isn't on nuget.org yet; the sample references a local build (`ActorsVersion` in
> `Directory.Packages.props`) until it is.

## Commercial Support

Shipping this to production? [ShinySoft Technologies](https://shinysoft.biz), the company that builds and maintains the Shiny libraries, offers priority issue response, support agreements, architecture reviews and private office hours. See [Commercial Support](https://shinylib.net/foundation/support/?utm_source=github&utm_medium=readme#commercial-support) or [get in touch](https://shinysoft.biz/contact?utm_source=github&utm_medium=readme). Community support through GitHub issues and discussions stays free.
