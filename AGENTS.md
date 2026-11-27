# Shiny.UniversalHubs — Working Notes

**SignalR-style hubs over transports.** A hub is written once and served over the transports it picks - Bluetooth LE,
HTTP (a WebSocket per hub connection, mDNS, tunnels), Apple's MultipeerConnectivity, Google's Nearby Connections, or a
transport someone writes. Every client shares the hub's groups and pushes, whichever way it connected.

Shiny.BluetoothLE.Hubs (the BLE-only product, once a separate repo - its history is in `~/Desktop/dev/blehubs`) no longer
exists on its own: its hubs, generator, files and media channels are the core here, and BLE is one transport package.

- **Related repos:** httpserver (`~/Desktop/dev/httpserver`), consumed from NuGet. Versions are in
  `Directory.Packages.props`.
- **Docs:** `README.md` is every package's NuGet readme. The design detail lives on the docs site
  (https://shinylib.net/universalhubs/).

## Layout

- `src/Shiny.UniversalHubs` (net11.0, no platform code): the core.
  - `Core/`: arguments, serializer (`IHubSerializer`, `JsonHubSerializer`), exceptions, `HubDisconnect`, protocol
    options, file types, `Protocol/` (BLE frame codec, chunking, handshake records).
  - `Host/`: `HubRuntime` (the transport-free hub core), `HubContext`, `IHubEndpoint` / `IHubPeerChannel`, dispatchers.
  - `Client/`: `HubProxy` (base of the generated proxies), `HubClientProtocol`, `IHubConnectionTransport`.
  - `Universal/Host/`: `UniversalHubHost`, `IHubTransport`, `AddUniversalServer` builders, `UniversalHub<T>`,
    `HubConnection`, dynamic hubs, `PeerHostSession` (the peer-link session HTTP, Multipeer and Nearby share).
  - `Universal/Client/`: `UniversalHubClient`, `IHubClientTransport`, priority / prefer / allow, runtime enable/disable,
    dynamic hub clients, `PeerHubTransport`.
  - Root: `PeerProtocol`, `PeerLink` (`IPeerConnection`), `PeerMediaChannel`, `UniversalHubProtocol`.
  - Packs the generator into `analyzers/dotnet/cs`.
- `src/Shiny.UniversalHubs.SourceGenerators` (netstandard2.0, not packed on its own): proxies, dispatchers, pushes.
- `src/Shiny.UniversalHubs.BluetoothLE` (`PlatformTargetFrameworks`): `BleHostTransport` (a GATT service per service
  UUID, advertising, L2CAP files and media), `FrameHostSession`, `BleClientTransport` / `FrameClientTransport`
  (scanning, GATT link), `BleUuid` / `BleAdvertisement` (short UUIDs, the 31-byte budget), the platform `#if`
  registrations.
- `src/Shiny.UniversalHubs.Http` (`PlatformTargetFrameworks`): `HttpHostTransport` (WebSocket route, mDNS),
  `HttpClientTransport`, `WebSocketPeerConnection` (the peer link over a WebSocket, files chunked in-band).
- `src/Shiny.UniversalHubs.Multipeer`: Multipeer, host and client. Everything but `Platforms/Apple/AppleMultipeerLink.cs`
  is plain .NET behind `IMultipeerLink`.
- `src/Shiny.UniversalHubs.Nearby`: Nearby. `NearbyLink` (channels, framing, files) is plain .NET behind `INearbyRadio`;
  the radios are `Platforms/Android` (Google Play services) and `Platforms/Apple` (`[LibraryImport]` into the bridge).
- `native/ShinyNearbyBridge`: Google's NearbyConnections Swift package behind `@_cdecl` C functions. `build.sh` makes
  `build/ios` and `build/macos` xcframeworks; run it before building the Nearby package or a sample for iOS / macOS.
  Its folder must not be named `nearby` (SwiftPM takes it for Google's package and reports a cycle).
- `tests/Shiny.UniversalHubs.BluetoothLE.Tests`: the hub core and the BLE transport - the generated proxy against a real
  `HubRuntime` through an in-memory frame link, the real `BleHostTransport` against `FakeHostingManager`, per-hub
  services and advertising, codecs and generator diagnostics.
- `tests/Shiny.UniversalHubs.Tests`: the real host and client over `Shiny.Net.HttpServer.Testing`'s in-memory transport
  (HTTP / WebSocket), over `FakeMultipeerNetwork` and `FakeNearbyNetwork`, custom transports, priority, serializers,
  dynamic hubs.
- `skills/shiny-universal-hubs`: the published Claude Code skill. `skills/shiny-ble-hubs` is a short migration pointer
  for people with old Shiny.BluetoothLE.Hubs code.
- `samples/Sample`: MAUI, iOS and Android - a game, a chat, calls (`samples/Sample/Calls`, app code over media
  channels), and the **Transports** page (`Services/TransportDriver.cs`): host and client switches per transport and
  Solo, so each transport can be tested on its own.
- `samples/Sample.MacOS`: the same app on macOS (maui-labs AppKit). It links all of `samples/Sample`, so code there must
  compile for macOS too: no MAUI `MainThread` (inject Shiny's `IPlatform`). Only `MauiProgram` and the app delegate are
  Mac-only, and it registers Shiny's macOS BLE stacks itself. Build it with `--no-incremental` after editing
  `samples/Sample`: incremental builds can skip the linked files.

## Rules

- **Hub logic lives in the core; transports carry it.** A transport reaches a hub only through the seams
  (`IHubBinding.Endpoint` / `IHubEndpoint`, `IHubPeerChannel` on the host; `HubClientContext.Connect` /
  `IHubConnectionTransport` on the client). A feature that needs hub behavior goes into the core, with its tests.
- **A transport's type is its identity.** Hubs pick transports by type, the host starts and stops them by type, clients
  order and filter them by type. No transport registry, no names-as-keys. Transports a platform lacks report
  `IsSupported = false` and are skipped quietly, so one registration works in a cross-platform app. First-party
  transports use the core's internals through `InternalsVisibleTo`; anything a third-party transport needs must be public.
- **A hub's name is its key on every transport** (HTTP path, mDNS / Multipeer / Nearby lists, peer handshake). BLE keys
  by characteristic. Dynamic hubs are keyed by name too.
- **AOT/trim safe, no reflection.** Dispatch, proxies and pushes come from the generator; arguments are serialized one
  by one with their static type through `IHubSerializer`. New public generics that DI constructs need
  `[DynamicallyAccessedMembers]`.
- **Targets.** The core is `net11.0`. `.BluetoothLE` and `.Http` multi-target `PlatformTargetFrameworks` (`net11.0`,
  `-android`, `-ios`, `-maccatalyst`); BLE keeps platform code to its `#if` registration blocks. Multipeer targets
  `net11.0`, `-ios`, `-maccatalyst`, `-macos` (CI installs the `macos` workload); its only platform code is the Apple
  link. Nearby targets `net11.0`, `-android`, `-ios`, `-macos`; its iOS / macOS builds are binding projects (an empty
  `ApiDefinition.cs`) so the SDK packs the xcframework; Android references Shiny.Core and lifts
  `Xamarin.AndroidX.Fragment.Ktx` to 1.9 (Google Play services' `androidx.fragment` 1.9 absorbed it).
- **Registration is one call per side.** `AddUniversalServer(server => ...)` once, `AddUniversalHubClient<T>("name", ...)`
  per contract. New settings go on the builders or options. Registration mistakes throw at registration.
- **BLE advertisement budget.** Hubs may have their own service UUIDs, but every `advertise: true` service must fit one
  31-byte advertisement (3 bytes of flags, a field per UUID size). Registration throws when it doesn't; never let it
  silently truncate.
- **Hubs are foreground only.** No background BLE: no auto-connect, no state restoration.
- **Four versioned wires.** A breaking change bumps the version and the handshake refuses mismatches; additive optional
  fields don't bump. Hub methods and events travel by name, so renaming one breaks deployed clients.
  - BLE: `FrameCodec.ProtocolVersion` and the `FrameKind` values. Never renumber a `FrameKind`.
  - Peer links (HTTP, Multipeer, Nearby): `PeerProtocol.Version` - the message layout, `PeerMessageKind` values (never
    renumber), the control records, Multipeer's advertisement keys, Nearby's endpoint info and frames
    (`NearbyFrameKind`, never renumber), and `WebSocketPeerConnection`'s frame kinds.
  - HTTP: `UniversalHubProtocol.Version` - the WebSocket route and the mDNS TXT keys.
  - Serializers: `IHubSerializer.Name` travels in the handshake; a mismatch is refused.
- **Transport-free cores.** `HubRuntime` (host) and `HubClientProtocol` / `HubProxy.ConnectExternal` (client) don't know
  about any transport. Keep logic in the cores, testable without hardware.
- **Disconnect is cooperative** (iOS peripherals can't drop a central). Host-side disconnects send a `Disconnect` frame
  over BLE and a `Disconnect` message over the peer link (HTTP, Multipeer, Nearby).
- **Ordering:** whole messages per peer under a lock; client pushes raised one at a time in order; a client is
  registered and `OnConnectedAsync` has run before the handshake reply; `Context.Abort()` takes effect after the reply.
- **Code style:** no `continue` (filter with `Where`, or nest), no `goto`, no object construction inside an `if`
  condition.

### Source generator

- Incremental, with equatable models (`EquatableArray`, records only, no symbols in models).
- Projects inside this repo that declare hubs or contracts (tests, samples) reference it with
  `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`: project references don't carry analyzers the way the
  package does.
- It looks for `[UniversalHubContract]` and `UniversalHub<T>`, and skips referenced contracts whose assembly already has
  its proxy.
- New user-facing mistakes get an `SUH0xx` diagnostic plus a `GeneratorTests` case, documented in the
  `shiny-universal-hubs` skill and the docs site's contracts page.

## Required updates for EVERY fix & feature

A change is not "done" until these are in sync:

1. **Readme**: `README.md` is every package's NuGet readme.
2. **Tests**: every behavior change gets a test in `tests/Shiny.UniversalHubs.BluetoothLE.Tests` (core, BLE) or
   `tests/Shiny.UniversalHubs.Tests` (HTTP, peer links, transports, client).
3. **Skills** (`skills/shiny-universal-hubs/SKILL.md` + `reference/api-reference.md`): update the trigger keywords when
   a new public API is introduced. Skills sync to `shinyorg/skills` (plugin `shiny`) through
   `.github/workflows/sync-skills.yml` when a release ships. The docs site lists them in
   `src/content/docs/foundation/ai-skills.mdx` and `src/components/AiSkill.astro`.
4. **Docs site** (`~/Desktop/dev/documentation`, Astro / Starlight):
   - **Universal Hubs** (https://shinylib.net/universalhubs/): `src/content/docs/universalhubs/*.mdx`.
   - **Release notes**: `universalhubs/release-notes.mdx`, with `<RN type="feature|enhancement|fix|chore">`
     (optional `breaking`, `platform`), grouped `## v<major>` → `### <version> - <date>`. Use `### <version> - TBD` for
     unreleased work.
   - **Menu**: the **Universal Hubs** node in `src/sidebar-topics.mjs`; the homepage entry in
     `src/data/libraryCatalog.ts` (its `label` must match the sidebar).
   - The old `/blehubs/` pages redirect to Universal Hubs in `astro.config.mjs`; moving or renaming a page needs a
     redirect there too.
   - Run `npm run build` in the docs repo after changes, because MDX errors only surface at build time.

**Never write a release note for a sample-app fix.** Release notes describe the shipped libraries. Changes confined to
`samples/` get no note.

## Tests

Both must stay green, and run without hardware or a network:

```
dotnet test --project tests/Shiny.UniversalHubs.BluetoothLE.Tests
dotnet test --project tests/Shiny.UniversalHubs.Tests
```

xunit v3 4.x runs on Microsoft.Testing.Platform: `global.json` opts `dotnet test` into it, so a project is named with
`--project` (a bare path is refused), and options go straight after it (`-c Release --no-build`).

Run timing-sensitive tests a few times before calling them done (`--no-build` loop). Use `WaitFor` polling, never
fixed sleeps.

## Verifying on hardware

Unit tests prove nothing about radios or real networks. BLE needs **two physical devices**: the iOS simulator has no
Bluetooth and Android emulators are unreliable. Run the sample on both and use its Transports page to Solo each transport
in turn, in both directions (iOS ↔ Android, phone ↔ Mac). When BLE misbehaves, compare against a generic tool (nRF
Connect) to separate "the OS can't" from "the library doesn't".

## Build & release

- **Branches and versions**: `main` is the only working branch (`v1` is left as it is). Versions come from git tags,
  not the repo: there is no `version.json` / Nerdbank.GitVersioning. Untagged builds are `0.0.0-dev`
  (`Directory.Build.props`); every package shares every version. **A version never carries the git commit hash**: no
  `-g<hash>` package suffix and no `+<hash>` metadata (`IncludeSourceRevisionInInformationalVersion=false`). Keep it
  that way.
- **Releasing**: push a `vX.Y.Z` (or `vX.Y.Z-beta.1`) tag on a `main` commit, or run the **Release** workflow by hand
  with a version (it creates the tag). `release.yml` builds the Nearby bridge, builds `Build.slnf` with
  `-p:Version=<tag>`, runs both test suites, pushes every package to NuGet (trusted publishing through OIDC) and creates
  a GitHub release with generated notes. A `-suffix` makes it a prerelease. nuget.org's trusted-publishing policy must
  list this repo for every package, including the new `Shiny.UniversalHubs.BluetoothLE` and `Shiny.UniversalHubs.Http`.
  When you tag, promote the `### <version> - TBD` heading in the release notes to the release date.
- **Retired packages**: `Shiny.BluetoothLE.Hubs`, `.Host`, `.Client` and `Shiny.UniversalHubs.Host`, `.Client` are no
  longer built. Deprecate them on nuget.org, pointing at `Shiny.UniversalHubs` (a manual step).
- **CI**: `ci.yml` builds and tests every push / PR to `main` the way a release does (macOS runner, Nearby bridge
  cached on its sources, `Build.slnf` in Release packing to `artifacts/`, both test suites) and publishes nothing. Its
  runner and toolchain pins (.NET SDK, Xcode) match `release.yml`: bump them together. **The samples are not built in
  CI**, so build them locally after API changes (`-f net11.0-ios`, `-f net11.0-android`, and `Sample.MacOS`). Every
  package the libraries and tests use must be on nuget.org, or restore fails.
- **Package icon**: `nuget.png` packs as `icon.png` for every package.
- **Skill sync**: `skills/` syncs to `shinyorg/skills` only as the last job of a release (`release.yml` calls
  `sync-skills.yml`), so the published skills always match a shipped version. It opens a PR on branch
  `update-skills-hubs` and needs the `SKILLS_REPO_TOKEN` secret. Run **Sync Skills** by hand to re-sync without
  releasing.

## Blog posts (only when explicitly requested)

Do **not** write blog posts automatically as part of a fix or feature. Write them **only when the user asks**:
1. A docs-site post first (`~/Desktop/dev/documentation`, no hero image), with `<NugetBadge name="..." />` for the
   package it is about.
2. Then a personal-blog adaptation (`~/Desktop/dev/blog`): first-person, and a hero SVG is required
   (`src/assets/<slug>-hero.svg`).
