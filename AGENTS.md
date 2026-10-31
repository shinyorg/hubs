# Shiny Hubs — Working Notes

This repo holds every hub library:
- **Shiny.BluetoothLE.Hubs** (`src/Shiny.BluetoothLE.Hubs*`): SignalR-style hubs over Bluetooth LE. Hub logic, the
  wire protocol, the source generator, media channels and L2CAP files.
- **Shiny.UniversalHubs** (`src/Shiny.UniversalHubs*`): the same hubs over Wi-Fi as well, through
  **Shiny.Net.HttpServer.Switchboard** and mDNS, and between Apple devices over **MultipeerConnectivity**
  (`Shiny.UniversalHubs.Multipeer`).

The two were separate repos until 2026-10-23. blehubs' history stays in `~/Desktop/dev/blehubs`, which is no longer
developed. Everything happens here, and all packages ship together.

- **Related repos:** httpserver (`~/Desktop/dev/httpserver`), consumed from NuGet. Versions are in
  `Directory.Packages.props`.
- **Design docs:**
  - `PLAN.md`: UniversalHubs (wire protocol over Wi-Fi, decisions, status, the calls sample)
  - `PLAN.BluetoothLE.Hubs.md`: BluetoothLE Hubs (GATT layout, framing, generator, media channels, status)

## Layout

- `src/Shiny.BluetoothLE.Hubs`: protocol (framing, chunking, codec), `[BleHubClient]`, `IBleHubMediaChannel`. Packs the
  generator into `analyzers/dotnet/cs`.
- `src/Shiny.BluetoothLE.Hubs.SourceGenerators`: proxies, dispatchers, push extensions (netstandard2.0, not packed on its own).
- `src/Shiny.BluetoothLE.Hubs.Host` / `.Client`: `BleHub<T>`, `IBleHubHost`, `IHubContext<THub>`; `BleHubClient`, discovery.
- `src/Shiny.UniversalHubs`: the shared Wi-Fi wire protocol (`UniversalHubProtocol`, `HubJson`).
- `src/Shiny.UniversalHubs.Host`: the hub Switchboard, `IUniversalHubHost`, file and media routes, mDNS.
- `src/Shiny.UniversalHubs.Client`: `IUniversalHubClient<T>`, merged discovery, `SwitchboardTransport`.
- `src/Shiny.UniversalHubs.Multipeer`: the MultipeerConnectivity transport, host and client (PLAN.md §10). Everything
  but `Platforms/Apple/AppleMultipeerLink.cs` is plain .NET behind `IMultipeerLink`; it plugs into the internal add-on
  seams (`IUniversalHubTransport`, `IUniversalHubClientTransport`) through `InternalsVisibleTo`.
- `tests/Shiny.BluetoothLE.Hubs.Tests`: the generated proxy against a real `HubRuntime` through an in-memory radio, the
  real `BleHubHost` against `FakeHostingManager`, codecs and generator diagnostics.
- `tests/Shiny.UniversalHubs.Tests`: the real host and client over `Shiny.Net.HttpServer.Testing`'s in-memory transport,
  and over `FakeMultipeerNetwork` (in-memory MultipeerConnectivity) for Multipeer.
- `skills/shiny-ble-hubs`, `skills/shiny-universal-hubs`: the published Claude Code skills.
- `samples/Sample`: MAUI, iOS and Android. `samples/Sample/Calls` is the FaceTime-style call: app code over media
  channels, Shiny.Audio's `IAudioDuplex` and the camera's `VideoEncoder` / `RemoteVideoView`, not a package.
- `samples/Sample.MacOS`: the same app on macOS (maui-labs AppKit). It links all of `samples/Sample` (App, AppShell,
  Pages, Hub, Game, Services, Calls), so code there must compile for macOS too: no MAUI `MainThread` (inject Shiny.Maui.Shell's
  `IMainThread`). Only `MauiProgram` and the app delegate are Mac-only. Build it with `--no-incremental` after editing
  `samples/Sample`: incremental builds can skip the linked files.

## Rules

- **Hub logic lives in Shiny.BluetoothLE.Hubs; UniversalHubs carries it.** UniversalHubs reaches the hub only through
  the transport seams (`IHubContext<T>.TransportEndpoint`, `BleHubClient.ConnectExternal`, `IBleHubClientTransport`,
  `IBleHubPeerChannel`). A feature that needs hub behavior goes into `Shiny.BluetoothLE.Hubs*` with its tests, in
  the same change.
- **AOT/trim safe, no reflection.** Dispatch, proxies and pushes come from the generator; arguments are serialized one
  by one with their static type through `IBleHubSerializer`. Wi-Fi JSON goes through the source-generated
  `UniversalHubJsonContext` or `JsonElement`. New public generics that DI constructs need `[DynamicallyAccessedMembers]`.
- **Targets.** `Shiny.BluetoothLE.Hubs` and `Shiny.UniversalHubs` are `net10.0`. The host and client packages
  multi-target `net10.0`, `-android`, `-ios` and `-maccatalyst` (`PlatformTargetFrameworks`) so `AddBleHubServer` /
  `AddBleHubClient` register the platform BLE stacks. Keep platform code to those `#if` registration blocks.
  `Shiny.UniversalHubs.Multipeer` targets `net10.0`, `-ios`, `-maccatalyst` and `-macos` (CI installs the `macos`
  workload for it); its only platform code is the Apple link.
- **Registration is one call per side.** `AddBleHubServer(server => ...)` / `AddUniversalServer(server => ...)` once,
  and `AddBleHubClient<T>` / `AddUniversalHubClient<T>` per contract. New settings go on the builders or options, not
  new `Configure*` methods. Registration mistakes throw at registration.
- **Hubs are foreground only.** No background BLE: no auto-connect, no state restoration.
- **Three versioned wires.**
  - BLE: `FrameCodec.ProtocolVersion` and the `FrameKind` values. Never renumber a `FrameKind`.
  - Wi-Fi: `UniversalHubProtocol.Version` covers the reserved method names, envelopes, handshake records, TXT keys and
    the file and media routes.
  - Multipeer: `MultipeerProtocol.Version` covers the message layout, the `MultipeerMessageKind` values (never
    renumber), the control records, the advertisement keys and the file resource names.
  - A breaking change bumps the version, and the handshake refuses mismatches. Hub methods and events travel by name,
    so renaming one breaks deployed clients. Additive optional fields don't bump.
- **Transport-free cores.** `HubRuntime` (host) and `HubClientProtocol` / `BleHubClient.ConnectTransport` (client) don't
  know about BLE; `BleHubHost` and `BleHubClient.Connect` are thin adapters. Keep logic in the cores, testable without
  hardware.
- **Disconnect is cooperative** (iOS peripherals can't drop a central). Host-side disconnects send a `Disconnect` frame
  over BLE, a `HubHangUp` over Wi-Fi and a `Disconnect` message over Multipeer.
- **Add-on transports stay add-ons.** A transport beyond BLE and HTTP goes in its own package behind
  `IUniversalHubTransport` / `IUniversalHubClientTransport`, registered from the builders (`ServiceActions`,
  `Transports`). Where it can't run it throws `PlatformNotSupportedException` from `Start` (skipped quietly) and returns
  nothing from `Discover`, so one registration works in a cross-platform app.
- **Ordering:** whole messages per peer under a lock; client pushes raised one at a time in order; a client is
  registered and `OnConnectedAsync` has run before the handshake reply; `Context.Abort()` takes effect after the reply.
- **One BLE service UUID, per-hub start/stop.** Every hub is a characteristic in one service. Never bring back per-hub
  service UUIDs: two 128-bit UUIDs overflow the advertisement.
- **The Switchboard source generator is removed from the UniversalHubs host project on purpose** (see its csproj).
  `HubBoard<THub>` is dispatched by a hand-registered `SwitchboardDescriptor`.

### Source generator

- Incremental, with equatable models (`EquatableArray`, records only, no symbols in models).
- Projects inside this repo that declare hubs or contracts (tests, samples) reference it with
  `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`: project references don't carry analyzers the way the
  package does.
- New user-facing mistakes get an `SBH0xx` diagnostic plus a `GeneratorTests` case, documented in
  `PLAN.BluetoothLE.Hubs.md`, the `shiny-ble-hubs` skill and `README.BluetoothLE.Hubs.md`.

## Required updates for EVERY fix & feature

A change is not "done" until these are in sync, for whichever library it touches:

1. **Readmes** (each is also its packages' NuGet readme):
   - `README.md`: UniversalHubs
   - `README.BluetoothLE.Hubs.md`: BluetoothLE Hubs (`PackageReadmeSource` in `src/Directory.Build.props`)
2. **Design docs**: `PLAN.md` / `PLAN.BluetoothLE.Hubs.md`: design sections, decisions and status.
3. **Tests**: every behavior change gets a test in `tests/Shiny.BluetoothLE.Hubs.Tests` or `tests/Shiny.UniversalHubs.Tests`.
4. **Skills** (`skills/<name>/SKILL.md` + `reference/api-reference.md`): update the trigger keywords when a new public
   API is introduced. Hub behavior belongs in `shiny-ble-hubs`; `shiny-universal-hubs` points at it. Both sync to
   `shinyorg/skills` (plugin `shiny`) through `.github/workflows/sync-skills.yml`. The docs site lists them in
   `src/content/docs/foundation/ai-skills.mdx` and `src/components/AiSkill.astro`.
5. **Docs site** (`~/Desktop/dev/documentation`, Astro / Starlight):
   - **BluetoothLE Hubs** (https://shinylib.net/blehubs/): `src/content/docs/blehubs/*.mdx`: `index`, `contracts`,
     `hosting`, `client`, `files` (L2CAP and media channels), `how-it-works`.
   - **Universal Hubs** (https://shinylib.net/universalhubs/): `src/content/docs/universalhubs/*.mdx`: `index`,
     `hosting`, `client`, `how-it-works`. Link to the blehubs pages for hub behavior rather than repeating it.
   - **Release notes**: `blehubs/release-notes.mdx` and `universalhubs/release-notes.mdx`, with `<RN type="feature|enhancement|fix|chore">`
     (optional `breaking`, `platform`), grouped `## v<major>` → `### <version> - <date>`. Both libraries share one
     version, so a release gets the same heading in both. Use `### <version> - TBD` for unreleased work.
   - **Menu**: `src/sidebar-topics.mjs`: the **BluetoothLE Hubs** node, then **Universal Hubs** right after it.
   - **Homepage catalog**: both entries in `src/data/libraryCatalog.ts`; each `label` must match the sidebar.
   - Moving or renaming a page needs a redirect in `astro.config.mjs`.
   - Run `npm run build` in the docs repo after changes, because MDX errors only surface at build time.

**Never write a release note for a sample-app fix.** Release notes describe the shipped libraries. Changes confined to
`samples/` get no note.

## Tests

Both must stay green, and run without hardware or a network:

```
dotnet test tests/Shiny.BluetoothLE.Hubs.Tests
dotnet test tests/Shiny.UniversalHubs.Tests
```

Run timing-sensitive tests a few times before calling them done (`--no-build` loop). Use `WaitFor` polling, never
fixed sleeps.

## Verifying on hardware

Unit tests prove nothing about radios or real networks. BLE needs **two physical devices**: the iOS simulator has no
Bluetooth and Android emulators are unreliable. Run the sample on both and work through PLAN.md §7. Exercise both
directions (iOS ↔ Android, phone ↔ Mac), over Wi-Fi and over Bluetooth only. When BLE misbehaves, compare against a
generic tool (nRF Connect) to separate "the OS can't" from "the library doesn't".

## Build & release

- **Versioning**: Nerdbank.GitVersioning, one `version.json` for every package (BluetoothLE.Hubs and UniversalHubs
  release together). `PublicRelease` is always on (`Directory.Build.props`), and `Directory.Build.targets` strips NBGV's
  `+<commit>` from the informational version. **A version never carries the git commit hash**: no `-g<hash>` package
  suffix and no `+<hash>` metadata. Keep it that way.
- **Package icon**: `nuget.png` packs as `icon.png` for every package.
- **CI**: `.github/workflows/build.yml` runs on macOS. It builds `Build.slnf` (every library and both test projects,
  Release, which packs to `artifacts/`) and runs both test suites. **The samples are not built in CI**, so build them
  locally after API changes. It publishes to NuGet through OIDC on pushes and manual runs (`workflow_dispatch`) on
  `preview` / `v*`. nuget.org's trusted-publishing policy must list this repo for the `Shiny.BluetoothLE.Hubs*`
  packages too. Every package the libraries and tests use must be on nuget.org, or CI restore fails.
- **Skill sync**: `.github/workflows/sync-skills.yml` opens a PR against `shinyorg/skills` on branch
  `update-skills-hubs` with every skill under `skills/`. It needs the `SKILLS_REPO_TOKEN` secret.

## Blog posts (only when explicitly requested)

Do **not** write blog posts automatically as part of a fix or feature. Write them **only when the user asks**:
1. A docs-site post first (`~/Desktop/dev/documentation`, no hero image), with `<NugetBadge name="..." />` for the
   package it is about.
2. Then a personal-blog adaptation (`~/Desktop/dev/blog`): first-person, and a hero SVG is required
   (`src/assets/<slug>-hero.svg`).
