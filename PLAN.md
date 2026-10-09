# Shiny.UniversalHubs — Plan

**Goal:** a game (or any hub app) connects over BLE when the players are close together without a usable network, such as on
a plane, and over Wi-Fi at home. Hub contracts, hub classes and app code stay the same. The client picks the transport when
it connects.

UniversalHubs adds Wi-Fi to [Shiny.BluetoothLE.Hubs](../blehubs), which it references **through NuGet**:
- The Wi-Fi side is [Shiny.Net.HttpServer.Switchboard](../httpserver): SSE downstream, POST upstream, with resume.
- Discovery is mDNS through Shiny.Net.HttpServer.Discovery.

## 1. Packages

| Package | Contents |
|---|---|
| `Shiny.UniversalHubs` | Wire protocol: `UniversalHubProtocol`, handshake records, `HubJson` (value re-framing and envelopes), `HubTransports` |
| `Shiny.UniversalHubs.Host` | `AddUniversalHubs().AddHub<THub>()`, `IUniversalHubHost`, the hub Switchboard, file routes, mDNS advertising |
| `Shiny.UniversalHubs.Client` | `AddUniversalHubClient<T>()`, `IUniversalHubClient<T>`, merged discovery, the Switchboard transport, preference and fallback |

## 2. What blehubs provides (its transport seams)

These hidden (`[EditorBrowsable(Never)]`) seams were added to blehubs for this library. blehubs owns their code and
tests, and this section is their design doc. Change them in blehubs and consume the new version here.

### 2.1 Host: `IHubContext<THub>.TransportEndpoint` → `IBleHubTransportEndpoint`

- `Connect(connectionId, HandshakeInfo, IBleHubPeerChannel, ct)`:
  - applies the same rules as a BLE handshake: `MaxClients` (counted across every transport) and `ValidateClient`
  - registers the client and runs `OnConnectedAsync` before returning
  - returns null when accepted, otherwise the rejection
  - does not check the protocol version or whether the hub is running; the transport checks its own
- `Invoke` runs the generated dispatcher. It returns `BleHubInvocationResult(Result, AbortRequested, AbortReason)`: the
  transport sends the reply first, then calls `Disconnect`, so `Context.Abort` keeps its "after the reply" ordering.
  Hub exceptions are thrown to the transport, which relays them.
- `Stream` works the same way. An abort inside a stream disconnects once the stream completes.
- `Disconnected(connectionId, HubDisconnect)`: the client left. Runs `OnDisconnectedAsync` and leaves its groups.
- `Disconnect(connectionId, HubDisconnect)`: the hub ends the session through the channel
  (`IBleHubPeerChannel.Disconnect(HubDisconnect)`).
- `FindClient(connectionId)`.
- **Inside `HubRuntime`** these are ordinary peers with a `Channel`:
  - pushes go through `IBleHubPeerChannel.Push`, under the same per-peer send lock as BLE, so order holds
  - disconnects go through `IBleHubPeerChannel.Disconnect`
  - the BLE `SubscribedCentrals` sweep skips them
  - `IHubContext.Stop` disconnects them like every other client

### 2.2 Client: `BleHubClient.ConnectExternal(events => IBleHubClientTransport, options, ct)`

- The transport does `Handshake`, `Invoke`, `Stream`, `Upload`, `Download` and `Close`. It reports host pushes and closes
  through `IBleHubClientTransportEvents`.
- `BleHubClient` keeps its guarantees on top of any transport:
  - `RequestTimeout` and `MaxPayloadSize`
  - pushes raised one at a time, in order, through the push channel
  - pending calls fail with `BleHubDisconnectedException` when the transport reports a close
  - `Disconnected` carries the `HubDisconnect`, and so do `StatusChanged` and `BleHubDisconnectedException`. The
    transport reports it through `IBleHubClientTransportEvents.Closed(HubDisconnect)`.
  - `UploadFile` / `UploadStream` / `DownloadFile` route to the transport
- `ExternalTransport` is set while connected this way. `Host` (a BLE peripheral) is null.

Tested in blehubs: `ExternalTransportTests`, which joins both seams in memory, including BLE and external clients sharing groups and
pushes. The sweep is tested in `BleHubHostTests.SweepLeavesClientsOnOtherTransportsAlone`.

## 3. Wire protocol (v2, `UniversalHubProtocol.Version`)

Each hub is one Switchboard at `{BasePath}/{characteristicUuid}`, `/hubs/...` by default. The hub's own methods are not
Switchboard methods. Hand-registered reserved methods carry them by name, exactly like the BLE frames do:

| Call | Args | Returns |
|---|---|---|
| `hub.handshake` | `{protocol, name, appVersion, properties}` | `{accepted, reason, hostName, hostId, fileToken}` |
| `hub.invoke` | `method`, `[args...]` | `{"result": value}`, `{}` for Task, or `{"error": {type, message}}` |
| `hub.stream` | `method`, `[args...]` | a stream of `{"item": value}`, possibly ending with one `{"error": ...}` |
| `hub.push` (host → client) | `event`, `[args...]` | – |

- **Values.** Each value is the exact JSON `IBleHubSerializer` produced. The library re-frames it between BLE's
  `[count][len][bytes]` argument format and a JSON array, and never re-serializes it. A serializer that doesn't produce
  JSON fails with an explanatory error.
- **Errors.** They travel in envelopes, not as Switchboard errors, so the remote exception type and message survive as
  `BleHubRemoteException`, the same as over BLE. Unknown or wrong-kind methods report `HubMethodNotFound`.
- **Handshake.** It is the first call on a line. Calls made before it fail with "Handshake required". The host refuses:
  - other protocol versions
  - any handshake while Wi-Fi hosting is stopped
  - whatever the hub's own rules refuse
- **Disconnect.**
  - A hub-initiated disconnect is a Switchboard hang-up with `allowReconnect: false`. `Context.Abort` hangs up after
    the reply has been sent.
  - The hang-up message is a `HubHangUp` JSON, `{"reason": <HubDisconnectReason>, "message": ...}`. Switchboard
    delivers it with the close, so the client reads the exact reason (`ServerDisconnect` vs `ServerShutdown`). The
    host's own `OnDisconnectedAsync(DisconnectContext)` reads it back too.
  - Closes without that message map by how the line closed:

    | | ClientDisconnect | ClientTimeout | ServerDisconnect | ServerShutdown | ConnectionFailed |
    |---|---|---|---|---|---|
    | Host | `ClientClosed` | `Dropped` | `HungUp` | `ServerShutdown` | `Faulted` |
    | Client | `ClientClosed` | `Dropped`, `RetriesExhausted` | `HungUp` | `ServerClosed` | `Faulted` |

  - v2 added the `HubHangUp` message.
- **Resume.** Switchboard's resume window means a dropped line that comes back keeps its connection id, so the hub sees
  nothing. `OnDisconnectedAsync` runs only when the line is gone for good.
- **Files.** Plain HTTP, not Switchboard:
  - `PUT` / `GET {BasePath}/files/{name}`, with `X-Hub-File-Token` set to the token from the handshake.
  - They follow `BleHubHostOptions.EnableFileTransfers`: the directory, uploads/downloads allowed, overwrite and
    `MaxUploadSize`, plus `UniversalHubHostOptions.AuthorizeFile`.
  - Status codes: 400 traversal or bad name, 401 token, 403 refused, 404 missing or files off, 409 exists, 411 no length,
    413 too big.

### 3.1 `UniversalHub<TContract>`

- Lives in `Shiny.UniversalHubs.Host` and derives from `BleHub<TContract>`. The blehubs generator walks base types and skips
  abstract and generic classes, so hubs built on it get their dispatchers as before. No blehubs change was needed.
- Seals `OnConnectedAsync()` / `OnDisconnectedAsync(...)` and calls `OnConnectedAsync(HubConnection)` /
  `OnDisconnectedAsync(HubConnection, HubDisconnect)` instead. `this.Connection` gives hub methods the caller.
- `HubConnection(Id, Transport, Client)`: `Id` is the blehubs connection id, either the peripheral UUID (BLE) or
  `sb:{lineId}` (Wi-Fi). `Transport` is `HubTransports.Ble` or `HubTransports.Http`, worked out from the `sb:` prefix,
  which this repo owns (`HubConnection.HttpPrefix`). The prefix is host-internal, so it isn't part of the wire version.

### 3.2 Universal server registration

- **Terms:** a *universal hub* is one hub managed over both HTTP and BLE. A *universal server* hosts them.
- **Host:** `AddUniversalServer(server => ...)` is one call:
  - `.Ble(BleHubHostOptions)`, `.Http(UniversalHubHostOptions)` and `.HttpServer(ShinyHttpServerBuilder)` set up the two
    sides.
  - `.AddHub<THub>(serviceUuid, characteristicUuid, mdnsServiceType, BleHubOptions)` adds each hub, with the mDNS
    service type it's advertised under.
  - `server.Endpoint` is the base path, `/hubs` by default.
- **What it wires:** `AddBleHub` and `ConfigureBleHubHost` on the BLE side, and on the HTTP side
  `AddShinyHttpServer(autoStart: false)`, `AddUniversalHubs` (with `BasePath` set) and `AddHub(mdnsServiceType)`.
- **HttpServer address:** a server it creates binds `IPAddress.Any` on port 0. One already registered with
  `AddShinyHttpServer` keeps its own address and port.
- **Validation:**
  - It is called once, with at least one hub.
  - The service type must look like `_name._tcp`.
  - A hub can't be added twice.
- **Per-hub service type:** `UniversalHubHost` groups hubs by service type (`GetAdvertisements`) and publishes one
  advertisement per group. The TXT keys are unchanged, so the wire version is the same.
- **Endpoint over the wire:** the endpoint is a base path rather than a per-hub path, so it travels in the existing TXT
  `path` key and needs no wire change.
- **Client:** `AddUniversalHubClient<TContract>(serviceUuid, characteristicUuid, mdnsServiceType)` is
  `AddUniversalHubClient` with the service type set.
- **BLE stacks:** the host and client packages target `net10.0`, `-android`, `-ios` and `-maccatalyst`
  (`PlatformTargetFrameworks` in Directory.Build.props). On the platform targets:
  - `AddUniversalServer` calls `AddBluetoothLeHosting()`, and `AddUniversalHubClient` calls `AddBluetoothLE()`.
  - Both are idempotent in Shiny.
  - On Apple, a custom `AppleBleConfiguration` has to be registered first, because Shiny uses `TryAdd` for it.
- **Plain `net10.0`:** it has no Shiny BLE stack, so the app (or a test) registers one. Unit tests run on `net10.0` and
  can't see these calls; the sample is what exercises them.

### 3.3 One hub at a time

- **API:** `IUniversalHubHost.StartHub<THub>(transports)` / `StopHub<THub>(reason)` / `GetRunningTransports<THub>()`.
  `Start`/`Stop` are the same calls for every hub.
- **Bindings:** each `HubBinding` carries its own `IsHttpRunning`, and handshakes check the hub's flag.
- **The HTTP server:** it starts with the first hub hosted over HTTP and stops with the last, when `ManageHttpServer`
  is on.
- **mDNS:** it is re-advertised on every change, with only the running hubs.
- **BLE:** it goes through blehubs' `IHubContext<THub>.Start/Stop`.
  - Stopping a hub there also disconnects its remaining HTTP clients, which is what a per-hub stop wants.
  - `Stop()` of everything uses `IBleHubHost.Stop`, so the default "Host stopped" message stays.

### 3.4 Tunnels

- **Per hub:** `AddHub(..., allowTunnel)` and `UniversalHubOptions.AllowTunnel`. Off by default.
- **The gate:** middleware registered by `AddUniversalHubs` runs before routing. A request with
  `ctx.Connection.IsTunneled` gets a 404 for any hub route (`{BasePath}/{uuid}` and below) whose hub doesn't allow
  tunnels. `TunnelGate.IsAllowed` holds the rule and is unit-tested, because the in-memory test transport can't mark a
  connection as tunnelled.
- **File routes:** open through a tunnel when any hub allows it, because the token still ties each request to a client.
- **The tunnel itself:** belongs to the app (`.HttpServer(http => http.AddQuickTunnel())`), so this library doesn't
  depend on Shiny.Net.HttpServer.Ssh.
- **Reaching it:** `HubHostInfo.FromUrl(serverUrl, endpoint)` builds an HTTP-only host for an address learned some other
  way (a tunnel URL, typed, a QR code). `Connect` doesn't check TXT records.

### 3.5 What a hub sees of a connection

`HubConnection` (on `UniversalHub`) has three extra properties:
- **`Properties`:** blehubs' per-connection `Items`. It is the same bag on both transports.
- **`Ble`:** `BleHubConnectedClient.Peripheral`, which blehubs added: the central, or null on other transports.
- **`Http`:** Switchboard's `CallerContext` for the call being run.
  - `HubBinding` sets it in an `AsyncLocal` around the handshake (`OnConnectedAsync`), invoke and stream calls.
  - It is cleared for `OnDisconnectedAsync`, because Switchboard pools `HttpContext`s and they can't be held past the
    request.

## 4. Discovery

- **mDNS** advertises one instance per service type per host. Each hub picks its service type (`AddHub(...,
  mdnsServiceType)`); `_shinyhub._tcp` is the lower-level default. Each advertisement's `hubs` lists only the hubs of
  that type. The instance name is `BleHubHostOptions.LocalName`.
  The TXT record carries:
  - `proto`
  - `hostId`
  - `name`
  - `path` (the base path)
  - `hubs` (characteristic UUIDs, comma separated, at most about 6)
- **The client** merges BLE scan results and mDNS results by name, because Apple hosts can't put a host id in a BLE
  advertisement. `hostId` comes from TXT and from the handshake reply. mDNS hosts are filtered by `hubs` containing the
  contract's characteristic UUID and by `proto`.

## 5. Decisions

| Question | Decision |
|---|---|
| Where it lives | Its own repo, referencing blehubs through NuGet. blehubs only gained hidden transport seams |
| Wi-Fi transport | Shiny.Net.HttpServer.Switchboard, with resume on |
| Hub dispatch over Wi-Fi | The blehubs generated dispatcher, through reserved `hub.*` methods on a hand-registered `SwitchboardDescriptor`. The Switchboard generator is disabled in the host package because it rejects generic boards |
| Values | Re-framed JSON, never re-serialized. A non-JSON `IBleHubSerializer` isn't supported over Wi-Fi |
| Files over Wi-Fi | Plain HTTP routes owned by this library, with a token from the handshake |
| Transport choice | At connect. `Prefer` Wi-Fi by default, falling back to the other. A refusal isn't retried. No migration mid-session |
| Start/stop | `IUniversalHubHost` starts and stops both transports, for every hub or one hub (`StartHub<THub>`). A transport that can't start is skipped if the other starts |
| Tunnels | Each hub opts in (`AllowTunnel`). A gate returns 404 for the others. The tunnel provider is the app's choice |

## 6. Status

- **2026-10-08**: v1 built. Done so far:
  - host, client and sample
  - 22 tests (real host and client over an in-memory HTTP server), covering calls, errors, streams, ordered pushes, groups
    and `IHubContext` across Wi-Fi clients, abort, host/hub disconnect, stop/refusal, MaxClients, files and file rules,
    unknown methods and protocol version
  - blehubs 76 tests, including BLE and external clients sharing groups and pushes
  - The sample builds for Android and iOS.
  - Consumes blehubs `1.0.0-alpha-0006` from nuget.org, the first version with the transport seams.
  - **Not yet verified:** real devices. See §7.
- **2026-10-08**: `UniversalHub<T>` and `HubConnection` (§3.1). Its lifecycle hooks report the connection id and the
  transport. The sample's `GameHub` uses it. 26 tests.
- **2026-10-08**: `HubTransports.Wifi` renamed to `Http`, along with the identifiers built on it.
- **2026-10-08**: `AddUniversalServer` / `AddUniversalHubClient` (§3.2). The tests and the sample use them, and the
  sample's service type is `_unigame._tcp`. 36 tests.
- **2026-10-08**: The BLE stacks are registered automatically on Android, iOS and Mac Catalyst. The host and client
  packages now multi-target, and CI adds the `maui-maccatalyst` workload.
- **2026-10-08**: The mDNS service type moved from the universal server to each hub. The host publishes one
  advertisement per service type. 37 tests.
- **2026-10-08**: Typed disconnect reasons (`HubDisconnect` / `HubDisconnectReason`, added in blehubs) on both
  transports, for the host and the client. The protocol is now v2: hang-ups carry a `HubHangUp`. 49 tests.
  - **Needs a blehubs release.** The changes were built against a locally packed blehubs, so `BleHubsVersion` must be
    bumped once blehubs publishes.
- **2026-10-08**: Per-hub start/stop on both transports (§3.3), per-hub tunnels with `HubHostInfo.FromUrl` (§3.4), and
  `HubConnection.Properties`/`Ble`/`Http` (§3.5).
  - blehubs gained `BleHubConnectedClient.Peripheral`. 62 tests.
  - The sample now has:
    - an always-on chat: `ChatHub`, Shiny.Maui.Controls `ChatView` and Shiny.Notifications
    - Shiny.Actors (`GameActor` per game, `ConversationActor` per conversation)
    - a quick tunnel for the chat
    - a macOS AppKit client (`samples/Sample.MacOS`)
  - **Shiny.Actors:** not on nuget.org yet, so the sample references a local build.
- **2026-10-09**: Renamed from SwitchboardR to **Shiny.UniversalHubs**: packages, namespaces and types
  (`IUniversalHubHost`, `IUniversalHubClient<T>`, `UniversalHubProtocol`, `UniversalHub<T>`, `AddUniversalHubs`).
  The wire is unchanged (service type, reserved methods, TXT keys and routes), so `Version` stays 2.

## 7. Verifying on hardware

You need two physical devices. Run the sample on both and check:
- **Wi-Fi only:** host and join on the same network. The join list shows 📶 and the game says "Connected over Wi-Fi".
- **Bluetooth only:** airplane mode on with Bluetooth re-enabled. Shows 🔵. Pushes and avatars work.
- **Both:** the host appears once with both badges. Toggling "Prefer Bluetooth" changes the transport.
- **Isolated Wi-Fi** (guest network with client isolation): the host should fall back to BLE.
- **Mixed:** one player on Wi-Fi and a spectator on BLE. Both see every move.
- **Host stop:** reaches the client's Disconnected as `ServerShutdown` with the message, on both transports.
- **Disconnect reasons:**
  - The client leaving shows `ClientDisconnect` on the host.
  - Walking a BLE client out of range, or killing its app, shows `ClientTimeout`.
  - A kick shows `ServerDisconnect` on both sides.
- **Wi-Fi blip:** toggle Wi-Fi off and on quickly on the client. The game resumes without a disconnect.
- **Always-on chat:**
  - With the app open on two devices, each one lists the other under "Message someone".
  - A message raises a local notification unless that conversation is on screen.
  - Hosting and ending a game leaves the chat connected.
- **Tunnel:**
  - "Share on the internet" shows a public URL. A device on cellular can open it through "Address someone shared".
  - Through that URL the game route answers 404.
- **Mac client:** `samples/Sample.MacOS` lists the phones' chats and games over mDNS, can chat with them, and can
  join a game.

## 8. Future

- Migrating a live session between transports (BLE → Wi-Fi as a player walks in the door)
- A browser spectator page talking to the hub Switchboard directly
- Upload and download progress events on the host for Wi-Fi files
- Wi-Fi hosting on devices with no BLE hosting stack (Windows)
