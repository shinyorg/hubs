# Shiny.SwitchboardR — Plan

**Goal:** a game (or any hub app) connects over BLE when the players are close together without a usable network, such as on
a plane, and over Wi-Fi at home. Hub contracts, hub classes and app code stay the same. The client picks the transport when
it connects.

SwitchboardR adds Wi-Fi to [Shiny.BluetoothLE.Hubs](../blehubs), which it references **through NuGet**:
- The Wi-Fi side is [Shiny.Net.HttpServer.Switchboard](../httpserver): SSE downstream, POST upstream, with resume.
- Discovery is mDNS through Shiny.Net.HttpServer.Discovery.

## 1. Packages

| Package | Contents |
|---|---|
| `Shiny.SwitchboardR` | Wire protocol: `SwitchboardRProtocol`, handshake records, `HubJson` (value re-framing and envelopes), `HubTransports` |
| `Shiny.SwitchboardR.Host` | `AddSwitchboardR().AddHub<THub>()`, `ISwitchboardRHost`, the hub Switchboard, file routes, mDNS advertising |
| `Shiny.SwitchboardR.Client` | `AddSwitchboardRClient<T>()`, `ISwitchboardRClient<T>`, merged discovery, the Switchboard transport, preference and fallback |

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
- `Disconnected(connectionId, reason)`: the client left. Runs `OnDisconnectedAsync` and leaves its groups.
- `Disconnect(connectionId, reason)`: the hub ends the session through the channel.
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
  - `Disconnected` carries the reason
  - `UploadFile` / `UploadStream` / `DownloadFile` route to the transport
- `ExternalTransport` is set while connected this way. `Host` (a BLE peripheral) is null.

Tested in blehubs: `ExternalTransportTests`, which joins both seams in memory, including BLE and external clients sharing groups and
pushes. The sweep is tested in `BleHubHostTests.SweepLeavesClientsOnOtherTransportsAlone`.

## 3. Wire protocol (v1, `SwitchboardRProtocol.Version`)

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
- **Disconnect.** A hub-initiated disconnect is a Switchboard hang-up with `allowReconnect: false`, and its reason reaches
  the client's `Disconnected`. `Context.Abort` hangs up after the reply has been sent.
- **Resume.** Switchboard's resume window means a dropped line that comes back keeps its connection id, so the hub sees
  nothing. `OnDisconnectedAsync` runs only when the line is gone for good.
- **Files.** Plain HTTP, not Switchboard:
  - `PUT` / `GET {BasePath}/files/{name}`, with `X-Hub-File-Token` set to the token from the handshake.
  - They follow `BleHubHostOptions.EnableFileTransfers`: the directory, uploads/downloads allowed, overwrite and
    `MaxUploadSize`, plus `SwitchboardRHostOptions.AuthorizeFile`.
  - Status codes: 400 traversal or bad name, 401 token, 403 refused, 404 missing or files off, 409 exists, 411 no length,
    413 too big.

## 4. Discovery

- **mDNS** advertises one instance per host, type `_shinyhub._tcp`. The instance name is `BleHubHostOptions.LocalName`.
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
| Start/stop | `ISwitchboardRHost` starts and stops both. A transport that can't start is skipped if the other starts. Per-hub start/stop stays BLE-only |

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

## 7. Verifying on hardware

You need two physical devices. Run the sample on both and check:
- **Wi-Fi only:** host and join on the same network. The join list shows 📶 and the game says "Connected over Wi-Fi".
- **Bluetooth only:** airplane mode on with Bluetooth re-enabled. Shows 🔵. Pushes and avatars work.
- **Both:** the host appears once with both badges. Toggling "Prefer Bluetooth" changes the transport.
- **Isolated Wi-Fi** (guest network with client isolation): the host should fall back to BLE.
- **Mixed:** one player on Wi-Fi and a spectator on BLE. Both see every move.
- **Host stop:** reaches the client's Disconnected with the reason, on both transports.
- **Wi-Fi blip:** toggle Wi-Fi off and on quickly on the client. The game resumes without a disconnect.

## 8. Future

- Migrating a live session between transports (BLE → Wi-Fi as a player walks in the door)
- A browser spectator page talking to the hub Switchboard directly
- Upload and download progress events on the host for Wi-Fi files
- Wi-Fi hosting on devices with no BLE hosting stack (Windows)
