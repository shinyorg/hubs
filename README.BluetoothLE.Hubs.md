# Shiny.BluetoothLE.Hubs

Shiny.BluetoothLE.Hubs gives you SignalR-style hubs over Bluetooth LE. One device hosts a hub, and nearby devices discover it, connect, and call it through a source-generated, strongly typed proxy. The host pushes events back to everyone, to some clients, or to groups. Files move over L2CAP, a faster direct channel between the devices.

It's built on Shiny.BluetoothLE and Shiny.BluetoothLE.Hosting, and it is AOT- and trim-safe with no reflection.

```csharp
// shared contract
[BleHubClient]
public interface IGameHub
{
    Task<MoveResult> MakeMove(int cell);                          // client -> host
    IAsyncEnumerable<int> Countdown(int from, CancellationToken ct);
    event Action<GameState> StateChanged;                         // host -> clients
}

// host
public class GameHub(GameEngine engine) : BleHub<IGameHub>
{
    public async Task<MoveResult> MakeMove(int cell)
    {
        // Context.Client, Context.Abort(), Groups, OnConnectedAsync/OnDisconnectedAsync
        await Clients.All.StateChanged(engine.Snapshot());        // generated typed push
        return new MoveResult(true, null);
    }
    ...
}

// client - GameHubClient is generated
client.Hub.StateChanged += state => ...;
await client.Connect(host, new BleHubConnectOptions("Allan"));
var result = await client.Hub.MakeMove(4);
await client.UploadFile(path, "avatar.jpg");                      // L2CAP
```

## Packages

| Project | Purpose |
|---|---|
| `Shiny.BluetoothLE.Hubs` | Wire protocol (framing, chunking, reassembly), argument codec, serializer, `[BleHubClient]`, `IBleHubMediaChannel`. Ships the source generator |
| `Shiny.BluetoothLE.Hubs.Host` | `BleHub<T>`, `IBleHubHost`, `IHubContext<THub>`, groups, L2CAP file server, L2CAP media channels |
| `Shiny.BluetoothLE.Hubs.Client` | `BleHubClient` (base of the generated proxies), discovery (which leaves out the app's own host), shared connections, file upload and download, media channels |

## Setup

```csharp
Json.AddContext(MyJsonContext.Default);     // hub arguments/results are AOT-safe JSON by default

// host - one call registers every hub (and the platform BLE hosting stack)
services.AddBleHubServer(server => server
    .ServiceUuid(MyServiceUuid)                                                  // optional - a library default applies
    .Host(o => o.EnableFileTransfers(Path.Combine(FileSystem.AppDataDirectory, "files")))
    .AddHub<GameHub>(GameCharacteristicUuid, o => o.MaxClients = 6)
    .AddHub<ChatHub>(ChatCharacteristicUuid));
await serviceProvider.GetRequiredService<IBleHubHost>().Start();

// client - the same service UUID as the host (and the platform BLE stack)
services.AddBleHubClient<IGameHub>(GameCharacteristicUuid, o => o.ServiceUuid = MyServiceUuid);   // inject IBleHubClient<IGameHub>, GameHubClient or IGameHub
```

- **Registration**:
  - `AddBleHubServer` is called once, with at least one hub. It refuses a hub added twice, two hubs on one characteristic, and malformed UUIDs. `.Host(...)` sets `BleHubHostOptions` (name, file transfers, sweep) and `.Protocol(...)` the protocol limits.
  - `AddBleHubClient<T>(characteristicUuid, o => ...)` is called per contract. `BleHubClientOptions.ServiceUuid` must match the host's, and `o.Protocol(...)` sets the limits.
  - Protocol limits (`BleHubProtocolOptions`: payload size, timeouts) are app-wide, shared by every host and client registration.
  - **Platform BLE stacks are registered for you.** On Android, iOS and Mac Catalyst, `AddBleHubServer` calls `AddBluetoothLeHosting()` and `AddBleHubClient` calls `AddBluetoothLE()`. On Apple the client turns off iOS's background alerts (`NotifyOnConnection`, `NotifyOnDisconnection`, `NotifyOnNotification`), because hubs are used in the foreground. To use your own `AppleBleConfiguration`, call `AddBluetoothLE(config)` *before* `AddBleHubClient`: the first registration wins. On plain `net10.0` (Linux, tests), register an `IBleHostingManager` / `IBleManager` yourself.

- **Contract**: hub methods return `Task`, `Task<T>` or `IAsyncEnumerable<T>`. A trailing `CancellationToken` is passed through to the host. Events are `Action` / `Action<T1..T4>`. Compile errors are reported as `SBH001`–`SBH006`.
- **Hubs**: a new hub instance runs, in its own DI scope, for every call, like SignalR. The hub may take the contract's `CancellationToken` or leave it out.
- **Outside a hub**: inject `IHubContext<GameHub>` and call `context.Clients.All.StateChanged(state)`. `Clients` is a generated C# 14 extension property.
- **Start/stop**:
  - `IBleHubHost.Start()` / `Stop()` start or stop every hub.
  - `IHubContext<THub>.Start()` / `Stop(reason)` control one hub. A stopped hub disconnects its clients and refuses new ones.
  - A GATT service shared by several hubs stays up while any of them is running.
  - The advertisement follows the running hubs.
- **Disconnect is cooperative**, because iOS peripherals can't drop a central. `Context.Abort()` and `IHubContext.Disconnect()` ask the client to leave. The client library does so and raises `Disconnected` with the reason.
- **Disconnect reasons are typed.** Both sides get a `HubDisconnect(Reason, Message)` whose `HubDisconnectReason` you can switch on:
  - `ClientDisconnect`: the client called `Disconnect()` or was disposed. Over BLE the client sends a `Disconnect` frame before it unsubscribes, so the host can tell it left on purpose.
  - `ClientTimeout`: the link was lost (an unsubscribe with no goodbye, the host's sweep, or a drop seen by the client).
  - `ServerDisconnect`: `Context.Abort(reason)` or `IHubContext.Disconnect(id, reason)`.
  - `ServerShutdown`: `IBleHubHost.Stop(reason)` or `IHubContext.Stop(reason)`.
  - `ConnectionFailed`: a connect or handshake failed. No `Disconnected` event is raised, because the client never connected.

  On the host, override `OnDisconnectedAsync(HubDisconnect)` or read `ClientDisconnected`'s `e.Disconnect`. On the client, `Disconnected` is an `EventHandler<HubDisconnect>`, and `StatusChanged` and `BleHubDisconnectedException` carry it as `Disconnect`. `Description` (and the `Reason` string properties) give the message, or a default text for the reason. Existing `OnDisconnectedAsync(string? reason)` overrides keep working.
- **Renaming without reconnecting**:
  - Client: `await client.Rename("Allan B")` changes the name the host knows it by. The host runs `ValidateClient` with the new name, then the hub's `OnRenamedAsync(previousName)`. Either can refuse, which throws `BleHubRemoteException` with `RemoteErrorType == BleHubRemoteException.RenameRefused` and leaves the name unchanged. `ClientName` is the current name.
  - Host: `BleHubConnectedClient.Name` follows renames, and `IHubContext<THub>.ClientRenamed` is raised. Telling other clients is up to the hub, for example `Clients.Others.PlayerRenamed(previousName, Context.Client.Name)` in `OnRenamedAsync`.
  - `IBleHubHost.Rename("TTT 2")` changes the host's name without stopping anything. It re-advertises under the new name, and every connected client's `HostName` updates and `HostRenamed` is raised, in order with hub events. While stopped, it sets the name for the next `Start`.
  - Older hosts refuse a client's rename with a `BleHubRemoteException`. Older clients ignore a host rename and keep the handshake's name.

  ```csharp
  // hub
  public override Task OnRenamedAsync(string? previousName)
      => this.Clients.Others.PlayerRenamed(previousName, this.Context.Client.Name);   // throw to refuse

  // client
  client.HostRenamed += (_, name) => ...;                                            // client.HostName is already updated
  await client.Rename("Allan B");
  ```
- **One service, a characteristic per hub**: every hub on a host is a characteristic inside one GATT service, set with `server.ServiceUuid(...)` (`BleHubHostOptions.ServiceUuid`). It is the only UUID the host advertises (two 128-bit UUIDs overflow the 31 byte advertisement) and the one clients scan for (`BleHubClientOptions.ServiceUuid`), so set it the same on both sides. Both default to `BleHubProtocolOptions.DefaultServiceUuid`, which is shared by every app using this library, so set your own to keep other apps' hosts out of your scans. A scan can't tell which hubs a host is running: joining a stopped hub is refused by the handshake. Hub clients on the same device share one BLE connection.

- **Media channels** (live audio and video): an ordered, two-way pipe of raw binary packets beside each hub connection, on an L2CAP PSM of its own. It skips the hub's framing and serializer, so it suits a stream of audio and video frames.

  ```csharp
  // host
  services.AddBleHubServer(server => server
      .Host(o => o.EnableMediaChannels(maxPacketSize: 128 * 1024))      // opens a second L2CAP PSM on Start
      .AddHub<CallHub>(CallCharacteristicUuid));

  hubContext.MediaChannelOpened += async (_, e) =>                       // e.Client, e.Channel (ConnectionId = e.Client.Id)
  {
      await foreach (var packet in e.Channel.Receive())
          await e.Channel.Send(packet);                                  // echo
  };
  var channel = hubContext.GetMediaChannel(connectionId);                // null when that client has none open

  // client
  if (client.CanOpenMediaChannel)
  {
      var media = await client.OpenMediaChannel();                       // returns the one already open, if any
      await media.Send(frame);
      await foreach (var packet in media.Receive(ct)) { ... }
  }
  ```

  - `IBleHubMediaChannel`: `Send` (serialized; packets arrive whole and in order, or the channel closes), `Receive` (one reader at a time; completes when the channel closes), `MaxPacketSize` (the host's setting; a bigger packet throws `ArgumentException`), `IsOpen`, `Completion` and `DisposeAsync`. Packets nobody is reading yet are buffered.
  - The channel closes when either side disposes it, when its L2CAP link fails, and when the hub connection ends: the client disconnects or drops, or the hub stops. A client can open a new one after that.
  - Over BLE, the handshake ack carries the media PSM and a random per-connection token. The client's first frame on the channel is that token, and the host answers `[0x01]` or closes it, so only a connected client gets a channel, and only its own. Frames are `[u32 LE length][bytes]` (`MediaFrameCodec`).
  - `CanOpenMediaChannel` is false when the host didn't enable media channels, the host is older than 1.4.0, or the device has no L2CAP (iOS 11+ / Android 10+). The hubs keep working without it. `OpenMediaChannel` then throws `NotSupportedException`.
  - Other transports can carry it too, through the hidden seams `IBleHubClientTransport.OpenMediaChannel` and `IBleHubTransportEndpoint.MediaChannelOpened`. Shiny.UniversalHubs carries it over Wi-Fi.

- **Over Wi-Fi too**: [Shiny.UniversalHubs](https://github.com/shinyorg/hubs), from the same repo, serves the same hubs over Wi-Fi (with mDNS discovery) alongside BLE, and lets clients connect over whichever transport is available, with no change to hub or contract code. It builds on hidden transport seams in this library (`IHubContext<THub>.TransportEndpoint`, `BleHubClient.ConnectExternal`).

See [PLAN.BluetoothLE.Hubs.md](PLAN.BluetoothLE.Hubs.md) for the wire protocol, the design decisions and the roadmap.

## Sample

The [repo's sample](https://github.com/shinyorg/hubs/tree/v1/samples) is a tic-tac-toe game and an always-on
chat (with calls, photos and files) for iOS, Android and macOS. It hosts and joins hubs over BLE and Wi-Fi.

BLE needs two physical devices: simulators and emulators have no usable Bluetooth.

## Tests

```bash
dotnet test tests/Shiny.BluetoothLE.Hubs.Tests
```

The tests run the generated hub dispatcher and the generated client proxy against each other through an in-memory "radio", plus diagnostic tests for the generator. No hardware is needed.

Documentation and release notes: https://shinylib.net/blehubs/
