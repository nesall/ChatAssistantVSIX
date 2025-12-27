using ChatAssistantVSIX.Dialogs;
using ChatAssistantVSIX.ToolWindows;
using System.Windows.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.ButtonSettingsCommand)]
  internal sealed class ButtonSettingsCommand : BaseCommand<ButtonSettingsCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

      var dlg = new SettingsDialog();

      var hwnd = Process.GetCurrentProcess().MainWindowHandle;
      new WindowInteropHelper(dlg) { Owner = hwnd };

      dlg.ShowDialog();
    }
  }
}
