using ChatAssistantVSIX.Utils;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.ButtonInsertCommand)]
  internal sealed class ButtonInsertCommand : BaseCommand<ButtonInsertCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      await InlineCompletionCommands.RequestAsync();
    }
  }
}
