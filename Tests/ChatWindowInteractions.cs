using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChatAssistantVSIX;
using ChatAssistantVSIX.ToolWindows;
using ChatAssistantVSIX.Utils;
using Newtonsoft.Json.Linq;

// Chat tests compile the actual chat window and session.
internal static class ChatWindowInteractions
{
  private sealed class Pending
  {
    public Action<string> Token;
    public Action<JObject> Metadata;
    public string Target;
    public Collection<ChatMessage> History;
    public TaskCompletionSource<string> Result = new TaskCompletionSource<string>();
  }
  public static IEnumerable<string> Run()
  {
    var oldContext = SynchronizationContext.Current;
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
    var pending = new List<Pending>();
    MyToolWindowControl view = null;
    try
    {
      PhenixCodeCoreService.IsServiceRunning = true;
      PhenixCodeCoreService.ProviderQuery = _ => Task.FromResult(JObject.Parse("{'currentApi':'auto','completionApis':[{'id':'auto','name':'Auto'},{'id':'a','name':'Local','model':'Coder'},{'id':'b','name':'Remote','model':'Large'}]}"));
      PhenixCodeCoreService.ChatQuery = (token, history, target, cancellation, metadata) =>
      {
        var call = new Pending { Token = token, Metadata = metadata, History = history, Target = target }; pending.Add(call); return call.Result.Task;
      };
      var messenger = new ToolWindowMessenger();
      view = new MyToolWindowControl(messenger);
      view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
      var input = (TextBox)view.FindName("inputBox"); var send = (Button)view.FindName("sendButton"); var models = (ComboBox)view.FindName("providerBox");
      Require(models.Items.Count == 3 && !send.IsEnabled, "Default catalog/blank-input state incorrect");
      var refreshModels = typeof(MyToolWindowControl).GetMethod("RefreshProvidersAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
      PhenixCodeCoreService.ProviderQuery = _ => Task.FromResult(JObject.Parse("{'currentApi':'b','completionApis':[{'id':'a','name':'Local','model':'Coder'},{'id':'b','name':'Remote','model':'Large'}]}"));
      ((Task)refreshModels.Invoke(view, new object[] { false })).GetAwaiter().GetResult();
      Require(models.Items.Count == 2 && ((ChatProvider)models.SelectedItem).Id == "b" && ((ChatProvider)models.SelectedItem).IsDefault, "Periodic refresh did not update models/default while service stayed running");
      models.SelectedItem = models.Items.Cast<ChatProvider>().Single(p => p.Id == "b");
      input.Text = "Explain this code"; Click(send);
      Require(pending.Count == 1 && pending[0].Target == "b" && pending[0].History.Count == 1, "Send did not snapshot selected provider and history");
      Require((string)send.Content == "Stop", "Send did not become Stop");
      models.SelectedItem = models.Items.Cast<ChatProvider>().Single(p => p.Id == "a");
      ((Task)refreshModels.Invoke(view, new object[] { false })).GetAwaiter().GetResult();
      Require(((ChatProvider)models.SelectedItem).Id == "a", "Periodic refresh lost explicit model selection");
      input.Text = "Draft next question";
      pending[0].Token("{\"content\":\"partial text\"}");
      pending[0].Metadata(JObject.Parse("{'type':'context_sources','sources':['C:/repo/src/old.cpp']}"));
      Pump(() => view.Messages.Last().Content == "partial text");
      Click(send);
      Require(view.Messages.Last().Content == "partial text" && view.Messages.Last().Status.Contains("Stopped"), "Stop lost partial text");
      Require(input.Text == "Draft next question", "Stop discarded composer draft");
      Click((Button)view.FindName("retryButton"));
      Require(view.Messages.Last().Sources.Count == 0, "Retry retained old references");
      Require(pending.Count == 2 && pending[1].Target == "b" && view.Messages.Count == 2, "Retry changed original model or duplicated prompt");
      Action clear = () => messenger.Send("cmdidButtonClearChat"); clear();
      pending[0].Token("{\"content\":\"late original\"}"); pending[1].Token("{\"content\":\"late retry\"}");
      pending[0].Metadata(JObject.Parse("{'type':'context_sources','sources':['stale.cpp']}"));
      foreach (var request in pending) request.Result.TrySetResult("late response");
      Pump(() => (string)send.Content == "Send");
      Require(view.Messages.Count == 0 && input.Text == "Draft next question" && ((ChatProvider)models.SelectedItem).IsDefault, "Clear leaked stale content or did not reset provider");
      input.Text = "Try a request"; Click(send); pending.Last().Result.SetException(new System.Net.Http.HttpRequestException("Connection lost"));
      Pump(() => view.Messages.Last().Status.Contains("Response failed"));
      Require(((Button)view.FindName("retryButton")).Visibility == Visibility.Visible, "Failed request has no Retry action");
      clear();
      input.Text = "Give a code sample"; Click(send);
      var sample = "**Bold** and *italic*, [documentation](https://example.com).\n\n| Name | Value |\n| --- | --- |\n| Answer | 42 |\n\n- Parent\n  - Nested item\n\n```cs\nvar answer = 42;\n```\n\n> Quoted explanation\n>\n>     quoted_code();";
      pending.Last().Metadata(JObject.Parse("{'type':'context_sources','model':'actual-model','sources':['C:/repo/src/main.cpp','C:/repo/include/a[b]*.h',null,5]}"));
      pending.Last().Token(new JObject { ["content"] = sample }.ToString()); pending.Last().Result.SetResult(sample);
      Pump(() => (string)send.Content == "Send");
      Require(view.Messages.Count == 2 && view.Messages.Last().IsComplete, "Completed response not recorded");
      var response = view.Messages.Last();
      Require(response.SourceLabels.SequenceEqual(new[] { "src/main.cpp", "include/a[b]*.h" }) && response.Provider == "actual-model", "Source metadata or common-prefix shortening was lost");
      var responseDoc = FindDocuments(((StackPanel)view.FindName("messageList")).Children[1]).Single().Document;
      var displayed = new TextRange(responseDoc.ContentStart, responseDoc.ContentEnd).Text;
      Require(displayed.Contains("Sources:") && displayed.Contains("include/a[b]*.h") && displayed.IndexOf("Sources:") > displayed.IndexOf("Quoted explanation"), "Source list was not appended literally at the end of the response");
      Require(response.CopyContent.EndsWith("src/main.cpp\ninclude/a[b]*.h") && response.Content == sample, "Copy omits references or metadata contaminated assistant text");
      foreach (var size in new[] { new Size(360, 600), new Size(280, 600) }) Render(view, size);
      var tableDoc = MarkdownFlowDocument.Convert("| Name | Value |\n| --- | --- |\n| Important | 42 |", "Consolas", 12);
      var table = tableDoc.Blocks.OfType<Table>().Single();
      Require(table.RowGroups[0].Rows.Count == 2 && new TextRange(table.ContentStart, table.ContentEnd).Text.Contains("Important"), "Markdown table content disappeared");
      var nestedDoc = MarkdownFlowDocument.Convert("- parent\n  - child\n\n    ```cs\n    nested_code();\n    ```\n\n> quote\n>\n>     quoted_code();\n\n<aside>fallback</aside>", "Consolas", 12);
      var blocks = WalkBlocks(nestedDoc.Blocks).ToArray();
      Require(blocks.OfType<List>().Count() == 2, "Nested list was discarded");
      var code = blocks.OfType<BlockUIContainer>().SelectMany(b => ((Panel)b.Child).Children.OfType<TextBox>()).Select(b => b.Text).ToArray();
      Require(code.Any(t => t.Contains("nested_code")) && code.Any(t => t.Contains("quoted_code")), "Nested or indented code disappeared");
      Require(new TextRange(nestedDoc.ContentStart, nestedDoc.ContentEnd).Text.Contains("fallback"), "Unsupported Markdown disappeared instead of falling back to text");
      var doc = MarkdownFlowDocument.Convert("**strong** *emphasis* [label](https://example.com)", "Consolas", 12);
      var inlines = ((Paragraph)doc.Blocks.FirstBlock).Inlines.ToArray();
      Require(inlines.OfType<Span>().Any(s => s.FontWeight == FontWeights.Bold), "Markdown bold formatting missing");
      Require(inlines.OfType<Hyperlink>().Any(h => new TextRange(h.ContentStart, h.ContentEnd).Text == "label"), "Link label is outside hyperlink");
      Require(!((Paragraph)MarkdownFlowDocument.Convert("[unsafe](javascript:alert)", "Consolas", 12).Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Any(), "Unsafe link became executable");
      input.Text = "Old solution question"; Click(send); var stale = pending.Last();
      PhenixCodeCoreService.ScopeOverride = Path.Combine(PhenixCodeCoreService.Root, "other-solution.json");
      var refresh = typeof(MyToolWindowControl).GetMethod("RefreshProvidersAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
      ((Task)refresh.Invoke(view, new object[] { true })).GetAwaiter().GetResult();
      input.Text = "New solution question"; Click(send); var current = pending.Last();
      stale.Token("{\"content\":\"obsolete solution response\"}"); stale.Result.TrySetResult("obsolete solution response");
      stale.Metadata(JObject.Parse("{'type':'context_sources','sources':['stale.cpp']}"));
      current.Token("{\"content\":\"new solution response\"}"); current.Result.SetResult("new solution response");
      Pump(() => view.Messages.Count == 2 && view.Messages.Last().IsComplete);
      Require(view.Messages.First().Content == "New solution question" && view.Messages.Last().Content == "new solution response", "Old solution callback cleared or contaminated the new conversation");
      Require(view.Messages.Last().Sources.Count == 0, "Old solution references leaked into a new response");
      return new[] { "PASS: chat UI provider pinning, streaming, Stop, Retry, safe Clear, error recovery, copy controls, composer preservation, Markdown and narrow layouts." };
    }
    finally
    {
      view?.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
      foreach (var request in pending) request.Result.TrySetCanceled();
      PhenixCodeCoreService.IsServiceRunning = PhenixCodeCoreService.IsServiceStarting = false;
      PhenixCodeCoreService.ProviderQuery = null; PhenixCodeCoreService.ChatQuery = null;
      PhenixCodeCoreService.ScopeOverride = null;
      SynchronizationContext.SetSynchronizationContext(oldContext);
    }
  }
  private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
  private static IEnumerable<Block> WalkBlocks(BlockCollection blocks)
  {
    foreach (var block in blocks)
    {
      yield return block;
      if (block is Section section) foreach (var child in WalkBlocks(section.Blocks)) yield return child;
      if (block is List list) foreach (var item in list.ListItems) foreach (var child in WalkBlocks(item.Blocks)) yield return child;
    }
  }
  private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  private static void Pump(Func<bool> predicate)
  {
    var timeout = DateTime.UtcNow.AddSeconds(5);
    while (!predicate())
    {
      if (DateTime.UtcNow > timeout) throw new TimeoutException("Chat UI state did not settle.");
      var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
      timer.Tick += (_, _) => frame.Continue = false;
      try { timer.Start(); Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
  }
  private static IEnumerable<Button> FindButtons(DependencyObject node)
  {
    if (node is Button button) yield return button;
    foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) foreach (var found in FindButtons(child)) yield return found;
  }
  private static void Render(MyToolWindowControl view, Size size)
  {
    view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
    var send = (Button)view.FindName("sendButton");
    var models = (ComboBox)view.FindName("providerBox");
    var status = (TextBlock)view.FindName("serviceStatus");
    var bounds = send.TransformToAncestor(view).TransformBounds(new Rect(send.RenderSize));
    Require(bounds.Right <= size.Width && bounds.Bottom <= size.Height && bounds.Left >= 0, "Composer action is outside narrow pane");
    Require(models.ActualHeight <= 24.1 && models.ActualWidth <= 210.1, "Model picker is not compact");
    var modelPosition = models.TransformToAncestor(view).Transform(new Point());
    var statusPosition = status.TransformToAncestor(view).Transform(new Point());
    var composer = (Border)view.FindName("composer");
    var composerBounds = composer.TransformToAncestor(view).TransformBounds(new Rect(composer.RenderSize));
    var modelBounds = models.TransformToAncestor(view).TransformBounds(new Rect(models.RenderSize));
    Require(composerBounds.Contains(bounds) && composerBounds.Contains(modelBounds), "Model picker and Send are not inside the composer");
    Require(statusPosition.Y >= composerBounds.Bottom, "Service status is not below the composer");
    var input = (TextBox)view.FindName("inputBox");
    var inputBounds = input.TransformToAncestor(view).TransformBounds(new Rect(input.RenderSize));
    Require(inputBounds.Bottom <= modelPosition.Y && inputBounds.Bottom <= bounds.Top, "Composer controls overlap message text");
    Require(!FindButtons(view).Any(b => (b.Content as string) == "Settings" || (b.Content as string) == "Clear chat"), "Duplicate footer action remains");
    var copies = FindButtons(view).Where(b => (b.ToolTip as string) == "Copy message" || (b.ToolTip as string) == "Copy response").ToArray();
    Require(copies.Length == 2 && copies.All(b => b.Content is FrameworkElement && b.ActualWidth <= 24.1), "Turn copy actions are not compact icons with tooltips");
    var bubbles = ((StackPanel)view.FindName("messageList")).Children.OfType<Border>().ToArray();
    var user = FindDocuments(bubbles[0]).Single();
    Require(user.Document.TextAlignment == TextAlignment.Right && bubbles[0].Margin.Left > bubbles[1].Margin.Left, "User turn is not right-aligned");
    var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(view);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
    using (var file = File.Create(Path.Combine(PhenixCodeCoreService.Root, "chat-" + size.Width + "x" + size.Height + "-conversation.png"))) encoder.Save(file);
  }
  private static IEnumerable<RichTextBox> FindDocuments(DependencyObject node)
  {
    if (node is RichTextBox box) yield return box;
    foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) foreach (var found in FindDocuments(child)) yield return found;
  }
}
