global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
global using System;
global using Task = System.Threading.Tasks.Task;
using ChatAssistantVSIX.Options;
using ChatAssistantVSIX.ToolWindows;
using Microsoft.VisualStudio.Text.Editor;
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
  [ProvideOptionPage(typeof(IndexerOptionsPage), "PhenixCode Assistant", "General", 0, 0, true)]
  public sealed class ChatAssistantVSIXPackage : ToolkitPackage
  {
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
      AddService(typeof(ToolWindowMessenger), (_, _, _) => Task.FromResult<object>(new ToolWindowMessenger()));

      await this.RegisterCommandsAsync();

      this.RegisterToolWindows();
    }
  }
}