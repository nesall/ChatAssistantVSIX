# Production inline-completion editor integration

This separate VSIX compiles the actual production `InlineCompletionSession`,
`InlineCompletionService`, `InlineCompletionProgress`, and `InlineCompletionCommands` source files. The
completion backend and diagnostic output are replaced with local test stubs.
There are no HTTP calls, model calls, or real configuration writes.

`Run-Integration.ps1 -Visible` builds/deploys into **PhenixInlineIntegration**, a
dedicated VS profile, then opens `native-inline-integration.cpp`. The listener
attaches only to that file and its expected disposable header. Close this test
instance before rebuilding/deploying it. The main extension's VSIX is built
separately; this test extension is not part of its payload.

All test controls use **Ctrl+Alt+Shift** plus a letter:

| Key | Action |
| --- | --- |
| G | Immediate completion |
| M | Multiline completion |
| D | Delayed completion which deliberately ignores cancellation |
| N | Newer immediate completion |
| L | Release the oldest queued delayed completion |
| I | Record buffer, caret, selection, loading state, native proposal ID, progress visibility and dot opacities |
| A | Invoke production explicit acceptance |
| X | Invoke production explicit cancellation |
| E | Simulate a backend error |

The first editor focus positions the caret inside `std::puts()` and requests an
immediate suggestion. Tab acceptance and Esc cancellation use the real VS/native
command chain, including the production pending-request Escape handler.

## November 8, 2026 validation

Observed in the VS 2026 C++ editor, dark theme, normal editor zoom:

- Native single-line and multiline previews with existing suffix text.
- Tab acceptance, caret at insertion end, no selected text, one Ctrl+Z to undo.
- Explicit acceptance also commits through the native suggestion service.
- Accepting during loading leaves the original buffer untouched.
- Delayed status-bar feedback and Escape cancellation while no preview exists.
- Releasing a cancelled result does not resurrect a suggestion.
- D → N → L leaves the newer native proposal ID unchanged.
- Cursor movement while loading cancels and rejects the old-position response.
- Backend errors return to idle and show an explanatory status message.
- Document restored to its original text after the acceptance checks.

`Verify-Integration.ps1` checks these observations against `bin/integration.jsonl`.
The checked-in scenario expects the recorded sequence's request IDs. Reproduce
the documented actions in a fresh log when rerunning the complete verifier.
The log contains only the disposable test document and test statuses.

## Loading indicator validation

The progress follow-up was observed in the same isolated VS 2026 C++ editor.
The three-dot pill appeared near the caret after the delay, animated without
editing the buffer, and disappeared on Escape, clicking the pill, successful
completion, and closing the document. Fast backend failure left no indicator.
A cancelled response did not remove a newer request's pill or native preview.
The native Esc badge rendered correctly in this profile; the reported missing
badge could not be reproduced. Its key label is controlled by Visual Studio.

`Verify-Progress.ps1` checks only the latest editor session. Start with the
automatic immediate preview, dismiss it, and use this sequence (all letter keys
use Ctrl+Alt+Shift):

1. D; wait for the pill; I; wait briefly; I to sample animation.
2. D; wait for the pill; L to release the older request; Esc; L.
3. D; wait for the pill; N; L to check native-preview ownership.
4. D; wait for the pill; click the dots; L.
5. D; wait for the pill; L to finish successfully; E to simulate failure.
6. D; wait for the pill; Ctrl+F4 to close the disposable document.

Wait for the observed result between actions. The verifier uses this sequence's
request counts but ignores records from earlier sessions. Animation-disabled,
high-contrast and alternate-zoom behavior is implemented but was not exercised
in this focused runtime run.

All 12 progress assertions passed. The activity log also recorded built-in
IDE UI-library/menu, source-control option and duplicate-service errors during
startup; none identified the progress layer or production inline controller.

The harness references the installed editor assemblies; production builds use
the NuGet 17.14 editor contracts. Runtime testing of VS 2022 17.14, non-C++
languages, projection views and competing AI providers was not performed here.
The controller intentionally declines selection/multiple-caret/virtual-space
insertion and defers a new result while IntelliSense is active.

The fresh VS profile initially emitted C++ settings/CodeLens initialization
errors before a restart. Those built-in errors were in the activity log and did
not originate in the production controller. The successful run occurred after
the profile finished initializing. Existing VS profiles were not deployment
targets.
