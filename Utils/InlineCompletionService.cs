using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ChatAssistantVSIX.Utils
{
  [Export]
  internal sealed class InlineCompletionService
  {
    [Import(AllowDefault = true)] internal SuggestionServiceBase Suggestions = null;
    [Import] internal ICompletionBroker Completion = null;
    [Import] internal IAsyncCompletionBroker AsyncCompletion = null;
    private readonly InlineCompletionSession requests = new InlineCompletionSession();

    public Task RequestAsync(IWpfTextView view, string file)
      => view.Properties.GetOrCreateSingletonProperty(() => new Controller(this, view)).RequestAsync(file);

    public Task AcceptAsync(IWpfTextView view)
      => view.Properties.TryGetProperty<Controller>(typeof(Controller), out var owner) ? owner.AcceptAsync() : Task.CompletedTask;

    public Task CancelAsync(IWpfTextView view)
      => view.Properties.TryGetProperty<Controller>(typeof(Controller), out var owner) ? owner.CancelAsync() : Task.CompletedTask;

    public Task CancelAllAsync()
      => (requests.Current?.Owner as Controller)?.CancelAsync() ?? Task.CompletedTask;

    internal bool IsLoading(ITextView view)
      => requests.Current?.Owner is Controller owner && owner.View == view && !requests.Current.Visible;

    internal bool HasCompletion(ITextView view) => Completion.IsCompletionActive(view) || AsyncCompletion.IsCompletionActive(view);

    private static void Report(string message)
    {
      Diag.OutputMsg("[System] Inline suggestions: " + message);
      VS.StatusBar.ShowMessageAsync("PhenixCode: " + message).FireAndForget();
    }

    private sealed class Controller
    {
      private readonly InlineCompletionService host;
      public IWpfTextView View { get; }
      private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
      private JoinableTask<SuggestionManagerBase> registration;
      private SuggestionManagerBase manager;
      private SuggestionSessionBase display;
      private InlineCompletionSession.Request displayedRequest;
      private InlineCompletionProgress progress;
      private bool closed;

      public Controller(InlineCompletionService host, IWpfTextView view)
      {
        this.host = host; View = view;
        View.TextBuffer.Changed += Changed;
        View.Caret.PositionChanged += CaretMoved;
        View.Selection.SelectionChanged += SelectionChanged;
        View.LostAggregateFocus += LostFocus;
        View.Closed += Closed;
      }

      private bool CanInsert => !closed && !View.IsClosed && View.Selection.IsEmpty &&
        !View.GetMultiSelectionBroker().HasMultipleSelections &&
        !View.Caret.Position.VirtualBufferPosition.IsInVirtualSpace &&
        View.TextBuffer == View.TextViewModel.EditBuffer && View.TextBuffer == View.TextDataModel.DocumentBuffer &&
        !View.TextBuffer.IsReadOnly(View.Caret.Position.BufferPosition.Position);

      public async Task RequestAsync(string file)
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (closed) return;
        if (!CanInsert) { Report("Use a single caret in an editable document to request a suggestion."); return; }
        if (host.Suggestions == null) { Report("Native inline suggestions are unavailable in this editor."); return; }
        if (!PhenixCodeCoreService.IsServiceRunning) { Report("Start the service in PhenixCode Settings to request a suggestion."); return; }
        View.VisualElement.Focus();

        var snapshot = View.TextSnapshot;
        var caret = View.Caret.Position.VirtualBufferPosition;
        var scope = PhenixCodeCoreService.SolutionSettingsPath;
        var previous = host.requests.Current;
        var request = host.requests.Begin(this);
        bool displayed = false;
        Func<bool> valid = () => host.requests.IsCurrent(request) && CanInsert && View.HasAggregateFocus &&
          ReferenceEquals(snapshot, View.TextSnapshot) && caret == View.Caret.Position.VirtualBufferPosition &&
          string.Equals(scope, PhenixCodeCoreService.SolutionSettingsPath, StringComparison.OrdinalIgnoreCase);
        try
        {
          // Dismiss only our own prior native session. Never disable or globally
          // dismiss another provider (IntelliCode/Copilot/etc.).
          if (previous?.Owner is Controller old) await old.DismissAsync(previous);
          if (!valid()) return;
          ShowPendingAsync(request).FireAndForget();
          if (registration == null)
            registration = ThreadHelper.JoinableTaskFactory.RunAsync(() => host.Suggestions.TryRegisterProviderAsync(new Provider(this), View, "PhenixCode.InlineCompletion", lifetime.Token));
          manager = await registration.JoinAsync();
          if (!valid()) return;
          if (manager == null)
          {
            registration = null;
            Report("The editor's inline suggestion service is unavailable. Check Visual Studio's code completion settings.");
            return;
          }
          if (manager.CompletionState?.IsSnippet == true) { Report("Finish the active snippet before requesting a suggestion."); return; }
          int point = caret.Position.Position;
          int start = Math.Max(0, point - 3000), end = Math.Min(snapshot.Length, point + 1500);
          var result = await PhenixCodeCoreService.FillInTheMiddleAsync(snapshot.GetText(start, point - start), snapshot.GetText(point, end - point), file, request.Token);
          await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
          if (!valid()) return;
          if (string.IsNullOrWhiteSpace(result)) { Report("No inline suggestion returned."); return; }
          if (host.HasCompletion(View)) { Report("Finish or dismiss IntelliSense, then request the suggestion again."); return; }

          progress?.Hide(request);
          var suggestion = new NativeSuggestion(this, request);
          var session = await manager.TryDisplaySuggestionAsync(suggestion, request.Token);
          if (!valid())
          {
            if (session != null) await session.DismissAsync(ReasonForDismiss.DismissedBySession, CancellationToken.None);
            return;
          }
          if (session == null) { Report("The editor could not display the suggestion. Dismiss any other suggestion and try again."); return; }
          display = session; displayedRequest = request;
          host.requests.Show(request);
          var proposal = new Proposal("PhenixCode", new[] { new ProposedEdit(new SnapshotSpan(caret.Position, 0), result) }, caret,
            manager.CompletionState, ProposalFlags.None, null, Guid.NewGuid().ToString(), "to accept", null, null, "PhenixCode inline suggestion", null);
          await session.DisplayProposalAsync(proposal, request.Token);
          displayed = session.Proposal != null && ReferenceEquals(host.Suggestions.GetDisplayedProposal(View), session.Proposal);
          if (host.requests.IsCurrent(request)) Report(displayed ? "Inline suggestion ready — Tab to accept, Esc to dismiss." : "The editor could not display the suggestion.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
          if (host.requests.IsCurrent(request)) Report("Could not get an inline suggestion. " + ex.Message);
        }
        finally
        {
          progress?.Hide(request);
          // Old requests have no authority over newer UI, status, or sessions.
          if (host.requests.IsCurrent(request) && !displayed)
          {
            host.requests.End(request);
            await DismissAsync(request);
          }
        }
      }

      private async Task ShowPendingAsync(InlineCompletionSession.Request request)
      {
        try
        {
          await Task.Delay(350, request.Token);
          await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
          if (host.requests.IsCurrent(request) && !request.Visible)
          {
            Report("Generating inline suggestion… Press Esc to cancel.");
            try
            {
              if (progress == null) progress = new InlineCompletionProgress(View, () => CancelAsync().FireAndForget());
              progress.Show(request);
            }
            catch (ArgumentOutOfRangeException ex) { Diag.OutputMsg("Inline progress layer unavailable: " + ex.Message); }
          }
        }
        catch (OperationCanceledException) { }
      }

      public async Task AcceptAsync()
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var request = host.requests.Current;
        if (!host.requests.IsCurrent(request) || request.Owner != this || !request.Visible || display?.Proposal == null || !CanInsert || host.HasCompletion(View)) return;
        if (ReferenceEquals(host.Suggestions.GetDisplayedProposal(View), display.Proposal)) display.CommitSuggestion(false);
      }

      public async Task CancelAsync(InlineCompletionSession.Request expected = null)
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var request = host.requests.Current;
        if (request?.Owner != this || (expected != null && !ReferenceEquals(expected, request))) return;
        progress?.Hide(request);
        if (!closed) Report("Inline suggestion cancelled.");
        host.requests.End(request);
        await DismissAsync(request);
      }

      private async Task DismissAsync(InlineCompletionSession.Request request)
      {
        progress?.Hide(request);
        if (!ReferenceEquals(displayedRequest, request)) return;
        var session = display;
        display = null; displayedRequest = null;
        if (session != null && !closed) await session.DismissAsync(ReasonForDismiss.DismissedBySession, CancellationToken.None);
      }

      private async Task EndedAsync(InlineCompletionSession.Request request, string message)
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        progress?.Hide(request);
        if (!host.requests.IsCurrent(request)) return;
        host.requests.End(request);
        if (ReferenceEquals(displayedRequest, request)) { display = null; displayedRequest = null; }
        if (!closed) Report(message);
      }

      private void ContextChanged()
      {
        var request = host.requests.Current;
        if (request?.Owner != this) return;
        if (!request.Visible) CancelAsync(request).FireAndForget();
        // While visible, native callbacks own acceptance and invalidation. In
        // particular, do not cancel reentrantly during the native commit edit.
      }
      private void Changed(object sender, TextContentChangedEventArgs e) => ContextChanged();
      private void CaretMoved(object sender, CaretPositionChangedEventArgs e) => ContextChanged();
      private void SelectionChanged(object sender, EventArgs e) => ContextChanged();
      private void LostFocus(object sender, EventArgs e) => ContextChanged();
      private void Closed(object sender, EventArgs e)
      {
        closed = true;
        progress?.Dispose();
        View.TextBuffer.Changed -= Changed; View.Caret.PositionChanged -= CaretMoved;
        View.Selection.SelectionChanged -= SelectionChanged; View.LostAggregateFocus -= LostFocus; View.Closed -= Closed;
        if (host.requests.Current?.Owner == this)
        {
          Report("Inline suggestion cancelled.");
          host.requests.End(host.requests.Current);
        }
        lifetime.Cancel();
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
          try { var value = registration == null ? null : await registration.JoinAsync(); if (value != null) await value.DisposeAsync(); }
          catch (OperationCanceledException) { }
          catch (Exception ex) { Diag.OutputMsg("Inline suggestion cleanup: " + ex.Message); }
          finally { lifetime.Dispose(); }
        }).FireAndForget();
      }

      private sealed class Provider : SuggestionProviderBase
      {
        private readonly Controller owner;
        public Provider(Controller owner) { this.owner = owner; }
        public override Task DisabledAsync(CancellationToken cancel) => owner.CancelAsync();
      }
      private sealed class NativeSuggestion : SuggestionBase
      {
        private readonly Controller owner;
        private readonly InlineCompletionSession.Request request;
        public NativeSuggestion(Controller owner, InlineCompletionSession.Request request) { this.owner = owner; this.request = request; }
        public override EditDisplayStyle EditStyle => EditDisplayStyle.GrayText;
        public override TipStyle TipStyle => TipStyle.AlwaysShowTip | TipStyle.HideTipDescription;
        public override bool HasMultipleSuggestions => false;
        public override event PropertyChangedEventHandler PropertyChanged { add { } remove { } }
        public override Task OnAcceptedAsync(SuggestionSessionBase session, ProposalBase proposal, ProposalBase original, ReasonForAccept reason, CancellationToken cancel)
          => owner.EndedAsync(request, "Inline suggestion accepted.");
        public override Task OnDismissedAsync(SuggestionSessionBase session, ProposalBase proposal, ProposalBase original, ReasonForDismiss reason, CancellationToken cancel)
          => owner.EndedAsync(request, "Inline suggestion dismissed.");
        public override Task OnProposalUpdatedAsync(SuggestionSessionBase session, ProposalBase proposal, ProposalBase original, ReasonForUpdate reason, VirtualSnapshotPoint caret, CompletionState completion, CancellationToken cancel)
          => owner.CancelAsync(request);
        public override Task OnChangeProposalAsync(SuggestionSessionBase session, ProposalBase proposal, ProposalBase original, bool forward, CancellationToken cancel) => Task.CompletedTask;
      }
    }
  }
}
