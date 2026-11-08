using Microsoft.VisualStudio.Language.Proposals;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace PhenixCode.NativeGhostTextProbe
{
  // This experiment deliberately imports only public editor contracts. It has
  // no dependency on Copilot, the chat window, or a running PhenixCode service.
  [Export(typeof(IWpfTextViewCreationListener))]
  [ContentType("text")]
  [TextViewRole(PredefinedTextViewRoles.Document)]
  internal sealed class ProbeFactory : IWpfTextViewCreationListener
  {
    [Import] internal ITextDocumentFactoryService Documents = null;
    [Import(AllowDefault = true)] internal SuggestionServiceBase Suggestions = null;

    public void TextViewCreated(IWpfTextView view)
    {
      if (!Documents.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var document) ||
          !string.Equals(Path.GetFileName(document.FilePath), "native-ghost-probe.cpp", StringComparison.OrdinalIgnoreCase) ||
          !view.TextSnapshot.GetText().StartsWith("// PhenixCode native ghost-text probe -- disposable file.", StringComparison.Ordinal)) return;
      view.Properties.GetOrCreateSingletonProperty(() => new Probe(view, Suggestions, document.FilePath));
    }
  }

  internal sealed class Probe
  {
    private readonly IWpfTextView view;
    private readonly SuggestionServiceBase service;
    private readonly string log;
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private SuggestionManagerBase manager;
    private bool started, busy;

    public Probe(IWpfTextView view, SuggestionServiceBase service, string file)
    {
      this.view = view; this.service = service;
      var output = Path.Combine(Path.GetDirectoryName(file), "bin");
      Directory.CreateDirectory(output); log = Path.Combine(output, "events.log");
      Record("Attached; native service=" + (service != null) + "; editor=" + typeof(SuggestionBase).Assembly.GetName().Version);
      view.VisualElement.PreviewKeyDown += KeyDown;
      view.GotAggregateFocus += FirstFocus;
      view.TextBuffer.Changed += BufferChanged;
      view.Closed += Closed;
    }

    private void FirstFocus(object sender, EventArgs e)
    {
      if (started) return;
      started = true;
      // A known location makes the first visual check independent of the shell's
      // restored cursor position. Subsequent invocations use the real caret.
      int position = view.TextSnapshot.GetText().IndexOf("std::puts(", StringComparison.Ordinal);
      if (position >= 0) view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, position + "std::puts(".Length));
      Run(() => ShowAsync(false));
    }

    private void KeyDown(object sender, KeyEventArgs e)
    {
      if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) return;
      var key = e.Key == Key.System ? e.SystemKey : e.Key;
      if (key == Key.I) { e.Handled = true; RecordState("Inspect"); }
      else if (key == Key.G || key == Key.M) { e.Handled = true; Run(() => ShowAsync(key == Key.M)); }
    }

    private void Run(Func<Task> action)
    {
      _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        try { await action(); }
        catch (OperationCanceledException) { Record("Cancelled"); }
        catch (Exception ex) { Record("ERROR " + ex); }
      });
    }

    private async Task ShowAsync(bool multiline)
    {
      if (busy || view.IsClosed || service == null) return;
      busy = true;
      try
      {
        if (manager == null)
        {
          manager = await service.TryRegisterProviderAsync(new SuggestionProviderBase(), view, "PhenixCode.NativeGhostTextProbe", lifetime.Token);
          Record("Registered=" + (manager != null));
        }
        if (manager == null || view.IsClosed) return;
        await service.DismissProposalAsync(view, ReasonForDismiss.DismissedBySession, lifetime.Token);
        var caret = view.Caret.Position.VirtualBufferPosition;
        var snapshot = view.TextSnapshot;
        var text = multiline ? "\"PhenixCode native ghost text\");\r\n    std::puts(\"Second ghost line\"" : "\"PhenixCode native ghost text\"";
        var proposal = new Proposal("PhenixCode fixed suggestion", new[] { new ProposedEdit(new SnapshotSpan(caret.Position, 0), text) }, caret,
          manager.CompletionState, ProposalFlags.None, null, "PhenixCode.Probe", "to accept", null, null,
          "PhenixCode native ghost-text probe", null);
        var suggestion = new ProbeSuggestion(RecordState);
        var session = await manager.TryDisplaySuggestionAsync(suggestion, lifetime.Token);
        Record("Session=" + (session != null));
        if (session != null) await session.DisplayProposalAsync(proposal, lifetime.Token);
        Record("Displayed=" + manager.IsSuggestionDisplayed + "; buffer unchanged=" + ReferenceEquals(snapshot, view.TextSnapshot) + "; multiline=" + multiline);
        RecordState("After display");
      }
      finally { busy = false; }
    }

    // The view snapshot can lag behind e.After during this notification. Stable
    // buffer captures are recorded by the native callbacks and Inspect command.
    private void BufferChanged(object sender, TextContentChangedEventArgs e) => Record("Buffer changed; new length=" + e.After.Length);
    private void RecordState(string message) => Record(message + "; proposal=" + (service?.GetDisplayedProposal(view) != null) +
      "; caret=" + view.Caret.Position.BufferPosition.Position + "; selection=" + view.Selection.SelectedSpans[0].Length +
      "; buffer-base64=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(view.TextSnapshot.GetText())));
    private void Record(string message) => File.AppendAllText(log, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
    private void Closed(object sender, EventArgs e)
    {
      lifetime.Cancel();
      view.VisualElement.PreviewKeyDown -= KeyDown; view.GotAggregateFocus -= FirstFocus;
      view.TextBuffer.Changed -= BufferChanged; view.Closed -= Closed;
      Run(async () => { if (manager != null) await manager.DisposeAsync(); lifetime.Dispose(); Record("Closed"); });
    }
  }

  internal sealed class ProbeSuggestion : SuggestionBase
  {
    private readonly Action<string> record;
    public ProbeSuggestion(Action<string> record) { this.record = record; }
    public override EditDisplayStyle EditStyle => EditDisplayStyle.GrayText;
    public override TipStyle TipStyle => TipStyle.AlwaysShowTip;
    public override bool HasMultipleSuggestions => false;
    public override event PropertyChangedEventHandler PropertyChanged { add { } remove { } }
    public override Task OnAcceptedAsync(SuggestionSessionBase session, ProposalBase proposal, ProposalBase original, ReasonForAccept reason, CancellationToken cancel)
    { record("Accepted: " + reason); return Task.CompletedTask; }
    public override Task OnDismissedAsync(SuggestionSessionBase session, ProposalBase proposal, ProposalBase original, ReasonForDismiss reason, CancellationToken cancel)
    { record("Dismissed: " + reason); return Task.CompletedTask; }
    public override Task OnProposalUpdatedAsync(SuggestionSessionBase session, ProposalBase proposal, ProposalBase original, ReasonForUpdate reason, VirtualSnapshotPoint caret, CompletionState completion, CancellationToken cancel)
    { record("Updated: " + reason); return Task.CompletedTask; }
    public override Task OnChangeProposalAsync(SuggestionSessionBase session, ProposalBase proposal, ProposalBase original, bool forward, CancellationToken cancel) => Task.CompletedTask;
  }
}
