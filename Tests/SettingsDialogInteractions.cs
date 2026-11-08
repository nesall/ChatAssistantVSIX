using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ChatAssistantVSIX.Dialogs;
using ChatAssistantVSIX.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class SettingsDialogInteractions
{
 private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

 internal static IEnumerable<string> Run()
 {
  var files = Directory.GetFiles(PhenixCodeCoreService.Root, "fixture-*.json").ToDictionary(p => p, File.ReadAllText);
  var tests = new Dictionary<string, Action> {
   { "simultaneous project/provider JSON rename", AdoptTogether },
   { "invalid JSON prevents either document adoption and retains buffers", RejectTogether },
   { "project JSON repairs invalid stale numeric form", RepairForm },
   { "form capture is transactional", TransactionalCapture },
   { "provider references respect namespaces and ignore unrelated text", References },
   { "model choices exclude disabled and project-unallowed providers", Choices },
   { "unrelated form rebuild preserves dirty JSON", Rebuild },
   { "Apply receives coordinated JSON drafts and rejects unresolved references", ApplyDrafts },
   { "optional defaults remain inherited and match the core", Inherited },
   { "dynamic theme resources update existing controls", ThemeSwitch },
   { "running service is not reported failed when health is unavailable", StartupStatus },
   { "Start action succeeds without a health response", StartAction },
   { "invalid configuration exposes recovery and preserves backups", Recovery }
  };
  var results = new List<string>();
  foreach (var test in tests) {
   test.Value();
   foreach (var file in files) Require(File.ReadAllText(file.Key) == file.Value, "Interaction wrote fixture: " + file.Key);
   results.Add("PASS: " + test.Key + "; no fixture writes.");
  }
  return results;
 }

 private static SettingsDialog Fresh()
 {
  var dialog = new SettingsDialog();
  Require(((Button)dialog.FindName("Apply")).IsEnabled, "Fixture dialog failed to initialize: " + ((TextBox)dialog.FindName("Status")).Text);
  Require(!dialog.IsVisible, "Interaction dialog unexpectedly shown");
  return dialog;
 }
 private static void Recovery()
 {
  var valid = (JObject)Document(Fresh(), "settings").DeepClone();
  var path = Path.Combine(PhenixCodeCoreService.Root, "recovery-" + Guid.NewGuid().ToString("N") + ".json");
  try {
   File.WriteAllText(path, "{broken");
   PhenixCodeCoreService.ScopeOverride = path;
   var dialog = new SettingsDialog();
   var tabs = (TabControl)dialog.FindName("Pages");
   Require(!((Button)dialog.FindName("Apply")).IsEnabled && tabs.Items.OfType<TabItem>().Any(t => (string)t.Header == "Recovery"), "Broken configuration did not expose Recovery");
   Call(dialog, "WriteRecoveryFile", path, "{broken", valid);
   Require(File.ReadAllText(path + ".bak") == "{broken", "Repair did not preserve original bytes as backup");
   bool rejected = false;
   try { Call(dialog, "WriteRecoveryFile", path, "{broken", new JObject()); } catch (IOException) { rejected = true; }
   Require(rejected, "Recovery overwrote a file changed since opening");
   Call(dialog, "ReloadConfiguration");
   Require(((Button)dialog.FindName("Apply")).IsEnabled, "Repaired configuration could not reload without reopening Settings");
   PhenixCodeCoreService.ProvidersPathOverride = path + ".missing";
   var missing = new SettingsDialog();
   Require(((TabControl)missing.FindName("Pages")).Items.OfType<TabItem>().Any(t => (string)t.Header == "Recovery"), "Missing catalog did not expose Recovery");
  } finally {
   PhenixCodeCoreService.ScopeOverride = null; PhenixCodeCoreService.ProvidersPathOverride = null;
   File.Delete(path); File.Delete(path + ".bak");
  }
 }
 private static object Field(object owner, string name) => owner.GetType().GetField(name, Private | BindingFlags.Public).GetValue(owner);
 private static JObject Document(SettingsDialog dialog, string key) => (JObject)Field(dialog, key);
 private static object Draft(SettingsDialog dialog, string key) => ((IDictionary)Field(dialog, "jsonDrafts"))[key];
 private static TextBox Editor(SettingsDialog dialog, string key) => (TextBox)Field(Draft(dialog, key), "Editor");
 private static bool Dirty(SettingsDialog dialog, string key) => (bool)Draft(dialog, key).GetType().GetProperty("Dirty", Private | BindingFlags.Public).GetValue(Draft(dialog, key));
 private static object Call(SettingsDialog dialog, string method, params object[] args)
 {
  try { return typeof(SettingsDialog).GetMethod(method, Private).Invoke(dialog, args); }
  catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
 }
 private static JObject Clone(SettingsDialog dialog, string key) => (JObject)Document(dialog, key).DeepClone();
 private static void Equal(JToken expected, JToken actual, string message) => Require(JToken.DeepEquals(expected, actual), message);
 private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

 private static void Rename(SettingsDialog dialog, out JObject project, out JObject catalog)
 {
  project = Clone(dialog, "settings"); catalog = Clone(dialog, "providers");
  var provider = ((JArray)catalog["generation_providers"]).OfType<JObject>().Single(p => (string)p["id"] == "custom");
  provider["id"] = "interaction-renamed";
  project["generation"]["current_api"] = "interaction-renamed";
  var enabled = (JArray)project["generation"]["enabled_providers"];
  enabled[enabled.Values<string>().ToList().IndexOf("custom")] = "interaction-renamed";
 }
 private static void AdoptTogether()
 {
  var dialog = Fresh();
  Rename(dialog, out var project, out var catalog);
  var projectText = "\n" + project.ToString(Formatting.Indented) + "\n";
  var catalogText = catalog.ToString(Formatting.None) + "\n ";
  Editor(dialog, "settings").Text = projectText;
  Editor(dialog, "providers").Text = catalogText;
  Require((bool)Call(dialog, "AdoptJsonDrafts"), "Valid coordinated JSON was rejected");
  Equal(project, Document(dialog, "settings"), "Project JSON was not adopted exactly");
  Equal(catalog, Document(dialog, "providers"), "Provider JSON was not adopted exactly");
  Call(dialog, "CheckReferences");
  foreach (var key in new[] { "settings", "providers" }) {
   Require(Editor(dialog, key).Text == (key == "settings" ? projectText : catalogText), "Adoption reformatted the " + key + " buffer");
   Require(Editor(dialog, key).IsEnabled && !Editor(dialog, key).IsReadOnly, "Adoption disabled the " + key + " editor");
   Require(!Dirty(dialog, key), "Adopted " + key + " remains pending");
  }
 }
 private static void RejectTogether()
 {
  foreach (var invalidKey in new[] { "settings", "providers" }) {
   var dialog = Fresh(); var beforeProject = Clone(dialog, "settings"); var beforeCatalog = Clone(dialog, "providers");
   Rename(dialog, out var project, out var catalog);
   Editor(dialog, "settings").Text = project.ToString(); Editor(dialog, "providers").Text = catalog.ToString();
   Editor(dialog, invalidKey).Text = "{\"unfinished\":";
   var projectText = Editor(dialog, "settings").Text; var catalogText = Editor(dialog, "providers").Text;
   Require(!(bool)Call(dialog, "AdoptJsonDrafts"), "Invalid " + invalidKey + " JSON accepted");
   Equal(beforeProject, Document(dialog, "settings"), "Invalid batch partially adopted project");
   Equal(beforeCatalog, Document(dialog, "providers"), "Invalid batch partially adopted providers");
   Require(Editor(dialog, "settings").Text == projectText && Editor(dialog, "providers").Text == catalogText, "Rejected JSON buffers lost");
   Require(Dirty(dialog, "settings") && Dirty(dialog, "providers"), "Rejected JSON no longer pending");
   Require(Editor(dialog, "settings").IsEnabled && Editor(dialog, "providers").IsEnabled, "Rejected JSON editors disabled");
  }
 }
 private static void RepairForm()
 {
  var dialog = Fresh(); var project = Clone(dialog, "settings"); var catalog = Clone(dialog, "providers");
  Input(dialog, "chunking.nof_min_tokens").Text = "invalid-number";
  // A whitespace edit makes the original valid JSON an explicit repair candidate.
  Editor(dialog, "settings").Text += "\n";
  Require((bool)Call(dialog, "AdoptJsonDrafts"), "Valid JSON could not repair an invalid stale form");
  Equal(project, Document(dialog, "settings"), "Repair changed the original valid project candidate");
  Equal(catalog, Document(dialog, "providers"), "Repair changed providers");
  Call(dialog, "Capture", new object[] { null });
  Equal(project, Document(dialog, "settings"), "Rebuilt repair form is invalid or changed the candidate");
 }
 private static void TransactionalCapture()
 {
  var dialog = Fresh(); var project = Clone(dialog, "settings"); var catalog = Clone(dialog, "providers"); var runtime = Clone(dialog, "preferences");
  Input(dialog, "source.project_title").Text = "This earlier form edit must remain uncommitted";
  Input(dialog, "chunking.nof_min_tokens").Text = "invalid-number";
  Exception failure = null;
  try { Call(dialog, "Capture", new object[] { null }); } catch (InvalidDataException ex) { failure = ex; }
  Require(failure != null && failure.Message.Contains("chunking.nof_min_tokens"), "Capture did not reject invalid numeric form");
  Equal(project, Document(dialog, "settings"), "Failed capture committed an earlier project edit");
  Equal(catalog, Document(dialog, "providers"), "Failed capture changed providers");
  Equal(runtime, Document(dialog, "preferences"), "Failed capture changed preferences");
  Require(Input(dialog, "source.project_title").Text.StartsWith("This earlier"), "Failed capture lost form edits");
 }
 private static bool IsReferenced(SettingsDialog dialog, string id, string kind) => (bool)Call(dialog, "Referenced", id, kind);
 private static void References()
 {
  var dialog = Fresh(); var project = Document(dialog, "settings"); var generation = (JObject)project["generation"];
  project["embedding"]["current_api"] = "embedding-only";
  generation["current_api"] = "generation-current"; generation["enabled_providers"] = new JArray("generation-enabled");
  generation.Remove("auto_router"); project["source"]["project_title"] = "unrelated-id";
  project["source"]["project_description"] = "generation-current";
  Document(dialog, "preferences")["fim_provider"] = "fim-only";
  Require(IsReferenced(dialog, "embedding-only", "embedding_providers") && !IsReferenced(dialog, "embedding-only", "generation_providers"), "Embedding reference crossed namespace");
  foreach (var id in new[] { "generation-current", "generation-enabled", "fim-only" })
   Require(IsReferenced(dialog, id, "generation_providers") && !IsReferenced(dialog, id, "embedding_providers"), "Generation/FIM reference crossed namespace: " + id);
  Require(!IsReferenced(dialog, "unrelated-id", "generation_providers") && !IsReferenced(dialog, "unrelated-id", "embedding_providers"), "Unrelated project text treated as reference");
  generation["auto_router"] = JObject.Parse("{'classifier':{'api_id':'classifier-id'},'fallback':{'default_model_id':'fallback-id'},'routing_rules':{'tier_1_simple':{'direct_model_id':'direct-id'},'tier_2_medium':{'draft_model_ids':['draft-id'],'synthesizer_model_id':'synth-id'}}}");
  foreach (var id in new[] { "classifier-id", "fallback-id", "direct-id", "draft-id", "synth-id" })
   Require(IsReferenced(dialog, id, "generation_providers") && !IsReferenced(dialog, id, "embedding_providers"), "Router reference not protected in generation namespace: " + id);
 }
 private static IEnumerable<string> Options(SettingsDialog dialog, JProperty property) => (IEnumerable<string>)Call(dialog, "Choices", property);
 private static void Choices()
 {
  var dialog = Fresh(); var catalog = Document(dialog, "providers"); var project = Document(dialog, "settings");
  catalog["embedding_providers"] = JArray.Parse("[{'id':'embedding-ok'},{'id':'embedding-disabled','enabled':false}]");
  catalog["generation_providers"] = JArray.Parse("[{'id':'allowed','enabled':true,'fim':{}},{'id':'disabled','enabled':false,'fim':{}},{'id':'unallowed','enabled':true,'fim':{}},{'id':'chat-only','enabled':true}]");
  var generation = (JObject)project["generation"];
  generation["enabled_providers"] = new JArray("allowed", "disabled", "chat-only");
  generation["auto_router"] = JObject.Parse("{'classifier':{'api_id':'allowed'},'fallback':{'default_model_id':'allowed'},'routing_rules':{'tier_1_simple':{'direct_model_id':'allowed'}}}");
  Require(Options(dialog, ((JObject)project["embedding"]).Property("current_api")).SequenceEqual(new[] { "embedding-ok" }), "Embedding choices include disabled or generation IDs");
  foreach (var path in new[] { "generation.current_api", "generation.auto_router.classifier.api_id", "generation.auto_router.fallback.default_model_id", "generation.auto_router.routing_rules.tier_1_simple.direct_model_id" }) {
   var property = (JProperty)project.SelectToken(path).Parent;
   Require(Options(dialog, property).SequenceEqual(new[] { "allowed", "chat-only" }), "Model choices include disabled or unallowed IDs: " + path);
  }
  Require(Options(dialog, Document(dialog, "preferences").Property("fim_provider")).SequenceEqual(new[] { "", "allowed" }), "FIM choices include disabled, unallowed, or chat-only IDs");
 }
 private static void Rebuild()
 {
  var dialog = Fresh(); var catalog = Clone(dialog, "providers");
  var projectText = "{\"still-unfinished\":"; var providerText = Editor(dialog, "providers").Text + "\n ";
  Editor(dialog, "settings").Text = projectText; Editor(dialog, "providers").Text = providerText;
  Input(dialog, "source.project_title").Text = "Unrelated form update";
  Call(dialog, "Capture", new object[] { null }); Call(dialog, "Build");
  Require((string)Document(dialog, "settings")["source"]["project_title"] == "Unrelated form update", "Unrelated form edit not captured");
  Equal(catalog, Document(dialog, "providers"), "Unrelated rebuild changed providers");
  Require(Editor(dialog, "settings").Text == projectText && Editor(dialog, "providers").Text == providerText, "Rebuild replaced dirty JSON buffers");
  Require(Dirty(dialog, "settings") && Dirty(dialog, "providers"), "Rebuild cleared pending JSON edits");
  Require(Editor(dialog, "settings").IsEnabled && Editor(dialog, "providers").IsEnabled, "Rebuild disabled dirty JSON editors");
 }
 private static TextBox Input(SettingsDialog dialog, string tooltip) => Walk(dialog).OfType<TextBox>().Single(t => (t.ToolTip as string) == tooltip);
 private static void ApplyDrafts()
 {
  var dialog = Fresh(); Rename(dialog, out var project, out var catalog);
  Editor(dialog, "settings").Text = project.ToString(); Editor(dialog, "providers").Text = catalog.ToString();
  int saves = 0;
  PhenixCodeCoreService.SaveObserver = (s, p, path, runtime) => {
   ConfigurationStore.Validate(s, p); Equal(project, s, "Apply changed project JSON"); Equal(catalog, p, "Apply changed provider JSON"); saves++;
  };
  try {
   Require((bool)Call(dialog, "Persist") && saves == 1, "Apply did not adopt and submit both dirty documents");
   project["generation"]["current_api"] = "missing"; Editor(dialog, "settings").Text = project.ToString();
   Require(!(bool)Call(dialog, "Persist") && saves == 1, "Unresolved references reached the save boundary");
  } finally { PhenixCodeCoreService.SaveObserver = null; }
 }
 private static void Inherited()
 {
  var dialog = Fresh(); var catalog = Document(dialog, "providers");
  var provider = ((JArray)catalog["generation_providers"]).OfType<JObject>().First();
  provider.Remove("context_length"); provider.Remove("max_tokens_name");
  catalog["default_context_length"] = 77777;
  catalog["default_max_tokens_name"] = "max_completion_tokens";
  Call(dialog, "Build");
  var before = Clone(dialog, "providers");
  Require(Walk(dialog).OfType<TextBox>().Any(b => b.Text == "77777"), "Inherited catalog context length was not shown");
  Require(Walk(dialog).OfType<TextBox>().Any(b => b.Text == "max_completion_tokens"), "Inherited token parameter was not shown");
  Call(dialog, "Capture", new object[] { null }); Equal(before, Document(dialog, "providers"), "Showing inherited values materialized overrides");
 }
 private static void ThemeSwitch()
 {
  var dialog = Fresh();
  var palette = dialog.Resources.MergedDictionaries.OfType<Community.VisualStudio.Toolkit.PreviewThemeResources>().Single();
  var input = Input(dialog, "source.project_title");
  palette.ApplyPalette("Light");
  var light = ((System.Windows.Media.SolidColorBrush)dialog.Background).Color;
  palette.ApplyPalette("Dark");
  var dark = ((System.Windows.Media.SolidColorBrush)dialog.Background).Color;
  Require(light != dark && ((System.Windows.Media.SolidColorBrush)input.Background).Color == dark, "Existing window/editor did not follow dark palette update");
  palette.ApplyPalette("HighContrast");
  Require(((System.Windows.Media.SolidColorBrush)input.Foreground).Color != ((System.Windows.Media.SolidColorBrush)input.Background).Color, "High contrast foreground matches editor background");
 }
 private static IEnumerable<DependencyObject> Walk(DependencyObject node)
 {
  yield return node;
  foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
   foreach (var item in Walk(child)) yield return item;
 }
 private static void StartupStatus()
 {
  try {
   var dialog = Fresh();
   var status = (TextBox)dialog.FindName("Status");
   PhenixCodeCoreService.IsServiceStarting = true;
   Require(((System.Threading.Tasks.Task<bool>)Call(dialog, "ReportStartupAsync")).GetAwaiter().GetResult(), "Slow but live startup rejected");
   Require(status.Text.Contains("still starting") && (bool)Field(dialog, "monitorStartup"), "Slow startup was not left pending for automatic refresh");
   PhenixCodeCoreService.IsServiceRunning = true; PhenixCodeCoreService.IsServiceStarting = false;
   PhenixCodeCoreService.HealthProbe = () => System.Threading.Tasks.Task.FromResult(false);
   Require(((System.Threading.Tasks.Task<bool>)Call(dialog, "ReportStartupAsync")).GetAwaiter().GetResult(), "Running backend rejected because health is unavailable");
   Require(status.Text.Contains("Service started.") && status.Text.Contains("not yet confirmed"), "Unconfirmed health was presented as a failed launch");
   ((System.Threading.Tasks.Task)Call(dialog, "RefreshServiceAsync")).GetAwaiter().GetResult();
   Require((string)((Button)dialog.FindName("Service")).Content == "Stop service", "Existing dialog did not refresh the running-state button");
   PhenixCodeCoreService.HealthProbe = () => { throw new IOException("Temporary health endpoint failure"); };
   Require(((System.Threading.Tasks.Task<bool>)Call(dialog, "ReportStartupAsync")).GetAwaiter().GetResult(), "Health probe exception converted a running backend into failed launch");
   PhenixCodeCoreService.HealthProbe = () => System.Threading.Tasks.Task.FromResult(true);
   ((System.Threading.Tasks.Task)Call(dialog, "RefreshServiceAsync")).GetAwaiter().GetResult();
   Require(status.Text.Contains("health check passed") && !(bool)Field(dialog, "monitorStartup"), "Health recovery did not complete pending status automatically");
   typeof(SettingsDialog).GetField("monitorStartup", Private).SetValue(dialog, true);
   PhenixCodeCoreService.IsServiceRunning = false;
   ((System.Threading.Tasks.Task)Call(dialog, "RefreshServiceAsync")).GetAwaiter().GetResult();
   Require(status.Text.Contains("process exited") && (string)((Button)dialog.FindName("Service")).Content == "Start service", "Genuine process exit was not reported");
   PhenixCodeCoreService.IsServiceRunning = true;
   status.Text = "Requesting service shutdown";
   PhenixCodeCoreService.HealthProbe = () => {
    typeof(SettingsDialog).GetField("serviceOperation", Private).SetValue(dialog, 100);
    return System.Threading.Tasks.Task.FromResult(false);
   };
   ((System.Threading.Tasks.Task<bool>)Call(dialog, "ReportStartupAsync")).GetAwaiter().GetResult();
   Require(status.Text == "Requesting service shutdown", "Stale health response overwrote a newer operation");
  } finally {
   PhenixCodeCoreService.IsServiceRunning = PhenixCodeCoreService.IsServiceStarting = false;
   PhenixCodeCoreService.HealthProbe = null;
  }
 }
 private static void StartAction()
 {
  var context = System.Threading.SynchronizationContext.Current;
  var tokenizer = Path.Combine(PhenixCodeCoreService.Root, "start-tokenizer-" + Guid.NewGuid().ToString("N") + ".tmp");
  using (var process = System.Diagnostics.Process.GetCurrentProcess()) {
   try {
    var dialog = Fresh();
    Document(dialog, "preferences")["executable_path"] = process.MainModule.FileName;
    Call(dialog, "Build");
    PhenixCodeCoreService.SaveObserver = (s, p, path, runtime) => ConfigurationStore.Validate(s, p);
    Document(dialog, "settings")["tokenizer"]["config_path"] = tokenizer;
    Call(dialog, "Build");
    bool launched = false;
    PhenixCodeCoreService.LaunchService = (exe, pane) => { launched = true; return process; };
    ((System.Threading.Tasks.Task)Call(dialog, "OnServiceAsync")).GetAwaiter().GetResult();
    Require(!launched && ((TextBox)dialog.FindName("Status")).Text.Contains(tokenizer), "Missing tokenizer did not show its resolved path without launching");
    Require(((Button)dialog.FindName("Service")).IsEnabled && ((Button)dialog.FindName("Apply")).IsEnabled, "Invalid path left Settings disabled");
    Require(ServicePathValidation.Check(process.MainModule.FileName, "bad\0path", PhenixCodeCoreService.Root).Contains("path is invalid"), "Malformed tokenizer path escaped validation");
    File.WriteAllText(tokenizer, "{}");
    PhenixCodeCoreService.HealthProbe = () => System.Threading.Tasks.Task.FromResult(false);
    PhenixCodeCoreService.LaunchService = (exe, pane) => { PhenixCodeCoreService.IsServiceRunning = true; return process; };
    System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
    var start = (System.Threading.Tasks.Task)Call(dialog, "OnServiceAsync");
    var frame = new System.Windows.Threading.DispatcherFrame();
    var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
    timeout.Tick += (_, __) => frame.Continue = false;
    var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
    poll.Tick += (_, __) => { if (start.IsCompleted) frame.Continue = false; };
    try { timeout.Start(); poll.Start(); if (!start.IsCompleted) System.Windows.Threading.Dispatcher.PushFrame(frame); }
    finally { timeout.Stop(); poll.Stop(); }
    Require(start.IsCompleted, "Start action waited for the health timeout despite a confirmed running process");
    start.GetAwaiter().GetResult();
    Require(((TextBox)dialog.FindName("Status")).Text.Contains("Service started."), "Successful Start action reported failure");
    Require((string)((Button)dialog.FindName("Service")).Content == "Stop service" && ((Button)dialog.FindName("Service")).IsEnabled, "Start action did not enable Stop without reopening");
   } finally {
    File.Delete(tokenizer);
    System.Threading.SynchronizationContext.SetSynchronizationContext(context);
    PhenixCodeCoreService.IsServiceRunning = PhenixCodeCoreService.IsServiceStarting = false;
    PhenixCodeCoreService.SaveObserver = null; PhenixCodeCoreService.HealthProbe = null; PhenixCodeCoreService.LaunchService = null;
   }
  }
 }
}
