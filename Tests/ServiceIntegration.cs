using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using ChatAssistantVSIX.Utils;
using Newtonsoft.Json.Linq;
using Core = ChatAssistantVSIX.Utils.PhenixCodeCoreService;

internal static class ServiceIntegration
{
  private static readonly string Sandbox = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox"));
  private static readonly List<Process> Children = new List<Process>();
  private static int assertions;
  private static string Exe => Process.GetCurrentProcess().MainModule.FileName;
  private static void Check(bool value, string message)
  {
    if (!value) throw new Exception(message);
    assertions++;
  }
  private static string Safe(string path)
  {
    path = Path.GetFullPath(path);
    if (!path.StartsWith(Sandbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Path outside test sandbox: " + path);
    return path;
  }
  private static void Write(string path, string text)
  {
    path = Safe(path); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text);
  }
  private static async Task Until(Func<bool> predicate, string message)
  {
    var timer = Stopwatch.StartNew();
    while (!predicate())
    {
      if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(message);
      await Task.Delay(50);
    }
  }
  private static async Task Bounded(Task task)
  {
    if (await Task.WhenAny(task, Task.Delay(20000)) != task) throw new TimeoutException("Service operation exceeded 20 seconds");
    await task;
  }
  private static Dictionary<string, string> RealConfigSnapshot()
  {
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhenixCode");
    return new[] { "settings.json", "providers.json", "vsix.json" }.ToDictionary(n => Path.Combine(root, n),
      n => File.Exists(Path.Combine(root, n)) ? Convert.ToBase64String(File.ReadAllBytes(Path.Combine(root, n))) : null);
  }
  private static int Main(string[] args)
  {
    try
    {
      Environment.SetEnvironmentVariable("PHENIXCODE_PROVIDERS_CONFIG", null);
      if (args.Contains("--no-startup-tests")) return Backend(args).GetAwaiter().GetResult();
      if (args.Length == 2 && args[0] == "--attempt-start")
      {
        Core.InitOnSolutionReady(new Solution { Name = "Contender", FullPath = Safe(args[1]) });
        try
        {
          var unexpected = Core.StartProcess(Exe, null);
          if (unexpected != null) { unexpected.Kill(); unexpected.WaitForExit(5000); unexpected.Dispose(); }
          return 2;
        }
        catch (IOException ex) when (ex.Message.Contains("Another Visual Studio instance")) { return 0; }
      }
      var before = RealConfigSnapshot();
      try { if (args.Contains("--fim-only")) FimServiceIntegration.RunAsync(Sandbox).GetAwaiter().GetResult(); else Run().GetAwaiter().GetResult(); }
      finally
      {
        foreach (var child in Children)
        {
          try { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
          finally { child.Dispose(); }
        }
        var after = RealConfigSnapshot();
        Check(before.All(p => after[p.Key] == p.Value), "Original APPDATA config changed");
      }
      Console.WriteLine("PASS: " + assertions + " integration assertions; fixtures: " + Sandbox);
      return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
  }
  private static async Task Run()
  {
    Safe(Core.SettingsPath);
    // Retain prior evidence, but move only the verified redirected application directory.
    var configDir = Safe(Path.GetDirectoryName(Core.SettingsPath));
    if (Directory.Exists(configDir)) Directory.Move(configDir, Safe(configDir + ".previous-" + Guid.NewGuid().ToString("N")));
    await Core.InitAsync();
    Check(File.Exists(Core.SettingsPath) && File.Exists(Core.ProvidersPath), "Default settings/catalog not initialized");
    Check(!Core.LoadPreferences().HasValues, "Default preferences should be empty");
    Check(await Core.GetPaneAsync() != null, "Output pane stub");
    var original = File.ReadAllText(Core.SettingsPath);
    var legacy = JObject.Parse(original);
    legacy["generation"]["apis"] = new JArray(new JObject {
      ["id"] = "integration-legacy", ["model"] = "fake", ["api_url"] = "http://localhost/unused", ["api_key"] = "${TEST_SECRET}" });
    legacy["generation"]["current_api"] = "integration-legacy";
    legacy["generation"]["enabled_providers"] = new JArray("integration-legacy");
    Write(Core.SettingsPath, legacy.ToString());
    await Core.InitAsync();
    var migrated = ConfigurationStore.ReadJson(Core.SettingsPath);
    Check(migrated["generation"]["apis"] == null && File.Exists(Core.SettingsPath + ".legacy.bak"), "Legacy migration/backup");
    Check(ConfigurationStore.ReadJson(Core.ProvidersPath)["generation_providers"].Any(p => (string)p["id"] == "integration-legacy"), "Migrated provider missing");
    var snapshot = File.ReadAllText(Core.SettingsPath);
    var catalogSnapshot = File.ReadAllText(Core.ProvidersPath);
    var backup = File.ReadAllText(Core.SettingsPath + ".legacy.bak");
    await Core.InitAsync();
    Check(snapshot == File.ReadAllText(Core.SettingsPath) && catalogSnapshot == File.ReadAllText(Core.ProvidersPath) && backup == File.ReadAllText(Core.SettingsPath + ".legacy.bak"), "Repeated init changed migrated files");
    var runDir = Safe(Path.Combine(Sandbox, "run-" + Guid.NewGuid().ToString("N")));
    var solutionFile = Path.Combine(runDir, "fake solution.sln");
    Write(solutionFile, "test fixture");
    var blockedSolution = Path.Combine(runDir, "blocked", "readonly.sln");
    Write(blockedSolution, "test fixture");
    Write(Path.Combine(runDir, "blocked", ".phenixcode"), "A file blocks creation of the configuration directory.");
    Core.InitOnSolutionReady(new Solution { Name = "Blocked", FullPath = blockedSolution });
    Check(!Directory.Exists(Path.Combine(runDir, "blocked", ".phenixcode")), "Blocked configuration directory unexpectedly created");
    Core.InitOnSolutionReady(new Solution { Name = "Fake", FullPath = solutionFile });
    Check(Core.SolutionDir == runDir.Replace('\\', '/') && Core.ActiveSettingsPath == Core.SolutionSettingsPath, "Solution activation");
    Check(!File.ReadAllText(Core.SolutionSettingsPath).Contains("$(SolutionDir)"), "Solution placeholders not expanded");
    var settings = ConfigurationStore.ReadJson(Core.SolutionSettingsPath);
    var tokenizer = Path.Combine(runDir, "tokenizer fixture.json"); Write(tokenizer, "{}");
    settings["tokenizer"]["config_path"] = tokenizer;
    settings["generation"]["default_temperature"] = 0.83;
    settings["generation"]["default_max_tokens"] = 719;
    var alternate = Safe(Path.Combine(runDir, "catalog with spaces.json"));
    var prefs = new JObject { ["executable_path"] = Exe, ["watch"] = false, ["watch_interval"] = 3 };
    // A minimal fake catalog avoids unrelated default model requirements (and APIs).
    var fakeCatalog = new JObject {
      ["embedding_providers"] = new JArray(new JObject { ["id"] = "custom", ["model"] = "fake", ["api_url"] = "http://localhost/unused" }),
      ["generation_providers"] = new JArray(new JObject { ["id"] = "integration-legacy", ["model"] = "fake", ["api_url"] = "http://localhost/unused" }) };
    Core.SaveConfiguration(settings, fakeCatalog, alternate, prefs);
    Check(Core.ProvidersPath == alternate && File.Exists(alternate), "Alternate catalog not selected");
    Check((string)Core.LoadPreferences()["executable_path"] == Exe && (int)ConfigurationStore.ReadJson(Core.SolutionSettingsPath)["generation"]["default_max_tokens"] == 719, "SaveConfiguration persistence");
    Core.InitOnSolutionReady(new Solution { Name = "Fake", FullPath = solutionFile });
    Check((string)Core.LoadPreferences()["providers_path"] == alternate && Core.ProvidersPath == alternate, "Repeated solution init lost preferences");
    Check(!await Core.IsServiceHealthyAsync(), "Health before start");
    var info = Safe(Path.Combine(runDir, ".phenixcode", "info.json"));
    Check(ServicePathValidation.Check(Exe, Path.GetFileName(tokenizer), runDir) == null, "Solution-relative tokenizer path was rejected");
    Check(Core.StartProcess("bad\0executable", null) == null && !Core.IsServiceStarting, "Malformed executable threw or started a process");
    File.Delete(Safe(tokenizer));
    Check(Core.StartProcess(Exe, null) == null && !Core.IsServiceStarting && !File.Exists(info), "Missing tokenizer threw or started a process");
    Write(tokenizer, "{}");
    var child = Core.StartProcess(Exe, await Core.GetPaneAsync()); Children.Add(child);
    Check(child != null && !File.Exists(info) && Core.IsServiceStarting, "Pending startup not visible");
    bool blocked = false;
    try { var unexpected = Core.StartProcess(Exe, null); if (unexpected != null) Children.Add(unexpected); }
    catch (InvalidOperationException) { blocked = true; }
    Check(blocked && !File.Exists(info), "Double start must fail before info publication");
    var contender = Process.Start(new ProcessStartInfo(Exe, "--attempt-start \"" + solutionFile + "\"") {
      UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runDir });
    Children.Add(contender);
    await Until(() => contender.HasExited, "Startup contender did not finish");
    Check(contender.ExitCode == 0 && !File.Exists(info), "Cross-process startup lease did not reject pending duplicate");
    await Bounded(Core.ShutdownServiceAsync());
    await Until(() => child.HasExited, "Pending child did not shut down");
    Check(child.ExitCode == 0 && File.Exists(info + ".shutdown"), "Pending shutdown was not authenticated");
    var receipt = JObject.Parse(File.ReadAllText(info + ".argv.json"));
    Check((string)receipt["--providers-config"] == alternate && (string)receipt["--config"] == Core.SolutionSettingsPath, "Launch configuration arguments");
    Check(((string)receipt["--appkey"]).Length == 32 && (int)receipt["pid"] == child.Id && (string)receipt["exec"] == Exe, "App key/process receipt");
    // Old info deliberately remains: live process identity must determine startup/running.
    child = Core.StartProcess(Exe, await Core.GetPaneAsync()); Children.Add(child);
    await Until(() => Core.IsServiceRunning, "Second child never published identity");
    Check(await Core.IsServiceHealthyAsync() && !Core.IsServiceStarting, "Live health");
    var models = await Core.GetChatProvidersAsync();
    Check((string)models["currentApi"] == "integration-legacy" && (string)models["completionApis"][0]["id"] == "integration-legacy", "Running backend provider catalog");
    var stats = JObject.Parse(await Core.GetStatsAsync());
    Check((int)stats["total_chunks"] == 42 && (int)stats["vector_count"] == 40 && (string)stats["sources"][0]["id"] == "fixture", "Running backend statistics");
    using (var cancelled = new System.Threading.CancellationTokenSource())
    {
      cancelled.Cancel();
      bool observedCancellation = false;
      try { await Core.GetStatsAsync(cancelled.Token); }
      catch (OperationCanceledException) { observedCancellation = true; }
      Check(observedCancellation, "Statistics request ignored cancellation");
    }
    using (var found = Core.FetchRunningServiceProcess()) Check(found != null && found.Id == child.Id, "Running identity");
    using (var unauthorized = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) })
    {
      unauthorized.DefaultRequestHeaders.Add("X-App-Key", "incorrect-test-key");
      using var response = await unauthorized.PostAsync("http://localhost:59241/api/shutdown", new System.Net.Http.StringContent("{}"));
      Check(response.StatusCode == HttpStatusCode.Forbidden && !child.HasExited, "Shutdown accepted incorrect app key");
    }
    var messages = new Collection<ChatMessage> {
      new ChatMessage { Role = "system", Content = "system" },
      new ChatMessage { Role = "user", Content = "previous" },
      new ChatMessage { Role = "assistant", Content = "answer" },
      new ChatMessage { Role = "user", Content = "default" } };
    var callbacks = new List<string>();
    using var chatCancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
    var result = await Core.ChatCompletionAsync(callbacks.Add, messages, cancellationToken: chatCancellation.Token);
    Check(result == "Hello world" && string.Concat(callbacks.Select(p => (string)JObject.Parse(p)["content"])) == result, "Buffered SSE completion/callbacks");
    messages.Last().Content = "explicit"; callbacks.Clear();
    string actualModel = null;
    result = await Core.ChatCompletionStreamAsync(callbacks.Add, messages, new List<string> { "src" },
      new List<Core.Attachment> { new Core.Attachment { Filename = "a.cs", Content = "fixture" } },
      temperature: 0.25, maxTokens: 123, targetApi: "integration-legacy", ctxRatio: 0.7, attachedOnly: true, cancellationToken: chatCancellation.Token,
      onMetadata: data => { if (data["model"] != null) actualModel = (string)data["model"]; });
    Check(result == "Hello world" && callbacks.Count == 2, "Streaming SSE completion/callbacks");
    Check(actualModel == "fake-model", "Streaming model metadata");
    messages.Last().Content = "failure";
    bool surfaced = false;
    try { await Core.ChatCompletionStreamAsync(null, messages, propagateErrors: true); }
    catch (InvalidOperationException ex) { surfaced = ex.Message.Contains("fake provider failure"); }
    Check(surfaced && !child.HasExited, "Provider failure must reach the retryable UI without being collapsed to empty text");
    Check(File.Exists(info + ".chat-default.json") && File.Exists(info + ".chat-explicit.json"), "Backend did not validate requests");
    await Bounded(Core.CloseSolutionAsync());
    Check(Core.SolutionDir == null && Core.SolutionSettingsPath == null && Core.ActiveSettingsPath == Core.SettingsPath && !Core.IsServiceRunning, "Close did not clear solution");
    await Until(() => child.HasExited, "Close did not shut owned child down");
    Check(child.ExitCode == 0, "Backend validation failure");
    Check(!await Core.IsServiceHealthyAsync(), "Health after close");
  }

  // Minimal HTTP/1.1 backend: no HttpListener URL ACL and no calls to real model APIs.
  private static async Task<int> Backend(string[] args)
  {
    var options = new JObject();
    foreach (var name in new[] { "--providers-config", "--config", "--info-file", "--appkey", "--port" })
    {
      int index = Array.IndexOf(args, name);
      if (index < 0 || index + 1 >= args.Length) throw new Exception("Missing " + name);
      options[name] = args[index + 1];
    }
    var info = Safe((string)options["--info-file"]);
    Safe((string)options["--config"]); Safe((string)options["--providers-config"]);
    options["argv"] = new JArray(args); options["pid"] = Process.GetCurrentProcess().Id; options["exec"] = Exe;
    Write(info + ".argv.json", options.ToString());
    var listener = new TcpListener(IPAddress.Loopback, int.Parse((string)options["--port"]));
    listener.Start();
    try
    {
      await Task.Delay(2000);
      Write(info, new JObject { ["pid"] = Process.GetCurrentProcess().Id, ["exec"] = Exe, ["port"] = int.Parse((string)options["--port"]) }.ToString());
      var lifetime = Stopwatch.StartNew();
      while (lifetime.Elapsed < TimeSpan.FromSeconds(45))
      {
        var accept = listener.AcceptTcpClientAsync();
        if (await Task.WhenAny(accept, Task.Delay(5000)) != accept) break;
        using (var client = await accept)
        {
          client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
          using (var stream = client.GetStream())
          using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
          {
            var first = await reader.ReadLineAsync();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            { int colon = line.IndexOf(':'); headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim(); }
            int length = headers.ContainsKey("Content-Length") ? int.Parse(headers["Content-Length"]) : 0;
            var buffer = new char[length]; int read = 0;
            while (read < length) { int count = await reader.ReadAsync(buffer, read, length - read); if (count == 0) throw new EndOfStreamException(); read += count; }
            var path = first.Split(' ')[1];
            bool stop = false; int status = 200; string body;
            if (path == "/api/health") body = "{\"status\":\"ok\"}";
            else if (path == "/api/stats") body = "{\"total_chunks\":42,\"vector_count\":40,\"sources\":[{\"id\":\"fixture\",\"files\":3}]}";
            else if (path == "/api/settings") body = "{\"currentApi\":\"integration-legacy\",\"completionApis\":[{\"id\":\"integration-legacy\",\"model\":\"fake-model\",\"name\":\"Fixture\"}]}";
            else if (path == "/api/shutdown")
            {
              stop = headers.TryGetValue("X-App-Key", out var key) && key == (string)options["--appkey"];
              status = stop ? 200 : 403; body = "{}";
              if (stop) Write(info + ".shutdown", key);
            }
            else if (path == "/api/chat")
            {
              var request = JObject.Parse(new string(buffer));
              Check(first.StartsWith("POST "), "Chat method");
              Check((string)request["messages"].Last["role"] == "user" && request["messages"].Count() == 4, "Last user/history");
              bool explicitRequest = (string)request["messages"].Last["content"] == "explicit";
              if (explicitRequest)
              {
                Check((string)request["targetapi"] == "integration-legacy" && (double)request["temperature"] == 0.25 && (double)request["max_tokens"] == 123 && request["max_tokens"].Type == JTokenType.Float, "Explicit overrides");
                Check((string)request["sourceids"][0] == "src" && (string)request["attachments"][0]["content"] == "fixture" && (bool)request["attachedonly"] && (double)request["ctxratio"] == 0.7, "Attachments/context routing");
              }
              else
              {
                Check(request["targetapi"] == null && request["temperature"] == null && request["max_tokens"] == null, "Defaults must be absent");
                Check((double)request["ctxratio"] == 0.5 && !(bool)request["attachedonly"], "Default context");
              }
              Write(info + (explicitRequest ? ".chat-explicit.json" : ".chat-default.json"), request.ToString());
              body = (string)request["messages"].Last["content"] == "failure"
                ? "data: {\"error\":\"fake provider failure\"}\n\n"
                : ": ignored\n\ndata: {\"content\":\"[meta]ignored\"}\n\ndata: {\"content\":\"Hello \"}\n\ndata: {\"content\":\"world\"}\n\ndata: {\"type\":\"context_sources\",\"model\":\"fake-model\"}\n\ndata: [DONE]\n\ndata: {\"content\":\"ignored\"}\n\n";
            }
            else { status = 404; body = "{}"; }
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " OK\r\nContent-Type: " + (path == "/api/chat" ? "text/event-stream" : "application/json") + "\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, 0, head.Length); await stream.WriteAsync(bytes, 0, bytes.Length); await stream.FlushAsync();
            if (stop) return 0;
          }
        }
      }
      throw new TimeoutException("Fake backend idle/lifetime limit");
    }
    finally { listener.Stop(); }
  }
}
