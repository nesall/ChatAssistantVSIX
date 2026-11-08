using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatAssistantVSIX.Utils
{
  public static class PhenixCodeCoreService
  {
    /// <summary>
    /// SettingsPath stores universal settings only (i.e. no solution/project specific info like paths)
    /// </summary>
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhenixCode", "settings.json");
    public static string PreferencesPath => Path.Combine(Path.GetDirectoryName(SettingsPath), "vsix.json");
    public static string ActiveSettingsPath => SolutionSettingsPath ?? SettingsPath;
    public static string ProvidersPath
    {
      get
      {
        var configured = LoadPreferences()["providers_path"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(configured))
          configured = Environment.GetEnvironmentVariable("PHENIXCODE_PROVIDERS_CONFIG");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
          ? Path.Combine(Path.GetDirectoryName(SettingsPath), "providers.json") : configured);
      }
    }
    private static readonly object ConfigurationLock = new object();
    private static readonly Dictionary<string, string> OwnedServiceKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Process> OwnedProcesses = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
    private static Task pendingShutdown = Task.CompletedTask;
    public static string SolutionSettingsPath { get; private set; }
    public static string SolutionDir { get; private set; }
    private static string InfoFilePath { get; set; }
    private static Guid OutputPaneGuid { get; set; }

    public class Attachment
    {
      [JsonProperty("filename")]
      public string Filename { get; set; }
      [JsonProperty("content")]
      public string Content { get; set; }
    }

    public static void InitOnSolutionReady(Solution solution)
    {
      if (string.IsNullOrWhiteSpace(solution?.FullPath)) return;
      try
      {
        lock (ConfigurationLock)
        {
          Diag.OutputMsg($"InitOnSolutionReady solution.Name {solution.Name}, solution.FullPath {solution.FullPath}");
          string solutionDir = Path.GetDirectoryName(solution.FullPath);
          if (string.IsNullOrWhiteSpace(solutionDir)) return;
          string workDir = Path.Combine(solutionDir, ".phenixcode");
          SolutionSettingsPath = Path.Combine(workDir, "settings.json");
          InfoFilePath = Path.Combine(workDir, "info.json");
          SolutionDir = solutionDir.Replace('\\', '/');
          if (!Directory.Exists(workDir))
          {
            Directory.CreateDirectory(workDir);
          }
          EnsureConfiguration();
        }
      }
      catch (Exception ex) { Diag.OutputMsg($"[ERR] Could not initialize PhenixCode for this solution. Check folder permissions and configuration in Settings. {ex.Message}"); }
    }

    public static async Task InitAsync()
    {
      Debug.WriteLine("Creating Output Window Pane 'PhenixCode Service'");
      var pane = await VS.Windows.CreateOutputWindowPaneAsync("PhenixCode Service", false);
      OutputPaneGuid = pane.Guid;
      try { EnsureConfiguration(); }
      catch (Exception ex) { Diag.OutputMsg($"Configuration initialization failed: {ex.Message}"); }
    }

    public static JObject LoadPreferences() => File.Exists(PreferencesPath)
      ? ConfigurationStore.ReadJson(PreferencesPath) : new JObject();

    private static JObject ReadDefault(string name)
    {
      var assembly = typeof(PhenixCodeCoreService).Assembly;
      var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Resources." + name, StringComparison.Ordinal));
      using var stream = assembly.GetManifestResourceStream(resource);
      using var reader = new StreamReader(stream, Encoding.UTF8);
      return ConfigurationStore.ParseJson(reader.ReadToEnd());
    }

    public static void EnsureConfiguration()
    {
      lock (ConfigurationLock)
      {
        using var lease = new ConfigurationLease();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
        if (!File.Exists(SettingsPath)) ConfigurationStore.WriteJson(SettingsPath, ReadDefault("settings.default.json"));
        var providersPath = ProvidersPath;
        if (new[] { SettingsPath, ActiveSettingsPath, PreferencesPath }.Any(p => string.Equals(Path.GetFullPath(p), providersPath, StringComparison.OrdinalIgnoreCase)))
          throw new InvalidOperationException("The shared provider catalog must be a different file from project and VSIX settings.");
        var explicitPath = LoadPreferences()["providers_path"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(explicitPath)) explicitPath = Environment.GetEnvironmentVariable("PHENIXCODE_PROVIDERS_CONFIG");
        if (!File.Exists(providersPath) && !string.IsNullOrWhiteSpace(explicitPath))
          throw new FileNotFoundException("The configured providers file does not exist.", providersPath);
        var providers = File.Exists(providersPath) ? ConfigurationStore.ReadJson(providersPath) : ReadDefault("providers.default.json");
        if (!(providers["embedding_providers"] is JArray) || !(providers["generation_providers"] is JArray))
          throw new InvalidDataException("Providers must contain embedding_providers and generation_providers arrays: " + providersPath);
        var global = ConfigurationStore.ReadJson(SettingsPath);
        bool globalChanged = ConfigurationStore.Migrate(global, providers, SettingsPath);
        if (globalChanged) PreserveLegacy(SettingsPath);
        // Catalog must be persisted first so interruption never removes the only copy of legacy providers.
        if (!File.Exists(providersPath) || globalChanged) ConfigurationStore.WriteJson(providersPath, providers);
        if (globalChanged) ConfigurationStore.WriteJson(SettingsPath, global);
        if (SolutionSettingsPath != null)
        {
          if (!File.Exists(SolutionSettingsPath)) SaveToSolutionSettings(global.ToString());
          var project = ConfigurationStore.ReadJson(SolutionSettingsPath);
          if (ConfigurationStore.Migrate(project, providers, SolutionSettingsPath))
          {
            PreserveLegacy(SolutionSettingsPath);
            ConfigurationStore.WriteJson(providersPath, providers);
            ConfigurationStore.WriteJson(SolutionSettingsPath, project);
            Diag.OutputMsg("Migrated solution configuration to shared providers; previous files are retained as .bak files.");
          }
        }
      }
    }

    private static void PreserveLegacy(string path)
    {
      if (File.Exists(path) && !File.Exists(path + ".legacy.bak")) File.Copy(path, path + ".legacy.bak", false);
    }

    public static void SaveConfiguration(JObject settings, JObject providers, string providersPath, JObject preferences, IDictionary<string, string> expectedFiles = null)
    {
      lock (ConfigurationLock)
      {
        using var lease = new ConfigurationLease();
        if (expectedFiles != null)
          foreach (var entry in expectedFiles)
            if ((File.Exists(entry.Key) ? File.ReadAllText(entry.Key) : null) != entry.Value)
              throw new IOException("Configuration changed outside this dialog. Reopen settings before saving: " + entry.Key);
        providersPath = Path.GetFullPath(providersPath);
        var target = ActiveSettingsPath;
        if (new[] { target, SettingsPath, PreferencesPath }.Any(p => string.Equals(Path.GetFullPath(p), providersPath, StringComparison.OrdinalIgnoreCase)))
          throw new InvalidOperationException("Providers and project/application settings must use different files.");
        ConfigurationStore.Validate(settings, providers);
        var executable = preferences["executable_path"]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(executable) && !File.Exists(executable))
          throw new InvalidOperationException("Choose an existing PhenixCode executable.");
        var interval = preferences["watch_interval"]?.Value<int>() ?? 60;
        if (interval < 1) throw new InvalidOperationException("Watch interval must be at least one second.");
        var fim = preferences["fim_provider"]?.Value<string>();
        var enabledForProject = settings["generation"]?["enabled_providers"]?.Values<string>() ?? Enumerable.Empty<string>();
        if (!string.IsNullOrEmpty(fim) && !((JArray)providers["generation_providers"]).OfType<JObject>().Any(p =>
          p["id"]?.Value<string>() == fim && p["enabled"]?.Value<bool>() != false && p["fim"] is JObject && enabledForProject.Contains(fim)))
          throw new InvalidOperationException("The completion provider must support FIM and be enabled globally and for this project.");
        preferences = (JObject)preferences.DeepClone();
        preferences["providers_path"] = providersPath;
        var writes = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase)
        {
          [providersPath] = providers, [target] = settings, [PreferencesPath] = preferences
        };
        var previous = writes.Keys.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllText(p) : null, StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();
        try
        {
          foreach (var entry in writes)
          {
            if (previous[entry.Key] != null && JToken.DeepEquals(ConfigurationStore.ParseJson(previous[entry.Key]), entry.Value)) continue;
            ConfigurationStore.WriteJson(entry.Key, entry.Value);
            written.Add(entry.Key);
          }
        }
        catch
        {
          foreach (var path in written.AsEnumerable().Reverse())
          {
            try
            {
              if (previous[path] == null) File.Delete(path);
              else ConfigurationStore.WriteJson(path, ConfigurationStore.ParseJson(previous[path]));
            }
            catch (Exception ex) { Diag.OutputMsg($"Could not restore configuration file {path}: {ex.Message}"); }
          }
          throw;
        }
      }
    }

    private sealed class ConfigurationLease : IDisposable
    {
      private readonly Mutex mutex = new Mutex(false, @"Local\PhenixCode.VSIX.Configuration");
      public ConfigurationLease()
      {
        try
        {
          if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
          {
            mutex.Dispose();
            throw new IOException("Another Visual Studio instance is saving PhenixCode settings. Try again shortly.");
          }
        }
        catch (AbandonedMutexException) { /* The previous writer exited; atomic files remain readable. */ }
      }
      public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }

    public static void SaveToSolutionSettings(string config)
    {
      string text = config.Replace("$(SolutionDir)", SolutionDir);
      ConfigurationStore.WriteJson(SolutionSettingsPath, ConfigurationStore.ParseJson(text));
    }

    public static Task ShutdownServiceAsync()
    {
      var infoPath = InfoFilePath;
      Process owned = null;
      string key = null;
      lock (OwnedServiceKeys)
      {
        if (infoPath != null)
        {
          OwnedProcesses.TryGetValue(infoPath, out owned);
          OwnedServiceKeys.TryGetValue(infoPath, out key);
        }
      }
      return ShutdownServiceAsync(infoPath, owned, key);
    }

    private static async Task ShutdownServiceAsync(string infoPath, Process owned, string key)
    {
      int port = 0;
      int pid = 0;
      try
      {
        if (string.IsNullOrEmpty(infoPath)) return;
        JObject info = null;
        // A launched process may not publish info.json until indexing and initialization finish.
        if (owned != null)
        {
          var timer = Stopwatch.StartNew();
          while (ProcessAlive(owned) && timer.Elapsed < TimeSpan.FromSeconds(10))
          {
            try
            {
              var candidate = File.Exists(infoPath) ? ConfigurationStore.ReadJson(infoPath) : null;
              if (candidate?["pid"]?.Value<int>() == owned.Id) { info = candidate; break; }
            }
            catch (IOException) { }
            catch (JsonException) { }
            await Task.Delay(200).ConfigureAwait(false);
          }
          if (!ProcessAlive(owned)) return;
          if (info == null)
          {
            owned.Kill();
            Diag.OutputMsg("Stopped the VS-owned service before startup completed.");
            return;
          }
        }
        if (!File.Exists(infoPath)) return;
        info = info ?? ConfigurationStore.ReadJson(infoPath);
        port = info["port"]?.Value<int>() ?? 0;
        pid = info["pid"]?.Value<int>() ?? 0;
        if (0 < port)
        {
          using (var client = new System.Net.Http.HttpClient())
          {
            client.Timeout = TimeSpan.FromSeconds(10);
            if (!string.IsNullOrEmpty(key)) client.DefaultRequestHeaders.Add("X-App-Key", key);
            Diag.OutputMsg($"Posting /api/shutdown for port {port} and pid {pid}");
            var res = await client.PostAsync(
                $"http://localhost:{port}/api/shutdown",
                new System.Net.Http.StringContent("{}", Encoding.UTF8, "application/json")
            ).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            Diag.OutputMsg($"Posted /api/shutdown with {res.StatusCode} {res}");
          }
        }
      }
      catch (Exception ex)
      {
        Diag.OutputMsg($"Service shutdown failed: {ex.Message}");
        throw;
      }
    }

    public static async Task CloseSolutionAsync()
    {
      Task shutdown;
      lock (ConfigurationLock)
      {
        var infoPath = InfoFilePath;
        bool owned;
        lock (OwnedServiceKeys) owned = infoPath != null && OwnedServiceKeys.ContainsKey(infoPath);
        // Capture ownership before clearing paths. External services are left running.
        shutdown = Task.WhenAll(pendingShutdown, owned ? ShutdownServiceAsync() : Task.CompletedTask);
        pendingShutdown = ObserveShutdownAsync(shutdown);
        SolutionSettingsPath = null;
        SolutionDir = null;
        InfoFilePath = null;
      }
      await ObserveShutdownAsync(shutdown).ConfigureAwait(false);
    }

    private static async Task ObserveShutdownAsync(Task shutdown)
    {
      // Shutdown uses only process/file/network operations and never needs the VS UI context.
#pragma warning disable VSTHRD003
      try { await shutdown.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
      catch (Exception ex) { Diag.OutputMsg($"Solution close: {ex.Message}"); }
    }

    public static Process StartProcess(string executablePath, OutputWindowPane pane)
    {
      lock (ConfigurationLock) return StartOwnedProcess(executablePath, pane);
    }

    private static Process StartOwnedProcess(string executablePath, OutputWindowPane pane)
    {
      if (string.IsNullOrWhiteSpace(SolutionSettingsPath)) throw new InvalidOperationException("Open a solution before starting PhenixCode.");
      EnsureConfiguration();
      var settings = ConfigurationStore.ReadJson(SolutionSettingsPath);
      ConfigurationStore.Validate(settings, ConfigurationStore.ReadJson(ProvidersPath));
      var tokenizer = settings["tokenizer"]?["config_path"]?.Value<string>() ?? "tokenizer.json";
      var pathError = ServicePathValidation.Check(executablePath, tokenizer, SolutionDir);
      if (pathError != null) { Diag.OutputMsg("[ERR] Cannot start service: " + pathError); return null; }
      executablePath = Path.GetFullPath(executablePath);
      if (IsServiceRunning) throw new InvalidOperationException("PhenixCode is already running for this solution.");
      lock (OwnedServiceKeys)
        if (OwnedProcesses.TryGetValue(InfoFilePath, out var pending) && ProcessAlive(pending))
          throw new InvalidOperationException("PhenixCode is still starting. Stop it before starting another instance.");
      var preferences = LoadPreferences();
      var key = Guid.NewGuid().ToString("N");
      var interval = preferences["watch_interval"]?.Value<int>() ?? 60;
      if (interval < 1) throw new InvalidOperationException("Watch interval must be at least one second.");
      var watch = preferences["watch"]?.Value<bool>() != false ? $" --watch --interval {interval}" : "";
      var psi = new ProcessStartInfo
      {
        FileName = executablePath,
        Arguments = $"--no-startup-tests --providers-config \"{ProvidersPath}\" --config \"{SolutionSettingsPath}\" serve --yes --appkey {key} --info-file \"{InfoFilePath}\" --port 59241{watch}",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = SolutionDir
      };

      Process proc = new() { StartInfo = psi, EnableRaisingEvents = true };
      var infoPath = InfoFilePath;
      FileStream launchLease;
      try { launchLease = new FileStream(Path.Combine(Path.GetDirectoryName(infoPath), "vsix-service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
      catch (IOException ex) { throw new IOException("Another Visual Studio instance already owns a running or starting service for this solution.", ex); }
      // Retain an OS file handle until exit; it also protects startup before info.json exists across VS instances.
      proc.Exited += (_, _) =>
      {
        launchLease.Dispose();
        lock (OwnedServiceKeys)
        {
          if (OwnedProcesses.TryGetValue(infoPath, out var process) && ReferenceEquals(process, proc))
          {
            OwnedProcesses.Remove(infoPath);
            OwnedServiceKeys.Remove(infoPath);
          }
        }
      };

      if (pane != null)
      {
        proc.OutputDataReceived += (s, args) => { if (args.Data != null) { pane.WriteLine($"[OUT] {args.Data}"); } };
        proc.ErrorDataReceived += (s, args) => { if (args.Data != null) { pane.WriteLine($"[ERR] {args.Data}"); } };
      }

      try
      {
        lock (OwnedServiceKeys)
        {
          if (!proc.Start()) { launchLease.Dispose(); return null; }
          OwnedServiceKeys[infoPath] = key;
          OwnedProcesses[infoPath] = proc;
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
      }
      catch
      {
        if (ProcessAlive(proc)) proc.Kill();
        launchLease.Dispose();
        throw;
      }
    }

    private static bool ProcessAlive(Process process)
    {
      try { return process != null && !process.HasExited; }
      catch (InvalidOperationException) { return false; }
    }

    public static bool IsServiceStarting
    {
      get
      {
        lock (OwnedServiceKeys)
          return InfoFilePath != null && OwnedProcesses.TryGetValue(InfoFilePath, out var process) && ProcessAlive(process) && !IsServiceRunning;
      }
    }

    public static async Task<bool> IsServiceHealthyAsync()
    {
      var path = InfoFilePath;
      if (string.IsNullOrEmpty(path) || !IsServiceRunning) return false;
      try
      {
        var port = ConfigurationStore.ReadJson(path)["port"]?.Value<int>() ?? 0;
        if (port <= 0) return false;
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        using var response = await client.GetAsync($"http://localhost:{port}/api/health");
        return response.IsSuccessStatusCode && JObject.Parse(await response.Content.ReadAsStringAsync())["status"]?.Value<string>() == "ok";
      }
      catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException || ex is JsonException) { return false; }
    }

    public static async Task<string> FillInTheMiddleAsync(string prefix, string suffix, string fileName, CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (string.IsNullOrEmpty(InfoFilePath) || !File.Exists(InfoFilePath))
        throw new InvalidOperationException("Start the service in PhenixCode Settings.");
      if (string.IsNullOrEmpty(SolutionSettingsPath) || !File.Exists(SolutionSettingsPath))
        throw new InvalidOperationException("Open a solution before requesting an inline suggestion.");
      var info = ConfigurationStore.ReadJson(InfoFilePath);
      int port = info["port"]?.Value<int>() ?? 0;
      if (port <= 0 || port > 65535) throw new InvalidOperationException("The service has not published a valid port.");
      var settings = ConfigurationStore.ReadJson(SolutionSettingsPath);
      var providers = ConfigurationStore.ReadJson(ProvidersPath);
      var currentApi = LoadPreferences()["fim_provider"]?.Value<string>();
      var enabledForProject = settings["generation"]?["enabled_providers"]?.Values<string>() ?? Enumerable.Empty<string>();
      var candidates = (providers["generation_providers"] as JArray ?? new JArray()).OfType<JObject>()
        .Where(p => p["enabled"]?.Value<bool>() != false && p["fim"] is JObject && enabledForProject.Contains(p["id"]?.Value<string>())).ToArray();
      if (string.IsNullOrEmpty(currentApi))
      {
        var selected = settings["generation"]?["current_api"]?.Value<string>();
        currentApi = candidates.FirstOrDefault(p => p["id"]?.Value<string>() == selected)?["id"]?.Value<string>()
          ?? candidates.FirstOrDefault()?["id"]?.Value<string>();
      }
      if (string.IsNullOrEmpty(currentApi) || !candidates.Any(p => p["id"]?.Value<string>() == currentApi))
        throw new InvalidOperationException("Enable a FIM-capable provider for this project in Settings.");
      var timeoutMs = settings["generation"]?["timeout_ms"]?.Value<double>() ?? 30000;
      using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        { Timeout = TimeSpan.FromMilliseconds(Math.Min(30000, Math.Max(1000, timeoutMs))) };
      using var content = new StringContent(JsonConvert.SerializeObject(new
        { prefix, suffix, filename = fileName ?? string.Empty, targetapi = currentApi, temperature = 0.0, max_tokens = 256 }), Encoding.UTF8, "application/json");
      try
      {
        using var response = await client.PostAsync($"http://localhost:{port}/api/fim", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = ConfigurationStore.ParseJson(await response.Content.ReadAsStringAsync());
        if (result["error"] != null) throw new InvalidOperationException(result["error"].ToString());
        if (result["completion"]?.Type != JTokenType.String) throw new InvalidOperationException("The service returned an invalid inline completion response.");
        return result["completion"].Value<string>();
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
        throw new TimeoutException("Inline generation timed out. Try again or check the service.");
      }
    }

    private static TimeSpan ChatTimeout()
    {
      var settings = ConfigurationStore.ReadJson(SolutionSettingsPath);
      var generationMs = settings["generation"]?["timeout_ms"]?.Value<double>() ?? 120000;
      var classifierMs = settings["generation"]?["auto_router"]?["classifier"]?["timeout_ms"]?.Value<double>() ?? 3500;
      return TimeSpan.FromMilliseconds(Math.Min(int.MaxValue, Math.Max(1000, generationMs) + Math.Max(0, classifierMs) + 30000));
    }

    public static async Task<string> ChatCompletionAsync(
        Action<string> onTokenReceived,
        Collection<ChatMessage> messages,
        List<string> sourceIds = null,
        List<Attachment> attachments = null,
        double? temperature = null,
        int? maxTokens = null,
        string targetApi = null,
        double ctxRatio = 0.5,
        bool attachedOnly = false,
        CancellationToken cancellationToken = default)
    {
      if (string.IsNullOrEmpty(InfoFilePath) || !File.Exists(InfoFilePath))
      {
        Diag.OutputMsg("ChatCompletionAsync: InfoFilePath is invalid");
        return string.Empty;
      }

      if (string.IsNullOrEmpty(SolutionSettingsPath) || !File.Exists(SolutionSettingsPath))
      {
        Diag.OutputMsg("ChatCompletionAsync: SolutionSettingsPath is invalid");
        return string.Empty;
      }

      try
      {
        var info = JObject.Parse(File.ReadAllText(InfoFilePath));
        int port = info["port"]?.Value<int>() ?? 0;

        if (port <= 0)
        {
          Diag.OutputMsg("ChatCompletionAsync: port is invalid");
          return string.Empty;
        }


        using (var client = new HttpClient { Timeout = ChatTimeout() })
        {
          var requestData = ChatProtocol.CreateRequest(messages, sourceIds,
            attachments == null ? null : JArray.FromObject(attachments), temperature, maxTokens, targetApi, ctxRatio, attachedOnly);
          var jsonContent = requestData.ToString(Formatting.None);
          var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

          var response = await client.PostAsync(
              $"http://localhost:{port}/api/chat",
              content,
              cancellationToken
          );

          if (response.IsSuccessStatusCode)
          {
            var text = await response.Content.ReadAsStringAsync();
            using var reader = new StringReader(text);
            return await ChatProtocol.ReadCompletionAsync(reader, onTokenReceived, cancellationToken);
          }
          else
          {
            Diag.OutputMsg($"ChatCompletionAsync: status {response.StatusCode}, {response.Content}");
          }
        }
      }
      catch (Exception ex)
      {
        Diag.OutputMsg($"ChatCompletionAsync error: {ex.Message}");
      }

      return string.Empty;
    }

    public static async Task<string> GetStatsAsync(CancellationToken cancellationToken = default)
    {
      if (string.IsNullOrEmpty(SolutionSettingsPath)) throw new InvalidOperationException("Open a solution to view statistics.");
      if (!IsServiceRunning) throw new InvalidOperationException("Start the PhenixCode service in Settings.");
      var port = ConfigurationStore.ReadJson(InfoFilePath)["port"]?.Value<int>() ?? 0;
      if (port <= 0 || port > 65535) throw new InvalidOperationException("The service has not published a valid port yet.");
      using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
      using var response = await client.GetAsync($"http://localhost:{port}/api/stats", cancellationToken);
      response.EnsureSuccessStatusCode();
      return await response.Content.ReadAsStringAsync();
    }

    public static async Task<JObject> GetChatProvidersAsync(CancellationToken cancellationToken = default)
    {
      if (!IsServiceRunning) throw new InvalidOperationException("Start the PhenixCode service in Settings.");
      var port = ConfigurationStore.ReadJson(InfoFilePath)["port"]?.Value<int>() ?? 0;
      if (port <= 0) throw new InvalidOperationException("The service has not published its port yet.");
      using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
      using var response = await client.GetAsync($"http://localhost:{port}/api/settings", cancellationToken);
      response.EnsureSuccessStatusCode();
      return ConfigurationStore.ParseJson(await response.Content.ReadAsStringAsync());
    }

    public static async Task<string> ChatCompletionStreamAsync(
          Action<string> onTokenReceived,
          Collection<ChatMessage> messages,
          List<string> sourceIds = null,
          List<Attachment> attachments = null,
          double? temperature = null,
          int? maxTokens = null,
          string targetApi = null,
          double ctxRatio = 0.5,
          bool attachedOnly = false,
          CancellationToken cancellationToken = default,
          bool propagateErrors = false,
          Action<JObject> onMetadata = null)
    {
      if (string.IsNullOrEmpty(InfoFilePath) || !File.Exists(InfoFilePath))
      {
        Diag.OutputMsg("ChatCompletionStreamAsync: InfoFilePath is invalid");
        if (propagateErrors) throw new InvalidOperationException("The service has not published its connection details. Open Settings to check startup.");
        return string.Empty;
      }

      if (string.IsNullOrEmpty(SolutionSettingsPath) || !File.Exists(SolutionSettingsPath))
      {
        Diag.OutputMsg("ChatCompletionStreamAsync: SolutionSettingsPath is invalid");
        if (propagateErrors) throw new InvalidOperationException("Open a solution before chatting.");
        return string.Empty;
      }

      try
      {
        var info = JObject.Parse(File.ReadAllText(InfoFilePath));
        int port = info["port"]?.Value<int>() ?? 0;

        if (port <= 0)
        {
          Diag.OutputMsg("ChatCompletionStreamAsync: port is invalid");
          if (propagateErrors) throw new InvalidOperationException("The service connection details are invalid. Open Settings to check the service.");
          return string.Empty;
        }


        using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = ChatTimeout() })
        {
          var requestData = ChatProtocol.CreateRequest(messages, sourceIds,
            attachments == null ? null : JArray.FromObject(attachments), temperature, maxTokens, targetApi, ctxRatio, attachedOnly);
          var jsonContent = requestData.ToString(Formatting.None);

          using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/api/chat")
          {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
          };

          using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
          );

          response.EnsureSuccessStatusCode();
          using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
          streamCancellation.CancelAfter(ChatTimeout());
          using var registration = streamCancellation.Token.Register(() => response.Dispose());
          using var stream = await response.Content.ReadAsStreamAsync();
          using var reader = new StreamReader(stream);
          try { return await ChatProtocol.ReadCompletionAsync(reader, onTokenReceived, streamCancellation.Token, onMetadata); }
          catch when (streamCancellation.IsCancellationRequested)
          {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The response timed out. Partial text was kept; retry or choose another model.");
          }
        }
      }
      catch (Exception ex)
      {
        Diag.OutputMsg($"ChatCompletionStreamAsync error: {ex.Message}");
        if (propagateErrors) throw;
      }

      return string.Empty;
    }

    public static Process FetchRunningServiceProcess()
    {
      if (string.IsNullOrEmpty(InfoFilePath) || !File.Exists(InfoFilePath))
        return null;

      try
      {
        var info = JObject.Parse(File.ReadAllText(InfoFilePath));
        int pid = info["pid"]?.Value<int>() ?? 0;

        if (pid <= 0)
          return null;

        var exec = info["exec"]?.ToString() ?? "";

        var process = Process.GetProcessById(pid);
        if (process != null && !process.HasExited)
        {
          string path = process.MainModule.FileName;
          path = path.Replace('\\', '/').ToLower();
          exec = exec.Replace('\\', '/').ToLower();
          return path == exec ? process : null;
        }
        return null;
      }
      catch (FileNotFoundException)
      {
        return null;
      }
      catch (JsonException)
      {
        return null;
      }
      catch (ArgumentException) // Process.GetProcessById throws this if process doesn't exist
      {
        return null;
      }
      catch (InvalidOperationException) // Process has exited
      {
        return null;
      }
      catch
      {
        // Log unexpected errors if needed
        return null;
      }
    }

    public static bool IsServiceRunning
    {
      get
      {
        using var process = FetchRunningServiceProcess();
        return process != null;
      }
    }

    public static async Task<OutputWindowPane> GetPaneAsync() => await VS.Windows.GetOutputWindowPaneAsync(OutputPaneGuid);
  }
}
