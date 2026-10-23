# Shiny.UniversalHubs — Working Notes

UniversalHubs serves **Shiny.BluetoothLE.Hubs** hubs over Wi-Fi as well as BLE, using **Shiny.Net.HttpServer.Switchboard**
and mDNS.

- **Related repos:**
  - blehubs: `~/Desktop/dev/blehubs`
  - httpserver: `~/Desktop/dev/httpserver`
- **Referenced through NuGet only, never by project reference.** Versions are in `Directory.Packages.props`.
- **`PLAN.md` is the living design doc**: wire protocol, decisions and status.

## Layout

- `src/Shiny.UniversalHubs`: the shared wire protocol (`UniversalHubProtocol`, `HubJson`).
- `src/Shiny.UniversalHubs.Host`: the hub Switchboard, `IUniversalHubHost`, file routes and mDNS.
- `src/Shiny.UniversalHubs.Client`: `IUniversalHubClient<T>`, discovery and `SwitchboardTransport`.
- `tests/Shiny.UniversalHubs.Tests`: the real host and client over `Shiny.Net.HttpServer.Testing`'s in-memory transport.
- `skills/shiny-universal-hubs`: the published Claude Code skill.
- `samples/Sample`: MAUI, iOS and Android. `samples/Sample/Calls` is the FaceTime-style call: app code over media
  channels, Shiny.Audio's `IAudioDuplex` and the camera's `VideoEncoder` / `RemoteVideoView`, not a package.
- `samples/Sample.MacOS`: the same app on macOS (maui-labs AppKit). It links all of `samples/Sample` (App, AppShell,
  Pages, Hub, Game, Services, Calls), so code there must compile for macOS too: no MAUI `MainThread` (inject Shiny.Maui.Shell's
  `IMainThread`). Only `MauiProgram` and the app delegate are Mac-only.

## Rules

- **Hub logic stays in blehubs.** This repo only carries calls through the blehubs seams:
  - host: `IHubContext<T>.TransportEndpoint`
  - client: `BleHubClient.ConnectExternal`

  If a feature needs hub behavior, add it to blehubs (with tests there) and consume the new version.
- **The wire is versioned.** `UniversalHubProtocol.Version` covers the reserved method names, envelopes, handshake
  records, TXT keys and file routes. A breaking change bumps it, and the handshake refuses mismatches.
- **AOT/trim safe:** no reflection. JSON goes through the source-generated `UniversalHubJsonContext` or `JsonElement`.
- **The Switchboard source generator is removed from the host project on purpose** (see its csproj). `HubBoard<THub>` is
  dispatched by a hand-registered `SwitchboardDescriptor`.

## Required updates for EVERY fix & feature

A change is not "done" until these are in sync:

1. **README.md** (repo root): reflect new or changed behavior. It is also the NuGet package readme.
2. **PLAN.md**: update the design sections, the decisions and the status when behavior or a decision changes.
3. **Tests**: every behavior change gets a test in `tests/Shiny.UniversalHubs.Tests` (see below).
4. **Skill** (`skills/shiny-universal-hubs/SKILL.md` + `reference/api-reference.md`): the agent-facing "how to
   generate correct code" doc. Update the trigger keyword list when a new public API is introduced. Hub behavior
   belongs in blehubs' `shiny-ble-hubs` skill; this one points at it. The skill syncs to `shinyorg/skills` (plugin
   `shiny`) through `.github/workflows/sync-skills.yml` on pushes to main/v* that touch `skills/**`. The docs site
   lists it in `src/content/docs/foundation/ai-skills.mdx` and `src/components/AiSkill.astro`.
5. **Docs site** (`~/Desktop/dev/documentation`, Astro / Starlight, rendered to https://shinylib.net/universalhubs/):
   - **Feature pages**: `src/content/docs/universalhubs/*.mdx`:
     - `index` (Getting Started, setup, platforms, things to know)
     - `hosting` (`AddUniversalServer`, `UniversalHub<T>`, start/stop, tunnels, disconnect reasons)
     - `client` (discovery, transport choice, resume, `FromUrl`, files)
     - `how-it-works` (wire protocol, mDNS TXT, file routes)
   - **Release notes**: `src/content/docs/universalhubs/release-notes.mdx`, using `<RN>` the same way blehubs does.
     Use `### <version> - TBD` for unreleased work.
   - **Menu**: the **Universal Hubs** node in `src/sidebar-topics.mjs`, right after BluetoothLE Hubs.
   - **Homepage catalog**: the `Universal Hubs` entry in `src/data/libraryCatalog.ts`. Its `label` must match the
     sidebar.
   - Hub behavior is documented on the blehubs pages; link to them rather than repeating it.
   - Run `npm run build` in the docs repo after changes, because MDX errors only surface at build time.

**Never write a release note for a sample-app fix.** Release notes describe the shipped library. Changes confined to
`samples/` get no note.

## Tests

`dotnet test tests/Shiny.UniversalHubs.Tests` must stay green. It runs without hardware or a network.

Run timing-sensitive tests a few times before calling them done (`--no-build` loop). Use `WaitFor` polling, never
fixed sleeps.

## blehubs version

`BleHubsVersion` in `Directory.Packages.props` sets the `Shiny.BluetoothLE.Hubs*` packages. The single service UUID and
one-call registration (`AddBleHubServer` / `AddBleHubClient` options) shipped in `1.1.0`, so don't go below it. `1.2.0` adds own-host filtering in `Discover()` and the max-MTU request, and httpserver `2.1.1` filters the app's own mDNS advertisements. When the seams change, publish blehubs first, then bump the version
here. Don't add a local package source.

## Verifying on hardware

Unit tests prove nothing about radios or real networks. Run the sample on two physical devices and work through
PLAN.md §7.

## Build & release

- **Versioning**: Nerdbank.GitVersioning (`version.json`). `PublicRelease` is always on
  (`Directory.Build.props`), and `Directory.Build.targets` strips NBGV's `+<commit>` from the informational version.
  **A version never carries the git commit hash**: no `-g<hash>` package suffix and no `+<hash>` metadata. Keep it
  that way.
- **Package icon**: `nuget.png` packs as `icon.png` (`Directory.Build.targets`), the same logo as blehubs.
- **CI**: `.github/workflows/build.yml` runs on macOS. It builds `Build.slnf` (the libraries and tests, Release, which
  packs to `artifacts/`) and runs the tests. **The samples are not built in CI**, so build them locally after API
  changes. It publishes to NuGet through OIDC on pushes and manual runs (`workflow_dispatch`) on `preview` / `v*`. Every package the libraries and tests use
  must be on nuget.org, or CI restore fails.
- **Skill sync**: `.github/workflows/sync-skills.yml` opens a PR against `shinyorg/skills` on branch
  `update-skills-universal-hubs`. It needs the `SKILLS_REPO_TOKEN` secret.

## Blog posts (only when explicitly requested)

Do **not** write blog posts automatically as part of a fix or feature. Write them **only when the user asks**. Follow
the blog section of blehubs' `AGENTS.md`: a docs-site post first (`~/Desktop/dev/documentation`, no hero image), then
a personal-blog adaptation (`~/Desktop/dev/blog`, first-person, hero SVG required). Use
`<NugetBadge name="Shiny.UniversalHubs" />` in the docs post.
