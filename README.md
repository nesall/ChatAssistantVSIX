# ChatAssistantVSIX

A Visual Studio **ToolWindow VSIX extension** that provides a chat interface similar to GitHub Copilot Chat.  
It leverages a **locally deployed Retrieval-Augmented Generation (RAG) system** for interacting with codebases.

## Features

- ToolWindow integrated directly into Visual Studio  
- Chat with context-aware responses from your local codebase  
- Powered by a custom RAG setup for private, offline code assistance  
- Minimal dependencies; fully runs locally  

## Installation

Requires Visual Studio 2022 **17.14 or newer**, or Visual Studio 2026, on Windows x64. The editor references target the public 17.14 suggestion APIs; the native editor integration has been exercised on VS 2026.

1. Build the VSIX project in Visual Studio.  
2. Install the generated `.vsix` file.  
3. Open the **ChatAssistant ToolWindow** from the `View -> Other Windows` menu.  

## Usage

- Type queries or prompts in the chat window.  
- The extension will retrieve relevant context from your codebase and generate helpful responses.  

## Development

- C# ToolWindow VSIX project  
- .NET Framework / .NET Standard (as per project configuration)  
- Local RAG service is required to provide code-aware responses  

## Chat window

The message field, compact model dropdown and Send/Stop button share one rounded composer, with the controls below the text and service status outside it. The dropdown lists each enabled provider once, marks the current default with **(default)** and selects it initially. Automatic routing appears as Auto-router when enabled by the running backend. Model names are shown directly; provider names distinguish entries sharing the same model, and tooltips retain provider details. Choosing a model affects subsequent requests in this conversation without modifying project settings. The choice is captured when sending, and missing models are marked unavailable. Clear or switching solutions resets the choice to the project default.

Responses stream into the conversation. **Send** becomes **Stop** while generating; stopping preserves partial text and any draft in the composer. **Retry response** repeats the failed or stopped turn using its original model and history without duplicating the prompt. Error text and incomplete assistant responses are excluded from subsequent request history. **Clear chat** cancels the active request and rejects late callbacks, including responses from a previously open solution.

Streams that end without the completion marker are reported as interrupted, retain their partial text and offer Retry. Markdown tables, nested lists, and code within lists or quotes are rendered; unsupported constructs remain visible as plain Markdown. The enabled-model list refreshes periodically while the service runs, preserving explicit selections and updating the default marker. Backend model metadata is displayed. File references from `context_sources` appear at the end of the response with their common directory prefix removed; hover a reference for its full path. Copy response includes the shortened list. References remain separate from assistant text sent in follow-up requests, and Retry clears references from the previous attempt.

Enter sends, Shift+Enter inserts a newline, and Escape stops generation while the composer has focus. The transcript follows new text until you scroll up; **Jump to latest** resumes following. Responses and fenced code blocks have copy actions. Inline service status links to Settings instead of interrupting typing with a modal warning.

## Inline suggestions

**Insert code** requests a suggestion at the active editor caret using the configured FIM provider. Existing shortcuts assigned to `Edit.InsertPhenixCodeSuggestion`, `Edit.AcceptPhenixCodeSuggestion`, and `Edit.RejectPhenixCodeSuggestion` remain usable. Suggestions are manually triggered; closing or hiding the chat window does not disable editor commands.

Suggested code appears as native ghost text, including multiline previews and existing code after the caret. **Tab** accepts and **Esc** dismisses. Acceptance keeps the cursor at the end of the inserted code, leaves no selection, and is a single undo operation. It does not run Format Selection. The native hint bar provides mouse actions as well as keyboard hints.

After 350 ms, generation shows a small animated three-dot indicator near the caret alongside status-bar feedback. Click the dots or press Esc to cancel. The indicator follows editor scrolling and zoom, uses the IDE theme, and leaves the document unchanged. Dots remain static when Windows animations are disabled or high contrast is active. Typing, moving the caret, selecting text, switching editors, closing the document, or changing solutions invalidates a pending result. A discarded or superseded request cannot redisplay its result or clear a newer indicator or suggestion. Requests require a single caret in an editable, non-projection document; selections, virtual space, multiple carets, and active snippets are not supported for insertion. IntelliSense is allowed to finish before a new FIM result is displayed, and the explicit accept command does not commit an active IntelliSense list.

Inline requests use up to 3,000 preceding and 1,500 following characters, a 256-token generation limit, and a maximum 30-second timeout (or the project's shorter generation timeout). Backend errors and unavailable native editor services appear in the status bar and diagnostic output. No automatic requests are made while typing.

## Configuration

The extension supports PhenixCode's separate provider catalog and project configuration:

| File | Scope |
| --- | --- |
| `%APPDATA%/PhenixCode/providers.json` | Shared embedding and generation providers, endpoints, credentials, model capabilities and pricing |
| `<solution>/.phenixcode/settings.json` | Source paths, selected/enabled provider IDs, routing, retrieval, chunking and storage |
| `%APPDATA%/PhenixCode/settings.json` | Template for newly opened solutions |
| `%APPDATA%/PhenixCode/vsix.json` | Executable, shared providers path, file watching and inline-completion provider preferences |

Settings shows the active scope. Use **Shared providers** to load an existing catalog (including one managed by the PhenixCode dashboard). The `PHENIXCODE_PROVIDERS_CONFIG` environment variable is used when no explicit path has been saved. An explicitly configured missing or invalid file produces an error; the extension does not replace it with defaults.

Legacy combined settings are migrated automatically. Embedded `embedding.apis` and `generation.apis` move to the provider catalog, while selections stay in project settings. Conflicting provider IDs receive deterministic new IDs and the migrating project's references are updated. Original settings are retained in a one-time `.legacy.bak`; atomic saves retain the preceding file as `.bak`. Unknown fields and `${ENV_VAR}` credential references are preserved. Keep `.phenixcode` runtime data and backup files out of source control when they contain private configuration.

**Apply** saves without closing; **Save and close** saves and closes; **Cancel** discards unapplied edits. Existing source entries and their filters survive unrelated edits. The VS-project convenience action appends missing source directories. JSON editors open with the current draft and retain edits across form rebuilds. **Use JSON** adopts all edited documents together, allowing provider IDs and project references to change in one draft; errors appear beside the editor. Apply also adopts pending JSON and validates cross-file references before saving. **Refresh from forms** explicitly replaces a JSON buffer after confirmation if it has edits. Saves reject detected external file changes, so reopening the dialog is necessary after editing the same files in the dashboard or another VS instance.

If configuration cannot load, Settings offers a Recovery view with raw JSON repair editors, backup loading, provider-catalog selection and reload. Repairs require explicit confirmation, retain the replaced file as `.bak` and reject files changed since opening. Normal Apply/Save remains disabled until configuration loads successfully. Folder-creation failures during solution initialization are caught and reported in the output pane.

Provider edits are shared across solutions. The extension validates references in the current configuration and does not rewrite unopened solutions. Review other projects before renaming, disabling or removing a shared provider. Changing an embedding model, tokenizer, document/query format or vector dimensions requires reindexing.

**Start service** saves first and passes both configuration files explicitly. Missing or malformed executable and tokenizer paths prevent launch and display an explanation in Settings, including the resolved missing path and the tab where it can be corrected. Changes to a running service take effect after stopping and starting it. Relative source, database, log and tokenizer paths resolve from the solution directory; select the tokenizer file from your PhenixCode installation if it is not in the solution. Closing a solution stops only a service launched by the current VS session.

Startup status distinguishes process state from HTTP health: a confirmed running process is reported as started even if the health endpoint is temporarily unavailable. A process still initializing after the initial wait remains pending, with automatic status refresh and a Stop action; reopening Settings is not required. A process that exits during startup is reported separately.

Normal chat follows the project's automatic routing/default model. Inline suggestions use the chosen FIM-capable provider, or a compatible catalog provider when no explicit completion preference is set. Global provider definitions are distinct from `generation.enabled_providers`, the models allowed for a particular project.

The implementation follows the current core schema: `current_api`, `generation.enabled_providers`, `generation.auto_router.enabled`, `source.default_extensions`, and `chunking.nof_min_tokens`/`nof_max_tokens`/`overlap_percentage`.

## Validation

Build the extension with the Visual Studio SDK installed:

```powershell
msbuild ChatAssistantVSIX.sln /t:Build /p:Configuration=Debug
msbuild Tests/ConfigurationStore.Tests.csproj /t:Build
./Tests/bin/ConfigurationStore.Tests.exe
```

The standalone regression harness exercises migration, provider conflicts, schema validation, preservation of unknown properties, atomic writes, and the chat/SSE protocol without touching real user settings. Additional isolated checks are documented in [ServiceIntegration.README.md](Tests/ServiceIntegration.README.md) and [SettingsDialogPreview.md](Tests/SettingsDialogPreview.md). The service harness uses a fake local backend; the WPF preview uses a stub VS theme. Verify a save/restart/reopen cycle in Visual Studio's Experimental Instance before installing into your main instance.

The regression executable also covers inline request ownership and stale-result rejection. `ServiceIntegration.exe --fim-only` tests the actual FIM HTTP method against a fake server on a free local port, with redirected configuration. `Tests/NativeInlineIntegration` compiles the production inline controller and Escape handler into an isolated editor test extension with a fake completion source; it makes no provider/backend requests.

## License

[Not set]  
