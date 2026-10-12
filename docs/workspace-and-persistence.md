# Local-PC workspace, performance and persistence

This is an independent Windows App-inspired local-PC workspace, not complete Windows App service or visual parity. The public [Windows App connection flow](https://learn.microsoft.com/en-us/windows-app/get-started-connect-devices-desktops-apps) is the reference for Add PC, optional additional settings, Add & connect, device cards and favorites. Artwork is independently drawn; no Microsoft wallpaper or application binary is embedded.

## Interaction and controls

The Uno shell exposes Favorites, Devices, Apps, active Sessions, Add and Settings. Apps explicitly describes the unimplemented cloud/RemoteApp services; a saved local PC is not represented as a cloud subscription. Both tile and list views offer connection, editing, duplication, details, favorites, export and removal confirmation. Group and text filters compose, and empty results provide a Clear filters action. Refresh reloads saved devices without disconnecting active sessions.

A new PC needs only an address. An omitted friendly name becomes the normalized host name. An explicit address port overrides the additional Port field; that field supplies the fallback otherwise. Bracket IPv6 when adding a port, for example `[::1]:3390`. Existing profiles preserve their separate port on edit. NLA remains the default and text clipboard redirection stays opt-in.

Additional profile fields are created on first expansion, not when a minimal new-PC dialog is opened. They have an attached named semantic parent; children are removed before their parent is detached on collapse. Values remain available for subsequent expansion and saving. This fixes the pinned Skia host's eager detached-input peer registration without manipulating browser DOM or weakening the hidden-field assertion. The browser regression checks absence, real keyboard entry, collapse/reopen and retained values.

The device views use `ItemsRepeater` with `UniformGridLayout` for tiles and `StackLayout` for rows, each under a finite-viewport `ScrollViewer`. The pinned Uno Skia backend does not implement `ItemsWrapGrid`; explicitly using it produced saved, zero-sized items without any visible cards. Changing container styles alone did not fix that unsupported panel. Replacing it with a nonvirtualized WrapPanel would render but lose the required large-catalog behavior, so the shell uses the supported virtualizing layout engine instead. Tile geometry adapts to the viewport without reconstructing the layout. Browser checks require positive command bounds, visibly painted artwork, and bounded realized commands after importing 1,000 PCs.

Primary Uno references: [ItemsWrapGrid Skia declaration](https://github.com/unoplatform/uno/blob/1a48a44f98202d1fc59c801c5f644265e5e0d22d/src/Uno.UI/Generated/3.0.0.0/Microsoft.UI.Xaml.Controls/ItemsWrapGrid.cs), [UniformGridLayout implementation](https://github.com/unoplatform/uno/blob/1a48a44f98202d1fc59c801c5f644265e5e0d22d/src/Uno.UI/UI/Xaml/Controls/Repeater/UniformGridLayout/UniformGridLayout.mux.cs), and [official repeater example](https://github.com/unoplatform/uno/blob/1a48a44f98202d1fc59c801c5f644265e5e0d22d/src/SamplesApp/SamplesApp.Samples/Microsoft/UI/Xaml/Controls/Repeater/UniformGridLayout_Simple.xaml).

`DeviceCard` reuses its control tree when a container is rebound. `DeviceArtwork` records three normalized Skia pictures once and replays them. Only the selected grid/list has an ItemsSource. Other navigation sections release those item sources instead of retaining a second hidden realized catalog. Compact adaptive list rows and detached session windows are not part of this change.

`RecyclingContentHost` provides a named live parent for each realized template. Its child visual is created once, disconnected on ElementClearing, then reattached before rebinding on ElementPrepared. This preserves reuse without leaving actionable semantic peers for filtered or recycled devices. The child's explicit data context also prevents a later repeater context-clear from updating detached inputs. This helper changes hosting, not device commands or the previously unpublished compact-card follow-up.

## Catalog performance and transactions

`DeviceCatalog` is single-owner. Callers serialize mutations; it is not a concurrent collection. Identity lookup builds an index once per device revision. Sorted results, group lists and the most recent filtered result are cached as read-only views. A repeated unchanged query returns its existing view. Extending the same search prefix can filter the previous matches, whereas backspacing, filter changes, sorting and edits re-evaluate the current full order. Only one search result is retained, not an unbounded cache of every query.

`Fork` shares immutable internal records until an edit creates a replacement array. The application saves a candidate fork before publishing it, so failed writes need no serialize/deserialize rollback. `UpsertRange` validates a bounded import and builds one replacement in O(n+m); invalid or duplicate input IDs cannot produce a partial import. It preserves the recency of existing entries. Public snapshots clone the device array so caller edits cannot mutate the catalog.

The regression workload has 10,000 named devices. Narrowing `Node 99` to `Node 999` scans 100 previous matches, and the next character scans 10. A reference full-filter comparison verifies both matches and ordering across searches, filters, forks and mutations. Warm repeated-query allocation and bulk-import measurements are printed by `CatalogRegressions`; they are catalog microbenchmarks, not UI frame rate or network latency.

## Storage and files

Native `FileWorkspaceStore` holds an exclusive persistent lock file while comparing the exact previous document and replacing it. It writes bounded strict UTF-8 into a unique same-directory temporary file and flushes it before replacement. Competing cooperating stores report a conflict rather than overwrite each other. A failed load does not authorize saving over corrupt data. This contract does not coordinate arbitrary external editors that ignore the lock, certify network filesystems, or guarantee survival of every power-loss/disk-failure mode.

The browser store waits for IndexedDB transaction completion and compares the entire prior document in that transaction. Two application windows cannot silently replace one another's saved devices. Refresh recovers from a conflict using the actual store. Browser storage may still be cleared or evicted; exported workspaces provide a user-controlled backup.

`IUserFileAccess` / `SystemFileAccess` is a reusable user-initiated import/export adapter, not remote filesystem redirection. It checks metadata and actual read length, restricts the requested extension, cleans picker state and bounds retained download URLs. Native input is also bounded while reading non-seekable or growing streams. Browser export reports a download request, not a completed disk save. Workspace imports are limited to 10,000 devices and 16 MiB of encoded JSON; RDP files are limited to 1 MiB.

Passwords and bridge tokens are never part of saved device documents. Hostnames, usernames, groups and exported screenshots may still be sensitive infrastructure information; same-origin storage is not a credential vault.

## Evidence and remaining qualification

The exact candidate's CI run is authoritative. `RemoteSpace.Workspace.Tests` covers catalog transactions/cache invariants, endpoint normalization, incremental search, file-store races/conflicts/cancellation and bounded input. Node tests execute the exact JavaScript file and clipboard modules shipped by the controls library. Those tests supplement, rather than replace, actual Uno execution.

The browser workspace suite drives the real app at a Pages-style project path: profiles, filters, list actions, downloads, two-window conflicts, refresh, theme persistence, narrow layout, user-selected bulk imports and bounded grid/list realization with 1,000 devices. The live session suite separately exercises the original RDP engine through its bridge and an authorized loopback XRDP server.

CI uses Chromium/SwiftShader. It does not establish pixel-perfect Windows App visuals, hardware-GPU throughput, complete accessibility/IME coverage, native UI behavior on every desktop host, or a security audit. The broader unimplemented protocol/services and qualification gaps remain in [compatibility](compatibility.md) and [validation](validation.md).

## Modal input and generated-import compatibility

Device information uses a reusable factory that returns a normal Fluent ContentDialog, with explicit accessible heading/body text and a contextual close-command name. Workspace headings, result counts and empty-state descriptions publish their current text to the accessibility tree. No browser-only DOM patch is used.

Profile editing reserves its dialog slot immediately but yields one UI turn before constructing the default-Save dialog. In the pinned Skia runtime, a semantic menu item's Enter keydown first invokes the item and then bubbles to native keyboard routing. Constructing the modal synchronously lets the same Enter save it before the user can edit. The browser regression holds the menu activation key through the transition and requires the visible editor to remain available.

`DeviceCatalog.Parse` uses assembly-private, source-generated mutable read contracts and maps them into validated immutable public records. This preserves initialized defaults for omitted fields on .NET 10, where the generated init-only path can overwrite initializers with CLR defaults. Explicit nulls, invalid dimensions/ports/enums and unknown schema versions remain invalid; optional fields do not silently enable clipboard or weaken NLA. Serialization remains on the public immutable model and its wire schema is unchanged. The native regression suite covers minimal and complete documents, every saved field, omission versus null, and invalid values; the browser imports the same abbreviated 1,000-device document through the actual file picker. See Microsoft's [init-only serialization guidance](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json-immutability#init-only-properties).
