global using System;
global using Task = System.Threading.Tasks.Task;
global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
using ChatAssistantVSIX.Utils;
using Microsoft.VisualStudio.Language.Suggestions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;
using System.ComponentModel.Composition;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace ChatAssistantVSIX.Utils
{
  // Only the backend is fake. The editor service, request ownership, native
  // rendering, cancellation handler, and acceptance are production source.
  public static class PhenixCodeCoreService
  {
    public static bool IsServiceRunning => true;
    public static string SolutionSettingsPath => "inline-integration-fixture";
    internal static Func<Task<string>> Completion;
    internal static Queue<TaskCompletionSource<string>> Delayed = new Queue<TaskCompletionSource<string>>();
    internal static int Calls;
    public static Task<string> FillInTheMiddleAsync(string prefix, string suffix, string filename, CancellationToken token)
    {
      int id = ++Calls;
      TestLog.Write("request", new JObject { ["id"] = id, ["prefix"] = prefix, ["suffix"] = suffix, ["filename"] = filename });
      token.Register(() => TestLog.Write("cancelled", new JObject { ["id"] = id }));
      return Completion(); // Deliberately allow a late response after cancellation.
    }
  }
  public static class Diag { public static void OutputMsg(string message) => TestLog.Write("status", new JObject { ["message"] = message }); }
}

internal static class TestLog
{
  internal static string Path;
  internal static void Write(string name, JObject data = null)
  {
    if (Path == null) return;
    var value = data ?? new JObject(); value["event"] = name; value["time"] = DateTime.UtcNow.ToString("O");
    lock (typeof(TestLog)) File.AppendAllText(Path, value.ToString(Newtonsoft.Json.Formatting.None) + Environment.NewLine);
  }
}

[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class NativeInlineIntegrationFactory : IWpfTextViewCreationListener
{
  [Import] internal ITextDocumentFactoryService Documents = null;
  [Import] internal InlineCompletionService Inline = null;
  [Import] internal SuggestionServiceBase Native = null;
  public void TextViewCreated(IWpfTextView view)
  {
    if (Documents.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var doc) &&
        System.IO.Path.GetFileName(doc.FilePath) == "native-inline-integration.cpp" &&
        view.TextSnapshot.GetText().StartsWith("// PhenixCode native inline integration -- disposable file."))
      view.Properties.GetOrCreateSingletonProperty(() => new Driver(view, doc.FilePath, Inline, Native));
  }

  private sealed class Driver
  {
    private readonly IWpfTextView view;
    private readonly string file;
    private readonly InlineCompletionService inline;
    private readonly SuggestionServiceBase native;
    private bool started;
    public Driver(IWpfTextView view, string file, InlineCompletionService inline, SuggestionServiceBase native)
    {
      this.view = view; this.file = file; this.inline = inline; this.native = native;
      var output = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file), "bin"); Directory.CreateDirectory(output);
      TestLog.Path = System.IO.Path.Combine(output, "integration.jsonl");
      view.GotAggregateFocus += Focus;
      view.VisualElement.PreviewKeyDown += OnKeyDown;
      view.Closed += Closed;
      TestLog.Write("attached");
    }
    private void Focus(object sender, EventArgs e)
    {
      if (started) return; started = true;
      var position = view.TextSnapshot.GetText().IndexOf("std::puts(") + "std::puts(".Length;
      view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, position));
      Inspect("baseline"); Generate("immediate");
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
      if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) return;
      var key = e.Key == Key.System ? e.SystemKey : e.Key;
      if (key == Key.G || key == Key.N || key == Key.M || key == Key.D || key == Key.E)
      { e.Handled = true; Generate(key == Key.D ? "delayed" : key == Key.N ? "newer" : key == Key.M ? "multiline" : key == Key.E ? "error" : "immediate"); }
      else if (key == Key.I) { e.Handled = true; Inspect("inspect"); }
      else if (key == Key.L)
      {
        e.Handled = true;
        if (PhenixCodeCoreService.Delayed.Count > 0) PhenixCodeCoreService.Delayed.Dequeue().TrySetResult("\"late obsolete suggestion\"");
        Run(async () => { await Task.Delay(60); Inspect("released"); });
      }
      else if (key == Key.A) { e.Handled = true; Run(async () => { await inline.AcceptAsync(view); Inspect("explicit-accept"); }); }
      else if (key == Key.X) { e.Handled = true; Run(async () => { await inline.CancelAsync(view); Inspect("explicit-cancel"); }); }
    }
    private void Generate(string mode)
    {
      if (mode == "delayed")
      {
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        PhenixCodeCoreService.Delayed.Enqueue(delayed);
        PhenixCodeCoreService.Completion = () => delayed.Task;
      }
      else PhenixCodeCoreService.Completion = () => mode == "error" ? Task.FromException<string>(new InvalidOperationException("Fixture failure")) :
        Task.FromResult(mode == "multiline" ? "\"native integrated\");\n    std::puts(\"second line\"" : mode == "newer" ? "\"newer native suggestion\"" : "\"native integrated\"");
      Run(async () => { await inline.RequestAsync(view, file); Inspect("after-" + mode); });
    }
    private void Inspect(string name)
    {
      var proposal = native.GetDisplayedProposal(view);
      var progress = view.GetAdornmentLayer(InlineCompletionProgress.LayerName).Elements.Select(e => e.Adornment).OfType<Button>().FirstOrDefault();
      TestLog.Write(name, new JObject { ["buffer"] = view.TextSnapshot.GetText(), ["caret"] = view.Caret.Position.BufferPosition.Position,
        ["selection"] = view.Selection.SelectedSpans[0].Length, ["loading"] = inline.IsLoading(view),
        ["proposal"] = proposal != null, ["proposalId"] = proposal?.ProposalId, ["calls"] = PhenixCodeCoreService.Calls,
        ["progress"] = progress != null, ["dotOpacities"] = progress == null ? new JArray() : new JArray(((StackPanel)progress.Content).Children.OfType<Ellipse>().Select(d => d.Opacity)) });
    }
    private static void Run(Func<Task> action)
    {
      _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () => { try { await action(); } catch (Exception ex) { TestLog.Write("ERROR", new JObject { ["error"] = ex.ToString() }); } });
    }
    private void Closed(object sender, EventArgs e)
    { view.GotAggregateFocus -= Focus; view.VisualElement.PreviewKeyDown -= OnKeyDown; view.Closed -= Closed; TestLog.Write("closed"); }
  }
}
