using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChatAssistantVSIX.Dialogs;
using ChatAssistantVSIX.Utils;
using Newtonsoft.Json.Linq;

internal static class StatsDialogInteractions
{
  private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
  public static IEnumerable<string> Run()
  {
    var context = SynchronizationContext.Current;
    var culture = CultureInfo.CurrentCulture;
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
    try
    {
      var dialog = new StatsDialog();
      var content = (ScrollViewer)dialog.FindName("StatisticsContent");
      var status = (TextBlock)dialog.FindName("Status");
      var refresh = (Button)dialog.FindName("Refresh");
      var pending = new TaskCompletionSource<string>();
      int requests = 0;
      PhenixCodeCoreService.StatsQuery = token => { requests++; return pending.Task; };
      dialog.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
      Require(requests == 1 && !refresh.IsEnabled && status.Text.Contains("Loading"), "Dialog did not load asynchronously on open");
      Await(Load(dialog));
      Require(requests == 1, "Repeated refresh started overlapping requests");
      pending.SetResult(Fixture());
      Pump(() => refresh.IsEnabled);
      var original = (IndexStatistics)content.DataContext;
      Require(original.General.Count == 5 && original.General[0].Value == "29,493" && original.General[4].Value == "36.3 MiB", "General totals were not formatted");
      Require(original.General[4].ToolTip == "38,083,032 bytes", "Exact byte count is unavailable");
      Require(original.Directories.First().Name == "(solution root)" && original.Directories.Last().Name == "src/very-long-directory-name-for-layout-checks/nested", "Directory paths are not solution relative");
      Require(original.Languages.First().Name == "C++" && original.Languages.First().FileCount == "1,087", "Language file counts were not ranked/formatted");
      Require(original.TopFiles.Count == 10 && original.TopFiles.First().Path == "src/sample-0.cpp" && original.TopFiles.Last().Path == "src/sample-9.cpp", "Top files were not limited to 10 ranked by chunks");
      Require(original.TopFiles.First().Details.Last().Value != "—" && original.TopFiles.First().FullPath.StartsWith(PhenixCodeCoreService.Root), "File metadata/full path missing");
      foreach (var size in new[] { new Size(720,600), new Size(440,320) }) Render(dialog, size);
      var small = JObject.Parse(Fixture());
      small["total_chunks"] = small["vector_count"] = 8;
      var smallSources = (JObject)small["sources"];
      smallSources["total_files"] = 1; smallSources["total_lines"] = 244; smallSources["total_size_bytes"] = 7782;
      smallSources["by_directory"] = new JObject { [PhenixCodeCoreService.Root.TrimEnd('\\','/')] = 1 };
      smallSources["by_language"] = new JObject { ["C++"] = 1 };
      smallSources["top_files"] = new JArray(new JObject {
        ["path"] = Path.Combine(PhenixCodeCoreService.Root, "tdgainloss.cpp"), ["language"] = "C++",
        ["chunks"] = 8, ["lines"] = 244, ["size_bytes"] = 7782, ["last_modified"] = 1767139620
      });
      content.DataContext = IndexStatistics.Parse(small.ToString(), PhenixCodeCoreService.Root);
      Render(dialog, new Size(720,600), "single-file");
      content.DataContext = original;

      PhenixCodeCoreService.StatsQuery = _ => Task.FromException<string>(new InvalidOperationException("Start the PhenixCode service in Settings."));
      refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
      Require(refresh.IsEnabled && status.Text.Contains("Start the PhenixCode service") && status.Text.Contains("previously loaded") && ReferenceEquals(content.DataContext, original), "Refresh failure lost data or omitted actionable error");
      PhenixCodeCoreService.StatsQuery = _ => Task.FromException<string>(new TaskCanceledException());
      Await(Load(dialog));
      Require(status.Text.Contains("timed out"), "Timeout was not explained");
      PhenixCodeCoreService.StatsQuery = _ => Task.FromResult("Invalid statistics response");
      Await(Load(dialog));
      Require(status.Text.Contains("invalid statistics") && ReferenceEquals(content.DataContext, original), "Malformed response replaced good statistics");
      PhenixCodeCoreService.StatsQuery = _ => Task.FromResult("");
      Await(Load(dialog));
      Require(status.Text.Contains("no statistics"), "Empty response was reported as success");
      PhenixCodeCoreService.StatsQuery = _ => Task.FromResult("{\"error\":\"Index unavailable\"}");
      Await(Load(dialog));
      Require(status.Text.Contains("Index unavailable") && ReferenceEquals(content.DataContext, original), "Backend error was displayed as valid statistics");

      PhenixCodeCoreService.StatsQuery = _ => Task.FromResult("{\"total_chunks\":0,\"vector_count\":0,\"sources\":{\"total_files\":0,\"total_lines\":0,\"total_size_bytes\":0}}");
      Await(Load(dialog));
      var empty = (IndexStatistics)content.DataContext;
      Require(empty.General[0].Value == "0" && empty.TopFiles.Count == 0 && empty.General[4].Value == "0 B", "Empty index did not preserve zero values");
      Render(dialog, new Size(440,320), "empty");
      var partial = IndexStatistics.Parse("{\"total_chunks\":12,\"sources\":{\"top_files\":[{\"path\":\"D:/outside/file.cpp\",\"last_modified\":9223372036854775807}]}}", PhenixCodeCoreService.Root);
      Require(partial.General[1].Value == "—" && partial.TopFiles[0].Details.Last().Value == "—" && partial.TopFiles[0].Path == "D:/outside/file.cpp", "Missing fields, invalid dates or external paths are misleading");

      pending = new TaskCompletionSource<string>();
      PhenixCodeCoreService.StatsQuery = _ => pending.Task;
      var stale = Load(dialog);
      PhenixCodeCoreService.ScopeOverride = Path.Combine(PhenixCodeCoreService.Root, "other-solution.json");
      pending.SetResult("{\"total_chunks\":999}");
      Await(stale);
      Require(content.DataContext == null && status.Text.Contains("solution changed"), "Old solution statistics were displayed after switching");
      PhenixCodeCoreService.ScopeOverride = null;

      pending = new TaskCompletionSource<string>();
      CancellationToken requestToken = default;
      PhenixCodeCoreService.StatsQuery = token => { requestToken = token; return pending.Task; };
      var closing = Load(dialog);
      typeof(StatsDialog).GetMethod("OnClosed", Private).Invoke(dialog, new object[] { dialog, EventArgs.Empty });
      Require(requestToken.IsCancellationRequested, "Closing did not cancel the request");
      var beforeClose = status.Text;
      pending.SetResult("{\"total_chunks\":999}");
      Await(closing);
      Require(content.DataContext == null && status.Text == beforeClose && !dialog.IsVisible, "Closed dialog accepted a late response");
      return new[] { "PASS: statistics totals, directory/language file counts, top 10 ranking, paths, sizes, dates, empty/partial data, refresh/error retention, timeout, solution switch and cancellation; all sections rendered at both sizes." };
    }
    finally
    {
      PhenixCodeCoreService.StatsQuery = null;
      PhenixCodeCoreService.ScopeOverride = null;
      SynchronizationContext.SetSynchronizationContext(context);
      CultureInfo.CurrentCulture = culture;
    }
  }

  private static Task Load(StatsDialog dialog) => (Task)typeof(StatsDialog).GetMethod("RefreshAsync", Private).Invoke(dialog, null);
  private static void Await(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
  private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
  private static void Pump(Func<bool> complete)
  {
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (!complete())
    {
      if (DateTime.UtcNow > deadline) throw new TimeoutException("Statistics UI did not settle.");
      var frame = new DispatcherFrame();
      var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
      timer.Tick += (_, __) => frame.Continue = false;
      try { timer.Start(); Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
  }
  private static string Fixture()
  {
    var files = new JArray(Enumerable.Range(0,12).Reverse().Select(i => new JObject {
      ["path"] = Path.Combine(PhenixCodeCoreService.Root, "src", "sample-" + i + ".cpp"),
      ["language"] = "C++", ["chunks"] = 839 - i, ["lines"] = 21329,
      ["size_bytes"] = 969130, ["last_modified"] = 1793089955
    }));
    return new JObject {
      ["total_chunks"] = 29493, ["vector_count"] = 29493,
      ["sources"] = new JObject {
        ["total_files"] = 1233, ["total_lines"] = 1128637, ["total_size_bytes"] = 38083032,
        ["by_directory"] = new JObject {
          [PhenixCodeCoreService.Root.TrimEnd('\\','/')] = 10,
          [Path.Combine(PhenixCodeCoreService.Root,"src")] = 311,
          [Path.Combine(PhenixCodeCoreService.Root,"src/very-long-directory-name-for-layout-checks/nested")] = 45
        },
        ["by_language"] = new JObject { ["Python"] = 4, ["C++"] = 1087, ["C"] = 125, ["Other"] = 12, ["JavaScript"] = 1 },
        ["top_files"] = files
      }
    }.ToString();
  }
  private static void Render(StatsDialog dialog, Size size, string state = "response")
  {
    var content = (FrameworkElement)dialog.Content;
    content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
    var refresh = (Button)dialog.FindName("Refresh");
    var bounds = refresh.TransformToAncestor(content).TransformBounds(new Rect(refresh.RenderSize));
    Require(bounds.Right <= size.Width && bounds.Bottom <= size.Height, "Statistics footer is outside dialog");
    var scroll = (ScrollViewer)dialog.FindName("StatisticsContent");
    Require(scroll.ActualHeight > 100, "Statistics viewport is too small");
    foreach (var name in new[] { "DirectoryDivider", "LanguageDivider", "FilesDivider" })
    {
      var divider = (Border)dialog.FindName(name);
      Require(divider.ActualHeight >= 1 && divider.ActualWidth > scroll.ActualWidth - 40 && divider.Background is SolidColorBrush brush && brush.Color.A > 0,
        "Section divider is missing or invisible: " + name);
    }
    scroll.ScrollToTop(); content.UpdateLayout();
    Save(content, size, state);
    if (state == "response")
    {
      foreach (var name in new[] { "DirectoryRows", "LanguageRows", "TopFileRows" })
      {
        var rows = (ItemsControl)dialog.FindName(name);
        Require(rows.Items.Count > 0, "Section did not bind: " + name);
        rows.BringIntoView(); content.UpdateLayout();
        Save(content, size, name);
      }
    }
  }
  private static void Save(FrameworkElement content, Size size, string state)
  {
    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
    var background = new DrawingVisual();
    using (var drawing = background.RenderOpen()) drawing.DrawRectangle((Brush)content.TryFindResource(Microsoft.VisualStudio.Shell.VsBrushes.WindowKey) ?? Brushes.White, null, new Rect(size));
    bitmap.Render(background);
    bitmap.Render(content);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using (var file = File.Create(Path.Combine(PhenixCodeCoreService.Root, "stats-" + size.Width + "x" + size.Height + "-" + state + ".png"))) encoder.Save(file);
  }
}
