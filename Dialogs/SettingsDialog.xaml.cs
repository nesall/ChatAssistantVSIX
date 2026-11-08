using ChatAssistantVSIX.Utils;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ChatAssistantVSIX.Dialogs
{
  public partial class SettingsDialog : DialogWindow
  {
    private JObject settings, providers, preferences, originalProviders;
    private string providersPath, savedState, settingsPath;
    private readonly List<KeyValuePair<string, Action>> readers = new List<KeyValuePair<string, Action>>();
    private string buildingDocument = "settings";
    private List<Action> stagedChanges;
    private readonly Dictionary<string, JsonDraft> jsonDrafts = new Dictionary<string, JsonDraft>();
    private sealed class JsonDraft
    {
      public string Text, Baseline, Error, Model;
      public int Caret;
      public double Scroll;
      public TextBox Editor;
      public TextBlock Feedback;
      public bool Dirty => Text != Baseline;
    }
    private readonly Dictionary<string, string> disk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private bool ready, busy, closingSaved, loadingProviderFile;
    private bool monitorStartup, refreshingService;
    private int serviceOperation;
    private readonly DispatcherTimer serviceRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

    public SettingsDialog()
    {
      InitializeComponent();
      ReloadConfiguration();
      serviceRefresh.Tick += (_, _) => ThreadHelper.JoinableTaskFactory.RunAsync(RefreshServiceAsync).FireAndForget();
      Loaded += (_, _) => serviceRefresh.Start();
      Closed += (_, _) => { serviceRefresh.Stop(); monitorStartup = false; serviceOperation++; };
    }

    private void ReloadConfiguration()
    {
      ready = false;
      settingsPath = PhenixCodeCoreService.ActiveSettingsPath;
      try
      {
        ThreadHelper.JoinableTaskFactory.Run(async () =>
        {
          await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
          if (string.IsNullOrEmpty(PhenixCodeCoreService.SolutionSettingsPath))
          {
            var solution = await VS.Solutions.GetCurrentSolutionAsync();
            if (solution != null) PhenixCodeCoreService.InitOnSolutionReady(solution);
          }
        });
        settingsPath = PhenixCodeCoreService.ActiveSettingsPath;
        PhenixCodeCoreService.EnsureConfiguration();
        providersPath = PhenixCodeCoreService.ProvidersPath;
        settings = Read(settingsPath);
        providers = Read(providersPath);
        preferences = PhenixCodeCoreService.LoadPreferences();
        if (string.IsNullOrWhiteSpace((string)preferences["executable_path"]))
          preferences["executable_path"] = (string)settings["_exec"] ?? "";
        originalProviders = (JObject)providers.DeepClone();
        RememberFiles();
        Build();
        savedState = State();
        ready = true;
        Message("Ready. Apply saves all three files; Cancel discards changes since the last Apply. Running services are never restarted automatically.");
      }
      catch (Exception ex)
      {
        BuildRecovery();
        Message("Configuration could not be loaded. Use Recovery to repair files or select a catalog, then reload. " + ex.Message);
      }
      Apply.IsEnabled = Save.IsEnabled = ready;
      UpdateService();
    }

    private void BuildRecovery()
    {
      Pages.Items.Clear(); readers.Clear(); jsonDrafts.Clear();
      var page = Page(Pages, "Recovery");
      Note(page, "Normal saving is disabled until configuration loads. Repair one file at a time below. Nothing is replaced until you explicitly save a repair; the previous file is retained as .bak.");
      Button(page, "Reload configuration", ReloadConfiguration);
      Button(page, "Choose and save provider catalog…", () => Guard(() =>
      {
        CheckScope();
        var dialog = new OpenFileDialog { Filter = "Provider JSON|*.json|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        ValidateProviderFile(Read(dialog.FileName));
        var path = PhenixCodeCoreService.PreferencesPath;
        var previous = File.Exists(path) ? File.ReadAllText(path) : null;
        var candidate = previous == null ? new JObject() : ParseObject(previous);
        candidate["providers_path"] = Path.GetFullPath(dialog.FileName);
        if (!Confirm("Save this provider catalog location and reload configuration?")) return;
        WriteRecoveryFile(path, previous, candidate);
        ReloadConfiguration();
      }));
      var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PhenixCodeCoreService.PreferencesPath, PhenixCodeCoreService.SettingsPath, settingsPath };
      try { paths.Add(PhenixCodeCoreService.ProvidersPath); }
      catch (Exception ex) { Note(page, "Repair VSIX preferences first to resolve the provider catalog path. " + ex.Message); }
      foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p)))
      {
        var section = Page(Pages, Path.GetFileName(path) + " — repair");
        Note(section, path);
        try
        {
          var previous = File.Exists(path) ? File.ReadAllText(path) : null;
          var editor = new TextBox { Text = previous ?? "{}", AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 180, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
          section.Children.Add(editor);
          Button(section, "Load backup into editor", () => Guard(() => { editor.Text = File.ReadAllText(path + ".bak"); Message("Backup loaded into the repair editor. Review it before saving."); }));
          Button(section, "Save repaired JSON and reload", () => Guard(() =>
          {
            var candidate = ParseObject(editor.Text);
            if (!Confirm("Replace " + path + " with this repaired JSON and reload? The previous file will be backed up.")) return;
            WriteRecoveryFile(path, previous, candidate);
            ReloadConfiguration();
          }));
        }
        catch (Exception ex) { Note(section, "Could not read this file. Check its permissions, then reload. " + ex.Message); }
      }
      Pages.SelectedIndex = 0;
    }

    private void WriteRecoveryFile(string path, string expected, JObject candidate)
    {
      CheckScope();
      if ((File.Exists(path) ? File.ReadAllText(path) : null) != expected)
        throw new IOException("File changed outside this dialog. Reload before repairing: " + path);
      ConfigurationStore.WriteJson(path, candidate);
    }

    private static JObject Read(string path) => ConfigurationStore.ReadJson(path);
    private static string Text(JToken value) => value == null ? "" : value.Type == JTokenType.String ? (string)value : value.ToString(Formatting.None);
    private string State() => settings.ToString(Formatting.None) + providers.ToString(Formatting.None) + preferences.ToString(Formatting.None) + providersPath;
    private void AddReader(Action read) => readers.Add(new KeyValuePair<string, Action>(buildingDocument, read));
    private void Stage(Action change) => stagedChanges.Add(change);
    private void Capture(ISet<string> replacedDocuments = null)
    {
      stagedChanges = new List<Action>();
      try
      {
        foreach (var read in readers.ToArray())
          if (replacedDocuments == null || !replacedDocuments.Contains(read.Key)) read.Value();
        foreach (var change in stagedChanges) change();
      }
      finally { stagedChanges = null; }
    }
    private void Message(string text) { Status.Text = text; Diag.OutputMsg("[System] Settings: " + text); }
    private bool Confirm(string text) => System.Windows.MessageBox.Show(this, text, "PhenixCode Settings", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void Guard(Action action) { try { action(); } catch (Exception ex) { Message(ex.Message); } }
    private static JObject Obj(JObject parent, string key)
    {
      if (parent[key] == null) parent[key] = new JObject();
      return parent[key] as JObject ?? throw new InvalidDataException(key + " must be an object. Use JSON to correct it.");
    }
    private static void Defaults(JObject obj, string json)
    {
      foreach (var p in JObject.Parse(json).Properties()) if (obj[p.Name] == null) obj[p.Name] = p.Value;
    }
    private StackPanel Page(TabControl tabs, string title)
    {
      var panel = new StackPanel { Margin = new Thickness(14) };
      tabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
      return panel;
    }
    private static void Note(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
    private static Button Button(Panel panel, string title, Action click)
    {
      var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 8, 8) };
      button.Click += (s, e) => click(); panel.Children.Add(button); return button;
    }
    private static Button Button(Panel panel, string title, Func<Task> click)
    {
      return Button(panel, title, () => { ThreadHelper.JoinableTaskFactory.RunAsync(click).FireAndForget(); });
    }
    private void Build()
    {
      var selectedTab = Pages.SelectedIndex; var selectedModel = ((Pages.Items.OfType<TabItem>().FirstOrDefault(t => (string)t.Header == "Models")?.Content as ScrollViewer)?.Content as StackPanel)?.Children.OfType<TabControl>().FirstOrDefault()?.SelectedIndex ?? 0;
      foreach (var draft in jsonDrafts.Values)
        if (draft.Editor != null) { draft.Caret = draft.Editor.CaretIndex; draft.Scroll = draft.Editor.VerticalOffset; }
      readers.Clear(); Pages.Items.Clear();
      buildingDocument = "settings";
      Scope.Text = (string.IsNullOrEmpty(PhenixCodeCoreService.SolutionSettingsPath) ? "Global project template: " : "Current solution: ") + settingsPath;
      var source = Page(Pages, "Sources");
      Note(source, "Edit individual source entries without rebuilding the list. Adding Visual Studio projects only appends missing directories; custom entries and filters are preserved.");
      Button(source, "Add Visual Studio project directories", async () =>
      {
        try
        {
          Capture();
          var projects = await VS.Solutions.GetAllProjectsAsync();
          var src = Obj(settings, "source");
          var paths = src["paths"] as JArray;
          if (paths == null) { paths = new JArray(); src["paths"] = paths; }
          foreach (var project in projects)
          {
            if (string.IsNullOrEmpty(project.FullPath)) continue;
            var dir = Path.GetDirectoryName(project.FullPath);
            if (!string.IsNullOrEmpty(dir) && !paths.OfType<JObject>().Any(p => EquivalentPath((string)p["path"], dir)))
              paths.Add(new JObject { ["path"] = dir, ["type"] = "directory", ["recursive"] = true, ["exclude"] = new JArray(), ["extensions"] = new JArray() });
          }
          Build(); Message("Project directories appended. Review Sources before applying.");
        }
        catch (Exception ex) { Message("Could not add projects: " + ex.Message); }
      });
      Form(source, Obj(settings, "source"));
      var models = new TabControl(); var modelPage = Page(Pages, "Models"); modelPage.Children.Add(models);
      var embedding = Page(models, "Embedding");
      Note(embedding, "Provider IDs refer to Shared providers. Changing the embedding model, formats, tokenizer or vector dimension requires reindexing existing data.");
      Form(embedding, Obj(settings, "embedding"));
      var generation = Page(models, "Generation");
      Note(generation, "Select enabled provider IDs and the default current_api. Router rules use direct_model_id for each of the three tiers. Provider credentials belong in Shared providers only.");
      var generationConfig = Obj(settings, "generation");
      if (generationConfig["auto_router"] == null)
        Button(generation, "Enable auto-router…", () => Guard(() =>
        {
          Capture();
          var id = (string)generationConfig["current_api"] ?? "";
          generationConfig["auto_router"] = new JObject {
            ["enabled"] = true,
            ["classifier"] = new JObject { ["type"] = "internal", ["api_id"] = id, ["max_tokens"] = 20, ["temperature"] = 0.0, ["timeout_ms"] = 3500,
              ["prompt"] = "Classify the user programming task into exactly one tier: [TIER_1_SIMPLE] for syntax or lookup; [TIER_2_MEDIUM] for multi-file edits or bug fixing; [TIER_3_COMPLEX] for architecture or complex reasoning. Answer ONLY with the tag." },
            ["fallback"] = new JObject { ["default_model_id"] = id },
            ["routing_rules"] = new JObject(new[] { "tier_1_simple", "tier_2_medium", "tier_3_complex" }.Select(t => new JProperty(t, new JObject { ["strategy"] = "direct", ["direct_model_id"] = id })))
          };
          Build();
        }));
      Form(generation, generationConfig);
      buildingDocument = "providers";
      var shared = Page(Pages, "Shared providers");
      Note(shared, "Shared across solutions. Changes can affect unopened solutions. Referenced IDs in this project or FIM preferences cannot be renamed or deleted. Other solutions are never rewritten. Prefer ${ENV_VAR} credentials; literal keys are stored in this shared file.");
      Note(shared, "Loaded file: " + providersPath);
      var path = new TextBox { Text = providersPath, ToolTip = "Choose a path then explicitly load it. The global providers_path preference is saved only on Apply." }; shared.Children.Add(path);
      AddReader(() => { if (!loadingProviderFile && !string.Equals(path.Text, providersPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use Load provider file to adopt the selected path, or restore the loaded path before continuing."); });
      Button(shared, "Browse…", () => { var dialog = new OpenFileDialog { Filter = "JSON files|*.json|All files|*.*" }; if (dialog.ShowDialog(this) == true) path.Text = dialog.FileName; });
      Button(shared, "Load provider file…", () => Guard(() =>
      {
        loadingProviderFile = true;
        try { Capture(); } finally { loadingProviderFile = false; }
        if (!Confirm("Replace the in-dialog shared provider edits with this file? Project and runtime edits will be retained. Nothing is saved yet.")) return;
        var candidatePath = Path.GetFullPath(path.Text); var candidate = Read(candidatePath);
        ValidateProviderFile(candidate);
        providers = candidate; providersPath = candidatePath; preferences["providers_path"] = candidatePath;
        disk[providersPath] = File.ReadAllText(providersPath);
        Build(); Message("Provider file loaded. Apply will save this location globally.");
      }));
      ProviderList(shared, "embedding_providers"); ProviderList(shared, "generation_providers");
      buildingDocument = "settings";
      var indexing = Page(Pages, "Index & storage");
      foreach (var section in new[] { "chunking", "database", "tokenizer", "logging" }) Section(indexing, section, Obj(settings, section));
      Note(indexing, "Relative project paths resolve from solution directory; choose tokenizer from PhenixCode installation. Changing the tokenizer requires reindexing.");
      Button(indexing, "Browse tokenizer file…", () => Guard(() => { Capture(); var dialog = new OpenFileDialog { Filter = "Tokenizer JSON|*.json|All files|*.*" }; if (dialog.ShowDialog(this) == true) { Obj(settings, "tokenizer")["config_path"] = dialog.FileName; Build(); } }));
      buildingDocument = "preferences";
      var runtime = Page(Pages, "Runtime");
      Note(runtime, "Global Visual Studio preferences. Executable compatibility for a solution is handled by the core. Apply does not restart any service. Start saves first; Stop remains available even when the executable path is invalid.");
      Defaults(preferences, "{'executable_path':'','watch':true,'watch_interval':60,'fim_provider':''}");
      Form(runtime, preferences, new HashSet<string> { "providers_path" });
      Button(runtime, "Browse executable…", () => Guard(() => { Capture(); var dialog = new OpenFileDialog { Filter = "Executables|*.exe|All files|*.*" }; if (dialog.ShowDialog(this) == true) { preferences["executable_path"] = dialog.FileName; Build(); } }));
      Note(runtime, "An empty FIM provider uses the default or first compatible project-enabled provider. Providers excluded from this project are never offered for completion.");
      var json = Page(Pages, "JSON");
      Note(json, "Edit either or both documents, then choose Use JSON to update the draft together. Cross-file references are validated on Apply. No files are written until Apply or Save and close.");
      JsonEditor(json, "settings", "Project settings", () => settings);
      JsonEditor(json, "providers", "Shared providers (contains credentials)", () => providers);
      JsonEditor(json, "preferences", "Global preferences", () => preferences);
      Pages.SelectedIndex = selectedTab < 0 ? 0 : selectedTab; models.SelectedIndex = selectedModel;
    }
    private static JObject ParseObject(string text)
    {
      using (var input = new StringReader(text))
      using (var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None })
      {
        var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        while (reader.Read()) if (reader.TokenType != JsonToken.Comment) throw new JsonException("Unexpected trailing JSON.");
        return result;
      }
    }
    private bool EquivalentPath(string entry, string directory)
    {
      try { return string.Equals(Path.GetFullPath(Path.IsPathRooted(entry ?? "") ? entry : Path.Combine(PhenixCodeCoreService.SolutionDir ?? Path.GetDirectoryName(settingsPath), (entry ?? "").Replace("$(SolutionDir)", PhenixCodeCoreService.SolutionDir ?? ""))).TrimEnd('\\', '/'), Path.GetFullPath(directory).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
      catch { return false; }
    }
    private void Section(Panel panel, string title, JObject obj)
    {
      var body = new StackPanel(); panel.Children.Add(new GroupBox { Header = title.Replace('_', ' '), Content = body }); Form(body, obj);
    }
    // Editors patch only the property they own; untouched and extension properties are retained.
    private void Form(Panel panel, JObject obj, HashSet<string> skip = null)
    {
      foreach (var property in obj.Properties().ToArray())
      {
        if (property.Name.StartsWith("_", StringComparison.Ordinal) || (skip != null && skip.Contains(property.Name))) continue;
        if (property.Value is JObject nested) { Section(panel, property.Name, nested); continue; }
        if (property.Value is JArray array && property.Name == "paths" && array.All(t => t is JObject))
        { ObjectList(panel, property.Name, array); continue; }
        Field(panel, property);
      }
      KnownFields(panel, obj);
    }
    private void Field(Panel panel, JProperty property)
    {
      var label = Label(property);
      var value = property.Value;
      var options = Choices(property);
      if (options != null)
      {
        NoteLabel(panel, label);
        var combo = new ComboBox { Margin = new Thickness(0, 3, 0, 10), MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Stretch };
        Action refresh = () =>
        {
          var selected = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? Text(value);
          combo.Items.Clear();
          var available = Choices(property).ToArray();
          foreach (var option in available.Concat(new[] { selected }).Distinct())
          {
            var item = new ComboBoxItem { Tag = option, Content = option == "" ? (property.Name == "api_style" ? "Auto (detect from URL)" : property.Name == "fim_provider" ? "Automatic (project-enabled FIM provider)" : "(not configured)") : available.Contains(option) ? option : option + " (unavailable; choose another)" };
            combo.Items.Add(item);
            if (option == selected) combo.SelectedItem = item;
          }
        };
        refresh();
        panel.Children.Add(combo);
        var owner = (JObject)property.Parent;
        AddReader(() => { var selected = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? ""; Stage(() => { if (property.Name == "api_style" && selected.Length == 0) owner.Remove(property.Name); else owner[property.Name] = selected; }); });
        combo.DropDownOpened += (s, e) => Guard(() => { Capture(); refresh(); });
        return;
      }
      if (property.Name == "enabled_providers" && property.Parent.Path == "generation" && value is JArray enabled)
      {
        NoteLabel(panel, "Enabled generation providers (this project)");
        var choices = new List<KeyValuePair<string, CheckBox>>();
        foreach (var id in ProviderIds("generation_providers").Concat(enabled.Values<string>()).Distinct())
        {
          var check = new CheckBox { Content = id, IsChecked = enabled.Values<string>().Contains(id), Margin = new Thickness(0, 4, 0, 4) };
          panel.Children.Add(check); choices.Add(new KeyValuePair<string, CheckBox>(id, check));
        }
        AddReader(() => { var next = new JArray(choices.Where(c => c.Value.IsChecked == true).Select(c => c.Key)); Stage(() => property.Value = next); }); return;
      }
      if (value is JArray lines && new[] { "default_extensions", "global_exclude", "extensions", "exclude" }.Contains(property.Name) && lines.All(t => t.Type == JTokenType.String))
      {
        NoteLabel(panel, label + " (one per line)");
        var lineBox = new TextBox { Text = string.Join(Environment.NewLine, lines.Values<string>()), AcceptsReturn = true, MinHeight = 65, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(lineBox);
        var initial = lineBox.Text;
        AddReader(() => { if (lineBox.Text != initial || !JToken.DeepEquals(property.Value, lines)) { var next = new JArray(lineBox.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Where(line => line.Length > 0)); Stage(() => property.Value = next); } }); return;
      }
      if (value.Type == JTokenType.Boolean)
      {
        var check = new CheckBox { Content = label, IsChecked = (bool)value, Margin = new Thickness(0, 5, 0, 10) }; panel.Children.Add(check);
        AddReader(() => { var next = check.IsChecked == true; Stage(() => property.Value = next); }); return;
      }
      NoteLabel(panel, label + (property.Name == "stop_tokens" ? " — JSON array (e.g. [\"\\n\"])" : value is JArray ? " (JSON array)" : ""));
      if (property.Name == "api_key")
      {
        var password = new PasswordBox { Password = Text(value), Margin = new Thickness(0, 3, 0, 8), Padding = new Thickness(5), ToolTip = "API key or ${ENV_VAR}. Stored only in the shared provider file." };
        panel.Children.Add(password);
        var reveal = new TextBox { Visibility = Visibility.Collapsed, ToolTip = "Visible credential — use an environment reference where possible." }; panel.Children.Add(reveal);
        var show = new CheckBox { Content = "Show key / environment reference", Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(show);
        show.Checked += (s, e) => { reveal.Text = password.Password; password.Visibility = Visibility.Collapsed; reveal.Visibility = Visibility.Visible; };
        show.Unchecked += (s, e) => { password.Password = reveal.Text; reveal.Visibility = Visibility.Collapsed; password.Visibility = Visibility.Visible; };
        AddReader(() => { var next = show.IsChecked == true ? reveal.Text : password.Password; Stage(() => property.Value = next); }); return;
      }
      var box = new TextBox { Text = value is JArray ? value.ToString(Formatting.Indented) : Text(value), MinWidth = 100, ToolTip = property.Path };
      if (value is JArray || box.Text.Contains("\n") || property.Name == "prompt") { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.MinHeight = 65; box.MaxHeight = 160; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
      panel.Children.Add(box);
      AddReader(() =>
      {
        try
        {
          if (box.Text == (property.Value is JArray ? property.Value.ToString(Formatting.Indented) : Text(property.Value))) return;
          JToken next;
          if (value is JArray) next = JArray.Parse(box.Text);
          else if (value.Type == JTokenType.Integer) next = new JValue(long.Parse(box.Text, CultureInfo.InvariantCulture));
          else if (value.Type == JTokenType.Float) { var number = double.Parse(box.Text, CultureInfo.InvariantCulture); if (double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException(); next = new JValue(number); }
          else if (value.Type == JTokenType.Null) next = JToken.Parse(box.Text);
          else next = new JValue(box.Text);
          Stage(() => property.Value = next);
        }
        catch (Exception ex) { box.Focus(); throw new InvalidDataException(property.Path + ": invalid value. " + ex.Message); }
      });
    }
    private string Label(JProperty p)
    {
      if (p.Name == "current_api") return p.Parent.Path == "embedding" ? "Embedding provider" : "Default generation provider";
      if (p.Name == "api_id") return "Classifier provider";
      if (p.Name == "direct_model_id") return "Tier provider";
      if (p.Name == "default_model_id") return "Fallback provider";
      if (p.Name == "fim_provider") return "Completion (FIM) provider";
      if (p.Name == "api_style") return "API style";
      return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(p.Name.Replace('_', ' '));
    }
    private IEnumerable<string> ProviderIds(string key) => (providers[key] as JArray ?? new JArray()).OfType<JObject>().Where(p => (bool?)p["enabled"] != false).Select(p => (string)p["id"]).Where(id => !string.IsNullOrEmpty(id));
    private IEnumerable<string> ProjectGenerationIds() => ProviderIds("generation_providers").Where(id => (settings["generation"]?["enabled_providers"] as JArray ?? new JArray()).Values<string>().Contains(id));
    private IEnumerable<string> Choices(JProperty p)
    {
      if (p.Name == "current_api") return p.Parent.Path == "embedding" ? ProviderIds("embedding_providers") : ProjectGenerationIds();
      if (new[] { "api_id", "direct_model_id", "default_model_id" }.Contains(p.Name)) return ProjectGenerationIds();
      if (p.Name == "fim_provider") return new[] { "" }.Concat((providers["generation_providers"] as JArray ?? new JArray()).OfType<JObject>().Where(o => o["fim"] is JObject && (bool?)o["enabled"] != false && (settings["generation"]?["enabled_providers"] as JArray ?? new JArray()).Values<string>().Contains((string)o["id"])).Select(o => (string)o["id"]));
      if (p.Name == "api_style") return new[] { "", "openai", "anthropic" };
      if (p.Name == "type" && p.Parent.Path.Contains("classifier")) return new[] { "internal", "api" };
      if (p.Name == "type" && p.Parent.Path.StartsWith("source.paths[", StringComparison.Ordinal)) return new[] { "directory", "file", "url" };
      if (p.Name == "strategy") return new[] { "direct" };
      if (p.Name == "distance_metric") return new[] { "cosine", "l2" };
      return null;
    }
    private void OptionalProviderFields(Panel panel, JObject provider, string kind)
    {
      var defaults = JObject.Parse("{'api_style':'','enabled':true,'stream':true,'temperature_support':false,'temperature':0.0,'max_tokens':0,'name':'','api_url':'','api_key':'','model':''}");
      if (kind == "embedding_providers") Defaults(defaults, "{'document_format':'','query_format':''}");
      else Defaults(defaults, "{'context_length':32000,'max_tokens_name':'max_tokens','pricing_tpm':{'input':0.0,'output':0.0,'cached_input':0.0}}");
      if (kind == "generation_providers")
      {
        defaults["context_length"] = providers["default_context_length"]?.DeepClone() ?? new JValue(32000);
        defaults["max_tokens_name"] = providers["default_max_tokens_name"]?.DeepClone() ?? new JValue("max_tokens");
      }
      defaults["api_url"] = provider["apiUrl"]?.DeepClone() ?? defaults["api_url"];
      defaults["api_key"] = provider["apiKey"]?.DeepClone() ?? defaults["api_key"];
      foreach (var p in defaults.Properties().ToArray()) if (provider[p.Name] != null) p.Remove();
      var initial = (JObject)defaults.DeepClone();
      var inherited = new StackPanel(); panel.Children.Add(new Expander { Header = "Inherited / unset provider fields (editing creates an override)", Content = inherited });
      Note(inherited, "Values below reflect core/catalog defaults. Zero maximum tokens uses the project generation limit. API style Auto is detected from the URL.");
      Form(inherited, defaults);
      AddReader(() => Stage(() => { foreach (var p in initial.Properties()) { var next = defaults[p.Name]; if (next != null && !JToken.DeepEquals(next, p.Value)) provider[p.Name] = next.DeepClone(); else provider.Remove(p.Name); } }));
    }
    private void KnownFields(Panel panel, JObject obj)
    {
      string schema;
      switch (obj.Path)
      {
        case "source": schema = "{'project_id':'','project_title':'','project_description':'','default_extensions':['.txt','.md'],'global_exclude':[],'encoding':'utf-8','max_file_size_mb':10}"; break;
        case "embedding": schema = "{'current_api':'','batch_size':4,'timeout_ms':10000,'retry_attempts':3,'top_k':5,'prepend_label_format':''}"; break;
        case "generation": schema = "{'current_api':'','enabled_providers':[],'timeout_ms':20000,'max_chunks':5,'max_full_sources':2,'max_related_per_source':3,'default_temperature':0.5,'default_max_tokens':2048,'prepend_label_format':'','excerpt':{'enabled':true,'min_chunks':3,'max_chunks':9,'threshold_ratio':0.6}}"; break;
        case "chunking": schema = "{'semantic':false,'nof_min_tokens':50,'nof_max_tokens':500,'overlap_percentage':0.1}"; break;
        case "database": schema = "{'sqlite_path':'db.sqlite','index_path':'index','vector_dim':768,'max_elements':100000,'distance_metric':'cosine'}"; break;
        case "tokenizer": schema = "{'config_path':'tokenizer.json'}"; break;
        case "logging": schema = "{'log_to_console':true,'log_to_file':true,'logging_file':'output.log','diagnostics_file':'diagnostics.log'}"; break;
        default:
          if (obj.Path.StartsWith("source.paths[", StringComparison.Ordinal)) schema = "{'path':'','recursive':true,'extensions':[],'exclude':[]}";
          else if (obj.Path.EndsWith(".fim", StringComparison.Ordinal)) schema = "{'api_url':'','prefix_name':'','suffix_name':'','format':'','file_divider_token':'','stop_tokens':[]}";
          else return;
          break;
      }
      var optional = JObject.Parse(schema);
      if (obj.Path.EndsWith(".fim", StringComparison.Ordinal)) optional["api_url"] = (obj.Parent?.Parent as JObject)?["api_url"]?.DeepClone() ?? new JValue("");
      foreach (var p in optional.Properties().ToArray()) if (obj[p.Name] != null) p.Remove();
      if (!optional.HasValues) return;
      // Preserve a real path for provider selectors without materializing optional defaults.
      var wrapper = new JObject { [obj.Path] = optional };
      var initial = (JObject)optional.DeepClone();
      var inherited = new StackPanel(); panel.Children.Add(new Expander { Header = "Inherited / unset fields (editing creates an override)", Content = inherited });
      foreach (var p in optional.Properties())
      {
        if (p.Value is JObject nested) Section(inherited, p.Name, nested);
        else Field(inherited, p);
      }
      AddReader(() => Stage(() => { foreach (var p in optional.Properties()) { if (!JToken.DeepEquals(p.Value, initial[p.Name])) obj[p.Name] = p.Value.DeepClone(); else obj.Remove(p.Name); } }));
    }
    private static void NoteLabel(Panel panel, string label) => panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 0), FontWeight = FontWeights.SemiBold });
    private void ObjectList(Panel panel, string title, JArray array)
    {
      NoteLabel(panel, title.Replace('_', ' '));
      foreach (var token in array.ToArray())
      {
        var body = new StackPanel(); panel.Children.Add(new Expander { Header = Text(token["id"] ?? token["path"] ?? token["url"]), Content = body, IsExpanded = title == "paths", Margin = new Thickness(0, 6, 0, 10), Padding = new Thickness(8) });
        if (token is JObject obj)
        {
          Form(body, obj);
          if (title == "paths" && obj["url"] == null)
          {
            var extra = new JObject { ["url"] = "" };
            Field(body, extra.Property("url"));
            AddReader(() => Stage(() => { if (!string.IsNullOrEmpty((string)extra["url"])) obj["url"] = extra["url"].DeepClone(); else obj.Remove("url"); }));
          }
          if (title.EndsWith("_providers", StringComparison.Ordinal))
          {
            OptionalProviderFields(body, obj, title);
            if (title == "generation_providers" && obj["fim"] == null)
              Button(body, "Add FIM support", () => Guard(() => { Capture(); obj["fim"] = JObject.Parse("{'api_url':'','prefix_name':'prompt','suffix_name':'suffix','stop_tokens':[]}"); Build(); }));
            if (obj["fim"] != null)
              Button(body, "Remove FIM support", () => Guard(() => { Capture(); if ((string)preferences["fim_provider"] == (string)obj["id"]) throw new InvalidOperationException("Choose another FIM provider in Runtime first."); if (Confirm("Remove FIM support from this shared provider?")) { obj.Remove("fim"); Build(); } }));
          }
        }
        var actions = new WrapPanel(); body.Children.Add(actions);
        Button(actions, "Remove", () => Guard(() => { Capture(); CheckRemoval(token); if (!Confirm("Remove this entry? Shared provider removal may affect other solutions.")) return; token.Remove(); Build(); }));
        Button(actions, "Move up", () => Guard(() => { Capture(); var i = array.IndexOf(token); if (i > 0) { token.Remove(); array.Insert(i - 1, token); Build(); } }));
        Button(actions, "Move down", () => Guard(() => { Capture(); var i = array.IndexOf(token); if (i < array.Count - 1) { token.Remove(); array.Insert(i + 1, token); Build(); } }));
      }
      Button(panel, "Add " + title.Replace('_', ' '), () => Guard(() =>
      {
        Capture();
        JObject entry;
        if (title == "paths") entry = JObject.Parse("{ 'path':'', 'type':'directory', 'recursive':true, 'extensions':[], 'exclude':[] }");
        else
        {
          entry = JObject.Parse("{ 'id':'new-provider', 'name':'New provider', 'api_url':'', 'api_key':'', 'model':'', 'api_style':'openai', 'enabled':true, 'stream':true, 'temperature_support':true }");
          var id = "new-provider"; var suffix = 1;
          while (array.OfType<JObject>().Any(p => (string)p["id"] == id)) id = "new-provider-" + suffix++;
          entry["id"] = id;
          if (title == "embedding_providers") Defaults(entry, "{'document_format':'{}','query_format':'{}'}");
          else Defaults(entry, "{'context_length':64000,'max_tokens_name':'max_tokens','pricing_tpm':{'input':0.0,'output':0.0,'cached_input':0.0}}");
        }
        array.Add(entry); Build();
      }));
    }
    private void ProviderList(Panel panel, string key)
    {
      var array = providers[key] as JArray;
      if (array == null) throw new InvalidDataException(key + " must be an array in the provider file.");

      ObjectList(panel, key, array);
    }
    private void JsonEditor(Panel panel, string key, string title, Func<JObject> get)
    {
      var model = get().ToString(Formatting.None);
      if (!jsonDrafts.TryGetValue(key, out var draft))
      {
        draft = new JsonDraft { Text = get().ToString(Formatting.Indented), Model = model };
        draft.Baseline = draft.Text;
        jsonDrafts.Add(key, draft);
      }
      if (!draft.Dirty && draft.Model != model)
      {
        draft.Text = draft.Baseline = get().ToString(Formatting.Indented); draft.Model = model;
      }
      var area = new StackPanel(); panel.Children.Add(new GroupBox { Header = title, Content = area });
      var box = new TextBox { Text = draft.Text, AcceptsReturn = true, AcceptsTab = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Height = 210, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
      area.Children.Add(box); draft.Editor = box;
      var feedback = new TextBlock { Text = draft.Error ?? "Draft editor. Use JSON updates the draft; Apply writes files.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8) };
      area.Children.Add(feedback); draft.Feedback = feedback;
      box.TextChanged += (_, _) => { draft.Text = box.Text; feedback.Text = "Edited JSON pending. Use JSON adopts all edited documents together."; };
      box.Loaded += (_, _) => { box.CaretIndex = Math.Min(draft.Caret, box.Text.Length); box.ScrollToVerticalOffset(draft.Scroll); };
      Button(area, "Refresh from forms", () => Guard(() =>
      {
        if (draft.Dirty && !Confirm("Discard edits in this JSON buffer and refresh it from the forms?")) return;
        Capture(); draft.Text = draft.Baseline = get().ToString(Formatting.Indented); draft.Model = get().ToString(Formatting.None);
        box.Text = draft.Text; feedback.Text = "Refreshed from forms. No files were saved.";
      }));
      Button(area, "Use JSON", () => AdoptJsonDrafts());
    }
    private bool AdoptJsonDrafts()
    {
      var candidates = new Dictionary<string, JObject>();
      try
      {
        foreach (var pair in jsonDrafts.Where(p => p.Value.Dirty))
        {
          try { var candidate = ParseObject(pair.Value.Text); ValidateShape(pair.Key, candidate); candidates.Add(pair.Key, candidate); pair.Value.Error = null; }
          catch (Exception ex)
          {
            pair.Value.Error = ex.Message; pair.Value.Feedback.Text = "JSON rejected: " + ex.Message;
            pair.Value.Editor.Focus(); throw;
          }
        }
        if (candidates.Count == 0) { Message("No pending JSON edits. Apply saves the current draft."); return true; }
        // Replaced documents are not read from stale forms, so JSON can repair invalid form values.
        Capture(new HashSet<string>(candidates.Keys));
        if (candidates.TryGetValue("settings", out var project)) settings = project;
        if (candidates.TryGetValue("providers", out var catalog)) providers = catalog;
        if (candidates.TryGetValue("preferences", out var runtime)) preferences = runtime;
        foreach (var key in candidates.Keys)
        {
          var draft = jsonDrafts[key]; draft.Baseline = draft.Text; draft.Model = candidates[key].ToString(Formatting.None);
          draft.Error = "Draft updated. Apply to validate references and save files.";
        }
        Build();
        try { ConfigurationStore.Validate(settings, providers); Message("JSON draft updated together. Apply to save files."); }
        catch (Exception ex)
        {
          foreach (var key in candidates.Keys) jsonDrafts[key].Feedback.Text = "Draft updated; resolve before saving: " + ex.Message;
          Message("JSON draft updated. Reference/validation issue: " + ex.Message);
        }
        return true;
      }
      catch (Exception ex) { Message("JSON was not adopted: " + ex.Message); return false; }
    }
    private void ValidateShape(string key, JObject candidate)
    {
      if (key == "settings")
      {
        RejectEmbeddedProviders(candidate);
        foreach (var section in new[] { "source", "embedding", "generation", "chunking", "database" })
          if (!(candidate[section] is JObject)) throw new InvalidDataException(section + " must be an object.");
        foreach (var section in new[] { "tokenizer", "logging" })
          if (candidate[section] != null && !(candidate[section] is JObject)) throw new InvalidDataException(section + " must be an object.");
        if (candidate["generation"]?["enabled_providers"] != null && (!(candidate["generation"]["enabled_providers"] is JArray allowed) || allowed.Any(t => t.Type != JTokenType.String)))
          throw new InvalidDataException("generation.enabled_providers must be an array of provider ID strings.");
      }
      else if (key == "providers") ValidateProviderFile(candidate);
      else if (candidate["providers_path"] != null && !string.Equals((string)candidate["providers_path"], providersPath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Choose the provider file through Shared providers, Load provider file.");
    }
    private static void ValidateProviderFile(JObject candidate)
    {
      foreach (var key in new[] { "embedding_providers", "generation_providers" })
      {
        if (!(candidate[key] is JArray list) || list.Any(t => !(t is JObject))) throw new InvalidDataException(key + " must be an array of provider objects.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JObject provider in list)
        {
          if (provider["id"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)provider["id"]) || !ids.Add((string)provider["id"]))
            throw new InvalidDataException(key + ": provider IDs must be nonempty and unique.");
          if (provider["enabled"] != null && provider["enabled"].Type != JTokenType.Boolean)
            throw new InvalidDataException(key + ": enabled must be true or false.");
        }
      }
    }
    private bool Referenced(string id, string kind)
    {
      if (string.IsNullOrEmpty(id)) return false;
      if (kind == "embedding_providers") return (string)settings["embedding"]?["current_api"] == id;
      var generation = settings["generation"] as JObject;
      if ((string)generation?["current_api"] == id || (generation?["enabled_providers"] as JArray)?.Values<string>().Contains(id) == true || (string)preferences["fim_provider"] == id) return true;
      if ((string)settings["_fim_provider"] == id || (string)generation?["_fim_provider"] == id) return true;
      var router = generation?["auto_router"] as JObject;
      if ((string)router?["classifier"]?["api_id"] == id || (string)router?["fallback"]?["default_model_id"] == id) return true;
      return (router?["routing_rules"] as JObject)?.Properties().Any(p => (string)p.Value["direct_model_id"] == id || (string)p.Value["synthesizer_model_id"] == id || (p.Value["draft_model_ids"] as JArray)?.Values<string>().Contains(id) == true) == true;
    }
    private void CheckRemoval(JToken token)
    {
      var id = (string)token["id"];
      if (Referenced(id, ((token.Parent as JArray)?.Parent as JProperty)?.Name)) throw new InvalidOperationException("Provider '" + id + "' is referenced in this project or FIM preferences. Select another provider first. Shared changes may also affect unopened solutions, which are never rewritten.");
    }
    private void CheckReferences()
    {
      RejectEmbeddedProviders(settings);
      foreach (var key in new[] { "embedding_providers", "generation_providers" })
      {
        var ids = new HashSet<string>((providers[key] as JArray ?? new JArray()).OfType<JObject>().Select(p => (string)p["id"]));
        foreach (var old in (originalProviders[key] as JArray ?? new JArray()).OfType<JObject>())
          if (!ids.Contains((string)old["id"])) CheckRemoval(old);
      }
      if (settings.Descendants().OfType<JProperty>().Any(p => p.Name == "api_key" && !string.IsNullOrEmpty(Text(p.Value))))
        throw new InvalidDataException("Provider keys belong in the shared providers file. Remove api_key from project settings using JSON.");
    }
    private static void RejectEmbeddedProviders(JObject project)
    {
      if ((project["embedding"] as JObject)?["apis"] != null || (project["generation"] as JObject)?["apis"] != null)
        throw new InvalidDataException("Embedded embedding.apis / generation.apis are legacy configuration. Keep provider records and credentials in Shared providers and select their IDs in Models.");
    }
    private void RememberFiles()
    {
      disk.Clear(); foreach (var path in new[] { settingsPath, providersPath, PhenixCodeCoreService.PreferencesPath }) disk[path] = File.Exists(path) ? File.ReadAllText(path) : null;
    }
    private bool Persist()
    {
      if (!ready || busy) return false;
      try
      {
        CheckScope(); if (jsonDrafts.Values.Any(d => d.Dirty) && !AdoptJsonDrafts()) return false; Capture(); CheckReferences();
        ConfigurationStore.Validate(settings, providers);
        preferences["providers_path"] = providersPath;
        foreach (var pair in disk)
          if ((File.Exists(pair.Key) ? File.ReadAllText(pair.Key) : null) != pair.Value)
            throw new IOException("File changed outside this dialog: " + pair.Key + ". Reopen the dialog to load the latest version; no files were saved.");
        PhenixCodeCoreService.SaveConfiguration(settings, providers, providersPath, preferences, disk);
        originalProviders = (JObject)providers.DeepClone(); RememberFiles(); Build(); savedState = State();
        Message("Saved project settings, shared providers and global preferences. Changes take effect on the next service start; no service was restarted."); return true;
      }
      catch (Exception ex) { Message("Save failed: " + ex.Message); return false; }
    }
    private void OnApply(object sender, RoutedEventArgs e) => Persist();
    private void OnSave(object sender, RoutedEventArgs e) { if (Persist()) { closingSaved = true; DialogResult = true; } }
    private void OnClosing(object sender, CancelEventArgs e)
    {
      if (busy) { Message("Wait for the service operation to finish before closing."); e.Cancel = true; return; }
      if (!ready || closingSaved) return;
      try { Capture(); if ((jsonDrafts.Values.Any(d => d.Dirty) || State() != savedState) && !Confirm("Discard changes since the last Apply?")) e.Cancel = true; }
      catch { if (!Confirm("Discard the unfinished or invalid edits?")) e.Cancel = true; }
    }
    private void UpdateService()
    {
      Service.IsEnabled = ready && !busy && !string.IsNullOrEmpty(PhenixCodeCoreService.SolutionSettingsPath);
      Service.Content = PhenixCodeCoreService.IsServiceStarting ? "Stop starting service" : PhenixCodeCoreService.IsServiceRunning ? "Stop service" : "Start service";
    }
    private async Task<bool> ReportStartupAsync()
    {
      CheckScope();
      var operation = serviceOperation;
      if (PhenixCodeCoreService.IsServiceRunning)
      {
        // Process identity is the same running-state check used when reopening this dialog.
        // An unavailable health endpoint must not turn a successful launch into a failure.
        bool healthy = false;
        try { healthy = await PhenixCodeCoreService.IsServiceHealthyAsync(); }
        catch (Exception ex) { Diag.OutputMsg("Service health check could not be completed: " + ex.Message); }
        CheckScope();
        if (operation != serviceOperation) return true;
        // The service can exit while the HTTP probe is in flight.
        if (!PhenixCodeCoreService.IsServiceRunning && !PhenixCodeCoreService.IsServiceStarting) return false;
        monitorStartup = !healthy;
        var text = healthy ? "Service started and health check passed." : "Service started. The process is running; HTTP health is not yet confirmed.";
        if (Status.Text != text) Message(text);
        return true;
      }
      if (PhenixCodeCoreService.IsServiceStarting)
      {
        monitorStartup = true;
        var text = "Service is still starting. Status will refresh automatically; you can stop the pending startup.";
        if (Status.Text != text) Message(text);
        return true;
      }
      return false;
    }
    private async Task RefreshServiceAsync()
    {
      if (!ready || busy || refreshingService) return;
      refreshingService = true;
      try
      {
        CheckScope();
        if (monitorStartup && !await ReportStartupAsync())
        {
          monitorStartup = false;
          Message("The service process exited during startup. Check the PhenixCode output pane for details.");
        }
        UpdateService();
      }
      catch (Exception ex) { monitorStartup = false; Diag.OutputMsg("Service status refresh: " + ex.Message); }
      finally { refreshingService = false; }
    }
    private void OnService(object sender, RoutedEventArgs e) => ThreadHelper.JoinableTaskFactory.RunAsync(OnServiceAsync).FireAndForget();
    private void CheckScope()
    {
      if (!string.Equals(settingsPath, PhenixCodeCoreService.ActiveSettingsPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The active solution changed. Reopen Settings for the current solution; nothing was saved or started.");
    }
    private async Task OnServiceAsync()
    {
      if (busy) return;
      var stopping = PhenixCodeCoreService.IsServiceRunning || PhenixCodeCoreService.IsServiceStarting;
      if (!stopping && !Persist()) return;
      monitorStartup = false;
      serviceOperation++;
      busy = true; Pages.IsEnabled = Apply.IsEnabled = Save.IsEnabled = Service.IsEnabled = false;
      try
      {
        CheckScope();
        if (stopping)
        {
          Message("Requesting service shutdown…");
          var shutdown = PhenixCodeCoreService.ShutdownServiceAsync();
          if (await Task.WhenAny(shutdown, Task.Delay(12000)) != shutdown)
          { _ = shutdown.ContinueWith(t => { var ignored = t.Exception; }, System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default); throw new TimeoutException("Shutdown request timed out. The service may still be running."); }
          await shutdown;
        }
        else
        {
          var executable = (string)preferences["executable_path"];
          var pathError = ServicePathValidation.Check(executable, (string)settings["tokenizer"]?["config_path"] ?? "tokenizer.json", PhenixCodeCoreService.SolutionDir);
          if (pathError != null) { Message("Cannot start service: " + pathError); return; }
          Message("Starting service; waiting for running status…");
          var pane = await PhenixCodeCoreService.GetPaneAsync();
          CheckScope();
          var process = await Task.Run(() => PhenixCodeCoreService.StartProcess(executable, pane));
          if (process == null) throw new InvalidOperationException("The core did not start a process.");
        }
        var timer = Stopwatch.StartNew();
        bool complete = false;
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
          CheckScope();
          complete = stopping ? !PhenixCodeCoreService.IsServiceRunning && !PhenixCodeCoreService.IsServiceStarting : PhenixCodeCoreService.IsServiceRunning;
          CheckScope();
          if (complete) break;
          await Task.Delay(250);
        }
        if (stopping)
        {
          if (!complete) throw new TimeoutException("Service is still running after the stop timeout.");
          Message("Service stopped.");
        }
        else if (!await ReportStartupAsync())
          throw new InvalidOperationException("The service process exited before startup completed. Check the PhenixCode output pane for details.");
      }
      catch (Exception ex) { Message("Service operation failed: " + ex.Message); }
      finally { busy = false; Pages.IsEnabled = Apply.IsEnabled = Save.IsEnabled = ready; UpdateService(); }
    }
  }
}
