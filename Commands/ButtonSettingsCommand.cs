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
      await VS.MessageBox.ShowAsync(
        "Settings are in the global Options page.",
        "Tools -> Options -> PhenixCode Assistant",
        icon: OLEMSGICON.OLEMSGICON_INFO,
        buttons: OLEMSGBUTTON.OLEMSGBUTTON_OK
        );
    }
  }
}
