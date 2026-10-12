# Per-PC display settings

The additional PC settings now group requested Width, Height, Color depth,
Fit session to window and Local zoom in a reusable display-settings card.
Restore display defaults resets this group without changing identity, account,
certificate, security, clipboard or other saved-PC fields. Apply is still the
profile dialog's Save/Add command. Cancel discards the candidate and never writes
preferences or changes an active session.

The interaction follows the distinction in Microsoft's [Windows App display
settings](https://learn.microsoft.com/en-us/windows-app/display-settings): fitting
scales the local presentation without changing remote resolution. This is an
independent single-display implementation, not complete Windows App UI or service
parity. Automatic resolution-on-window-resize is described in [automatic resolution](automatic-resolution.md).
Multi-monitor configuration and server DPI preferences remain separate unimplemented settings.

## Model and compatibility

`ConnectionProfile.FitSessionToWindow` defaults to true for new profiles and old
workspace documents. `LocalZoom` defaults to 1 and accepts finite factors from
0.1 to 4. `GetLocalDisplayScale()` returns null for Fit or the validated fixed
scale otherwise. It uses value types and allocates no new object per read.
`ToOptions()` validates local preferences but does not pass them to the RDP
engine: server pixel dimensions and security options are unchanged by local zoom.

The controls host applies these preferences when creating each new session.
Temporary toolbar changes do not write to a saved PC. Reconnection applies the
saved choices again. Changing a saved profile does not resize or rescale already
open sessions. The selected fixed zoom is retained while Fit is enabled, so
turning Fit back off does not lose the previous selection.

Workspace JSON persists both properties. The mutable input DTO retains initialized
defaults for omitted fields, including older saved documents and minimal imports.
Invalid/null/nonfinite preferences are rejected before the candidate enters the
catalog. Forks, duplication and bulk-import validation preserve these values and
retain their existing conflict/isolation contracts.

RDP import/export supports the standard `smart sizing:i:0|1` field. Its documented
absent-field default is 0 (no automatic local fitting), unlike a newly created
RemoteSpace profile's default. Export writes the field explicitly. Local fixed
magnification is saved in workspace JSON only; it is **not** exported as
`desktopscalefactor`, which concerns remote-session scaling. Unsupported server
DPI properties continue to produce import warnings. The standard dynamic-resolution
property is now supported; see the automatic-resolution document for its defaults.
Known integer fields reject embedded/trailing NUL instead of accepting a numeric
prefix through .NET's parser. The source reference is Microsoft's [supported RDP
properties](https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties).

## Controls and validation

`SavedDisplaySettingsEditor` owns no transport or storage. It keeps one set of
controls and realizes them under a live `SemanticLayoutHost` when loaded. Its
Read method updates only display fields in the supplied immutable profile;
normal profile validation then checks the final normalized endpoint as well.
Preset choices retain an imported non-preset zoom value rather than rounding it.
Colors use theme resources and the form works with the existing light/dark shell.
The profile dialog also uses the reusable accessible `ValidationMessage` control
instead of an initially empty text peer that can be pruned by the browser host.

`DisplayPreferencesRegressions` tests default-filled imports, both fit modes,
fractional zoom preservation, invalid model/JSON values, unchanged RDP options,
copy-on-write cancellation, duplication, smart-sizing round trips and strict
integer rejection. It separately checks 100,000 warmed scale reads for managed
allocation; that is not a UI/network/GPU benchmark.

Real browser tests use the profile editor and file export to verify persistence,
reload, Cancel, and restoring only display defaults. Live XRDP tests create a PC
with 150% local zoom, require that scale before any toolbar override, count that
no server resize was sent, and check that reconnect uses the saved preference.
The exact commit's workflow artifacts distinguish passing execution from source
implementation. Full Windows/RDS, hardware-GPU, accessibility/IME/platform and
security qualification remain listed in the compatibility matrix.


The pinned Uno browser ComboBox factory omitted the initial disabled state for
local zoom. The editor publishes its actual IsEnabled state through the public
automation peer when attached and after changes, without rewriting browser
attributes or changing input values. The existing browser checks still require
the control to be disabled while Fit is selected and enabled when it is cleared.
