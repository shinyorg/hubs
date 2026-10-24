# Shiny.BluetoothLE.Hubs — Plan

**SignalR-style hubs over Bluetooth LE**, built on **Shiny.BluetoothLE** (client/central) and **Shiny.BluetoothLE.Hosting** (host/peripheral).

- One device hosts one or more hubs.
- Clients discover the host, connect, and call hub methods through a **source-generated, strongly typed proxy**.
- The host pushes events to all clients, some clients, or groups of clients.
- Files move over a separate **L2CAP** channel, a faster direct link between the devices.

Sample: the repo's `samples/Sample` (the tic-tac-toe game and chat, over BLE and Wi-Fi). The BLE-only TicTacToe sample
stayed in the old blehubs repo.

> **2026-10-23: moved into the Shiny.UniversalHubs repo.** blehubs was its own repo until now; its history stays there.
> This document is the BluetoothLE Hubs half of the design; `PLAN.md` is the UniversalHubs half. Both libraries share one
> version (1.5 onwards) and release together.

> History: v0 dispatched Shiny.Mediator contracts over BLE. On 2026-10-02 it was replaced by the hub model below. Mediator is no longer a dependency. The wire framing, transport, handshake and file transfer were kept. The library was named Shiny.SmartBle until 2026-10-02.

---

## 1. Developer experience

### Contract (shared by host and client)
```csharp
[BleHubClient]
public interface IGameHub
{
    // client -> host. Each must return Task, Task<T> or IAsyncEnumerable<T>.
    // A trailing CancellationToken parameter is passed through and is never serialized.
    Task<JoinResult> Join(string playerName, string? avatarFile);
    Task<MoveResult> MakeMove(int cell);
    Task Rematch();
    IAsyncEnumerable<int> Countdown(int from, CancellationToken cancellationToken);

    // host -> client, fire-and-forget. Action or Action<T1..T4>.
    event Action<GameState> StateChanged;
    event Action<string, string> Emote;
}
```

### Host
```csharp
services.AddBleHubServer(server => server
    .ServiceUuid("<service uuid>")                                   // optional - a default applies; clients must match
    .Host(o => { o.LocalName = "TTT"; o.EnableFileTransfers(dir); })
    .AddHub<GameHub>("<characteristic uuid>"));

public class GameHub(GameEngine engine) : BleHub<IGameHub>
{
    public override Task OnConnectedAsync() => Groups.AddToGroupAsync(Context.ConnectionId, "lobby");
    public override Task OnDisconnectedAsync(HubDisconnect disconnect) => ...;   // or OnDisconnectedAsync(string? reason)

    public async Task<MoveResult> MakeMove(int cell)
    {
        // Context.Client (name, properties, items), Context.Abort("reason")
        await Clients.All.StateChanged(engine.Snapshot());      // generated typed push
        await Clients.Group("spectators").Emote("host", "👀");
        return new MoveResult(true, null);
    }
    ...
}

// outside a hub
public class Something(IHubContext<GameHub> hub)
{
    Task Tick() => hub.Clients.All.StateChanged(state);       // generated extension property
}

await host.Start();   // IBleHubHost - adds the GATT service, L2CAP and advertising
```

### Client
```csharp
services.AddBleHubClient<IGameHub>("<characteristic uuid>", o => o.ServiceUuid = "<service uuid>");

public class GameViewModel(IBleHubClient<IGameHub> client)  // or the generated GameHubClient, or IGameHub
{
    client.Hub.StateChanged += state => ...;
    client.Connected += ...; client.Disconnected += (_, disconnect) => ...;   // HubDisconnect

    client.Discover().Subscribe(host => ...);
    await client.Connect(host, new BleHubConnectOptions("Allan"));
    var result = await client.Hub.MakeMove(4);
    await foreach (var n in client.Hub.Countdown(3, ct)) { }
    await client.UploadFile(path, "avatar.jpg");
}
```

## 2. GATT layout

- **One characteristic per hub**, with **Write** (client → host) and **Notify** (host → client) on the same characteristic.
- **One service per host**: every hub is a characteristic inside one GATT service: `BleHubHostOptions.ServiceUuid` on the host (`server.ServiceUuid(...)`) and `BleHubClientOptions.ServiceUuid` on each client, both defaulting to `BleHubProtocolOptions.DefaultServiceUuid`. Hosts and clients must agree on it. Hubs are routed by characteristic UUID, so frames never need to carry a hub name.
- The host advertises that one UUID, and clients scan for it. Two 128-bit UUIDs (37 bytes) don't fit a 31 byte advertisement: Android refuses to advertise (`ADVERTISE_FAILED_DATA_TOO_LARGE`), and iOS moves the extra UUID to an Apple-only overflow area that Android scanners can't see. So per-hub service UUIDs were removed (2026-10-09).
- A scan therefore can't tell which hubs a host is running. Connecting to a hub that is stopped is refused by the handshake, and to a host without the hub's characteristic (another app on the default UUID) fails at characteristic discovery.
- Several hub clients connected to the same peripheral share one BLE connection, which is reference counted. Disconnecting one hub doesn't drop the others.

### Why notifications instead of reads for responses
- With reads, the client can't tell when a response is ready, so it would have to poll.
- A read value is ambiguous when several calls are in flight.
- Reads are still limited by the MTU and the 512-byte attribute cap.

Correlated, chunked notifications avoid all three problems.

## 3. Wire protocol (v1)

Every write or notification is one **frame**, at most `MTU - 3` bytes (capped at 512).

- **Units:** inside blehubs "MTU" is always the ATT MTU (`FrameCodec.GetFrameSize(attMtu)`, `BleHubConnectedClient.Mtu`). Shiny's `IPeripheral.Mtu`, on both the client and the host, is the payload size (ATT MTU minus 3), so the BLE adapters add `BleConstants.AttHeaderSize` where it enters (`BleHubClient.Connect`, `BleHubHost.OnWrite`). Before 1.2 they didn't, and frames came out 3 bytes short.
- Receivers reassemble from each frame's header, never from a frame size, so peers with different frame sizes interoperate. Frame size changes are wire compatible.

```
offset size  field
0      1     protocol version (1)
1      1     kind
2      2     message id (uint16 LE)
4      2     sequence (uint16 LE)
6      1     flags: bit0 FIRST, bit1 LAST
7      4     total body length (uint32 LE), FIRST frame only
...          body chunk
```
The body is `[name length:1][name utf8][payload]`. Frames are reassembled per (peer, hub, message id).

| Value | Kind | Dir | Name | Payload |
|---|---|---|---|---|
| 0x01 | Handshake | C→H | – | `HandshakeInfo` (protocol version, client name, app version, properties) |
| 0x02 | HandshakeAck | H→C | – | `HandshakeAck` (accepted, reason, host name, file PSM, file secure, media PSM, media secure, media token, media max packet size) |
| 0x10 | Invoke | C→H | method | arguments |
| 0x12 | StreamInvoke | C→H | method | arguments |
| 0x13 | Cancel | C→H | – | (cancels the invocation or stream with that id) |
| 0x14 | Rename | C→H | – | `RenameInfo` (the client's new name). Answered with `Completion`, or `Error` of type `HubRenameRefused` |
| 0x21 | Completion | H→C | – | serialized result, empty for `Task` |
| 0x22 | StreamItem | H→C | – | serialized item |
| 0x23 | StreamEnd | H→C | – | – |
| 0x24 | Error | H→C | – | `RemoteError` (type, message) |
| 0x30 | Push | H→C | event | arguments |
| 0x31 | HostRenamed | H→C | – | `RenameInfo` (the host's new name) |
| 0x40 | Disconnect | H→C, C→H | – | `DisconnectInfo` (reason, kind) |

**Arguments**: `[count:1]` followed by `count × ([length:4][serialized value])`. Each value is serialized with its static type through `IBleHubSerializer`, which keeps it AOT-safe.

**Disconnect**: `DisconnectInfo` is `{ Reason, Kind }`. `Reason` is the optional message and `Kind` is the
`HubDisconnectReason` as a number (`ClientDisconnect` 0, `ClientTimeout` 1, `ServerDisconnect` 2, `ServerShutdown` 3,
`ConnectionFailed` 4). These values travel on the wire, so they are never renumbered. `Kind` is additive JSON, so the
protocol version stays at 1.
- **Host → client**: the host is ending the session (`ServerDisconnect` or `ServerShutdown`). A frame with no `Kind`, from
  an older host, is treated as `ServerDisconnect`.
- **Client → host**: the client is leaving (`ClientDisconnect`). Over BLE the client sends it before it unsubscribes. The
  host handles it inline in the GATT write, so the unsubscribe that follows finds the client already gone instead of
  reporting a timeout. An unsubscribe with no goodbye is a `ClientTimeout`, because iOS and Android both report a
  dropped central as an unsubscribe. An older host answers this frame with an `Error` frame, which the leaving client
  ignores, and still sees the unsubscribe.

**Media channel fields** (1.4.0): `HandshakeAck` gained `MediaChannelPsm`, `MediaChannelSecure`, `MediaToken` and
`MediaMaxPacketSize` (§6b). They are additive JSON with defaults (0 / false / null / 0), so the protocol version stays at
1: an older client ignores them, and an ack from an older host reads as "no media channels".

**Rename**: `RenameInfo` is `{ Name }`. Both frames are additive, so the protocol version stays at 1.
- **Client → host** (`Rename`): the host runs `ValidateClient` with the new name (and the handshake's app version and
  properties), then `OnRenamedAsync`, and replies `Completion`. A refusal is an `Error` of type `HubRenameRefused`
  (`BleHubRemoteException.RenameRefused`) whose message is the reason. An older host answers with an `Error`
  ("Unexpected message kind"), so the client gets a `BleHubRemoteException` either way.
- **Host → client** (`HostRenamed`): sent with a host-allocated id. The client delivers it through the same ordered pump
  as pushes, so it is raised in order with hub events. An older client drops it (unknown host message id) and keeps the
  handshake's host name.

**Rules**
- Client message ids are 1..0x7FFF. Host-allocated ids (pushes, host renames, disconnect) have the high bit set.
- Reassembly limits:
  - `MaxPayloadSize` (default 256 KB)
  - reassembly timeout (default 30 s)
  - at most 16 partial messages per peer

  A violation fails the GATT write and sends an `Error` frame.
- The host sends each message to a peer under a lock, so frames for one peer never interleave. The client also serializes its writes per message.
- The host refuses any hub call made before the handshake, and refuses unknown protocol versions.

## 4. Source generator (`Shiny.BluetoothLE.Hubs.SourceGenerators`, shipped in the core package)

For each `[BleHubClient]` interface, the generator emits:
- **Client proxy**, emitted when `Shiny.BluetoothLE.Hubs.Client` is referenced. `GameHubClient : BleHubClient, IGameHub, IBleHubClient<IGameHub>` implements each method through `Invoke` / `Stream`, implements the events, and dispatches pushes to them. A `[ModuleInitializer]` registers a factory so `AddBleHubClient<IGameHub>()` can create the proxy without reflection.
- **Typed push senders**, emitted when `Shiny.BluetoothLE.Hubs.Host` is referenced and a hub uses the contract. These are extension methods on `BleHubPush<IGameHub>`, such as `StateChanged(GameState)`.

For each `class X : BleHub<TContract>`, the generator emits:
- **Dispatcher**: a switch on the method name, typed argument reads, a call into the hub, and result serialization. It is registered through a `[ModuleInitializer]`.
- **`IHubContext<X>.Clients`**: a C# 14 extension property typed to `IHubClients<BleHubPush<TContract>>`.

Hubs don't need to be `partial`. The generator checks the hub's methods against the contract and reports any mismatch.

Contracts declared in a referenced assembly are supported. Proxies and senders generated for them are `internal` to avoid clashes.

**Diagnostics**

| Id | Problem |
|---|---|
| SBH001 | The hub is missing a contract method, or its signature doesn't match |
| SBH002 | Unsupported return type (must be `Task`, `Task<T>` or `IAsyncEnumerable<T>`) |
| SBH003 | Overloaded method names |
| SBH004 | Unsupported event delegate (must be `Action` / `Action<…>`, at most 4 arguments) |
| SBH005 | `BleHub<T>` where `T` isn't a `[BleHubClient]` interface |
| SBH006 | `ref` / `out` / `in` parameters, or more than 255 parameters |

## 5. Host (`Shiny.BluetoothLE.Hubs.Host`)

- **`AddBleHubServer(server => ...)`**, called once with at least one hub:
  - `.ServiceUuid(uuid)` sets `BleHubHostOptions.ServiceUuid`, `.Host(...)` the rest of `BleHubHostOptions`, and `.Protocol(...)` the app-wide `BleHubProtocolOptions`.
  - `.AddHub<THub>(characteristicUuid, o => ...)` registers the hub as transient (a new DI scope for each invocation, like SignalR) and `IHubContext<THub>`. A hub type or characteristic added twice, and a malformed UUID, are refused at registration.
  - registers the shared `IBleHubHost`, and on Android, iOS and Mac Catalyst the platform hosting stack (`AddBluetoothLeHosting()`).
- **`IBleHubHost`**:
  - `Start()` / `Stop(reason)` start or stop **every** hub.
  - `IsRunning` is true while any hub runs.
  - `Rename(localName)` changes the host's name without stopping anything. It sets `BleHubHostOptions.LocalName`,
    restarts advertising under the new name while hubs run (even though the service UUID didn't change), and sends
    `HostRenamed` to every connected client on every transport. While stopped it only sets the name for the next `Start`.
- **Per-hub start/stop**: `IHubContext<THub>.Start()` / `Stop(reason)` / `IsRunning`.
  - A stopped hub tells its clients to disconnect and refuses new handshakes ("Hub is not running").
  - The one GATT service holds every registered hub's characteristic. It is added when the first hub starts and removed only when the last one stops, so stopping one hub never drops another hub's clients.
  - Advertising (the one service UUID) starts with the first running hub and stops with the last. Starting or stopping a hub beside another changes nothing on air; only `Rename` restarts it.
  - BLE access, the L2CAP file server and the cleanup sweep start with the first running hub and stop with the last.
  - Also: `IsRunning`, `FileTransferPsm`, `FileTransferred` / `FileTransferProgress` events.
- **`BleHub<TContract>`**:
  - `Context`: `ConnectionId`, `Client` (name, app version, properties, `Items`, MTU), `Abort(reason)`, and `ConnectionAborted`
  - `Clients`: `All`, `Others`, `Caller`, `Client(id)`, `Clients(ids)`, `AllExcept(ids)`, `Group(name)`, `Groups(names)`, `GroupExcept(name, ids)`, `OthersInGroup(name)`
  - `Groups`: `AddToGroupAsync` / `RemoveFromGroupAsync`
  - `OnConnectedAsync` / `OnDisconnectedAsync(HubDisconnect)`. By default the latter calls `OnDisconnectedAsync(string? reason)` with `disconnect.Description`, so either override works.
  - `OnRenamedAsync(previousName)`: runs when a connected client renames, after `ValidateClient` accepted the new name and before the client is told. `Context.Client.Name` already has the new name. Throwing refuses the rename: the name is put back and the client gets the exception's message. Renames run one at a time per client.
- **`IHubContext<THub>`**:
  - `Start()` / `Stop(reason)` / `IsRunning`
  - `Clients`, through the generated extension property
  - `Groups`
  - `ConnectedClients`
  - `Disconnect(connectionId, reason)`
  - `ClientRenamed`: `BleHubClientRenamedEventArgs(Client, PreviousName)`, raised after a rename is accepted. `BleHubConnectedClient.Name` follows renames.
- **Disconnect is cooperative.** iOS `CBPeripheralManager` can't drop a central, so the host sends `Disconnect` and forgets the client. The client library disconnects itself when it receives it.
- **Disconnect reasons**: every departure is a `HubDisconnect(Reason, Message)`. `Description` is the message, or a default text for the reason.
  - `ClientDisconnect`: the client sent a `Disconnect` frame (it called `Disconnect()` or was disposed).
  - `ClientTimeout`: an unsubscribe without that frame (message "Unsubscribed"), or the cleanup sweep.
  - `ServerDisconnect`: `Context.Abort(reason)` or `IHubContext.Disconnect(id, reason)`.
  - `ServerShutdown`: `IBleHubHost.Stop(reason)`, or `IHubContext.Stop(reason)` (default message "Hub stopped").
  - `ClientDisconnected` raises `BleHubClientDisconnectedEventArgs(Client, Disconnect)`. Its `Reason` string is `Disconnect.Description`.
- **Client options**: `MaxClients` and `ValidateClient` (return a rejection reason) are set per hub through `server.AddHub<THub>(..., o => ...)`.
- **Cleanup**: a peer that unsubscribes is removed right away. A periodic sweep also removes peers that no longer appear in `SubscribedCentrals`, because Android doesn't always report the unsubscribe.

## 6. Client (`Shiny.BluetoothLE.Hubs.Client`)

- **`AddBleHubClient<TContract>(characteristicUuid, o => ...)`** registers the generated proxy. `BleHubClientOptions.ServiceUuid` (default `DefaultServiceUuid`) must match the host's, and `o.Protocol(...)` sets the app-wide limits. On Android, iOS and Mac Catalyst it registers the platform BLE stack (`AddBluetoothLE()`); on Apple with an `AppleBleConfiguration` that turns off iOS's background alerts (`NotifyOnConnection` / `NotifyOnDisconnection` / `NotifyOnNotification`), since hubs are foreground only. An app's own `AddBluetoothLE(config)`, called first, wins.
- **`BleHubClient`** is the base class for the generated proxies. `IBleHubClient<TContract>` exposes:
  - `Hub`
  - `Status`, plus the `StatusChanged`, `Connected` and `Disconnected` events
  - `Discover()`: scans by the client's `ServiceUuid`, so it finds every host of this library (on that UUID), whichever hubs it runs. It leaves out the app's own host: a scan on the device that is advertising can report that advertisement. The host records what it advertises (service UUID and local name) in `LocalHubAdvertisement`, a per-container singleton shared with the clients, and `Discover()` drops a result with the same UUID and the same advertised name. A result with no name is always kept
  - `Connect(host, options, ct)`:
    1. Connect.
    2. Request the largest ATT MTU, 517 (`FrameCodec.MaxAttMtu`). Android negotiates it; Apple negotiates on its own and can't be asked, so its MTU stands. A failed request keeps the current MTU. The result is logged.
    3. Subscribe to notifications.
    4. Handshake.
    5. Become `Ready`.
  - `Disconnect()`
  - `HostName` (kept up to date when the host renames), `CanTransferFiles`
  - `Rename(name)` / `ClientName`, and the `HostRenamed` event
  - `UploadFile` / `UploadStream` / `DownloadFile`
- **Calls**:
  - A per-call timeout comes from `BleHubProtocolOptions.RequestTimeout`. Streams have no overall timeout.
  - A `CancellationToken` sends `Cancel` to the host.
  - A host exception becomes `BleHubRemoteException`.
  - A dropped connection fails every pending call with `BleHubDisconnectedException`, whose `Disconnect` says why.
- **Disconnect reasons**: `Disconnected` is an `EventHandler<HubDisconnect>`, and `BleHubStatusChangedEventArgs(Status, Disconnect)` carries it for `Disconnecting` and `Disconnected`. Both expose a computed `Reason` string.
  - `ClientDisconnect`: `Disconnect()` or `Dispose()`. Over BLE, `Disconnect()` first sends a `Disconnect` frame (2 s timeout, errors ignored).
  - `ClientTimeout`: the BLE link dropped.
  - `ServerDisconnect` / `ServerShutdown`: from the host's `Disconnect` frame.
  - `ConnectionFailed`: a connect or handshake failed. `Disconnected` isn't raised, because the client never connected.
- **Renaming**: `Rename(name)` needs a connection (`BleHubDisconnectedException` otherwise). Renaming to the current name is a no-op. A refusal throws `BleHubRemoteException` of type `RenameRefused` and keeps the old name. `ClientName` is `BleHubConnectOptions.Name` or the latest rename, and null while disconnected. A later `Connect` uses its own options' name. `HostRenamed` is raised after `HostName` changes, in order with pushes.
- Pushes are raised on a background thread. UI code marshals them to the main thread.

## 6a. File transfer (L2CAP)

Files don't go through hub framing. They use a separate L2CAP channel, and the client exposes them as their own methods.

- **Host**: `AddBleHubServer(server => server.Host(o => o.EnableFileTransfers(dir, ft => ...)))`.
  - **Directory mode**, the default, wraps `OpenL2CapFileServer`: upload and download flags, `MaxUploadSize`, overwrite rules and an `Authorize` hook. Shiny refuses path traversal.
  - **Custom mode** registers `IBleHubFileHandler`, which wraps `HandleL2CapRequests`.
- **PSM discovery**: the platform assigns the PSM at runtime, and it reaches the client in every hub's `HandshakeAck` together with the secure flag. On Android the secure and insecure channels listen separately, so the client must open the matching one. A PSM of 0 means file transfer is unavailable, and the file calls throw `BleHubFileTransferNotSupportedException`.
- **Client**: `UploadFile`, `UploadStream` and `DownloadFile` map to Shiny's `IPeripheral.UploadFile` / `DownloadFile` and to the L2CAP channel extensions.
- **Platforms**: iOS/macOS, and Android API 29+. Elsewhere the host serves PSM 0 and logs a warning.
- **Identifying the client**: the L2CAP peer is matched to the hub's connected client by peer id where possible.

## 6b. Media channels (L2CAP, 1.4.0)

An ordered, bidirectional packet pipe per hub connection, for live audio and video (Shiny.UniversalHubs builds calls on
it). It skips hub framing and the serializer: packets are raw bytes.

- **API** (`IBleHubMediaChannel : IAsyncDisposable`, core package): `ConnectionId` (host side; null on the client),
  `MaxPacketSize`, `IsOpen`, `ValueTask Send(ReadOnlyMemory<byte>, ct)`, `IAsyncEnumerable<ReadOnlyMemory<byte>>
  Receive(ct)`, `Task Completion`.
  - `Send` is serialized: packets go out whole and in call order. A packet over `MaxPacketSize` throws `ArgumentException`
    (the channel stays open). Sending on a closed channel throws `BleHubException`. A send that fails or is cancelled
    after it started writing closes the channel, because the far side can't find the next frame boundary in half a frame.
  - `Receive` allows one reader at a time (a second concurrent one throws `InvalidOperationException`) and completes when
    the channel closes, after draining what arrived. Packets that arrive before anyone reads are buffered (unbounded).
  - `Completion` completes, never faults, when the channel closes for any reason.
- **Host**: `BleHubHostOptions.EnableMediaChannels(maxPacketSize = 128 KB, secure = false)` opens a second L2CAP PSM
  (`IBleHostingManager.OpenL2Cap`) with the shared infrastructure, beside the file server, and closes it when the last
  hub stops. One PSM serves every hub; the token says which hub and client a channel belongs to. If L2CAP is unavailable
  the hubs still start, with PSM 0, and a warning is logged.
  - `IHubContext<THub>.MediaChannelOpened` (`BleHubMediaChannelEventArgs(Client, Channel)`) and
    `GetMediaChannel(connectionId)` (null when none is open).
  - One channel per client. A new one replaces (and closes) the old.
  - A client's channel is closed when it disconnects, drops, is disconnected, or the hub stops (`OnPeerGone`).
- **Client**: `IBleHubConnection.CanOpenMediaChannel` and `OpenMediaChannel(ct)`, serialized, returning the open channel
  when there is one. It closes when the connection tears down. `NotSupportedException` when the host or device can't;
  `BleHubException` when the host refuses.
- **Opening** over BLE:
  1. The handshake ack of an accepted client carries the media PSM, the secure flag, `MediaMaxPacketSize` and a
     `MediaToken`: 16 random bytes as lowercase hex, issued once per connection and kept for its life (so a closed
     channel can be reopened).
  2. The client opens the L2CAP channel on that PSM and sends the token's UTF-8 bytes as its first frame.
  3. The host compares it (fixed time) with the tokens of its connected BLE clients across every hub. On a match it
     answers one frame `[0x01]`, then attaches the channel to that client and raises `MediaChannelOpened`. The answer goes
     out before the hub hears of the channel, so nothing the hub sends can get ahead of it. Otherwise, or after 10 s with
     no token, it closes the channel.
  4. The client waits up to `RequestTimeout` for `[0x01]`. Anything else, or the channel closing, is a refusal.
- **Framing**: `[u32 LE length][bytes]` on the L2CAP byte stream (`MediaFrameCodec`), the token and the answer included.
  Writes are split into pieces of at most 4096 bytes, each its own array. A length over `MaxPacketSize` is a protocol
  error that closes the channel. The transport-free channel (`FramedMediaChannel`, internal) is shared by host and
  client and fed from the L2CAP `DataReceived` observable, subscribed as soon as the channel opens.
- **Identifying the client** is by token alone. The L2CAP peer identifier isn't compared with the GATT central's, since
  platforms don't promise they match.

## 7. Repo layout

```
Shiny.BluetoothLE.Hubs.slnx
Directory.Build.props, Directory.Packages.props (central package management)
src/Shiny.BluetoothLE.Hubs/                    net10.0: framing, arguments codec, serializer, options, [BleHubClient], exceptions
src/Shiny.BluetoothLE.Hubs.SourceGenerators/   netstandard2.0 Roslyn generator (packed into the core package's analyzers folder)
src/Shiny.BluetoothLE.Hubs.Host/               BleHub, IBleHubHost, IHubContext, groups, L2CAP file server
src/Shiny.BluetoothLE.Hubs.Client/             BleHubClient, discovery, connection sharing, file transfer
tests/Shiny.BluetoothLE.Hubs.Tests/            xUnit: framing, codec, generated hub + proxy end to end over an in-memory radio
samples/TicTacToe/                     .NET MAUI (iOS + Android), Shiny.Maui.Shell
```

The core package targets `net10.0`. The host and client packages target `net10.0`, `-android`, `-ios` and `-maccatalyst` (`PlatformTargetFrameworks` in Directory.Build.props) only so that `AddBleHubServer` / `AddBleHubClient` can register the platform BLE stacks (`AddBluetoothLeHosting()` / `AddBluetoothLE()`). The code is otherwise the same on every target and uses only the Shiny abstractions; on plain `net10.0` the app registers an `IBleHostingManager` / `IBleManager` itself.

## 8. Tic-tac-toe sample

- **Hub**: `GameHub : BleHub<IGameHub>`
  - `Join(name, avatar)` → `JoinResult(mark, state)`. The first client is O, and later clients are spectators placed in the `"spectators"` group.
  - `MakeMove(cell)` → `MoveResult`
  - `Rematch()`
  - `SendEmote(emoji)`
  - `SendChat(text)`: the host trims the text to 200 characters and stamps the sender's name and mark, so clients can't spoof who said it
  - pushes `StateChanged(GameState)`, `Emote(from, emoji)` and `ChatReceived(ChatMessage)`. The sender gets its own line back through the push
  - `OnDisconnectedAsync` frees the O seat.
- **Host-local play**: the host's own UI calls `GameEngine` directly, then broadcasts through `IHubContext<GameHub>`. That shows hub usage from outside a hub.
- **Spectators**: "Remove spectators" uses `IHubContext.Disconnect` for every member of the spectators group.
- **Avatars**: the client uploads its avatar over L2CAP before calling `Join`. Each side downloads the other's avatar using the file name carried in `GameState`.
- **Pages**: `HomePage` (name, avatar, Host or Join), `JoinPage` (live discovery list), and `GamePage` (board, avatars, score, emotes, chat, rematch, leave). They use `[ShellMap]` with `AddGeneratedMaps()`, plus `INavigator` and `IDialogs`.
- **Platform setup**:
  - Android: `BLUETOOTH_SCAN` (`neverForLocation`), `BLUETOOTH_CONNECT` and `BLUETOOTH_ADVERTISE`, plus the legacy permissions with `maxSdkVersion=30`.
  - iOS: `NSBluetoothAlwaysUsageDescription`.

## 9. Decisions

| Question | Decision |
|---|---|
| Programming model | SignalR-style hubs. Mediator was dropped (2026-10-02) |
| Binding | Source generated: AOT/trim safe, with compile-time diagnostics |
| Contract | One `[BleHubClient]` interface. Methods are client → host, `event`s are host → client |
| Host → client | Fire-and-forget pushes. No client results in v1 |
| v1 hub features | Groups, streaming (`IAsyncEnumerable<T>`), `OnConnected` / `OnDisconnected`, `Context.Abort`, multiple hubs |
| GATT | One write+notify characteristic per hub, routed by characteristic UUID |
| Service UUID (2026-10-09) | One per host (`server.ServiceUuid`, matched by each client's `ServiceUuid`), holding every hub. Per-hub service UUIDs were removed because two 128-bit UUIDs overflow the advertisement (Android fails, iOS hides one from Android). The cost: a scan can't tell which hubs a host runs, so a stopped hub is refused at the handshake |
| Platforms | iOS + Android in both roles. Windows can't host |
| Registration (2026-10-09) | One `AddBleHubServer(server => ...)` call per app for the host side, `AddBleHubClient<T>(characteristic, o => ...)` per contract. Both register the platform BLE stacks, so the host and client packages multi-target. Replaces `AddBleHub`, `ConfigureBleHubHost` and `ConfigureBleHubProtocol` |
| Payload size | Hub messages are chunked over GATT, 256 KB max by default. Files go over L2CAP |
| Serialization | Pluggable `IBleHubSerializer`. The default is Shiny's AOT JSON, using contexts registered with `Json.AddContext` |
| Background | Foreground only |
| Auth / security | None in v1, apart from opt-in exposure and the `ValidateClient` handshake hook |
| Disconnect reasons | Typed `HubDisconnectReason` + optional message, on both sides (2026-10-08). The client says goodbye with a `Disconnect` frame so the host can tell leaving from a dropped link. Additive JSON, protocol version stays 1 |
| Renaming (2026-10-09) | Clients and the host rename without reconnecting, through additive `Rename` / `HostRenamed` frames. The protocol version stays 1, because older peers refuse or ignore the frames cleanly |
| Telling other clients about a rename | App logic in the hub (`OnRenamedAsync` pushes to `Clients.Others`). The library gives the hook and `ClientRenamed`, and pushes nothing itself |
| MTU (2026-10-10) | Every new BLE connection asks for ATT MTU 517, the maximum. Inside blehubs MTU means ATT MTU; Shiny's payload size is converted at the BLE adapters |
| Own advertisement (2026-10-10) | `Discover()` drops the app's own host, matched by service UUID and advertised local name while it advertises. The 31 byte advertisement has no room for an id beside the 128-bit UUID, so the name is the only signal: another host with the same name is hidden while this app advertises, and a nameless advertisement is never treated as this app's |
| Media channels (2026-10-23) | A packet pipe per connection on its own L2CAP PSM (§6b), not hub frames: GATT write-with-response is a few KB/s, and media wants raw bytes with no serializer. Gated by a random token from the handshake ack. Additive ack fields, protocol version stays 1. Other transports carry it through seams (§13) |
| Other transports (§13) | Hidden host and client seams in this repo. Wi-Fi itself (Switchboard, mDNS, transport choice) lives in Shiny.UniversalHubs, its own repo, which references this one through NuGet |

## 10. Status

- **2026-10-02**: the Mediator-based v0 was built and tested (42 tests), then superseded by this hub design.
- **2026-10-02**: hub rewrite done. Generator, host, client and sample are migrated.
  - 55 tests pass: framing, argument codec, generator diagnostics, and generated hub + proxy end to end. The end-to-end tests cover calls, streams, cancellation, timeouts, errors, pushes in order, Others and groups, `IHubContext`, cooperative disconnect, connection loss and client limits.
  - The sample builds for iOS and Android.
  - `Context.Abort()` inside a hub method takes effect after that method's reply is sent.
  - Per-hub start/stop added. A fake `IBleHostingManager` now tests `BleHubHost` itself: GATT writes and notifications, shared and separate services, advertising updates and restarts. 63 tests pass.
  - **Not yet verified:** a real two-device run.
- **2026-10-08**: transport seams added (§13) for Shiny.SwitchboardR, which serves hubs over Wi-Fi from its own repo. 76 tests pass.
- **2026-10-08**: typed disconnect reasons (`HubDisconnect`, `HubDisconnectReason`). The client now sends `Disconnect` to the host before unsubscribing, and `DisconnectInfo` gained `Kind`. **Breaking API**: `IBleHubConnection.Disconnected` is `EventHandler<HubDisconnect>`, and `BleHubClientDisconnectedEventArgs`, `BleHubStatusChangedEventArgs` and `BleHubDisconnectedException` carry a `HubDisconnect`. The wire stays compatible. 82 tests pass.
- **2026-10-08**: `BleHubConnectedClient.Peripheral`, the central a BLE client is connected through. It is null for clients on another transport, and Shiny.SwitchboardR exposes it as `HubConnection.Ble`. 83 tests pass.
  - **Not yet verified:** that a real iOS and Android host each see the goodbye before the unsubscribe.
- **2026-10-09**: renaming without reconnecting. New frames `Rename` (0x14) and `HostRenamed` (0x31), additive, so the protocol stays at 1. Client: `Rename`, `ClientName`, `HostRenamed`. Host: `OnRenamedAsync`, `IHubContext.ClientRenamed`, `IBleHubHost.Rename` (re-advertises without stopping). Transport seams gained rename members (§13). 94 tests pass.
  - **Not yet verified:** that a real client's scan shows the host's new advertised name.
- **2026-10-09**: one service UUID per host. `BleHubProtocolOptions.ServiceUuid` (default `DefaultServiceUuid`) holds every hub as a characteristic and is the only advertised UUID. **Breaking API**: `AddBleHub<THub>(characteristicUuid)`, `AddBleHubClient<T>(characteristicUuid)` and `BleHubRegistration(HubType, CharacteristicUuid, Options)` lost their service UUID. Found on devices: a host running two hubs on separate services couldn't be seen (iOS) or couldn't advertise at all (Android). The wire is unchanged. 97 tests pass.
  - **Not yet verified:** that an iOS and an Android host running two hubs are each seen by the other platform.
- **2026-10-09**: registration cleanup, matching Shiny.UniversalHubs. **Breaking API**:
  - Host: `AddBleHubServer(server => server.ServiceUuid(..).Host(..).Protocol(..).AddHub<THub>(characteristic, ..))`, called once. It refuses duplicate hubs and characteristics and malformed UUIDs. The host's service UUID moved to `BleHubHostOptions.ServiceUuid`.
  - Client: `AddBleHubClient<T>(characteristic, o => ...)` with `BleHubClientOptions` (`ServiceUuid`, `Protocol(...)`).
  - Removed: `AddBleHub`, `ConfigureBleHubHost`, `BleHubProtocolOptions.ServiceUuid`. `ConfigureBleHubProtocol` and `AddBleHubCore` are internal.
  - The host and client packages multi-target and register the platform BLE stacks. On Apple the client turns off iOS's background alerts.
  - 103 tests pass (new `RegistrationTests`).
- **2026-10-10**: `Discover()` leaves out the app's own host advertisement (`LocalHubAdvertisement`, set by `BleHubHost` when it advertises, cleared when it stops). Found on devices: an app hosting and scanning listed itself. 109 tests pass (new `DiscoveryTests`, with a fake `IBleManager`).
  - **Not yet verified:** on hardware, that a hosting phone and Mac no longer list themselves and still see each other.
- **2026-10-10**: MTU. The client asks for ATT MTU 517 (was 512) on every new BLE connection and logs what it got. Fixed a unit bug: Shiny's `IPeripheral.Mtu` is the payload size, and blehubs used it as the ATT MTU on both sides, so every frame was 3 bytes smaller than the link allowed. It is converted at the adapters now; `BleHubConnectedClient.Mtu` reports the ATT MTU. Wire compatible. 114 tests pass (new `MtuTests`, and a host frame-size test).

- **2026-10-23** (1.4.0): media channels (§6b). `IBleHubMediaChannel`, `BleHubHostOptions.EnableMediaChannels`,
  `IHubContext.MediaChannelOpened` / `GetMediaChannel`, `IBleHubConnection.CanOpenMediaChannel` / `OpenMediaChannel`,
  `MediaFrameCodec`, and seams for other transports (§13). `HandshakeAck` gained four additive fields; wire compatible. The
  fake hosting manager gained `OpenL2Cap` and an in-memory L2CAP link that re-chunks writes. 143 tests pass (new
  `MediaFrameCodecTests`, `MediaChannelTests`, and media cases in `ExternalTransportTests`).
  - **Not yet verified:** on hardware, a media channel between an iOS and an Android device (both directions), secure
    channels on Android, and throughput with 128 KB packets.

- **2026-10-23** (1.4.1): `Connect` uses the manager's current peripheral (`IBleManager.GetKnownPeripheral`). Found on
  devices: an app that rescanned between discovering a host and connecting to it timed out on iPhone and Mac. Shiny's
  Apple manager clears unconnected peripherals when a scan starts, so the connection's events went to a new instance
  while the client waited on the discovered one. 144 tests pass (`DiscoveryTests.ConnectUsesTheManagersCurrentPeripheral`).
## 11. Future

- Client results (the host awaits a value from a client)
- Client → host streaming (`IAsyncEnumerable<T>` parameters)
- Sending oversized hub payloads over L2CAP automatically
- Auth and bonding options
- Background support
- Write-without-response with flow control for throughput
- An optional Mediator adapter package

## 12. Known limitations and risks

- BLE needs **two physical devices**. The iOS simulator has no Bluetooth.
- When an iOS host is backgrounded, it advertises only through the overflow area and without a local name. This is out of scope (foreground only).
- 31-byte advertisements: a 128-bit UUID plus a long local name may not fit. Keep `LocalName` short.
- Telling the app's own advertisement apart relies on the local name. Another host with the same name is hidden from discovery while this app advertises, so give hosts distinct names.
- Throughput with write-with-response is a few KB/s, which is fine for game- and command-sized messages.

## 13. Other transports

Hidden seams let another package carry hubs over something other than BLE: `IHubContext<THub>.TransportEndpoint` on the
host and `BleHubClient.ConnectExternal` on the client. Their design, and the Wi-Fi transport built on them, live in
**Shiny.UniversalHubs**' PLAN.md (`~/Desktop/dev/universalhubs`, §2).

The seams carry a `HubDisconnect`, so a transport reports why a client left:
- host: `IBleHubPeerChannel.Disconnect(HubDisconnect, ct)`, and `IBleHubTransportEndpoint.Disconnected(connectionId, HubDisconnect)` /
  `Disconnect(connectionId, HubDisconnect)`
- client: `IBleHubClientTransportEvents.Closed(HubDisconnect)`

A transport sends the `HubDisconnect` to the other side itself. `BleHubClient.Disconnect()` sends the BLE `Disconnect`
frame only over BLE.

Renames cross the seams too:
- host: `IBleHubTransportEndpoint.Rename(connectionId, name, ct)` returns null or the refusal reason (relay it as a
  `BleHubRemoteException` of type `RenameRefused`), and `IBleHubPeerChannel.HostRenamed(hostName, ct)`, called in
  order with `Push`. It is a default interface method (no-op), so existing channels keep compiling.
- client: `IBleHubClientTransport.Rename(name, ct)` (the default throws `NotSupportedException`), and
  `IBleHubClientTransportEvents.HostRenamed(hostName)`, called in order with `Pushed`.

Media channels cross the seams too (1.4.0):
- client: `IBleHubClientTransport.CanOpenMediaChannel` (default false) and `OpenMediaChannel(ct)` (the default throws
  `NotSupportedException`). `BleHubClient.OpenMediaChannel` routes to it when connected through `ConnectExternal`,
  serializes it, reuses an open channel, and closes the channel when the connection ends.
- host: `IBleHubTransportEndpoint.MediaChannelOpened(connectionId, channel)`, called once the transport has checked its
  own token. It raises `IHubContext.MediaChannelOpened` and closes the channel when that client goes. It returns false
  for an unknown client, and the caller closes the channel.
