Build with `msbuild Tests/ServiceIntegration.csproj /t:Rebuild`, then run
`Tests/service/bin/ServiceIntegration.exe`. Requires the .NET Framework 4.8.1
developer pack. Newtonsoft.Json is taken from the existing extension output or
Visual Studio's bundled NuGet installation; override with
`/p:NewtonsoftJsonPath=C:\path\Newtonsoft.Json.dll` if needed. No extension build
or backend checkout is required.

Only the executable's fake backend is launched. All runtime fixtures, receipts,
and the source hash manifest live under `Tests/service/bin/sandbox`.
The generated core differs solely in the SettingsPath root expression.
Each run uses a fresh fixture directory; evidence is retained for inspection.

The prep script requires exactly one matching ApplicationData expression in
SettingsPath and verifies the replacement round trip. `source-hash.json` retains
SHA256 hashes for the production and generated files. The build regenerates this
copy on every compile. ConfigurationStore, ChatProtocol and ChatMessage compile
directly from production; embedded defaults retain their Resources.* suffixes.

Coverage includes default initialization, legacy migration and backup retention,
repeated initialization, solution path expansion, preferences and alternate
catalog saving, same-process and cross-process pending-start rejection, shutdown
during delayed startup, process identity, health, authenticated shutdown, buffered
and streaming SSE, request defaults/overrides/history/attachments and solution
close. The fake backend uses TcpListener, validates requests before writing chat
receipts, and never invokes provider URLs. The real APPDATA settings, providers,
and preferences are compared byte-for-byte before and after the run. Inherited
PHENIXCODE_PROVIDERS_CONFIG is cleared only in the test process.

All child processes have bounded waits and final cleanup. The production port
59241 must be free; no URL ACL or other system configuration is modified. The
test moves previous redirected application config into sandbox evidence folders
instead of deleting it. Fake children time out if orphaned.

The embedded defaults are tested for initialization, while SaveConfiguration and
startup use a minimal fake provider catalog. The separate configuration regression
harness validates the shipped catalog, including whitespace FIM stop tokens.
