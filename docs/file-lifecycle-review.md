# Local file lifecycle review

This change is independent of the automatic-resolution/settings-tab work in PR #7.
It is based on deployed main, changes no RDP protocol or authentication behavior,
and includes none of the blocked local tab-control source.

## Faults reproduced

Five focused regressions fail against the previous exact interop module: input
creation failure strands the single-picker guard; a captured late change callback
reads content after Cancel; repeated change callbacks start concurrent reads;
pagehide releases blob URLs but retains their timers; and a failed download-anchor
creation leaves a URL and byte reservation retained indefinitely. The new tests
keep those assertions and add setup/read errors, timeout, navigation, disposal,
and stale-completion isolation.

## Implementation

The single selection owns its DOM input, deadline and FileReader. Every setup
step is guarded. Its idempotent completion first relinquishes ownership, then
removes handlers, clears its deadline, detaches the input and aborts a pending
read before resolving or rejecting. Cancel or pagehide cannot read a later file,
and a late completion cannot release a newer selection. One read starts per
selection even if duplicate change notifications arrive.

Reads use FileReader.readAsArrayBuffer on a bounded file slice, at most the
configured byte limit plus one overflow-detection byte. Metadata is checked
before starting a read and the actual result is checked again. This avoids
reading an entire oversized range before discovering the overflow, while
FileReader.abort can terminate work on cancellation/navigation/timeout.

The native API behavior is defined by the W3C File API:
https://www.w3.org/TR/FileAPI/#readAsArrayBuffer
https://www.w3.org/TR/FileAPI/#abort
https://www.w3.org/TR/FileAPI/#slice-method-algo

Downloads retain the existing maximum of four URLs and 64 MiB in total. Each URL
owns its timer. Completion, pagehide and setup failures release both the URL and
timer and return the byte reservation. A synchronous lifecycle exit while a link
click executes does not create a new timer after the URL has been released.

These changes do not guarantee a download reached disk, change picker consent,
add remote filesystem access, or read files without user selection. The .NET
service API and native desktop file path are unchanged.

## Validation

`node --test tests/browser/file-service.test.mjs` executes the exact embedded
module. Fault injection covers conditions difficult to force reliably with OS
file dialogs. The stress checks send 10,000 duplicate change notifications for
one selection and 1,000 selection/download/lifecycle-exit iterations.

`python tests/browser/file_service_native.py <output>` executes the same module
in Chromium using actual File, FileReader, native file-chooser automation and
Blob downloads. It checks bytes, early rejection, empty files, real reader abort
on a delivered pagehide event, restoration, and download bytes. This is a
component integration test, not an Uno application substitute. It is added
before the existing workspace run; the full Uno and live RDP tests remain
unchanged deployment gates.

The managed build, full Uno application and live RDP behavior require their own
exact-head CI results. Passing these local component tests is not evidence of
full Windows App parity, hardware-GPU speed, or complete application validation.
