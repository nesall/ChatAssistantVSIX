using ChatAssistantVSIX.Dialogs;
using System.Diagnostics;
using System.Windows.Interop;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.cmdidButtonStatsCommand)]
  internal sealed class ButtonStatsCommand : BaseCommand<ButtonStatsCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      var dlg = new StatsDialog();
      var hwnd = Process.GetCurrentProcess().MainWindowHandle;
      new WindowInteropHelper(dlg) { Owner = hwnd };
      dlg.ShowDialog();
    }
  }
}
