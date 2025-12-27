using ChatAssistantVSIX.ToolWindows;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.cmdidButtonClearChat)]
  internal sealed class ButtonClearChatCommand : MessengerCommand<ButtonClearChatCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      SendToolWindowMessageFireAndForget("cmdidButtonClearChat");
    }
  }
}
