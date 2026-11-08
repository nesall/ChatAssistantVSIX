using ChatAssistantVSIX.Utils;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.cmdidRejectGhostText)]
  internal sealed class RejectGhostTextCommand : BaseCommand<RejectGhostTextCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      await InlineCompletionCommands.CancelAsync();
    }
  }
}
