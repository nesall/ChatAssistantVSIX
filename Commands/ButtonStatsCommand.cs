using ChatAssistantVSIX.Dialogs;
using ChatAssistantVSIX.ToolWindows;
using System.Windows.Interop;
using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.cmdidButtonStatsCommand)]
  internal sealed class ButtonStatsCommand : MessengerCommand<ButtonStatsCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

      var dlg = new StatsDialog();

      // Set Visual Studio as owner so the dialog stays on top and behaves modally.
      // Using the current process main window handle (devenv) works inside a VSIX.
      var hwnd = Process.GetCurrentProcess().MainWindowHandle;
      new WindowInteropHelper(dlg) { Owner = hwnd };

      bool? result = dlg.ShowDialog();
      if (result == true)
      {
        // Read values and persist as you need
        var apiKey = dlg.ApiKey;
        var enableFeature = dlg.EnableFeature;

        // Example: persist using Properties.Settings (if you have them),
        // or use WritableSettingsStore, or implement a VS Tools->Options page.
        // Properties.Settings.Default.ApiKey = apiKey;
        // Properties.Settings.Default.EnableFeature = enableFeature;
        // Properties.Settings.Default.Save();
      }
      else
      {
        // user cancelled
      }
    }
  }
}
