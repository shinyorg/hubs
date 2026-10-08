# Shiny.SwitchboardR

**[Shiny.BluetoothLE.Hubs](https://github.com/shinyorg/bluetoothle-hubs) over Wi-Fi as well as Bluetooth LE.** Write the hub
once. Players on a plane connect over BLE. At home they connect over Wi-Fi, which has more range and bandwidth and no MTU.
Both kinds of client share the same hub, groups and pushes.

- **Host**: the same `BleHub<T>` is served over GATT and over a [Shiny.Net.HttpServer](https://github.com/shinyorg/httpserver)
  Switchboard (SSE + POST, with resume), and advertised over mDNS (`_shinyhub._tcp`).
- **Client**: one discovery list merges BLE scanning and mDNS browsing. Connect prefers Wi-Fi and falls back to BLE. The
  generated proxy, events, `Disconnected` and file methods work the same on either transport.
- **Files**: L2CAP over BLE, plain HTTP over Wi-Fi, behind the same `UploadFile` / `DownloadFile`.

| Package | What it is |
|---|---|
| `Shiny.SwitchboardR.Host` | `AddSwitchboardR().AddHub<THub>()`, `ISwitchboardRHost` |
| `Shiny.SwitchboardR.Client` | `AddSwitchboardRClient<TContract>()`, `ISwitchboardRClient<TContract>` |
| `Shiny.SwitchboardR` | The shared Wi-Fi wire protocol. Comes with both. |

## Host

Register the hub with blehubs as usual, then serve it over Wi-Fi too:

```csharp
services.AddBluetoothLeHosting();
services.AddBleHub<GameHub>(ServiceUuid, CharacteristicUuid, o => o.MaxClients = 6);
services.ConfigureBleHubHost(o => o.EnableFileTransfers(filesDir));       // L2CAP and HTTP both use this

services.AddShinyHttpServer(http =>
{
    http.Options.Address = IPAddress.Any;   // reachable from other devices
    http.Options.Port = 0;                  // any free port - mDNS publishes it
    http.AddSwitchboardR(o => o.AuthorizeFile = req => req.FileName.StartsWith("avatar-")).AddHub<GameHub>();
}, autoStart: false);
```

```csharp
public class Lobby(ISwitchboardRHost host)
{
    // BLE, Wi-Fi or both - whichever this device can do right now
    public Task<HubTransports> Open() => host.Start(HubTransports.All);
    public Task Close() => host.Stop("The host ended the game");
}
```

Hub code doesn't change. `Clients.All`, groups, `IHubContext<THub>`, `MaxClients`, `ValidateClient`, `Context.Abort` and
`OnConnectedAsync` / `OnDisconnectedAsync` cover BLE and Wi-Fi clients together. Wi-Fi connection ids start with `sb:`.

## Client

```csharp
services.AddBluetoothLE();
services.AddSwitchboardRClient<IGameHub>(ServiceUuid, CharacteristicUuid);
```

```csharp
public class JoinViewModel(ISwitchboardRClient<IGameHub> client)
{
    // a host seen over BLE and Wi-Fi is one HubHostInfo; Transports == None means it has gone
    IDisposable Scan() => client.DiscoverAll().Subscribe(host => /* host.Name, host.Transports, host.Rssi */);

    async Task Join(HubHostInfo host)
    {
        await client.Connect(host, new HubConnectOptions("Allan") { Prefer = HubTransports.Wifi });
        // client.Transport is Wifi or Ble
        await client.Hub.MakeMove(4);
    }
}
```

- `Prefer` is tried first when the host is reachable both ways, and the other transport is the fallback. `Allowed`
  limits which transports may be used at all.
- A refusal (host full, `ValidateClient`, not running) is not retried on the other transport.
- `ISwitchboardRClient<T>` is an `IBleHubClient<T>`. `Discover()` and `Connect(BleHubHostInfo)` still give BLE-only behavior.
- A short Wi-Fi drop is resumed: the line keeps its id and groups, and missed pushes are replayed. The hub sees no
  disconnect. Turn this off with `SwitchboardRClientOptions.AutomaticReconnect = false`.

## Platform setup

| | Host | Client |
|---|---|---|
| iOS | `NSBluetoothAlwaysUsageDescription`, `NSLocalNetworkUsageDescription`, `NSBonjourServices` = `_shinyhub._tcp` | the same, plus `NSAppTransportSecurity` / `NSAllowsLocalNetworking` (games are plain HTTP on the LAN) |
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

`samples/TicTacToe` is the Shiny.BluetoothLE.Hubs tic-tac-toe game, hosting over both transports. The join list shows how
each game is reachable, and the game screen says which transport is in use.

```
dotnet build samples/TicTacToe -f net10.0-android -t:Run
dotnet build samples/TicTacToe -f net10.0-ios -t:Run
```
