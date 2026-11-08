using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;

namespace ChatAssistantVSIX.Utils
{
  internal static class InlineCompletionCommands
  {
    private static InlineCompletionService active;
    public static async Task RequestAsync()
    {
      var document = await VS.Documents.GetActiveDocumentViewAsync();
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      if (document?.TextView == null) return;
      var service = await VS.GetMefServiceAsync<InlineCompletionService>();
      active = service;
      await service.RequestAsync(document.TextView, document.Document?.FilePath);
    }
    public static async Task AcceptAsync()
    {
      if (active == null) return;
      var document = await VS.Documents.GetActiveDocumentViewAsync();
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      if (document?.TextView != null) await active.AcceptAsync(document.TextView);
    }
    public static async Task CancelAsync()
    {
      if (active == null) return;
      var document = await VS.Documents.GetActiveDocumentViewAsync();
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      if (document?.TextView != null) await active.CancelAsync(document.TextView);
    }
    public static async Task CancelAllAsync()
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      if (active != null) await active.CancelAllAsync();
    }
  }

  [Export(typeof(ICommandHandler))]
  [Name("PhenixCode.CancelPendingInlineCompletion")]
  [ContentType("text")]
  internal sealed class CancelInlineCompletionHandler : ICommandHandler<EscapeKeyCommandArgs>
  {
    [Import] internal InlineCompletionService Service = null;
    public string DisplayName => "Cancel pending PhenixCode inline suggestion";
    public CommandState GetCommandState(EscapeKeyCommandArgs args) => Service.IsLoading(args.TextView) ? CommandState.Available : CommandState.Unspecified;
    public bool ExecuteCommand(EscapeKeyCommandArgs args, CommandExecutionContext context)
    {
      if (!Service.IsLoading(args.TextView)) return false;
      Service.CancelAsync((Microsoft.VisualStudio.Text.Editor.IWpfTextView)args.TextView).FireAndForget();
      return !Service.HasCompletion(args.TextView);
    }
  }
}
