global using System;
global using Task = System.Threading.Tasks.Task;
global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
namespace Microsoft.VisualStudio.PlatformUI {
 public class DialogWindow : Window { }
 public static class EnvironmentColors {
  public static object ToolWindowBackgroundBrushKey { get; } = "Environment.ToolWindowBackground";
  public static object ToolWindowTextBrushKey { get; } = "Environment.ToolWindowText";
  public static object SystemGrayTextBrushKey { get; } = "Environment.SystemGrayText";
  public static object ControlOutlineBrushKey { get; } = "Environment.ControlOutline";
  public static object ToolWindowBorderBrushKey { get; } = "Environment.ToolWindowBorder";
  public static object ComboBoxBackgroundBrushKey { get; } = "Environment.ComboBoxBackground";
  public static object ComboBoxTextBrushKey { get; } = "Environment.ComboBoxText";
 }
}
namespace Microsoft.VisualStudio.Shell {
 // Stable keys let the isolated preview replace palettes without copying resolved brushes.
 public static class VsBrushes {
  public static object CommandBarBorderKey => ButtonShadowKey;
  public static object AccentMediumKey => HighlightKey;
  public static object WindowKey { get; } = "VS.Window";
  public static object WindowTextKey { get; } = "VS.WindowText";
  public static object ButtonFaceKey { get; } = "VS.ButtonFace";
  public static object ButtonTextKey { get; } = "VS.ButtonText";
  public static object ButtonShadowKey { get; } = "VS.ButtonShadow";
  public static object GrayTextKey { get; } = "VS.GrayText";
  public static object HighlightKey { get; } = "VS.Highlight";
  public static object HighlightTextKey { get; } = "VS.HighlightText";
  public static object ToolWindowTabBorderKey { get; } = "VS.TabBorder";
  public static object ToolWindowTabMouseOverBackgroundBeginKey { get; } = "VS.TabHover";
  public static object ToolWindowTabMouseOverTextKey { get; } = "VS.TabHoverText";
  public static object ToolWindowTabMouseOverBorderKey { get; } = "VS.TabHoverBorder";
  public static object ToolWindowTabSelectedTabKey { get; } = "VS.TabSelected";
  public static object ToolWindowTabSelectedTextKey { get; } = "VS.TabSelectedText";
 }
 public static class VsResourceKeys {
  public static object ButtonStyleKey { get; } = "VS.ButtonStyle";
  public static object CheckBoxStyleKey { get; } = "VS.CheckBoxStyle";
  public static object ScrollViewerStyleKey { get; } = "VS.ScrollViewerStyle";
  public static object ScrollBarStyleKey { get; } = "VS.ScrollBarStyle";
 }
 public static class ThreadHelper { public static Factory JoinableTaskFactory { get; } = new Factory(); public static void ThrowIfNotOnUIThread() { } }
 public class Factory {
  public void Run(Func<Task> action) => action().GetAwaiter().GetResult();
  public Task SwitchToMainThreadAsync() => Task.CompletedTask;
  public Task RunAsync(Func<Task> action) => action();
 }
}
namespace Microsoft.VisualStudio.Threading {
 public static class Extensions {
  public static readonly System.Collections.Concurrent.ConcurrentQueue<Exception> Errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
  public static void FireAndForget(this Task task) {
   _ = task.ContinueWith(t => Errors.Enqueue(t.Exception), System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
  }
 }
}
namespace Community.VisualStudio.Toolkit {
 public static class ToolkitResourceKeys {
  public static object TextBoxStyleKey { get; } = "Toolkit.TextBoxStyle";
  public static object ComboBoxStyleKey { get; } = "Toolkit.ComboBoxStyle";
  public static object PasswordBoxStyleKey { get; } = "Toolkit.PasswordBoxStyle";
 }
 // Replaces only the real toolkit dictionary in preview XAML. No VS runtime is loaded.
 public class PreviewThemeResources : ResourceDictionary {
  public PreviewThemeResources() {
   ApplyPalette(Environment.GetEnvironmentVariable("PHENIXCODE_PREVIEW_THEME") ?? "Light");
   InputStyle(typeof(TextBox), ToolkitResourceKeys.TextBoxStyleKey);
   var combo = InputStyle(typeof(ComboBox), ToolkitResourceKeys.ComboBoxStyleKey);
   // The native OS ComboBox template ignores Background in its chrome. The production
   // toolkit supplies a VS template; this offscreen stand-in must bind its surface too.
   var comboBorder = new FrameworkElementFactory(typeof(Border));
   comboBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
   comboBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
   comboBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
   comboBorder.SetValue(Border.PaddingProperty, new Thickness(5));
   var selection = new FrameworkElementFactory(typeof(ContentPresenter));
   selection.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("SelectionBoxItem") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
   comboBorder.AppendChild(selection);
   combo.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ComboBox)) { VisualTree = comboBorder }));
   InputStyle(typeof(PasswordBox), ToolkitResourceKeys.PasswordBoxStyleKey);
   var button = InputStyle(typeof(Button), VsResourceKeys.ButtonStyleKey);
   var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
   hover.Setters.Add(BrushSetter(Control.BackgroundProperty, VsBrushes.ToolWindowTabMouseOverBackgroundBeginKey));
   hover.Setters.Add(BrushSetter(Control.ForegroundProperty, VsBrushes.ToolWindowTabMouseOverTextKey));
   button.Triggers.Add(hover);
   InputStyle(typeof(CheckBox), VsResourceKeys.CheckBoxStyleKey);
   this[VsResourceKeys.ScrollViewerStyleKey] = new Style(typeof(ScrollViewer));
   this[VsResourceKeys.ScrollBarStyleKey] = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar));
  }
  private static Setter BrushSetter(DependencyProperty property, object key) => new Setter(property, new System.Windows.DynamicResourceExtension(key));
  private Style InputStyle(Type type, object key) {
   var style = new Style(type);
   style.Setters.Add(BrushSetter(Control.BackgroundProperty, VsBrushes.WindowKey));
   style.Setters.Add(BrushSetter(Control.ForegroundProperty, VsBrushes.WindowTextKey));
   style.Setters.Add(BrushSetter(Control.BorderBrushProperty, VsBrushes.ButtonShadowKey));
   var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
   disabled.Setters.Add(BrushSetter(Control.ForegroundProperty, VsBrushes.GrayTextKey));
   style.Triggers.Add(disabled);
   this[key] = style;
   return style;
  }
  public void ApplyPalette(string palette) {
   // Deliberately synthetic fixture colors, never used by the extension.
   bool dark = palette == "Dark", contrast = palette == "HighContrast";
   string surface = contrast ? "#000000" : dark ? "#1E1E1E" : "#FFFFFF";
   string text = contrast ? "#FFFFFF" : dark ? "#F1F1F1" : "#1E1E1E";
   string face = contrast ? "#000000" : dark ? "#333337" : "#F0F0F0";
   string border = contrast ? "#FFFFFF" : dark ? "#707070" : "#808080";
   string highlight = contrast ? "#FFFF00" : "#007ACC";
   string selectedText = contrast ? "#000000" : "#FFFFFF";
   Put(VsBrushes.WindowKey, surface); Put(VsBrushes.WindowTextKey, text);
   Put(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBackgroundBrushKey, surface);
   Put(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowTextBrushKey, text);
   Put(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ComboBoxBackgroundBrushKey, contrast ? "#000000" : dark ? "#333337" : "#FFFFFF");
   Put(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ComboBoxTextBrushKey, text);
   Put(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ControlOutlineBrushKey, border);
   Put(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.ToolWindowBorderBrushKey, border);
   Put(Microsoft.VisualStudio.PlatformUI.EnvironmentColors.SystemGrayTextBrushKey, contrast ? "#00FF00" : dark ? "#A0A0A0" : "#666666");
   Put(VsBrushes.ButtonFaceKey, face); Put(VsBrushes.ButtonTextKey, text);
   Put(VsBrushes.ButtonShadowKey, border); Put(VsBrushes.ToolWindowTabBorderKey, border);
   Put(VsBrushes.GrayTextKey, contrast ? "#00FF00" : dark ? "#A0A0A0" : "#666666");
   Put(VsBrushes.HighlightKey, highlight); Put(VsBrushes.HighlightTextKey, selectedText);
   Put(VsBrushes.ToolWindowTabMouseOverBackgroundBeginKey, contrast ? "#00FFFF" : dark ? "#3E3E42" : "#D6E8F5");
   Put(VsBrushes.ToolWindowTabMouseOverTextKey, contrast ? "#000000" : text);
   Put(VsBrushes.ToolWindowTabMouseOverBorderKey, highlight);
   Put(VsBrushes.ToolWindowTabSelectedTabKey, highlight); Put(VsBrushes.ToolWindowTabSelectedTextKey, selectedText);
  }
  private void Put(object key, string color) => this[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
 }
 public static class Themes {
  public static readonly DependencyProperty UseVsThemeProperty = DependencyProperty.RegisterAttached("UseVsTheme", typeof(bool), typeof(Themes), new PropertyMetadata(false));
  public static void SetUseVsTheme(DependencyObject obj, bool value) {
   obj.SetValue(UseVsThemeProperty, value);
  }
  public static bool GetUseVsTheme(DependencyObject obj) => (bool)obj.GetValue(UseVsThemeProperty);
 }
 public class Project { public string FullPath { get; set; } }
 public static class VS { public static SolutionsApi Solutions { get; } = new SolutionsApi(); }
 public class SolutionsApi {
  public Task<IEnumerable<Project>> GetAllProjectsAsync() => Task.FromResult<IEnumerable<Project>>(new Project[0]);
  public Task<object> GetCurrentSolutionAsync() => Task.FromResult<object>(null);
 }
}
namespace ChatAssistantVSIX.Utils {
 public static class Diag { public static void OutputMsg(string text) { } }
 public static class PhenixCodeCoreService {
  public static string Root => AppDomain.CurrentDomain.BaseDirectory;
  public static string ScopeOverride;
  public static string ActiveSettingsPath => ScopeOverride ?? Path.Combine(Root, "fixture-settings.json");
  public static string SettingsPath => ActiveSettingsPath;
  public static string SolutionSettingsPath => ActiveSettingsPath;
  public static string ProvidersPathOverride;
  public static string ProvidersPath => ProvidersPathOverride ?? Path.Combine(Root, "fixture-providers.json");
  public static string PreferencesPath => Path.Combine(Root, "fixture-preferences.json");
  public static string SolutionDir => Root;
  public static bool IsServiceRunning { get; set; }
  public static bool IsServiceStarting { get; set; }
  public static Func<Task<bool>> HealthProbe;
  public static Func<System.Threading.CancellationToken, Task<JObject>> ProviderQuery;
  public static Func<System.Threading.CancellationToken, Task<string>> StatsQuery;
  public static Task<string> GetStatsAsync(System.Threading.CancellationToken cancellationToken = default) => StatsQuery(cancellationToken);
  public static Func<Action<string>, Collection<ChatMessage>, string, System.Threading.CancellationToken, Action<JObject>, Task<string>> ChatQuery;
  public static Task<JObject> GetChatProvidersAsync(System.Threading.CancellationToken cancellationToken = default) => ProviderQuery(cancellationToken);
  public static Task<string> ChatCompletionStreamAsync(Action<string> callback, Collection<ChatMessage> history,
   string targetApi = null, System.Threading.CancellationToken cancellationToken = default, bool propagateErrors = false, Action<JObject> onMetadata = null)
   => ChatQuery(callback, history, targetApi, cancellationToken, onMetadata);
  public static Func<string, object, Process> LaunchService;
  public static string SafePath(string path) {
   if (!Path.GetFullPath(path).StartsWith(Root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Preview path escaped fixtures.");
   return path;
  }
  public static void EnsureConfiguration() { }
  public static void InitOnSolutionReady(object solution) { }
  public static JObject LoadPreferences() => ConfigurationStore.ReadJson(PreferencesPath);
  public static Action<JObject, JObject, string, JObject> SaveObserver;
  public static void SaveConfiguration(JObject settings, JObject providers, string path, JObject preferences, Dictionary<string,string> disk) {
   if (SaveObserver == null) throw new InvalidOperationException("Saving disabled in layout preview.");
   SaveObserver((JObject)settings.DeepClone(), (JObject)providers.DeepClone(), path, (JObject)preferences.DeepClone());
  }
  public static Task<bool> IsServiceHealthyAsync() => HealthProbe?.Invoke() ?? Task.FromResult(false);
  public static Task<object> GetPaneAsync() => Task.FromResult<object>(null);
  public static Process StartProcess(string executable, object pane) => LaunchService?.Invoke(executable, pane);
  public static Task ShutdownServiceAsync() => Task.CompletedTask;
 }
}
