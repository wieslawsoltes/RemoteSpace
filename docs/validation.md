# Validation and evidence

The workflow run for an exact commit is authoritative. Source implementation, successful compilation, unit tests, live-server tests, browser tests and hardware/platform validation are different evidence levels.

## Component tests

`tests/RemoteSpace.Tests` executes protocol golden-byte, truncation, framing, bitmap/cursor, profile, display-layout and dynamic-channel regressions. DVC tests cover all ID widths, fragmented payload lengths, interleaved channels, allocation budgets, malformed continuation, duplicate IDs, channel closure and v2 priority scheduling. `InfoRegressions` also checks client-info flags and the independent protocol build advertised in GCC. XRDP 0.9.24 treats client builds at or below 419 as pre-resize clients; RemoteSpace uses its own compatibility build 1000, not a claimed Windows OS version.

Framebuffer regressions exercise dirty-region union, partial snapshots, ten thousand pending updates without queue growth, concurrent producers, generation changes, snapshot isolation, bounds, and disposal. Bridge regressions cover multilingual text, surrogate pairs, encoded-message limits, dirty rectangle framing and signed monitor coordinates. The encoded JSON limit is checked before a WebSocket send, including when Unicode escaping expands the message.

`tests/RemoteSpace.Workspace.Tests` exercises input mapping/release behavior, Unicode validation, device identity, filtering/sorting, serialization and bridge endpoint policy. `tests/RemoteSpace.Integration` always runs CredSSP/certificate-policy checks and runs network tests only when an authorized target is explicitly configured.

## Live RDP fixture

The interoperability workflow provisions a disposable loopback-only XRDP/Xorg desktop, random credentials and a short-lived certificate with an explicit SHA-256 pin. It opens the desktop with RemoteSpace's original client, waits for the terminal's actual decoded pixels, sends physical keyboard input into xterm and independently checks the resulting file on the server. This readiness condition avoids racing a newly activated RDP session before the terminal has started; no server-side input injection or pre-created proof file is used. The fixture records X input events for failure diagnosis. With `RDP_TEST_RESIZE=1`, it waits for display capabilities, requests 1024×768 and requires both the server's new desktop size and real pixel updates. The old framebuffer remains bounded until the server reactivates it.

Do not run `tools/start-test-server.sh` on a normal workstation: it modifies system services, creates a test user and expects an ephemeral CI machine plus `GITHUB_ENV`.

## Actual browser application

The Uno job compiles desktop and publishes WebAssembly, then packs reusable controls. Browser validation is a separate job that downloads that exact build. Playwright drives the real Uno Skia accessibility peers and the actual canvas, not a separately written HTML mock.

`tests/browser/workspace.py` covers boot, accessible commands, precise profile text entry, saved-device creation, search, favorites, reload persistence and missing-bridge diagnostics. Popup commands use keyboard activation because Uno semantic DOM bounds are not necessarily absolute canvas hit-test coordinates.

`tests/browser/session.py` starts the real bridge, rejects bad authentication and non-allowlisted targets, opens an authorized desktop through the app, observes RDP-derived surface data, types a command and independently verifies its effect on the remote server. A resolution command must produce server-confirmed new dimensions and subsequent pixels. `assert_pixels.py` requires substantial areas of the fixture's real terminal and desktop colors in the composed screenshot, before and after the resize. The checker rejects the earlier blank-tab rendering even when the socket received valid pixels and input worked.

With clipboard consent explicitly enabled in the real profile editor, `tests/browser/clipboard.py` transfers multilingual text through the actual Send clipboard command and verifies the exact value using the remote X clipboard. The reverse direction independently sets the remote clipboard and verifies the actual Copy remote text command updates the browser clipboard. Neither direction substitutes an application-private API for the user command. Tokens/passwords are redacted from diagnostic artifacts.

The browser tests use Chromium with SwiftShader. Screenshots prove rendered output in that configuration, not hardware-GPU acceleration, broad browser compatibility or visual parity with Windows App.

## Artifacts and deployment

Validate uploads an exact source ZIP, git history bundle, commit/tree IDs, hashes, component logs and packages. The Uno workflow retains the compiled browser before runtime tests, plus build logs, screenshots, browser diagnostics and controls packages. Native interoperability retains desktop captures and server/client logs.

Only the main-branch Uno workflow can deploy Pages, and only after build and browser validation succeed. It uploads the already-tested static distribution and verifies its published `commit.txt`. A successful PR build by itself does not mean Pages was deployed.

## Remaining validation

Live Windows/RDS NLA and licensing matrix; all desktop host operating systems; sustained/multi-session load; hardware GPU throughput; network impairment/reconnection; broad IME/accessibility/localization testing; security audit, fuzzing and deployment hardening remain necessary. The [compatibility matrix](compatibility.md) lists unimplemented services separately from validation gaps.
