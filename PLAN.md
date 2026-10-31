# Shiny.UniversalHubs — Plan

**Goal:** a game (or any hub app) connects over BLE when the players are close together without a usable network, such as on
a plane, and over Wi-Fi at home. Hub contracts, hub classes and app code stay the same. The client picks the transport when
it connects.

UniversalHubs adds Wi-Fi to [Shiny.BluetoothLE.Hubs](PLAN.BluetoothLE.Hubs.md), which lives in this repo (`src/Shiny.BluetoothLE.Hubs*`)
since 2026-10-23 and is referenced by project:
- The Wi-Fi side is [Shiny.Net.HttpServer.Switchboard](../httpserver): SSE downstream, POST upstream, with resume.
- Discovery is mDNS through Shiny.Net.HttpServer.Discovery.

## 1. Packages

| Package | Contents |
|---|---|
| `Shiny.UniversalHubs` | Wire protocol: `UniversalHubProtocol`, handshake records, `HubJson` (value re-framing and envelopes), `HubTransports` |
| `Shiny.UniversalHubs.Host` | `AddUniversalHubs().AddHub<THub>()`, `IUniversalHubHost`, the hub Switchboard, file routes, mDNS advertising |
| `Shiny.UniversalHubs.Client` | `AddUniversalHubClient<T>()`, `IUniversalHubClient<T>`, merged discovery, the Switchboard transport, preference and fallback |
| `Shiny.UniversalHubs.Multipeer` | The MultipeerConnectivity transport (§10): `server.Multipeer(...)`, `o.Multipeer(...)`, host and client in one. `net10.0` (the transport-free core) plus iOS, Mac Catalyst and macOS |

## 2. What blehubs provides (its transport seams)

These hidden (`[EditorBrowsable(Never)]`) seams live in `Shiny.BluetoothLE.Hubs*`, with their tests in
`tests/Shiny.BluetoothLE.Hubs.Tests`; this section is their design doc. Change them there and in UniversalHubs in the
same change.

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

- **Media channels.** A WebSocket, not Switchboard: `GET {BasePath}/media` with `X-Hub-Media-Token` set to the
  handshake reply's `mediaToken`. One binary message is one packet, at most `mediaMaxPacketSize` bytes. Both reply
  fields are null when the host has no media channels (`BleHubHostOptions.EnableMediaChannels`). The channel reaches the
  hub through `IBleHubTransportEndpoint.MediaChannelOpened`, like an L2CAP one. Status codes: 401 token, 404 no media
  channels (or tunnelled to a hub that doesn't allow it), 400 not an upgrade. The sample's calls (§9) are its first user.
  - The fields are optional, so `Version` stays 2: an older client ignores them, and an older host's reply means "no
    media channels".

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
  - `.AddHub<THub>(characteristicUuid, mdnsServiceType, BleHubOptions)` adds each hub, with the mDNS service type it's
    advertised under. Over BLE every hub is a characteristic in one service, `.ServiceUuid(...)` (blehubs'
    `DefaultServiceUuid` unless set). `.Protocol(...)` sets blehubs' protocol limits.
  - `server.Endpoint` is the base path, `/hubs` by default.
- **What it wires:** blehubs' `AddBleHubServer` on the BLE side (every hub, `.Ble` as its `Host`), and on the HTTP side
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
- **Client:** `AddUniversalHubClient<TContract>(characteristicUuid, mdnsServiceType, configure)` is
  `AddUniversalHubClient` with the service type set. `UniversalHubClientOptions.ServiceUuid` goes to blehubs'
  `AddBleHubClient`.
- **BLE stacks:** blehubs registers them (its host and client packages multi-target too). On Android, iOS and Mac
  Catalyst, `AddBleHubServer` calls `AddBluetoothLeHosting()` and `AddBleHubClient` calls `AddBluetoothLE()`.
  - Both are idempotent in Shiny.
  - On Apple, blehubs' client registers an `AppleBleConfiguration` with iOS's background alerts off. A custom one has to
    be registered first, because Shiny uses `TryAdd` for it.
  - This repo's packages still target `net10.0`, `-android`, `-ios` and `-maccatalyst` (`PlatformTargetFrameworks`), so
    the platform blehubs assets flow through.
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
- **BLE** advertises one service UUID per host (`server.ServiceUuid`, blehubs' default unless set) whatever hubs are running, so a
  BLE result says "a hub host", not which hubs. A client joining a stopped hub is refused at the handshake.
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
| Calls (2026-10-23) | Ride a *media channel* beside the hub (blehubs L2CAP over BLE, a WebSocket over Wi-Fi), not hub methods, so contracts don't change and hub logic stays in blehubs. Signalling travels in the same pipe. The host relays client-to-client calls |
| Call codecs (2026-10-23) | Both: H.264 (hardware) when both sides have it, JPEG frames otherwise. No common codec answers with audio rather than refusing. PCM16 over Wi-Fi, IMA ADPCM over BLE |
| Call pacing (2026-10-23) | Latest-wins video with key-frame recovery, audio first and capped at 200 ms queued: a slow link costs frames, never latency |
| Where calls live (2026-10-23) | The libraries gain capabilities (media channels, duplex voice, live video encode and view). The call itself is sample code, not a package |
| Multipeer (2026-10-30) | A third transport in its own package, Apple only (§10). It plugs into the same blehubs seams as HTTP, through internal add-on seams in the host and client packages. `HubTransports.All` includes it; where it can't run it is skipped |
| BLE service UUID (2026-10-09) | One for every hub (`server.ServiceUuid`, blehubs' default unless set); hubs are characteristics. Two 128-bit UUIDs overflow a 31-byte advertisement: Android refuses to advertise, iOS hides the second in an overflow area Android can't see. The cost: a BLE scan can't tell which hubs a host runs |

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
- **2026-10-09**: One BLE service UUID for every hub. `AddHub` and `AddUniversalHubClient` no longer take a service
  UUID: the server sets it once (`.ServiceUuid(...)`), clients in their options, and both default to blehubs'. Fixes the first device run: with the always-on chat and the
  game on separate services, an Android host couldn't advertise the game over BLE (and lost the chat's advertisement
  too), and Android clients couldn't see an iOS host.
  - `UniversalHubClient` passes through blehubs' new `ClientName`, `HostRenamed` and `Rename`. Over Wi-Fi, `Rename`
    throws NotSupportedException: the Switchboard transport doesn't carry renames yet (§8).
  - Built on blehubs 1.1's one-call registration: `AddUniversalServer` calls `AddBleHubServer` (with `.ServiceUuid`
    and `.Protocol` passed through), and `AddUniversalHubClient` calls `AddBleHubClient`. blehubs now registers the
    platform BLE stacks, so this repo no longer does. On Apple its client turns iOS's background alerts off
    (`NotifyOnConnection` / `NotifyOnDisconnection` / `NotifyOnNotification`). Hubs are foreground only. An app's own
    configuration, registered first, still wins.
  - `SwitchboardTransport`'s HttpClient has no overall timeout. The default 100s cut the line's event stream every
    100 seconds.
  - Sample: a joining device no longer reads pushes with its last role (it showed its own avatar for the host), and the
    avatar cache is cleared per join.
  - Consumes blehubs `1.1.0` from nuget.org. 65 tests; the sample builds for Android, iOS and macOS.
- **2026-10-10**: The macOS sample is a full host and client.
  - It links the whole phone app: the XAML app, Shiny Shell, pages, view models, hubs, game and services. Only its
    startup (`MauiProgram`, app delegate) is its own.
  - It registers Shiny's macOS BLE stacks itself, since blehubs only does that on Android, iOS and Mac Catalyst.
  - The shared chat services marshal to the main thread through Shiny.Maui.Shell's `IMainThread`. MAUI's `MainThread`
    throws on the AppKit backend, and Shell `7.0.2` adds a macOS implementation. `ChatSessionProvider` doesn't marshal
    at all, because ChatView takes care of that.
  - Consumes blehubs `1.2.0` and httpserver `2.1.1`. With them a device no longer lists itself: blehubs' `Discover()`
    skips its own advertisement (service UUID + advertised name), and the mDNS locator skips publications made on the same
    `IMdnsManager`, which the host's advertisers and the client's locator share. blehubs also asks for ATT MTU 517 on
    every new BLE connection. No universalhubs code changed.

- **2026-10-23**: Media channels over Wi-Fi (§3, §9), for live audio and video.
  - Host: the `{BasePath}/media` WebSocket route. Client: `SwitchboardTransport.OpenMediaChannel`. The handshake reply
    gained `mediaToken` / `mediaMaxPacketSize`. `UniversalHubClient` passes blehubs' `CanOpenMediaChannel` /
    `OpenMediaChannel` through.
  - Consumes blehubs `1.4.0` (media channels) from nuget.org. The sample also uses Shiny.Audio `3.0.0-beta-0034`
    (`IAudioDuplex`) and Shiny.Maui.Controls.Camera `2.1.0-beta-0002` (`VideoEncoder`, `RemoteVideoView`).
  - Tests: Wi-Fi media channels end to end (both directions, ordering, 200 KB packets, the size limit, reopening, closing
    on disconnect and on host dispose, tokens, a host without media channels).
  - Sample: FaceTime-style calls (`samples/Sample/Calls`). People lists everyone reachable with 💬 / 📞 / 📹, connecting first when needed; the call screen rings,
    then shows them full screen with your camera in the corner.
  - **Not yet verified:** real devices, real radios, echo cancellation, H.264 encode/decode. See §7.
- **2026-10-23**: **blehubs moved into this repo.** `src/Shiny.BluetoothLE.Hubs*`, `tests/Shiny.BluetoothLE.Hubs.Tests`,
  `skills/shiny-ble-hubs`, `README.BluetoothLE.Hubs.md` and `PLAN.BluetoothLE.Hubs.md` came across from blehubs'
  working tree (including its unreleased stale-peripheral fix); its history stays in the old repo. UniversalHubs, its
  tests and the samples use project references, and the tests and samples load the generator as an analyzer. One
  `version.json` for every package, now 1.5.0 (above blehubs' published 1.4.0). CI builds and tests both libraries.
  144 + 76 tests.
- **2026-10-23**: Wi-Fi connect re-resolves a moved host. A host restarted on a new port (or killed without an mDNS
  goodbye) stayed listed at its old address, and connecting failed until the client restarted. Now, when no address
  answers, `UniversalHubClient` looks the host up by instance name (`IHttpServerLocator.FindFirstAsync`,
  `HttpRediscoverTimeout`) and retries once at the new address. Addresses from `FromUrl` aren't looked up. 76 tests.
- **2026-10-23**: macOS sample: no alert ever showed, so Back on a hosted game (which asks "End game?") did nothing.
  maui-labs `preview.12.26421.1` predates its fix for dialog registration on MAUI 10.0.70+ (maui-labs #575). It is now on
  `0.1.0-preview.13.26512.5`, and `MauiVersion` is pinned to `10.0.110` instead of coming from the installed workload.
  Incremental builds of `Sample.MacOS` can skip recompiling the linked sample files: use `--no-incremental` after editing
  `samples/Sample`.

- **2026-10-30**: **Multipeer transport** (§10): `Shiny.UniversalHubs.Multipeer`.
  - Host and client packages gained internal add-on seams: `IUniversalHubTransport` (the host starts and stops it with
    BLE and HTTP), `IUniversalHubClientTransport` (discovery and connect), `UniversalServerBuilder.ServiceActions` and
    `UniversalHubClientOptions.Transports`. BLE and HTTP were left as they were rather than moved onto the seam.
  - `HubTransports.Multipeer` (4), included in `All`. `HubHostInfo.Multipeer`, `HubConnection.Multipeer` /
    `MultipeerPeer`, `mc:` connection ids. `HubConnectOptions` tries `Prefer` first, then Wi-Fi, Multipeer, BLE.
  - `FileRoutes.ResolvePath` is shared, so Multipeer files obey the same name rules as HTTP.
  - Sample: iOS and macOS host and join over Multipeer (`unihub-mc`); labels and the calls profile know it. Android gets
    the same registration and skips it.
  - CI installs the `macos` workload (the package targets `net10.0-macos`).
  - Tests: 46 new, on an in-memory Multipeer network (`FakeMultipeerNetwork`) beside real HTTP clients. 123 + 144 tests.
  - **Verified on real MultipeerConnectivity**, in one process on a Mac (two peers over Bonjour): discovery, invitation,
    encrypted session, handshake, calls, 20 ordered pushes, rename, a 300 KB upload and download (byte-identical),
    a media channel, client disconnect (`ClientDisconnect`), reconnect, host stop (`ServerShutdown` with its message).
    200 round trips took 31 ms.
  - **Not yet verified:** two physical devices, AWDL and Bluetooth-only links, iOS. See §7.

## 7. Verifying on hardware

You need two physical devices. Run the sample on both and check:
- **Wi-Fi only:** host and join on the same network. The join list shows 📶 and the game says "Connected over Wi-Fi".
- **Bluetooth only:** airplane mode on with Bluetooth re-enabled. Shows 🔵. Pushes and avatars work.
- **Both:** the host appears once with both badges. Toggling "Prefer Bluetooth" changes the transport.
- **BLE while the chat runs:** host a game on Android, then on iOS, and join each from the other phone over Bluetooth.
  The host's game screen says "Hosting over Wi-Fi and Bluetooth" and the chat stays reachable over BLE.
- **Isolated Wi-Fi** (guest network with client isolation): the host should fall back to BLE.
- **Mixed:** one player on Wi-Fi and a spectator on BLE. Both see every move.
- **Host stop:** reaches the client's Disconnected as `ServerShutdown` with the message, on both transports.
- **Disconnect reasons:**
  - The client leaving shows `ClientDisconnect` on the host.
  - Walking a BLE client out of range, or killing its app, shows `ClientTimeout`.
  - A kick shows `ServerDisconnect` on both sides.
- **Wi-Fi blip:** toggle Wi-Fi off and on quickly on the client. The game resumes without a disconnect.
- **Always-on chat:**
  - With the app open on two devices, each one lists the other under People.
  - A message raises a local notification unless that conversation is on screen.
  - Hosting and ending a game leaves the chat connected.
- **Tunnel:**
  - "Share on the internet" shows a public URL. A device on cellular can open it through "Address someone shared".
  - Through that URL the game route answers 404.
- **Calls** (People → 📞 / 📹 on a row, or the buttons at the top of a chat):
  - Over Wi-Fi, both directions (visitor calls owner, owner calls visitor). Video is smooth, and the status shows
    H.264 between two iPhones or two Androids, and between an iPhone and an Android.
  - Set People → More options → "Video codec for calls" to Jpeg on either phone: the next call says JPEG and still plays. Back to Auto: H.264 again.
  - Mid-call, 📷 switches to the back camera (the other side's picture follows) and 🎤 to a headset or USB mic (the other
    side hears it). The mic choice carries over to the next call.
  - With Wi-Fi off (Bluetooth only): voice is clear, video is a small, slow picture, and the status says Bluetooth.
  - Speakerphone: no echo on the other side. Toggle the speaker, mute and camera.
  - Walk out of range mid-call: the call ends as "Connection lost" on both sides.
  - Call someone already on a call: Busy. Don't answer: "No answer" after 45 s.
  - Rotate the phone during a video call: the other side stays upright, and the front camera is mirrored.
- **Mac host and client:** `samples/Sample.MacOS`
  - It hosts its chat over BLE and Wi-Fi as soon as it opens. Phones list it under People.
  - "Host a game" on the Mac shows up in the phones' join list, and a phone can join it over either transport.
  - It lists and joins the phones' games and chats, over Bluetooth or Wi-Fi.
  - Run it from Finder, `open` or `-t:Run`, not by executing the binary from a terminal. macOS charges the Bluetooth
    permission to the terminal, whose Info.plist has no `NSBluetoothAlwaysUsageDescription`, and kills the app.
- **Multipeer** (two iPhones, or an iPhone and the Mac):
  - **No network:** Wi-Fi on but not joined to any network on both. The join list shows 📡 and the game says
    "Connected over Multipeer". Pushes, avatars and a call work.
  - **Bluetooth only:** Wi-Fi off on both. Multipeer still connects (over Bluetooth), slower.
  - **Same network:** the host appears once, with 📶 and 📡; joining picks Wi-Fi. "Prefer Bluetooth" picks BLE, not
    Multipeer.
  - **Mixed:** an Android player over Wi-Fi or BLE and an iPhone over Multipeer in the same game. Both see every move.
  - **Discovery stopped before joining:** join a host after leaving the join list - the invitation must still go out
    through the browser that found it.
  - **Two hubs on one host:** chat and a game at once from the same phone (two sessions to the same host peer).
  - **Background** one side mid-game: the other sees `ClientTimeout` (no resume), and rejoining works.
  - **Missing plist entries** log a warning at start.
  - Console: `xcrun devicectl device process launch --console` shows `Multipeer session ... Connected`.

## 8. Future

- Migrating a live session between transports (BLE → Wi-Fi as a player walks in the door)
- A browser spectator page talking to the hub Switchboard directly
- Upload and download progress events on the host for Wi-Fi files
- Wi-Fi hosting on devices with no BLE hosting stack (Windows)
- Client and host renames over Wi-Fi (blehubs `IBleHubConnection.Rename`, `IBleHubHost.Rename` / `HostRenamed`).
  Multipeer already carries both.
- Multipeer: a host rename reaches connected clients, but the advertised display name stays until the next re-advertise
- Android ↔ iOS without a network: Wi-Fi Aware (iOS 26+, Android 8+) as another add-on transport

## 9. Calls: audio and video, like FaceTime (2026-10-23)

**Goal:** the libraries can carry a 1:1 live call, audio only or audio and video, over Wi-Fi and BLE. The call itself
is app code: `samples/Sample/Calls`. No new packages: each library gained what a call needs, and the sample puts them
together.

### 9.1 What each library gained

| Library | Addition |
|---|---|
| blehubs (`Shiny.BluetoothLE.Hubs*` `1.4.0`) | Media channels: an ordered, bidirectional packet pipe per hub connection on its own L2CAP PSM, plus transport seams so Wi-Fi can carry it |
| this repo (`Shiny.UniversalHubs.Host` / `.Client`) | Media channels over Wi-Fi: a WebSocket at `{BasePath}/media`, gated by a token from the handshake reply, reported through the blehubs seams |
| Shiny.Audio (`3.0.0` beta) | `IAudioDuplex`: mic capture and live PCM playback on one voice-processing session, so echo cancellation removes what the speaker plays |
| Shiny.Maui.Controls.Camera (`2.1.0` beta) | `VideoEncoder` (a `FrameAnalyzer` that emits JPEG or H.264 frames from a `CameraView`) and `RemoteVideoView` (renders received frames) |

The sample's `Calls` folder has the call protocol, signalling, host relay, IMA ADPCM, the send scheduler and the device
adapters. Audio and video reach the call logic through small interfaces (`ICallAudio`, `ICallVideoSource`,
`ICallVideoSink`), so the logic itself doesn't depend on MAUI.

### 9.2 Upstream contracts

**blehubs (`1.4.0`): media channels** (built; blehubs' PLAN.md §6b is the detail)
- `IBleHubMediaChannel : IAsyncDisposable` (in `Shiny.BluetoothLE.Hubs`): `ConnectionId` (host side; null on the client),
  `MaxPacketSize`, `IsOpen`, `ValueTask Send(ReadOnlyMemory<byte> packet, CancellationToken)` (serialized; packets arrive
  whole and in order or the channel closes; oversize throws `ArgumentException`), `IAsyncEnumerable<ReadOnlyMemory<byte>>
  Receive(CancellationToken)` (one reader; completes on close), `Task Completion`.
- Host: `BleHubHostOptions.EnableMediaChannels(maxPacketSize = 128 KB, secure = false)` (read back as
  `BleHubHostOptions.MediaChannels`) opens a second L2CAP PSM at start. `HandshakeAck` gained `MediaChannelPsm`,
  `MediaChannelSecure`, `MediaToken` and `MediaMaxPacketSize` (optional, so blehubs' protocol stays 1).
  `IHubContext<THub>.MediaChannelOpened` (`BleHubMediaChannelEventArgs(Client, Channel)`) and
  `GetMediaChannel(connectionId)`. A client's channel closes when it disconnects or the hub stops; a new one replaces the old.
- Client: `IBleHubConnection.CanOpenMediaChannel` and `OpenMediaChannel(CancellationToken)` (returns the open one if there
  is one). They're on `IBleHubConnection`, next to `CanTransferFiles`, because blehubs has no non-generic `IBleHubClient`.
- Seams: `IBleHubClientTransport.CanOpenMediaChannel` / `OpenMediaChannel` (default: false / NotSupported), and
  `IBleHubTransportEndpoint.MediaChannelOpened(connectionId, channel)` (returns false when the client is unknown).
- L2CAP framing: `[u32 LE length][bytes]` in writes of at most 4 KB. The client's first frame is the token; the host
  answers `[0x01]` or closes.

**Shiny.Audio (`3.0.0` beta): full-duplex voice** (built for iOS, Mac Catalyst, macOS and Android; not Windows)
- `IAudioDuplex.RequestAccess()`, `StartAsync(AudioDuplexOptions?, ct) → IAudioDuplexSession`.
- `AudioDuplexOptions`: `SampleRate` (8000 or 16000; 16000 by default), `Processing` (`VoiceChat` by default),
  `Speakerphone` (true), `FrameDuration` (20 ms), `PlaybackLatency` (target jitter buffer, 60 ms), `MaxPlaybackLatency`
  (200 ms; older audio is dropped).
- `IAudioDuplexSession`: `SampleRate`, `ChannelReader<ReadOnlyMemory<byte>> Captured` (PCM16 mono, one frame per item),
  `void Play(ReadOnlySpan<byte> pcm16)` (thread-safe; gaps play as silence), `TimeSpan Buffered`, `bool Speakerphone`
  (settable), `InputLevelChanged`, `StopAsync()`.
- Apple: AVAudioSession `PlayAndRecord` / `VoiceChat` and one AVAudioEngine with voice processing on its input node,
  playing through an AVAudioPlayerNode. Android: `AudioRecord` (VOICE_COMMUNICATION) + streaming `AudioTrack`
  (USAGE_VOICE_COMMUNICATION) on one audio session with `MODE_IN_COMMUNICATION`. Windows isn't registered: AudioGraph
  has no shared echo-cancelled capture-and-render path.
  The jitter buffer is shared managed code and unit-tested. An overflow trims back to `PlaybackLatency`, not to the
  maximum, so a burst doesn't leave the call 200 ms behind. A connected headset wins over `Speakerphone`. Android apps
  need `MODIFY_AUDIO_SETTINGS`.

**Shiny.Controls.Camera (`2.1.0` beta): live video**
- Shared: `enum VideoCodec { Jpeg, H264 }` and `record EncodedVideoFrame(Codec, Data, Width, Height, Rotation, IsMirrored,
  IsKeyFrame, Timestamp)`. H.264 is Annex B, and every key frame carries its SPS/PPS, so any key frame can start
  decoding.
- `VideoEncoder : FrameAnalyzer`: `Codec`, `MaxDimension` (640), `FrameRate` (15), `Bitrate` (H.264, 800 kbps),
  `JpegQuality` (0.6), `KeyFrameInterval` (2 s), `RequestKeyFrame()`, `static IsSupported(VideoCodec)`, and
  `event Action<EncodedVideoFrame> FrameEncoded`, raised on the encoder thread, not the UI thread. It rate-limits in
  `WantsFrame()`, and its settings can change while it's running.
- `RemoteVideoView : View`: `Render(EncodedVideoFrame)` (thread-safe; a delta frame before the first key frame is
  dropped), `Clear()`, `Aspect`, `static CanDecode(VideoCodec)`, and `event KeyFrameNeeded` (the decoder lost sync).
- H.264 on iOS, Mac Catalyst, macOS (VideoToolbox) and Android (MediaCodec). JPEG everywhere.

### 9.3 The sample's call protocol (`CallProtocol.Version` = 1)

Packets on the media channel, little-endian: `[version u8][type u8][callId 16 bytes]` then the body.

| Type | Body |
|---|---|
| `0x01 Invite` | `to` (string, empty = the host), `from` (string, filled in by the host), `fromName`, `wantsVideo`, video codecs (bitmask), audio codecs (bitmask) |
| `0x02 Accept` | `video` (bool), chosen video codec, chosen audio codec |
| `0x03 Decline` | reason (`Busy`, `Declined`, `Unsupported`, `Unreachable`), message |
| `0x04 HangUp` | reason, message |
| `0x05 Ringing` | – (the callee's device is ringing) |
| `0x10 Audio` | `seq u16`, codec, `timestamp u32` (ms), payload |
| `0x11 Video` | `seq u16`, codec, flags (key, mirrored), rotation/90, `width u16`, `height u16`, `timestamp u32`, payload |
| `0x12 KeyFrameRequest` | – |
| `0x13 MediaState` | `muted`, `cameraOn` |

- Strings are `[u16 length][UTF-8]`. An unknown type is ignored, and another version is declined as `Unsupported`.
- **Routing (host):** `CallRouter` watches `MediaChannelOpened` for each hub that has calls on. An invite to `""` is
  for the host's own `IHubCalls`. An invite to a connection id is forwarded to that client's channel, with `from` set to
  the caller's connection id (a client can't spoof it). The router keeps `callId → (leg A, leg B)` and forwards every
  later packet of that call to the other leg. An unknown target gets `Decline(Unreachable)`. Either leg's channel
  closing ends the call for the other with `HangUp(Dropped)`.
- **Negotiation:** the invite lists what the caller can encode *and* decode. The callee chooses
  `HubCallsOptions.PreferredVideoCodec` when both have it, otherwise H.264, then JPEG. With no codec in common (or
  `Accept(video: false)`) the call is answered with audio only, both ways, rather than refused. Audio: PCM16 over Wi-Fi,
  IMA ADPCM over BLE (each block carries its own decoder state, so a lost packet costs only itself). The profile is the
  slowest link's: the host sets it to BLE in a relayed invite when either leg is BLE, and the callee's accept carries it
  back to the caller.
- **Profiles (`CallQuality`):**

  | | Wi-Fi | BLE |
  |---|---|---|
  | Video | 640 px, 15 fps, H.264 800 kbps / JPEG 0.6 | 160 px, 3 fps, H.264 64 kbps / JPEG 0.4 |
  | Audio | PCM16 16 kHz (32 KB/s) | IMA ADPCM 8 kHz (4 KB/s) |

- **Pacing:** one sender loop per channel. Audio and signalling go first. Video is latest-wins: a frame waiting to be
  sent is replaced by a newer one, and after a dropped delta frame nothing is sent until the next key frame (the encoder
  is asked for one). Audio older than `MaxPlaybackLatency` is dropped on receive.
- **Timeouts:** ringing ends after 45 s (`HubCallsOptions.RingTimeout`), as `Missed` on both sides.
- **One call at a time** per device: placing a second throws, and a second caller hears `Busy`. A host relaying calls
  between clients is still free for its own.
- **Wi-Fi media channel:** `GET {BasePath}/media` as a WebSocket upgrade with `X-Hub-Media-Token`. The handshake reply
  gained `mediaToken` and `mediaMaxPacketSize` (optional, so `UniversalHubProtocol.Version` stays 2). One binary message
  is one packet, without per-message deflate. Tunnels: the route is open when any hub allows tunnels, and the token's hub
  must allow them too. On devices the client's WebSocket uses its own sockets, because the platform HTTP handlers
  (NSUrlSession, Android) can't upgrade.

### 9.4 The sample's call API (`samples/Sample/Calls`)

```csharp
server.Ble(o => o.EnableMediaChannels());
services.AddHubCalls(calls => calls
    .Host<ChatHub>()          // takes calls on this host's ChatHub, calls its clients, relays between them
    .Client<IChatHub>());     // calls through this app's IChatHub client (a media channel opens on every connect)
services.AddHubCallsMaui();   // IAudioDuplex audio, CameraView / RemoteVideoView video, IHubCalls at startup

IHubCalls calls;                                     // singleton
calls.Incoming += (_, call) => ...;                  // call.State == Ringing: Accept(video) / Decline()
var call = await calls.CallHost<IChatHub>(new CallOptions(Video: true));   // client → its host
var call = await calls.CallClient<ChatHub>(connectionId, options);         // host → one of its clients
var call = await calls.CallPeer<IChatHub>(connectionId, options);         // client → another client (relayed)

call.State / StateChanged / Ended (CallEnd(reason, message)) / IsMuted / IsCameraOn / VideoCodec / Transport / Stats
call.AttachVideo(cameraView, remoteVideoView);      // starts the encoder on the camera, renders the remote side
await call.HangUp();
```


## 10. Multipeer (2026-10-30)

**Goal:** Apple devices with no shared network (a plane, a park, a car) get Wi-Fi bandwidth instead of BLE. Apple's
MultipeerConnectivity picks the radio itself: the Wi-Fi network, peer-to-peer Wi-Fi (AWDL) or Bluetooth. It only
connects Apple devices, so Android players keep using HTTP or BLE against the same host and the same hub.

### 10.1 How it plugs in

- **The hub side is unchanged.** Like HTTP, Multipeer reaches each hub through blehubs' `IBleHubTransportEndpoint` and
  carries pushes through `IBleHubPeerChannel` (§2), so `Clients`, groups, `MaxClients`, `ValidateClient` and
  `IHubContext` span all three transports. The client uses `BleHubClient.ConnectExternal`.
- **Add-on seams** (internal, `InternalsVisibleTo` the Multipeer package):
  - Host: `IUniversalHubTransport` (`Start` / `Stop` / `IsRunning` per hub). `UniversalHubHost` starts each registered
    one when `transports` includes its flag, counts it in `RunningTransports` / `GetRunningTransports`, and stops it after
    HTTP and before BLE. One that throws `PlatformNotSupportedException` is skipped quietly. `IHubBinding.Endpoint`
    gives it the hub.
  - Client: `IUniversalHubClientTransport` (`Discover` as `HubHostSighting`s merged by name, `Connect`).
    `IHubTransportKind` says which transport `ExternalTransport` is.
  - Registration: `UniversalServerBuilder.ServiceActions` and `UniversalHubClientOptions.ServiceActions` / `Transports`,
    so registration stays one call per side (`server.Multipeer(...)`, `o.Multipeer(...)`).
- **`IMultipeerLink`** is the radio: advertise, browse, invite. `AppleMultipeerLink` wraps MultipeerConnectivity; the
  tests use `FakeMultipeerNetwork`. Everything above it is plain .NET and runs in the tests.

### 10.2 Discovery

- One advertisement per host under the Multipeer service type, like the one BLE service: `discoveryInfo` has the mDNS
  TXT keys (`proto` = `MultipeerProtocol.Version`, `hostId`, `name`, `hubs`). It is replaced when the running hubs change.
- The service type must differ from every hub's mDNS type name: MultipeerConnectivity advertises over Bonjour as
  `_{type}._tcp`, and NSBonjourServices needs that and `_{type}._udp`.
- The host's `MCPeerID` is archived in `NSUserDefaults` and reused while its display name is the same, so browsers don't
  list a relaunched host twice.
- Clients filter by `hubs` and `proto` and merge by `name` with BLE and mDNS. Invitations go out through the browser
  that found the peer, with that browser's `MCPeerID` (`MultipeerClientOptions.DisplayName`).

### 10.3 Sessions

- **One `MCSession` per hub connection.** The host accepts every invitation into a new session. No 8-peer limit, and
  clients never mesh with each other. A phone in the chat and a game has two sessions with the same host.
- `MCEncryptionPreference.Required`, no identity.
- Connection ids are `mc:{guid}`. `MultipeerPeers` maps them to the peer's display name for `HubConnection.Multipeer`.
- Messages are reliable and ordered. The host and client each send under a lock, so a session's messages leave in
  order; MultipeerConnectivity delivers them in order on its delegate queue.

### 10.4 Wire (`MultipeerProtocol.Version` = 1)

```
[0] version  [1] kind  [2..5] message id (LE)  [6] name length  [7..] name (UTF-8)  [...] payload
```

| Kind | Direction | Name | Payload |
|---|---|---|---|
| `Handshake` 0x01 | → host | characteristic UUID | `HubHandshake` (JSON), `Protocol` = Multipeer version |
| `HandshakeReply` 0x02 | → client | | `MultipeerHandshakeReply` (accepted, reason, host name/id, files, media packet size) |
| `Invoke` 0x10 / `Stream` 0x11 | → host | method | arguments, as `IBleHubSerializer` wrote them |
| `Cancel` 0x12 | → host | | (the id of the call) |
| `Rename` 0x13 / `HostRenamed` 0x31 | both | | `MultipeerRename` |
| `Completion` 0x20 / `StreamItem` 0x21 / `StreamEnd` 0x22 | → client | | the result / item, raw |
| `Error` 0x23 | → client | | `MultipeerError` (type, message): `BleHubRemoteException` |
| `Push` 0x30 | → client | event | arguments, raw |
| `Disconnect` 0x40 | both | | `HubHangUp`: host → why the hub ended it; client → it is leaving |
| `FileRequest` 0x50 / `FileReply` 0x51 / `FileDone` 0x52 | | | `MultipeerFileRequest` / `MultipeerFileReply` (HTTP status codes) / `MultipeerFileDone` |
| `MediaOpen` 0x60 / `MediaOpened` 0x61 / `MediaPacket` 0x62 / `MediaClose` 0x63 | | | one packet per `MediaPacket` |

- **Values are binary**, not re-framed JSON: any `IBleHubSerializer` works, as over BLE.
- **Errors** carry the same types and wording as BLE and HTTP (`HubMethodNotFound`, "Handshake required",
  `BleHubRemoteException.RenameRefused`).
- **Handshake refusals:** another protocol version, a hub that isn't running here, the hub's own rules. The host ends a
  refused session after two seconds if the client doesn't.
- **Disconnect:** the host sends `Disconnect` and ends the session two seconds later if the client hasn't. The client
  sends `Disconnect`, waits 250 ms, then ends it. A session that ends without one is `ClientTimeout` on both sides.
- **Files** are Multipeer resources named `hubfile-{id}`. Upload: request → reply → resource → `FileDone` (size checked,
  overwrite rule checked again, moved into place). Download: request (the client waits for the resource first) → reply
  with the length → resource. The receiving side copies MC's temp file before the callback returns, because MC deletes it.
  `FileTransferred` is raised on the host like HTTP's.
- **Media channels** are `MediaPacket` messages on the session, reliable and ordered like L2CAP and the WebSocket, so the
  calls' signalling can share them. One per connection; opening again replaces it.

### 10.5 Decisions

| Question | Decision |
|---|---|
| Package | Its own, `Shiny.UniversalHubs.Multipeer`, host and client together. Apps that don't use it never need the plist entries |
| In `HubTransports.All` | Yes. It only runs once the package is set up, and is skipped where MultipeerConnectivity doesn't exist |
| Connect order | `Prefer`, then Wi-Fi, Multipeer, BLE: Wi-Fi serves Android peers too; Multipeer beats BLE everywhere it works |
| Wire | Binary messages with the hub's own bytes, not the Wi-Fi JSON envelopes. Its own version |
| Session topology | One session per hub connection (§10.3) |
| Files | Multipeer resources, not chunked messages: MultipeerConnectivity does the flow control |
| Media | Reliable messages rather than MC streams: ordered like the other transports, and no NSStream run-loop plumbing. Revisit if latency on a real link says otherwise |
| A standalone Shiny MultipeerConnectivity package | Not now. The radio wrapper is internal to this package until something else needs it |
| Android ↔ iOS | Out of scope: MultipeerConnectivity is Apple-only. Wi-Fi Aware is the candidate (§8) |
