using ChatAssistantVSIX.ToolWindows;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.cmdidRejectGhostText)]
  internal sealed class RejectGhostTextCommand : MessengerCommand<RejectGhostTextCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      SendToolWindowMessageFireAndForget("cmdidRejectGhostText");
    }
  }
}
