# Shiny.UniversalHubs

**[Shiny.BluetoothLE.Hubs](https://github.com/shinyorg/bluetoothle-hubs) over Wi-Fi as well as Bluetooth LE.** Write the hub
once. Players on a plane connect over BLE. At home they connect over Wi-Fi, which has more range and bandwidth and no MTU.
Both kinds of client share the same hub, groups and pushes.

- **Host**: one `UniversalHub<T>` is served over GATT and over a [Shiny.Net.HttpServer](https://github.com/shinyorg/httpserver)
  Switchboard (SSE + POST, with resume), and advertised over mDNS under your app's service type.
- **Client**: one discovery list merges BLE scanning and mDNS browsing. Connect prefers Wi-Fi and falls back to BLE. The
  generated proxy, events, `Disconnected` and file methods work the same on either transport.
- **Files**: L2CAP over BLE, plain HTTP over Wi-Fi, behind the same `UploadFile` / `DownloadFile`.

| Package | What it is |
|---|---|
| `Shiny.UniversalHubs.Host` | `AddUniversalHubs().AddHub<THub>()`, `IUniversalHubHost` |
| `Shiny.UniversalHubs.Client` | `AddUniversalHubClient<TContract>()`, `IUniversalHubClient<TContract>` |
| `Shiny.UniversalHubs` | The shared Wi-Fi wire protocol. Comes with both. |

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
means the same thing on both transports, and the host and the client see the same value:

| Reason | BLE | HTTP |
|---|---|---|
| `ClientDisconnect` | the client called `Disconnect()` (it says goodbye before unsubscribing) | the client closed its line |
| `ClientTimeout` | an unsubscribe without a goodbye, or the client sweep | the line dropped and its resume window ran out |
| `ServerDisconnect` | `Context.Abort` / `IHubContext.Disconnect` | the same |
| `ServerShutdown` | `IUniversalHubHost.Stop`, `IHubContext.Stop` | the same |
| `ConnectionFailed` | the link failed | the line faulted |

- **Host:** `UniversalHub.OnDisconnectedAsync(connection, disconnect)` and `IHubContext.ClientDisconnected` carry it.
- **Client:** `Disconnected`, `StatusChanged` (`e.Disconnect`) and `BleHubDisconnectedException.Disconnect` carry it.
- **Short drops:** a brief HTTP drop that resumes isn't a disconnect at all.

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
        // client.Transport is Http or Ble
        await client.Hub.MakeMove(4);
    }
}
```

- `Prefer` is tried first when the host is reachable both ways, and the other transport is the fallback. `Allowed`
  limits which transports may be used at all.
- A refusal (host full, `ValidateClient`, not running) is not retried on the other transport.
- `IUniversalHubClient<T>` is an `IBleHubClient<T>`. `Discover()` and `Connect(BleHubHostInfo)` still give BLE-only behavior.
- A short Wi-Fi drop is resumed: the line keeps its id and groups, and missed pushes are replayed. The hub sees no
  disconnect. Turn this off with `UniversalHubClientOptions.AutomaticReconnect = false`.

## Platform setup

| | Host | Client |
|---|---|---|
| iOS | `NSBluetoothAlwaysUsageDescription`, `NSLocalNetworkUsageDescription`, `NSBonjourServices` = your hubs' mDNS service types | the same, plus `NSAppTransportSecurity` / `NSAllowsLocalNetworking` (games are plain HTTP on the LAN) |
| Android | BLE permissions, `INTERNET`, `ACCESS_NETWORK_STATE`, `ACCESS_WIFI_STATE`, `CHANGE_WIFI_MULTICAST_STATE` | the same, plus `android:usesCleartextTraffic="true"` |

## Things to know

- **Plane, hotel and public Wi-Fi usually isolate clients from each other.** mDNS and device-to-device traffic are
  blocked there, which is why BLE is the fallback.
- Hosting runs while the app is in the foreground, the same as BLE hosting.
- Hub values are carried as JSON over Wi-Fi. They are re-framed, never re-serialized, so `IBleHubSerializer` must produce
  JSON (the default does).
- The host still needs a BLE hosting stack registered (`AddBluetoothLeHosting`) even if Bluetooth is off. Wi-Fi-only
  hosting works on a device whose Bluetooth is off or unavailable.
- An mDNS TXT record fits about six hubs per host.

## Sample

`samples/Sample` is the Shiny.BluetoothLE.Hubs tic-tac-toe game, hosting over both transports, plus an always-on chat:

- **Game:** each hosted game is its own Shiny.Actors `GameActor`. `GameHub` runs only while hosting
  (`StartHub`/`StopHub<GameHub>`). The join list shows how each game is reachable, and the game screen says which
  transport is in use.
- **Chat:**
  - `ChatHub` starts with the app and stays open, so anyone nearby, on the network or (through the tunnel) on the
    internet can message the device's owner.
  - Each conversation is a `ConversationActor` kept on disk. New messages arrive on its actor stream and in Shiny.Maui.Controls' `ChatView`.
  - Incoming messages raise Shiny.Notifications local notifications.
  - "Share on the internet" starts a quick tunnel. Only `ChatHub` allows it.
- **`samples/Sample.MacOS`:** a macOS (AppKit) client on .NET MAUI's AppKit backend from dotnet/maui-labs. It links
  the hub contracts, finds people and games over mDNS (or a shared address), and chats and plays over HTTP.

```
dotnet build samples/Sample -f net10.0-android -t:Run
dotnet build samples/Sample -f net10.0-ios -t:Run
dotnet build samples/Sample.MacOS -t:Run
```

> Shiny.Actors isn't on nuget.org yet; the sample references a local build (`ActorsVersion` in
> `Directory.Packages.props`) until it is.
