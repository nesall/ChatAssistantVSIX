using ChatAssistantVSIX.ToolWindows;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.cmdidAcceptGhostText)]
  internal sealed class AcceptGhostTextCommand : MessengerCommand<AcceptGhostTextCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      SendToolWindowMessageFireAndForget("cmdidAcceptGhostText");
    }
  }
}
