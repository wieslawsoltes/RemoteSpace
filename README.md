# RemoteSpace

Independent, from-scratch RDP implementation and reusable Uno Platform remote-desktop controls.

**Status: experimental implementation in progress. This is not a full-parity Windows App replacement.** Unsupported protocol features will be explicitly reported rather than silently simulated. No Microsoft source code, binaries, logos, or proprietary assets are included.

The intended architecture separates protocol codecs, connection/session management, framebuffer and input primitives, reusable Uno controls, desktop/browser applications, and an authenticated browser bridge. Browser and desktop capabilities are tracked separately.

Implementation, tests, and a compatibility matrix are being added on a feature branch. A static GitHub Pages site cannot itself run the server-side RDP bridge.
