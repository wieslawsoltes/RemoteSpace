# Browser bridge

A static page cannot open a raw TCP RDP connection or use the desktop TLS/SPNEGO APIs. RemoteSpace's browser application uses a versioned authenticated WebSocket adapter. The bridge runs the **same original RemoteSpace.Client RDP implementation**, not FreeRDP, Guacamole, mstsc, or a video-streaming substitute.

The bridge is a trusted component: it receives credentials in memory and terminates the remote RDP TLS session. Operate it on infrastructure you control. GitHub Pages hosts only the static Uno application; it does not host this process.

```sh
export REMOTESPACE_TOKEN="$(openssl rand -hex 32)"
export REMOTESPACE_ORIGINS='https://wieslawsoltes.github.io'
export REMOTESPACE_TARGETS='desktop.example.net:3389;[::1]:3389'
dotnet run --project apps/RemoteSpace.Bridge -c Release --urls http://127.0.0.1:5080
```

Put the process behind an HTTPS/WebSocket reverse proxy and use a `wss://` endpoint. Plain WebSockets are accepted only on a loopback development connection. Add the exact local development origin explicitly when testing. There are no wildcard origins or wildcard RDP targets.

The first WebSocket message authenticates with a token; tokens/passwords never appear in a URL, are not written to logs or profile storage, and are not saved by the client. Default RDP certificate validation checks the trust chain and hostname. An optional explicit SHA-256 certificate pin must match exactly and does not allow an expired certificate. Obtain pins out of band, not by blindly trusting a connection error.

Each process permits at most eight active/handshaking sessions. Authentication has a five-second deadline. Control messages, surface rectangles, input rate and each output queue have explicit bounds. A slow client is disconnected rather than receiving a silently corrupted framebuffer. Clipboard text is opt-in; arbitrary filesystem, drive, printer, USB and audio redirection are not exposed.

This is an experimental service, not a reviewed enterprise gateway. Use an additional authenticated reverse proxy, private network access, token rotation, connection auditing and per-user policy before deployment. RD Gateway, AVD, Windows 365 and Dev Box authentication/discovery are not implemented by this bridge.
