using ChatAssistantVSIX.Utils;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.cmdidAcceptGhostText)]
  internal sealed class AcceptGhostTextCommand : BaseCommand<AcceptGhostTextCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      await InlineCompletionCommands.AcceptAsync();
    }
  }
}
