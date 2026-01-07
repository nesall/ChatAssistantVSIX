global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using System;
global using Task = System.Threading.Tasks.Task;
using ChatAssistantVSIX.ToolWindows;
using ChatAssistantVSIX.Utils;
using System.Runtime.InteropServices;
using System.Threading;

namespace ChatAssistantVSIX
{
  [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
  [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
  [ProvideToolWindow(typeof(MyToolWindow.Pane), Style = VsDockStyle.Tabbed, Window = WindowGuids.SolutionExplorer)]
  [ProvideMenuResource("Menus.ctmenu", 1)]
  [Guid(PackageGuids.ChatAssistantVSIXString)]
  [ProvideService(typeof(ToolWindowMessenger), IsAsyncQueryable = true)]
  public sealed class ChatAssistantVSIXPackage : ToolkitPackage
  {
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
      AddService(typeof(ToolWindowMessenger), (_, _, _) => Task.FromResult<object>(new ToolWindowMessenger()));

      await this.RegisterCommandsAsync();

      this.RegisterToolWindows();

      await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

      VS.Events.SolutionEvents.OnAfterCloseSolution += OnAfterCloseSolution; // triggeres package load error
      VS.Events.SolutionEvents.OnAfterOpenSolution += OnAfterOpenSolution;
      await PhenixCodeCoreService.InitAsync();
    }

    private void OnAfterOpenSolution(Solution solution)
    {
      PhenixCodeCoreService.InitOnSolutionReady(solution);
    }

    private void OnAfterCloseSolution()
    {
      ThreadHelper.JoinableTaskFactory
          .RunAsync(() => PhenixCodeCoreService.ShutdownServiceAsync())
          .FireAndForget();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        VS.Events.SolutionEvents.OnAfterCloseSolution -= OnAfterCloseSolution;

        // Final safety check: Kill if VS is closing entirely
        OnAfterCloseSolution();
      }
      base.Dispose(disposing);
    }
  }
}