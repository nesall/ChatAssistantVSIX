using ChatAssistantVSIX.Dialogs;
using Markdig.Helpers;
using Microsoft.VisualStudio.Threading;
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
using static System.Net.Mime.MediaTypeNames;

namespace ChatAssistantVSIX.Utils
{
  public static class PhenixCodeCoreService
  {
    /// <summary>
    /// SettingsPath stores universal settings only (i.e. no solution/project specific info like paths)
    /// </summary>
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhenixCode", "settings.json");
    public static string SolutionSettingsPath { get; private set; }
    public static string SolutionDir { get; private set; }
    private static string InfoFilePath { get; set; }
    private static Guid OutputPaneGuid { get; set; }

    public class Attachment
    {
      public string Filename { get; set; }
      public string Content { get; set; }
    }

    public static void InitOnSolutionReady(Solution solution)
    {
      Debug.Assert(solution != null);
      Diag.OutputMsg($"InitOnSolutionReady solution.Name {solution.Name}, solution.FullPath {solution.FullPath}");
      if (solution.FullPath != null)
      {
        string solutionDir = Path.GetDirectoryName(solution.FullPath);
        string workDir = Path.Combine(solutionDir, ".phenixcode");
        SolutionSettingsPath = Path.Combine(workDir, "settings.json");
        InfoFilePath = Path.Combine(workDir, "info.json");
        SolutionDir = solutionDir.Replace('\\', '/');
        if (!Directory.Exists(workDir))
        {
          Directory.CreateDirectory(workDir);
        }
        if (!File.Exists(SolutionSettingsPath))
        {
          SaveToSolutionSettings(File.ReadAllText(SettingsPath));
        }
        if (IsServiceRunning)
        {
          Diag.OutputMsg("InitOnSolutionReady: Service already running. Shutting down...");
          ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
          {
            await ShutdownServiceAsync();
            var stopwatch = Stopwatch.StartNew();
            while (IsServiceRunning && stopwatch.Elapsed < TimeSpan.FromSeconds(12))
            {
              await Task.Delay(200);
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var pane = await GetPaneAsync();
            await TaskScheduler.Default;
            try
            {
              JObject settings = JObject.Parse(File.ReadAllText(SolutionSettingsPath));
              var executablePath = settings["_exec"]?.Value<string>() ?? "";
              var proc = StartProcess(executablePath, pane);
              if (proc != null)
              {
                await pane.ActivateAsync();
                await pane.WriteLineAsync($"[System] Service started with PID {proc.Id}");
              }
            }
            catch (Exception ex)
            {
              Diag.OutputMsg($"{ex.Message}");
            }
          }).FireAndForget();
        }
      }
    }

    public static async Task InitAsync()
    {
      InitGlobalSettingsFile();
      Debug.WriteLine("Creating Output Window Pane 'PhenixCode Service'");
      var pane = await VS.Windows.CreateOutputWindowPaneAsync("PhenixCode Service", false);
      OutputPaneGuid = pane.Guid;
    }

    private static bool InitGlobalSettingsFile()
    {
      const string DefaultSettingsName = "settings.default.json";

      Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
      var asm = typeof(SettingsDialog).Assembly;

      // 1) Try to find an embedded resource named settings.default.json
      try
      {
        var resourceName = asm.GetManifestResourceNames()
                              .FirstOrDefault(n => n.EndsWith($"Resources.{DefaultSettingsName}", StringComparison.OrdinalIgnoreCase)
                                                || n.EndsWith(DefaultSettingsName, StringComparison.OrdinalIgnoreCase));
        if (resourceName != null)
        {
          using (var s = asm.GetManifestResourceStream(resourceName))
          {
            if (s != null)
            {
              using (var sr = new StreamReader(s, Encoding.UTF8))
              {
                File.WriteAllText(SettingsPath, sr.ReadToEnd());
                return true;
              }
            }
          }
        }
      }
      catch { /* ignore and fallback to file copy */ }

      // 2) Fallback: copy from the Resources folder next to the extension assembly
      var tpl = Path.Combine(Path.GetDirectoryName(asm.Location), "Resources", DefaultSettingsName);
      if (File.Exists(tpl))
      {
        File.Copy(tpl, SettingsPath, false);
        return true;
      }
      return false;
    }

    public static void SaveToSolutionSettings(string config)
    {
      string text = config.Replace("$(SolutionDir)", SolutionDir);
      File.WriteAllText(PhenixCodeCoreService.SolutionSettingsPath, text);
    }

    public static async Task ShutdownServiceAsync()
    {
      int port = 0;
      int pid = 0;
      try
      {
        var info = JObject.Parse(File.ReadAllText(InfoFilePath));
        port = info["port"]?.Value<int>() ?? 0;
        pid = info["pid"]?.Value<int>() ?? 0;
        if (0 < port)
        {
          using (var client = new System.Net.Http.HttpClient())
          {
            Diag.OutputMsg($"Posting /api/shutdown for port {port} and pid {pid}");
            var res = await client.PostAsync(
                $"http://localhost:{port}/api/shutdown",
                new System.Net.Http.StringContent("{}", Encoding.UTF8, "application/json")
            );
            Diag.OutputMsg($"Posted /api/shutdown with {res.StatusCode} {res}");
          }
        }
      }
      catch { }
    }

    public static Process StartProcess(string executablePath, OutputWindowPane pane)
    {
      var psi = new ProcessStartInfo
      {
        FileName = executablePath,
        Arguments = $"--no-startup-tests --config \"{SolutionSettingsPath}\" serve --watch --yes --info-file \"{InfoFilePath}\" --port 59241",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = SolutionDir
      };

      Process proc = new() { StartInfo = psi, EnableRaisingEvents = true };

      if (pane != null)
      {
        proc.OutputDataReceived += (s, args) => { if (args.Data != null) { pane.WriteLine($"[OUT] {args.Data}"); } };
        proc.ErrorDataReceived += (s, args) => { if (args.Data != null) { pane.WriteLine($"[ERR] {args.Data}"); } };
      }

      if (!proc.Start())
      {
        return null;
      }

      proc.BeginOutputReadLine();
      proc.BeginErrorReadLine();

      return proc;
    }

    public static async Task<string> FillInTheMiddleAsync(string prefix, string suffix, string fileName, CancellationToken cancellationToken = default)
    {
      /*
        curl -X POST "http://localhost:8590/api/fim" \
          -H "Content-Type: application/json" \
          -d '{
            "prefix": "std::string greet(const std::string &name) { return \"Hello, \"+",
            "suffix": "+\"!\"; }",
            "temperature": 0.0,
            "max_tokens": 64,
            "filename": "inference.cpp",
            "targetapi": "mistral-codestral"
          }'
      */

      if (string.IsNullOrEmpty(InfoFilePath) || !File.Exists(InfoFilePath))
      {
        Diag.OutputMsg("FillInTheMiddleAsync: InfoFilePath is invalid");
        return string.Empty;
      }

      if (string.IsNullOrEmpty(SolutionSettingsPath) || !File.Exists(SolutionSettingsPath))
      {
        Diag.OutputMsg("FillInTheMiddleAsync: SolutionSettingsPath is invalid");
        return string.Empty;
      }

      try
      {
        var info = JObject.Parse(File.ReadAllText(InfoFilePath));
        int port = info["port"]?.Value<int>() ?? 0;

        if (port <= 0)
        {
          Diag.OutputMsg("FillInTheMiddleAsync: port is invalid");
          return string.Empty;
        }

        JObject settings = JObject.Parse(File.ReadAllText(SolutionSettingsPath));
        var currentApi = settings["generation"]?["current_api"]?.ToString();
        if (string.IsNullOrEmpty(currentApi))
        {
          Diag.OutputMsg("FillInTheMiddleAsync: currentApi is invalid");
          return string.Empty;
        }

        using (var client = new System.Net.Http.HttpClient())
        {
          var requestData = new
          {
            prefix = prefix,
            suffix = suffix,
            temperature = 0.0,
            max_tokens = 64,
            filename = fileName,
            targetapi = currentApi
          };

          var jsonContent = JsonConvert.SerializeObject(requestData);
          var content = new System.Net.Http.StringContent(jsonContent, Encoding.UTF8, "application/json");

          var response = await client.PostAsync(
              $"http://localhost:{port}/api/fim",
              content,
              cancellationToken
          );

          if (response.IsSuccessStatusCode)
          {
            var responseJson = await response.Content.ReadAsStringAsync();
            var responseObj = JObject.Parse(responseJson);
            return responseObj["completion"]?.ToString() ?? string.Empty;
          }
          else
          {
            Diag.OutputMsg($"FillInTheMiddleAsync: status {response.StatusCode}, {response.Content}");
          }
        }
      }
      catch (Exception ex)
      {
        Diag.OutputMsg($"FillInTheMiddleAsync error: {ex.Message}");
      }

      return string.Empty;
    }

    private static async Task<string> ProcessCompletionAsync(
      TextReader reader,
      Action<string> onTokenReceived,
      CancellationToken cancellationToken)
    {
      var sb = new StringBuilder();

      string line;
      while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync()) != null)
      {
        if (line.Length == 0 || !line.StartsWith("data:"))
          continue;
        
        var payload = line.Substring(5).Trim();

        if (payload.SequenceEqual("[DONE]"))
          break;

        var obj = JObject.Parse(payload);
        string chunk = obj["content"]?.ToString();

        if (string.IsNullOrEmpty(chunk))
          continue;

        if (chunk.StartsWith("[meta]"))
          continue;

        sb.Append(chunk);

        try
        {
          onTokenReceived?.Invoke(payload.ToString());
        }
        catch { }
      }
      return sb.ToString();
    }

    public static async Task<string> ChatCompletionAsync(
        Action<string> onTokenReceived,
        Collection<ChatMessage> messages,
        List<string> sourceIds = null,
        List<Attachment> attachments = null,
        double temperature = 0.2,
        int maxTokens = 800,
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

        JObject settings = JObject.Parse(File.ReadAllText(SolutionSettingsPath));
        var currentApi = targetApi ?? settings["generation"]?["current_api"]?.ToString();
        if (string.IsNullOrEmpty(currentApi))
        {
          Diag.OutputMsg("ChatCompletionAsync: currentApi is invalid");
          return string.Empty;
        }

        using (var client = new HttpClient())
        {
          var requestData = new
          {
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            sourceids = sourceIds ?? new List<string>(),
            attachments = attachments ?? new List<Attachment>(),
            temperature = temperature,
            max_tokens = maxTokens,
            targetapi = currentApi,
            ctxratio = ctxRatio,
            attachedonly = attachedOnly
          };

          var jsonContent = JsonConvert.SerializeObject(requestData);
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
            return await ProcessCompletionAsync(reader, onTokenReceived, cancellationToken);
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

    public static async Task<string> ChatCompletionStreamAsync(
          Action<string> onTokenReceived,
          Collection<ChatMessage> messages,
          List<string> sourceIds = null,
          List<Attachment> attachments = null,
          double temperature = 0.2,
          int maxTokens = 800,
          string targetApi = null,
          double ctxRatio = 0.5,
          bool attachedOnly = false,
          CancellationToken cancellationToken = default)
    {
      if (string.IsNullOrEmpty(InfoFilePath) || !File.Exists(InfoFilePath))
      {
        Diag.OutputMsg("ChatCompletionStreamAsync: InfoFilePath is invalid");
        return string.Empty;
      }

      if (string.IsNullOrEmpty(SolutionSettingsPath) || !File.Exists(SolutionSettingsPath))
      {
        Diag.OutputMsg("ChatCompletionStreamAsync: SolutionSettingsPath is invalid");
        return string.Empty;
      }

      try
      {
        var info = JObject.Parse(File.ReadAllText(InfoFilePath));
        int port = info["port"]?.Value<int>() ?? 0;

        if (port <= 0)
        {
          Diag.OutputMsg("ChatCompletionStreamAsync: port is invalid");
          return string.Empty;
        }

        JObject settings = JObject.Parse(File.ReadAllText(SolutionSettingsPath));
        var currentApi = targetApi ?? settings["generation"]?["current_api"]?.ToString();
        if (string.IsNullOrEmpty(currentApi))
        {
          Diag.OutputMsg("ChatCompletionStreamAsync: currentApi is invalid");
          return string.Empty;
        }

        using (var client = new System.Net.Http.HttpClient())
        {
          var requestData = new
          {
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            sourceids = sourceIds ?? new List<string>(),
            attachments = attachments ?? new List<Attachment>(),
            temperature = temperature,
            max_tokens = maxTokens,
            targetapi = currentApi,
            ctxratio = ctxRatio,
            attachedonly = attachedOnly
          };

          var jsonContent = JsonConvert.SerializeObject(requestData);

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
          using var stream = await response.Content.ReadAsStreamAsync();
          using var reader = new StreamReader(stream);
          await ProcessCompletionAsync(reader, onTokenReceived, cancellationToken);
        }
      }
      catch (Exception ex)
      {
        Diag.OutputMsg($"ChatCompletionStreamAsync error: {ex.Message}");
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
        return FetchRunningServiceProcess() != null;
      }
    }

    public static async Task<OutputWindowPane> GetPaneAsync() => await VS.Windows.GetOutputWindowPaneAsync(OutputPaneGuid);
  }
}
