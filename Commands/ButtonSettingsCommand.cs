using ChatAssistantVSIX.Dialogs;
using System.Diagnostics;
using System.Windows.Interop;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.ButtonSettingsCommand)]
  internal sealed class ButtonSettingsCommand : BaseCommand<ButtonSettingsCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

      var dlg = new SettingsDialog();

      //var hwnd = Process.GetCurrentProcess().MainWindowHandle;
      //new WindowInteropHelper(dlg) { Owner = hwnd };

      dlg.ShowDialog();
    }
  }
}
