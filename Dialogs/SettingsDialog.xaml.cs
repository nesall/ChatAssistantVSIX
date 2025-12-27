using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace ChatAssistantVSIX.Dialogs
{
  public partial class SettingsDialog : Window, INotifyPropertyChanged
  {
    public class ProjectItem : INotifyPropertyChanged
    {
      private bool isSelected_;
      public string Name { get; set; }
      public string Path { get; set; }
      public bool IsSelected
      {
        get => isSelected_;
        set { isSelected_ = value; OnPropertyChanged(); }
      }
      public event PropertyChangedEventHandler PropertyChanged;
      protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public ObservableCollection<ProjectItem> ProjectList { get; set; } = new ObservableCollection<ProjectItem>();

    private JObject configJson_;
    private string selectedEmbeddingApi_;
    private string selectedGenerationApi_;

    public event PropertyChangedEventHandler PropertyChanged;

    // Properties
    public string ExcludePatterns { get; set; }
    public string DefaultExtensions { get; set; }
    public string EmbeddingUrl { get; set; }
    public string EmbeddingKey { get; set; }
    public string EmbeddingModel { get; set; }
    public string GenerationUrl { get; set; }
    public string GenerationKey { get; set; }
    public string GenerationModel { get; set; }
    public int ChunkMinTokens { get; set; }
    public int ChunkMaxTokens { get; set; }
    public double ChunkOverlapPercent { get; set; }
    public string ExecutablePath { get; set; }

    public List<string> EmbeddingChoices { get; set; } = new();
    public List<string> GenerationChoices { get; set; } = new();

    public string ServiceMessage { get; set; }
    public bool HasOpenSolution { get => !string.IsNullOrEmpty(ChatAssistantVSIXPackage.SolutionSettingsPath); }
    public string ServiceStartStopButtonText { get; private set; }

    public SettingsDialog()
    {
      InitializeComponent();
      LoadSettings();
      DataContext = this;
      _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        await LoadProjectsAsync();
      });
      UpdateServiceButtonText();
    }

    public string SelectedEmbeddingApi
    {
      get => selectedEmbeddingApi_;
      set { selectedEmbeddingApi_ = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsEmbeddingCustom)); AutoFillApi("embedding", value); }
    }

    public string SelectedGenerationApi
    {
      get => selectedGenerationApi_;
      set { selectedGenerationApi_ = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsGenerationCustom)); AutoFillApi("generation", value); }
    }

    public bool IsEmbeddingCustom => SelectedEmbeddingApi == "custom";
    public bool IsGenerationCustom => SelectedGenerationApi == "custom";

    public static async Task ShutdownServiceAsync()
    {
      int port = 0;
      int pid = 0;
      try
      {
        var info = JObject.Parse(File.ReadAllText(ChatAssistantVSIXPackage.InfoFilePath));
        port = info["port"]?.Value<int>() ?? 0;
        pid = info["pid"]?.Value<int>() ?? 0;
        if (0 < port)
        {
          using (var client = new System.Net.Http.HttpClient())
          {
            Debug.WriteLine($"Posting /api/shutdown for port {port} and pid {pid}");
            var res = await client.PostAsync(
                $"http://localhost:{port}/api/shutdown",
                new System.Net.Http.StringContent("{}", Encoding.UTF8, "application/json")
            );
            Debug.WriteLine($"Posted /api/shutdown with {res.StatusCode} {res}");
          }
        }
      }
      catch { }
    }

    private static JObject ReadJson()
    {
      try
      {
        return JObject.Parse(File.ReadAllText(ChatAssistantVSIXPackage.SolutionSettingsPath));
      }
      catch
      {
        // Solution not open, so we use global settings file.
      }
      return JObject.Parse(File.ReadAllText(ChatAssistantVSIXPackage.SettingsPath));
    }

    private void LoadSettings()
    {
      try
      {
        configJson_ = ReadJson();

        // Map Folders
        var src = configJson_["source"];
        ExcludePatterns = string.Join(";", src["global_exclude"].ToObject<string[]>());
        DefaultExtensions = string.Join(";", src["default_extensions"].ToObject<string[]>());

        // API Choices
        EmbeddingChoices = configJson_["embedding"]?["apis"]?.Select(a => a["id"].ToString()).ToList() ?? new List<string>();
        GenerationChoices = configJson_["generation"]?["apis"]?.Select(a => a["id"].ToString()).ToList() ?? new List<string>();

        SelectedEmbeddingApi = configJson_["embedding"]?["current_api"]?.ToString();
        SelectedGenerationApi = configJson_["generation"]?["current_api"]?.ToString();

        // Chunking
        var chk = configJson_["chunking"];
        ChunkMinTokens = chk["nof_min_tokens"]?.Value<int>() ?? 50;
        ChunkMaxTokens = chk["nof_max_tokens"]?.Value<int>() ?? 450;
        ChunkOverlapPercent = chk["overlap_percentage"]?.Value<double>() ?? 0.2;

        ExecutablePath = configJson_["_exec"]?.Value<string>() ?? "";

        OnPropertyChanged(string.Empty); // Refresh all bindings
      }
      catch (Exception ex) { VS.MessageBox.Show($"Load failed: {ex.Message}"); }
    }

    private void AutoFillApi(string section, string apiId)
    {
      var api = configJson_[section]?["apis"]?.FirstOrDefault(a => a["id"]?.Value<string>() == apiId);
      if (api == null) return;

      if (section == "embedding")
      {
        EmbeddingUrl = api["api_url"]?.ToString();
        EmbeddingKey = api["api_key"]?.ToString();
        EmbeddingModel = api["model"]?.ToString();
        OnPropertyChanged(nameof(EmbeddingUrl)); OnPropertyChanged(nameof(EmbeddingModel));
      }
      else
      {
        GenerationUrl = api["api_url"]?.ToString();
        GenerationKey = api["api_key"]?.ToString();
        GenerationModel = api["model"]?.ToString();
        OnPropertyChanged(nameof(GenerationUrl)); OnPropertyChanged(nameof(GenerationModel));
      }
    }
    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
      configJson_["source"]["global_exclude"] = new JArray(ExcludePatterns.Split([';'], StringSplitOptions.RemoveEmptyEntries));
      configJson_["source"]["default_extensions"] = new JArray(DefaultExtensions.Split([';'], StringSplitOptions.RemoveEmptyEntries));
      configJson_["embedding"]["current_api"] = SelectedEmbeddingApi;
      configJson_["generation"]["current_api"] = SelectedGenerationApi;

      var chk = configJson_["chunking"];
      chk["nof_min_tokens"] = ChunkMinTokens;
      chk["nof_max_tokens"] = ChunkMaxTokens;
      chk["overlap_percentage"] = ChunkOverlapPercent;

      SyncCustomApiBlock("embedding", EmbeddingUrl, EmbeddingKey, EmbeddingModel);
      SyncCustomApiBlock("generation", GenerationUrl, GenerationKey, GenerationModel);

      configJson_["source"]["paths"] = new JArray();
      configJson_.Remove("_exec");
      if (File.Exists(ChatAssistantVSIXPackage.SolutionSettingsPath))
      {
        configJson_["_exec"] = ExecutablePath;
        SyncProjectsToJObject();
        File.WriteAllText(ChatAssistantVSIXPackage.SolutionSettingsPath, configJson_.ToString(Formatting.Indented));
      }
      else
      {
        File.WriteAllText(ChatAssistantVSIXPackage.SettingsPath, configJson_.ToString(Formatting.Indented));
      }
      this.DialogResult = true;
    }

    private void SyncCustomApiBlock(string section, string url, string key, string model)
    {
      var apis = configJson_[section]?["apis"] as JArray;
      var customApi = apis?.FirstOrDefault(a => a["id"]?.ToString() == "custom") as JObject;
      if (customApi != null)
      {
        customApi["api_url"] = url;
        customApi["api_key"] = key;
        customApi["model"] = model;
      }
    }

    private void SyncProjectsToJObject()
    {
      var pathsArray = new JArray();

      foreach (var item in ProjectList.Where(p => p.IsSelected))
      {
        pathsArray.Add(new JObject
        {
          ["path"] = item.Path,
          ["recursive"] = true,
          ["type"] = "directory",
          ["exclude"] = new JArray(),
          ["extensions"] = new JArray()
        });
      }

      configJson_["source"]["paths"] = pathsArray;
    }

    private void OnStartStopService(object sender, RoutedEventArgs e)
    {
      ThreadHelper.ThrowIfNotOnUIThread();

      if (!Directory.Exists(ChatAssistantVSIXPackage.SolutionDir)) { ShowServiceMessage("Please open a solution before restarting the service."); return; }
      if (!File.Exists(ExecutablePath)) { ShowServiceMessage("Executable path is invalid."); return; }

      _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
          ShowServiceMessage("");

          if (ChatAssistantVSIXPackage.IsServiceRunning)
          {
            ShowServiceMessage("Attempting to shutdown the running service...");
            await ShutdownServiceAsync();
            ShowServiceMessage("Service stop request posted");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (ChatAssistantVSIXPackage.IsServiceRunning && stopwatch.Elapsed < TimeSpan.FromSeconds(12))
            {
              await Task.Delay(200);
              ShowServiceMessage($"Waiting for service to stop... ({stopwatch.Elapsed.TotalSeconds:0.0}s)");
            }

            if (ChatAssistantVSIXPackage.IsServiceRunning)
            {
              ShowServiceMessage("Service failed to stop within timeout period");
            }
            else
            {
              ShowServiceMessage("Service stopped successfully");
            }
            UpdateServiceButtonText();
            return;
          }

          var pane = await VS.Windows.GetOutputWindowPaneAsync(ChatAssistantVSIXPackage.OutputPaneGuid);

          // Switching to background thread.
          await TaskScheduler.Default;

          ShowServiceMessage("Initializing...");
          var psi = new ProcessStartInfo
          {
            FileName = ExecutablePath,
            Arguments = $"--no-startup-tests --config \"{ChatAssistantVSIXPackage.SolutionSettingsPath}\" serve --watch --yes --info-file \"{ChatAssistantVSIXPackage.InfoFilePath}\" --port 59241",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = ChatAssistantVSIXPackage.SolutionDir
          };

          Process proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

          proc.OutputDataReceived += (s, args) => { if (args.Data != null) { pane.WriteLine($"[OUT] {args.Data}"); } };
          proc.ErrorDataReceived += (s, args) => { if (args.Data != null) { pane.WriteLine($"[ERR] {args.Data}"); } };

          ShowServiceMessage("Starting service process...");
          if (proc.Start())
          {
            ShowServiceMessage("Service started successfully.");
          }
          else
          {
            ShowServiceMessage("Failed to start the service process.");
            return;
          }

          proc.BeginOutputReadLine();
          proc.BeginErrorReadLine();

          await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
          await pane.ActivateAsync();
          await pane.WriteLineAsync($"[System] Service started with PID {proc.Id}");

          var stopwatch2 = System.Diagnostics.Stopwatch.StartNew();
          while (!ChatAssistantVSIXPackage.IsServiceRunning && stopwatch2.Elapsed < TimeSpan.FromSeconds(6))
          {
            await Task.Delay(200);
          }
          UpdateServiceButtonText();
        }
        catch (Exception ex)
        {
          await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
          ShowServiceMessage($"Failed to restart service: {ex.Message}");
        }
      });
    }

    private async Task LoadProjectsAsync()
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

      var projects = await VS.Solutions.GetAllProjectsAsync();
      var currentPaths = GetExistingPathsFromConfig(); // Helper to read current json paths

      ProjectList.Clear();
      foreach (var project in projects)
      {
        if (string.IsNullOrEmpty(project.FullPath)) continue;

        string dir = System.IO.Path.GetDirectoryName(project.FullPath);
        ProjectList.Add(new ProjectItem
        {
          Name = project.Name,
          Path = dir,
          // Automatically check it if it's already in the settings.json
          IsSelected = currentPaths.Contains(dir, StringComparer.OrdinalIgnoreCase)
        });
      }
    }

    private string[] GetExistingPathsFromConfig()
    {
      try
      {
        var paths = configJson_["source"]?["paths"] as JArray;
        if (paths == null) return [];

        return paths.Select(p => p["path"]?.Value<string>())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToArray();
      }
      catch
      {
        return [];
      }
    }

    private void OnOpenJsonClick(object sender, RoutedEventArgs e)
    {
      ThreadHelper.JoinableTaskFactory.Run(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        if (File.Exists(ChatAssistantVSIXPackage.SolutionSettingsPath))
        {
          Debug.WriteLine("Opening Solution settings file");
          dte?.ItemOperations.OpenFile(ChatAssistantVSIXPackage.SolutionSettingsPath);
        }
        else
        {
          Debug.WriteLine("Opening global settings file");
          dte?.ItemOperations.OpenFile(ChatAssistantVSIXPackage.SettingsPath);
        }
      });
    }

    protected void OnPropertyChanged([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void ShowServiceMessage(string s)
    {
      ServiceMessage = s;
      OnPropertyChanged(nameof(ServiceMessage));
    }

    private void UpdateServiceButtonText()
    {
      ServiceStartStopButtonText = ChatAssistantVSIXPackage.IsServiceRunning ? "Stop service" : "Start service";
      OnPropertyChanged(nameof(ServiceStartStopButtonText));
    }
  }
}