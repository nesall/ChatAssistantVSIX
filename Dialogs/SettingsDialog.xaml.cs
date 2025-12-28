using ChatAssistantVSIX.Utils;
using Microsoft.VisualStudio.PlatformUI;
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
using System.Threading.Tasks;
using System.Windows;

namespace ChatAssistantVSIX.Dialogs
{
  public partial class SettingsDialog : DialogWindow, INotifyPropertyChanged
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
    public bool HasOpenSolution { get => !string.IsNullOrEmpty(PhenixCodeCoreService.SolutionSettingsPath); }
    public string ServiceStartStopButtonText { get; private set; }
    public bool ServiceButtonEnabled { get; private set; } = true;

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
      UpdateServiceButton(null);
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

    private static JObject ReadJson()
    {
      try
      {
        return JObject.Parse(File.ReadAllText(PhenixCodeCoreService.SolutionSettingsPath));
      }
      catch
      {
        // Solution not open, so we use global settings file.
      }
      return JObject.Parse(File.ReadAllText(PhenixCodeCoreService.SettingsPath));
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
      if (File.Exists(PhenixCodeCoreService.SolutionSettingsPath))
      {
        configJson_["_exec"] = ExecutablePath;
        SyncProjectsToJObject();
        File.WriteAllText(PhenixCodeCoreService.SolutionSettingsPath, configJson_.ToString(Formatting.Indented));
      }
      else
      {
        File.WriteAllText(PhenixCodeCoreService.SettingsPath, configJson_.ToString(Formatting.Indented));
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

      if (!HasOpenSolution) { ShowServiceMessage("Please open a solution before restarting the service."); return; }
      if (!File.Exists(ExecutablePath)) { ShowServiceMessage("Executable path is invalid."); return; }

      _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
          ShowServiceMessage("");

          if (PhenixCodeCoreService.IsServiceRunning)
          {
            UpdateServiceButton("Stopping...");
            ShowServiceMessage("Attempting to shutdown the running service...");
            await PhenixCodeCoreService.ShutdownServiceAsync();
            ShowServiceMessage("Service stop request posted");
            var stopwatch = Stopwatch.StartNew();
            while (PhenixCodeCoreService.IsServiceRunning && stopwatch.Elapsed < TimeSpan.FromSeconds(12))
            {
              await Task.Delay(200);
              ShowServiceMessage($"Waiting for service to stop... ({stopwatch.Elapsed.TotalSeconds:0.0}s)");
            }

            if (PhenixCodeCoreService.IsServiceRunning)
            {
              ShowServiceMessage("Service failed to stop within timeout period");
            }
            else
            {
              ShowServiceMessage("Service stopped successfully");
            }
            UpdateServiceButton(null);
            return;
          }

          UpdateServiceButton("Starting...");
          var pane = await PhenixCodeCoreService.GetPaneAsync();

          // Switching to background thread.
          await TaskScheduler.Default;

          ShowServiceMessage("Starting service process...");
          var proc = PhenixCodeCoreService.StartProcess(ExecutablePath, pane);

          await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
          if (proc != null)
          {
            ShowServiceMessage("Service started successfully.");
            await pane.ActivateAsync();
            await pane.WriteLineAsync($"[System] Service started with PID {proc.Id}");
          }
          else
          {
            ShowServiceMessage("Failed to start the service process.");
            return;
          }

          var stopwatch2 = Stopwatch.StartNew();
          while (!PhenixCodeCoreService.IsServiceRunning && stopwatch2.Elapsed < TimeSpan.FromSeconds(6))
          {
            await Task.Delay(200);
          }
          UpdateServiceButton(null);
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
        if (File.Exists(PhenixCodeCoreService.SolutionSettingsPath))
        {
          Debug.WriteLine("Opening Solution settings file " + PhenixCodeCoreService.SolutionSettingsPath);
          dte?.ItemOperations.OpenFile(PhenixCodeCoreService.SolutionSettingsPath);
        }
        else
        {
          Debug.WriteLine("Opening global settings file " + PhenixCodeCoreService.SettingsPath);
          dte?.ItemOperations.OpenFile(PhenixCodeCoreService.SettingsPath);
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

    private void UpdateServiceButton(string tempText)
    {
      ServiceButtonEnabled = true;
      if (!string.IsNullOrEmpty(tempText))
      {
        ServiceButtonEnabled = false;
        ServiceStartStopButtonText = tempText;
      }
      else
      {
        ServiceStartStopButtonText = PhenixCodeCoreService.IsServiceRunning ? "Stop service" : "Start service";
      }
      OnPropertyChanged(nameof(ServiceStartStopButtonText));
      OnPropertyChanged(nameof(ServiceButtonEnabled));
    }
  }
}