using ChatAssistantVSIX.Utils;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell.Interop;
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
using Microsoft.Build.Evaluation;


namespace ChatAssistantVSIX.Dialogs
{
  public partial class SettingsDialog : DialogWindow, INotifyPropertyChanged
  {
    public class ProjectItem : INotifyPropertyChanged
    {
      private bool isSelected_;
      public string Name { get; set; }
      public List<string> Dirs { get; set; }
      public List<string> Files { get; set; }
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
    private string selectedEmbeddingApi_ = "custom";
    private string selectedGenerationApi_ = "custom";

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
    public bool HasSolutionInitialized { get => !string.IsNullOrEmpty(PhenixCodeCoreService.SolutionSettingsPath); }
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

        if (!HasSolutionInitialized)
        {
          var solution = await VS.Solutions.GetCurrentSolutionAsync();
          if (solution != null)
          {
            // May happen when VS is open directly from solution file.
            PhenixCodeCoreService.InitOnSolutionReady(solution);
            Debug.Assert(HasSolutionInitialized);
          }
        }

        await LoadProjectsAsync();
      });
      UpdateServiceButton(null);
    }

    public string SelectedEmbeddingApi
    {
      get => selectedEmbeddingApi_;
      set { 
        selectedEmbeddingApi_ = value; 
        OnPropertyChanged(); 
        OnPropertyChanged(nameof(IsEmbeddingCustom)); 
        AutoFillApi("embedding", value);
      }
    }

    public string SelectedGenerationApi
    {
      get => selectedGenerationApi_;
      set { 
        selectedGenerationApi_ = value; 
        OnPropertyChanged(); 
        OnPropertyChanged(nameof(IsGenerationCustom)); 
        AutoFillApi("generation", value);
      }
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
        OnPropertyChanged(nameof(EmbeddingUrl));
        OnPropertyChanged(nameof(EmbeddingKey));
        OnPropertyChanged(nameof(EmbeddingModel));
      }
      else
      {
        GenerationUrl = api["api_url"]?.ToString();
        GenerationKey = api["api_key"]?.ToString();
        GenerationModel = api["model"]?.ToString();
        OnPropertyChanged(nameof(GenerationUrl));
        OnPropertyChanged(nameof(GenerationKey));
        OnPropertyChanged(nameof(GenerationModel));
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

      UpdateApiConfigBlock("embedding", EmbeddingUrl, EmbeddingKey, EmbeddingModel, selectedEmbeddingApi_);
      UpdateApiConfigBlock("generation", GenerationUrl, GenerationKey, GenerationModel, selectedGenerationApi_);

      configJson_["source"]["paths"] = new JArray();
      configJson_.Remove("_exec");
      if (File.Exists(PhenixCodeCoreService.SolutionSettingsPath))
      {
        configJson_["_exec"] = ExecutablePath;
        SyncProjectsToJObject();
        PhenixCodeCoreService.SaveToSolutionSettings(configJson_.ToString(Formatting.Indented));
      }
      else
      {
        File.WriteAllText(PhenixCodeCoreService.SettingsPath, configJson_.ToString(Formatting.Indented));
      }
      this.DialogResult = true;
    }

    private void UpdateApiConfigBlock(string section, string url, string key, string model, string apiId)
    {
      var apis = configJson_[section]?["apis"] as JArray;
      var api = apis?.FirstOrDefault(a => a["id"]?.ToString() == apiId) as JObject;
      if (api != null)
      {
        api["api_url"] = url;
        api["api_key"] = key;
        api["model"] = model;
      }
    }

    private void SyncProjectsToJObject()
    {
      var pathsArray = new JArray();

      foreach (var item in ProjectList.Where(p => p.IsSelected))
      {
        foreach (var dir in item.Dirs)
        {
          pathsArray.Add(new JObject
          {
            ["path"] = dir,
            ["recursive"] = true,
            ["type"] = "directory",
            ["exclude"] = new JArray(),
            ["extensions"] = new JArray()
          });
        }
      }

      configJson_["source"]["paths"] = pathsArray;
    }

    private void OnStartStopService(object sender, RoutedEventArgs e)
    {
      ThreadHelper.ThrowIfNotOnUIThread();

      if (!HasSolutionInitialized) { ShowServiceMessage("Please open a solution before restarting the service."); return; }
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
      string[] currentPaths = GetExistingPathsFromConfig(); // Helper to read current json paths
      var set = new HashSet<string>(currentPaths, StringComparer.OrdinalIgnoreCase);

      ProjectList.Clear();
      foreach (var project in projects)
      {
        if (string.IsNullOrEmpty(project.FullPath)) continue;

        var projectFiles = new List<string>();
        GetProjectFiles(project.FullPath, projectFiles);
        if (0 < projectFiles.Count)
        {
          var dirs = PathUtils.CommonPaths(projectFiles, 2);
          Debug.Assert(0 < dirs.Count);
          ProjectList.Add(new ProjectItem
          {
            Name = project.Name,
            Dirs = dirs,
            Files = projectFiles,
            // Automatically check it if it's already in the settings.json
            IsSelected = dirs.Any(set.Contains)
          });
        }
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

    private void GetProjectFiles(string projectPath_, List<string> fileList_)
    {
      try
      {
        // Use the global ProjectCollection or create a private one to avoid leaks
        var project = ProjectCollection.GlobalProjectCollection.LoadProject(projectPath_);

        // C# uses "Compile", C++ uses "ClCompile" and "ClInclude"
        var itemTypes = new[] { "Compile", "ClCompile", "ClInclude", "None", "Content" };

        foreach (var item in project.AllEvaluatedItems)
        {
          if (itemTypes.Contains(item.ItemType))
          {
            // EvaluatedInclude handles the path as it appears in the XML
            string fullPath = Path.IsPathRooted(item.EvaluatedInclude)
                ? item.EvaluatedInclude
                : Path.Combine(Path.GetDirectoryName(projectPath_), item.EvaluatedInclude);

            if (File.Exists(fullPath))
            {
              fileList_.Add(fullPath);
            }
          }
        }

        // Unload to prevent file locking
        ProjectCollection.GlobalProjectCollection.UnloadProject(project);
      }
      catch (Exception ex)
      {
        Diag.OutputMsg("GetProjectFiles: " + ex.Message);
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
          Diag.OutputMsg("Opening Solution settings file " + PhenixCodeCoreService.SolutionSettingsPath);
          dte?.ItemOperations.OpenFile(PhenixCodeCoreService.SolutionSettingsPath);
        }
        else
        {
          Diag.OutputMsg("Opening global settings file " + PhenixCodeCoreService.SettingsPath);
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