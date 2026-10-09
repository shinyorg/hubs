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
- `samples/Sample`: MAUI, iOS and Android.

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
- **Every behavior change** gets a test in `tests/Shiny.UniversalHubs.Tests`, plus updates to README.md and PLAN.md.
- Run the tests a few times (`--no-build` loop) before calling a timing-sensitive change done. Use `WaitFor` polling,
  never fixed sleeps.

## blehubs version

`BleHubsVersion` in `Directory.Packages.props` sets the `Shiny.BluetoothLE.Hubs*` packages. The transport seams first
shipped in `1.0.0-alpha-0006`, so don't go below it. When the seams change, publish blehubs first, then bump the version
here. Don't add a local package source.

## Verifying on hardware

Unit tests prove nothing about radios or real networks. Run the sample on two physical devices and work through
PLAN.md §7.
