# Native ghost-text feasibility probe

This is a separate MEF-only VSIX experiment. It does not change ChatAssistantVSIX,
its SDK references, commands, existing adornment, configuration, or FIM backend.
It imports the public `SuggestionServiceBase`, registers a custom provider, and
displays `Proposal`/`ProposedEdit` objects with `EditDisplayStyle.GrayText`.
Tab, Escape, rendering, layout space, and committing edits are supplied by VS.

The listener activates only in `native-ghost-probe.cpp` when its first comment
matches the disposable fixture header. It makes no network requests. Buffer
captures in `bin/events.log` contain only that disposable file.

## Run

Build `NativeGhostTextProbe.csproj` using full-framework MSBuild. `ProbeVsRoot`
defaults to the locally installed VS 2026 Community directory. References are
read directly from that installation; editor assemblies are not bundled.

Run `Run-Probe.ps1` to deploy into the dedicated **PhenixGhostProbe** profile.
Its optional `VsRoot` and `InstanceId` parameters identify the installation.
The normal and `Exp` profiles are not deployment targets. Close an existing
PhenixGhostProbe instance before rebuilding/deploying the experiment.

Open this folder's `native-ghost-probe.cpp` in that instance. The first focus
places the caret inside `std::puts()` and displays a fixed suggestion. Thereafter:

- **Ctrl+Alt+Shift+G**: single-line preview at the caret.
- **Ctrl+Alt+Shift+M**: multiline preview at the caret.
- **Ctrl+Alt+Shift+I**: record the stable buffer, caret, selection, and native proposal state.
- **Tab**: native acceptance. **Esc**: native dismissal. **Ctrl+Z**: editor undo.

The extra Shift avoids existing VS shortcuts such as Ctrl+Alt+I (Immediate Window).
`Verify-Results.ps1` checks the recorded run. It uses the first recorded preview
as the baseline, so later documentation-only fixture changes do not invalidate
historical evidence. The number and timing of undo keypresses are visually
verified; the log verifier confirms their resulting buffer states.

## Observed results — November 8, 2026

Tested in a real C++ Miscellaneous Files editor, dark theme, 100% zoom, on the
installed VS 2026 Community editor (`Microsoft.VisualStudio.Language.dll` file
version **18.11.46.52846**, assembly version **18.0.0.0**).

| Check | Result |
| --- | --- |
| Public service import / custom provider registration | Passed |
| Single-line native ghost text at the caret | Passed |
| Existing `);` suffix shown after the preview | Passed |
| Multiline preview reserves editor space | Passed; following real code moves down visually |
| Preview leaves document buffer unchanged | Passed for every recorded preview |
| Native hint bar with Tab / Esc | Passed |
| Tab accepts single-line and multiline edits | Passed; native commit preserved LF document line endings, no custom accept command or formatting step |
| Caret and selection after acceptance | Caret at inserted text end, no selected text |
| One Ctrl+Z undoes each acceptance | Passed, visually checked; restored buffer also logged |
| Esc dismissal | Passed: `DismissedAfterUserEscape` |
| Moving the caret within the same line | Passed: `DismissedAfterCaretMoved` |
| Typing a different character | Ghost disappears: `DivergedAfterTypeChar`; ordinary IntelliSense appears |
| Final buffer restored / no probe exceptions | Passed |

The original validated fixture SHA-256 was
`fdfedb976ab9f50014c90abb4e22e766ab11cdb2776eb69642237635e0aa3324`.
The later fixture comment update documents the tested three-modifier shortcuts.
The original raw events and decoded validation events are retained under ignored
`bin/`; they are diagnostic run artifacts, not required source files.

`EditDisplayStyle.DismissOnCaretMove` is marked obsolete and unimplemented in
this editor's API. The experiment therefore uses plain `GrayText`; native
movement invalidation worked in the tested case without that flag.

## Scope of the conclusion

Native ghost text is demonstrated with public APIs in the installed VS editor.
This is sufficient to proceed with integrating the existing `/api/fim` result
into native suggestions. It is not a production implementation.

The current application's 17.0 SDK references do not contain these public types.
The earliest supported VS 2022 update remains unverified. SDK upgrade/host-version
policy, IntelliSense Tab priority, simultaneous competing AI suggestions,
matching-prefix typing, word wrap, zoom, projection views, multi-caret and
selection behavior still need targeted validation during production integration.
An async FIM integration must also reject stale results and cancel requests on
context changes, regardless of which renderer is used.

Microsoft documents the [third-party proposal extension point](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.language.proposals.proposalsourceproviderbase?view=visualstudiosdk-2022)
and [native gray-text display style](https://learn.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.language.suggestions.editdisplaystyle?view=visualstudiosdk-2022).
