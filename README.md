# RemoteSpace

An independent RDP implementation with reusable .NET packages, Uno Platform desktop/browser clients, and reusable Skia remote-desktop controls.

**Experimental preview — not a full-parity Windows App replacement.** The implemented RDP path is tested against an isolated XRDP server. This is not a certification of compatibility with every Windows/RDS host or a security-reviewed enterprise remote-access product. See the [compatibility matrix](docs/compatibility.md) before deploying.

No Microsoft application source, RDP client binaries, logos, or proprietary visual assets are embedded. RemoteSpace implements its protocol codecs independently; it does not wrap FreeRDP, Guacamole, or mstsc. XRDP is used only as an external interoperability-test server.

## Applications

The Uno workspace provides saved PCs, editable profiles, favorites, search, sorting, list/tile views, settings, and multiple session tabs. A session surface supports keyboard/pointer input, fit/zoom, cursor composition, screenshots, consent-based text clipboard operations, and negotiated remote-resolution presets. Browser device storage uses transactional IndexedDB with cross-window conflict detection; connection passwords and bridge tokens are not persisted.

Desktop clients connect through `RemoteSpace.Client` directly. The browser uses `RemoteSpace.Transport` and the included authenticated bridge, which runs the same original RDP engine on a trusted machine. **GitHub Pages serves only the static application, not the bridge or an RDP server.** [Bridge configuration and trust boundaries](docs/browser-bridge.md).

The renderer uses Uno's Skia compositor. Bitmap protocol decoding and pixel updates currently run on the CPU; compositing can use the host GPU. CI uses Chromium software rendering and does not establish hardware-GPU performance or a WebGPU-native decoder.

## Build and run

Install .NET 10. `global.json` pins the Uno SDK to 6.7.30 and permits the installed stable .NET 10 feature band. Platform prerequisites for native Uno hosting must also be installed.

```sh
# Platform-neutral components and regression tests
dotnet run --project tests/RemoteSpace.Tests -c Release
dotnet run --project tests/RemoteSpace.Workspace.Tests -c Release

# Security-codec tests; network testing is not enabled without RDP_TEST_HOST
dotnet run --project tests/RemoteSpace.Integration -c Release

# Native Uno client
dotnet run --project apps/RemoteSpace.App -c Release -f net10.0-desktop

# Publish the actual Uno WebAssembly application
dotnet workload install wasm-tools
dotnet publish apps/RemoteSpace.App -c Release -f net10.0-browserwasm
python3 -m http.server 8080 --directory apps/RemoteSpace.App/bin/Release/net10.0-browserwasm/publish/wwwroot
```

Open the local browser app, configure your bridge endpoint under Settings, and add a PC. Enter credentials only in the connection dialog. NLA is the default; choose TLS-only explicitly only for an authorized host configured without NLA. Do not weaken a production host's authentication just to match the XRDP test fixture.

## Reusable packages

| Package | Responsibility |
| --- | --- |
| `RemoteSpace.Core` | Session contracts, options, input/surface/cursor/display models and geometry bounds |
| `RemoteSpace.Protocol` | Bounded binary readers/writers, TPKT/X.224, BER/PER, MCS/GCC, activation and clipboard framing |
| `RemoteSpace.Security` | TLS certificate policy and CredSSP using platform negotiation services |
| `RemoteSpace.Licensing` | Independent RDS licensing exchange and licensing cryptographic primitives |
| `RemoteSpace.Graphics` | Bitmap/RLE decoding, cursor masks/composition, framebuffer and viewport transforms |
| `RemoteSpace.Channels` | DVC v1/v2 negotiation, bounded fragmentation, priority scheduling and display-control codecs |
| `RemoteSpace.Client` | Direct TCP/TLS RDP session engine and channel integration |
| `RemoteSpace.Input` | Physical-key mapping, held-input tracking, release sequences and Unicode input |
| `RemoteSpace.Profiles` | Credential-free profiles and typed `.rdp` import/export with unsupported-setting warnings |
| `RemoteSpace.Workspace` | Device catalog, immutable snapshots, search/sorting and versioned workspace serialization |
| `RemoteSpace.Transport` | Browser-capable authenticated WebSocket session adapter and bounded bridge framing |
| `RemoteSpace.Controls` | Uno `RemoteDesktopView`, `DeviceCard`, and `ProfileEditor` controls |

For example, `dotnet pack src/RemoteSpace.Channels -c Release -o artifacts/packages` builds a reusable NuGet package. Pack `RemoteSpace.Controls` with its Uno build prerequisites installed; do not use `--no-build`, because resource packaging requires the Uno/MSBuild resource-generation targets. CI produces package artifacts; this repository does not imply publication to NuGet.org.

A control consumes `IRemoteSession`; it does not own the transport or profile persistence. The application owns session cancellation and disposal. See [architecture and extension contracts](docs/architecture.md).

## Validation and publishing

[GitHub Actions](https://github.com/wieslawsoltes/RemoteSpace/actions) runs Windows/Linux component tests, native RDP interoperability, Uno desktop/browser compilation, reusable-package builds, and browser interaction checks. The browser session test connects through the real bridge to the loopback XRDP fixture, verifies a server-side keyboard effect, and waits for server-confirmed resized desktop pixels. No synthetic desktop is substituted for this path.

The Uno workflow publishes to GitHub Pages only from `main`, after its build and browser jobs succeed. It checks the deployed `commit.txt` against the built source revision. Source/history snapshots, logs, screenshots, package files, and browser distributions are retained as workflow artifacts. [Validation procedures and scope](docs/validation.md).

## Protocol references

Implementation is guided by Microsoft's public [MS-RDPBCGR](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/), [MS-CSSP](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cssp/), [MS-RDPELE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpele/), [MS-RDPECLIP](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpeclip/), [MS-RDPEDYC](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpedyc/), and [MS-RDPEDISP](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpedisp/) specifications. Protocol compatibility does not imply Microsoft endorsement or entitlement to proprietary assets.
