# Security model

RemoteSpace is an experimental remote-desktop client and bridge, not a security-reviewed enterprise gateway. Connect only to machines you are authorized to administer. Do not expose an unrestricted bridge publicly.

## Trust boundaries

The native client validates the RDP TLS certificate. NLA is the default security mode. Explicit TLS-only operation exists for hosts configured without NLA; it is not an automatic fallback from a failed NLA connection. An explicit SHA-256 pin is a trust decision and must be obtained from the administrator through another trusted channel. It does not make an expired certificate acceptable.

The browser bridge sees connection credentials in memory and terminates the RDP TLS session. Run it only on infrastructure you trust, behind authenticated TLS/WebSocket infrastructure with private-network controls. The initial token is not a replacement for per-user enterprise authentication and auditing. Origin checks prevent accidental cross-origin use; they do not replace authentication because non-browser clients can choose an Origin header.

Only explicitly allowlisted target host/port pairs can be used. Restrict server egress as well: host-name allowlists are not a substitute for network isolation or trusted DNS. Browser access tokens and passwords are sent in the first WebSocket message, never in URLs or saved-device documents. Managed strings and browser runtimes do not provide a guarantee of immediate secret erasure from every memory copy.

## Limits

Authentication, connection establishment and reactivation have bounded waits. Concurrent bridge sessions, messages, queued bytes, input rates, clipboard requests, display requests and framebuffer dimensions are bounded. Dynamic-channel fragments have aggregate allocation limits and malformed state is discarded. A slow browser is disconnected instead of receiving silently dropped framebuffer state.

Clipboard text is opt-in. This preview does not expose drives, arbitrary files, printers, USB devices, smart cards, microphones or cameras. Unsupported channels are not implicitly enabled.

## Storage and reports

Saved hostnames, usernames, groups and preferences are not secrets in a credential vault; browser storage is accessible to code running in the same origin and can be evicted. Exported workspaces may contain identifying infrastructure information. Keep exports and desktop screenshots private as appropriate.

For a security issue, do not post credentials, tokens, private hostnames, screen contents or sensitive packet captures in a public issue. Use the repository's private vulnerability-reporting mechanism when available, or arrange a private report with the maintainer. Include the exact source commit and a sanitized reproducer.
