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
      // The package can load after Visual Studio has already restored a solution.
      var solution = await VS.Solutions.GetCurrentSolutionAsync();
      if (solution != null && string.IsNullOrEmpty(PhenixCodeCoreService.SolutionSettingsPath))
        PhenixCodeCoreService.InitOnSolutionReady(solution);
    }

    private void OnAfterOpenSolution(Solution solution)
    {
      InlineCompletionCommands.CancelAllAsync().FireAndForget();
      PhenixCodeCoreService.InitOnSolutionReady(solution);
    }

    private void OnAfterCloseSolution()
    {
      ThreadHelper.JoinableTaskFactory
          .RunAsync(async () => { await InlineCompletionCommands.CancelAllAsync(); await PhenixCodeCoreService.CloseSolutionAsync(); })
          .FireAndForget();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        VS.Events.SolutionEvents.OnAfterCloseSolution -= OnAfterCloseSolution;
        VS.Events.SolutionEvents.OnAfterOpenSolution -= OnAfterOpenSolution;

        // Await any pending solution shutdown before Visual Studio tears down the package.
        JoinableTaskFactory.Run(() => PhenixCodeCoreService.CloseSolutionAsync());
      }
      base.Dispose(disposing);
    }
  }
}
