using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChatAssistantVSIX.Dialogs;
using ChatAssistantVSIX.Utils;
internal static class SettingsDialogPreview {
 [STAThread] private static int Main() {
  try {
   File.WriteAllText(PhenixCodeCoreService.PreferencesPath, "{\"executable_path\":\"\",\"watch\":false,\"watch_interval\":60,\"fim_provider\":\"\"}");
   var before = Directory.GetFiles(PhenixCodeCoreService.Root, "fixture-*.json").ToDictionary(p => p, File.ReadAllText);
   var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
   var dialog = new SettingsDialog();
   var content = (FrameworkElement)dialog.Content;
   var tabs = (TabControl)dialog.FindName("Pages");
   var status = (TextBox)dialog.FindName("Status");
   Assert(((Button)dialog.FindName("Apply")).IsEnabled, "Dialog initialization failed: " + status.Text);
   var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
   var settingsBefore = ((Newtonsoft.Json.Linq.JObject)typeof(SettingsDialog).GetField("settings", flags).GetValue(dialog)).DeepClone();
   var providersBefore = ((Newtonsoft.Json.Linq.JObject)typeof(SettingsDialog).GetField("providers", flags).GetValue(dialog)).DeepClone();
   typeof(SettingsDialog).GetMethod("Capture", flags).Invoke(dialog, new object[] { null });
   Assert(Newtonsoft.Json.Linq.JToken.DeepEquals(settingsBefore, (Newtonsoft.Json.Linq.JObject)typeof(SettingsDialog).GetField("settings", flags).GetValue(dialog)), "Unedited forms changed project settings");
   Assert(Newtonsoft.Json.Linq.JToken.DeepEquals(providersBefore, (Newtonsoft.Json.Linq.JObject)typeof(SettingsDialog).GetField("providers", flags).GetValue(dialog)), "Unedited forms changed shared providers");
   var expected = new[] { "Sources", "Models", "Shared providers", "Index & storage", "Runtime", "JSON" };
   Assert(tabs.Items.Cast<TabItem>().Select(t => (string)t.Header).SequenceEqual(expected), "Unexpected tabs");
   var report = new List<string> { "Offscreen STA WPF preview; stub VS theme; sizes are CONTENT dimensions (no window chrome)." };
   report.AddRange(SettingsDialogInteractions.Run());
   report.AddRange(ChatWindowInteractions.Run());
   report.AddRange(StatsDialogInteractions.Run());
   foreach (var size in new[] { new Size(1000,580), new Size(780,500) }) {
    foreach (TabItem tab in tabs.Items) {
     tabs.SelectedItem = tab;
     if ((string)tab.Header == "Models") { var panel = (StackPanel)((ScrollViewer)tab.Content).Content; panel.Children.OfType<TabControl>().Single().SelectedIndex = 0; }
     Render(content, size);
     var visuals = Walk(content).ToArray();
     Assert(tabs.ActualHeight > 150, "Tab viewport too short");
     foreach (var name in new[] { "Apply", "Save", "Service" }) {
      var button = (Button)dialog.FindName(name);
      var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
      Assert(bounds.Left >= 0 && bounds.Right <= size.Width && bounds.Top >= 0 && bounds.Bottom <= size.Height, name + " outside viewport");
     }
     Assert(visuals.OfType<ScrollViewer>().Any(), "Missing scrolling");
     Assert(visuals.OfType<TextBox>().Count() >= 2 && visuals.OfType<Button>().Count() >= 4, "Missing expected form controls");
     var slug = ((string)tab.Header).Replace(" ", "-").Replace("&", "and");
     var filename = string.Format("{0}x{1}-{2}.png", size.Width,size.Height,slug);
     Save(content,size,filename);
     report.Add(filename + ": textboxes=" + visuals.OfType<TextBox>().Count() + ", buttons=" + visuals.OfType<Button>().Count() + ", checks=" + visuals.OfType<CheckBox>().Count() + ", tabHeight=" + tabs.ActualHeight);
     if ((string)tab.Header == "Models") {
      var nested = visuals.OfType<TabControl>().First(t => t != tabs);
      nested.SelectedIndex = 1; Render(content,size); Save(content,size,filename.Replace(".png","-Generation.png"));
     }
    }
   }
   foreach (var pair in before) Assert(File.ReadAllText(pair.Key) == pair.Value, "Fixture changed: " + pair.Key);
   Assert(!dialog.IsVisible, "Preview unexpectedly visible");
   Assert(Microsoft.VisualStudio.Threading.Extensions.Errors.IsEmpty, "An asynchronous preview operation failed.");
   report.Add("PASS: all six tabs at both sizes; footer bounds, viewport, scrolling, fixtures unchanged, no Show.");
   File.WriteAllLines(Path.Combine(PhenixCodeCoreService.Root,"layout-report.txt"),report);
   Console.WriteLine(string.Join(Environment.NewLine,report));
   app.Shutdown(); return 0;
  } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
 }
 private static void Assert(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
 private static IEnumerable<DependencyObject> Walk(DependencyObject node) {
  yield return node;
  for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) foreach(var child in Walk(VisualTreeHelper.GetChild(node,i))) yield return child;
 }
 private static void Render(FrameworkElement content,Size size) {
  content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
  content.Dispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(()=>{})); content.UpdateLayout();
 }
 private static void Save(FrameworkElement content,Size size,string name) {
  var bitmap = new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);
  var background = new DrawingVisual(); using(var drawing = background.RenderOpen()) drawing.DrawRectangle(Brushes.White,null,new Rect(size));
  bitmap.Render(background); bitmap.Render(content);
  var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
  using(var stream = File.Create(Path.Combine(PhenixCodeCoreService.Root,name))) encoder.Save(stream);
 }
}

