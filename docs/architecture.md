# Architecture

```text
Uno workspace
  ├─ Workspace → Profiles → Core
  └─ Controls → Core + Graphics + Input
       └─ IRemoteSession (injected)
            ├─ Desktop: Client → Protocol + Security + Licensing + Graphics + Channels
            └─ Browser: Transport → authenticated WebSocket → Bridge → Client
```

All wire codecs are independent of Uno. The shared `Core` contracts keep surface updates, connection state and input independent of any UI host. The bridge runs the same managed RDP engine used by the native client; it is not an adapter around another RDP library.

## Session ownership

Create a new session object for each connection attempt. Subscribe to events and attach the view before calling `RunAsync`. Credentials are separate from serializable options and profiles. The owner cancels/disconnects and disposes the session; `RemoteDesktopView` only attaches/detaches event handlers and serializes input. A slow or failed output path is explicitly terminated rather than silently dropping stateful surface or key events.

## Display-control extension

Use `IRemoteDisplayControl` only after its server capabilities are available and the session is active:

```csharp
if (session is IRemoteDisplayControl display &&
    session.State == SessionState.Active && display.DisplayCapabilities is not null)
{
    await display.RequestDisplayLayoutAsync(
        new[] { new DisplayMonitor(0, 0, 1920, 1080) }, cancellationToken);
}
```

Completion means the layout was sent. `DesktopResized` reports the server's actual negotiated size after reactivation, followed by `SurfaceUpdated` pixels. The renderer never changes its allocation merely because a resize was requested. Geometry is checked against both the server's advertised limits and the local 16-megapixel/8192-axis policy.

`Channels` owns DVC parsing, per-channel reassembly, aggregate memory limits and byte-weighted v2 priority scheduling. DVC creation is accepted only for registered listener names. Unknown listeners receive a failure response; v3 compression is not advertised. The client serializes complete channel writes and handles display capability and close messages.

The bridge extension is opt-in through `EnableDisplayControl` in the initial connection message. Older clients therefore do not receive unknown display messages. Both sides validate layouts; the bridge also limits resize request frequency.

## Rendering and persistence

The decoder emits bounded RGBA rectangles. `RemoteDesktopView` updates a Skia bitmap and invalidates dirty presentation on the Uno UI dispatcher. Native cursor masks are composed against the actual underlying desktop. GPU presentation is supplied by the Uno host; decode and bitmap uploads are not a compute-shader implementation.

Browser persistence uses source-generated .NET JavaScript interop into a fixed module. One IndexedDB read/write transaction compares the exact previous document and commits the replacement. Transaction completion is awaited before a save is reported. No user text is evaluated as JavaScript. Native storage uses a temporary file and replacement with a revision check; that is not a transactional cross-process compare-and-swap.
