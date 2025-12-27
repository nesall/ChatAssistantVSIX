global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using System;
global using Task = System.Threading.Tasks.Task;
using ChatAssistantVSIX.Dialogs;
using ChatAssistantVSIX.ToolWindows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ChatAssistantVSIX
{
  [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
  [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
  [ProvideToolWindow(typeof(MyToolWindow.Pane), Style = VsDockStyle.Tabbed, Window = WindowGuids.SolutionExplorer)]
  [ProvideMenuResource("Menus.ctmenu", 1)]
  [Guid(PackageGuids.ChatAssistantVSIXString)]
  [ProvideService(typeof(ToolWindowMessenger), IsAsyncQueryable = true)]
  //[ProvideOptionPage(typeof(IndexerOptionsPage), "PhenixCode Assistant", "General", 0, 0, true)]
  public sealed class ChatAssistantVSIXPackage : ToolkitPackage
  {

    public static Guid OutputPaneGuid { get; private set; }
    public static string SolutionSettingsPath { get; private set; }
    public static string InfoFilePath { get; private set; }
    public static string SolutionDir { get; private set; }

    /// <summary>
    /// SettingsPath stores universal settings only (i.e. no solution/project specific info like paths)
    /// </summary>
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhenixCode", "settings.json");

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
      AddService(typeof(ToolWindowMessenger), (_, _, _) => Task.FromResult<object>(new ToolWindowMessenger()));

      await this.RegisterCommandsAsync();

      this.RegisterToolWindows();

      await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

      InitGlobalSettingsFile();
      VS.Events.SolutionEvents.OnAfterCloseSolution += OnAfterCloseSolution; // triggeres package load error
      VS.Events.SolutionEvents.OnAfterOpenSolution += OnAfterOpenSolution;
      Debug.WriteLine("Creating Output Window Pane 'PhenixCode Service'");
      var pane = await VS.Windows.CreateOutputWindowPaneAsync("PhenixCode Service", false);
      OutputPaneGuid = pane.Guid;
    }

    private void OnAfterOpenSolution(Solution solution)
    {
      Debug.Assert(solution != null);
      string solutionDir = Path.GetDirectoryName(solution.FullPath);
      string workDir = Path.Combine(solutionDir, ".phenixcode");
      SolutionSettingsPath = Path.Combine(workDir, "settings.json");
      InfoFilePath = Path.Combine(workDir, "info.json");
      SolutionDir = solutionDir.Replace('\\', '/');
      if (!Directory.Exists(workDir))
      {
        Directory.CreateDirectory(workDir);
        File.Copy(SettingsPath, SolutionSettingsPath, true);
      }
    }

    private void OnAfterCloseSolution()
    {
      ThreadHelper.JoinableTaskFactory
          .RunAsync(() => SettingsDialog.ShutdownServiceAsync())
          .FireAndForget();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        // Unsubscribe to prevent memory leaks
        VS.Events.SolutionEvents.OnAfterCloseSolution -= OnAfterCloseSolution;

        // Final safety check: Kill if VS is closing entirely
        OnAfterCloseSolution();
      }
      base.Dispose(disposing);
    }


    public static bool IsServiceRunning
    {
      get
      {
        if (string.IsNullOrEmpty(InfoFilePath) || !File.Exists(InfoFilePath))
          return false;

        try
        {
          var info = JObject.Parse(File.ReadAllText(InfoFilePath));
          int pid = info["pid"]?.Value<int>() ?? 0;

          if (pid <= 0)
            return false;

          var exec = info["exec"]?.ToString() ?? "";

          // Optional: Check process name matches expected service
          var process = Process.GetProcessById(pid);
          if (process != null && !process.HasExited)
          {
            string path = process.MainModule.FileName;
            path = path.Replace('\\', '/').ToLower();
            exec = exec.Replace('\\', '/').ToLower();
            return path == exec;
          }
          return false;
        }
        catch (FileNotFoundException)
        {
          return false;
        }
        catch (JsonException)
        {
          return false;
        }
        catch (ArgumentException) // Process.GetProcessById throws this if process doesn't exist
        {
          return false;
        }
        catch (InvalidOperationException) // Process has exited
        {
          return false;
        }
        catch
        {
          // Log unexpected errors if needed
          return false;
        }
      }
    }

    private bool InitGlobalSettingsFile()
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
  }
}