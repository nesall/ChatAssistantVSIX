using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ChatAssistantVSIX.Utils
{
  internal sealed class InlineCompletionProgress : IDisposable
  {
    internal const string LayerName = "PhenixCode.InlineCompletion.Progress";
    private readonly IWpfTextView view;
    private readonly IAdornmentLayer layer;
    private readonly Button indicator;
    private readonly Ellipse[] dots = new Ellipse[3];
    private object owner;
    private bool attached, subscribed;

    public InlineCompletionProgress(IWpfTextView view, Action cancel)
    {
      this.view = view;
      layer = view.GetAdornmentLayer(LayerName);
      indicator = new Button
      {
        Width = 38, Height = 20, Focusable = false, IsTabStop = false,
        BorderThickness = new Thickness(1), Padding = new Thickness(6, 0, 6, 0),
        Cursor = System.Windows.Input.Cursors.Hand,
        ToolTip = "Generating a PhenixCode suggestion. Click or press Esc to cancel.",
        UseLayoutRounding = true, SnapsToDevicePixels = true
      };
      UpdateBackground();
      indicator.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.SystemGrayTextBrushKey);
      indicator.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ControlOutlineBrushKey);
      AutomationProperties.SetName(indicator, "Generating PhenixCode suggestion. Cancel");
      AutomationProperties.SetHelpText(indicator, "Click or press Esc to cancel generation.");
      indicator.Click += (_, _) => cancel();

      // Keep a small, theme-aware pill without inheriting a large IDE button
      // template. It cannot take keyboard focus away from the editor.
      var border = new FrameworkElementFactory(typeof(Border));
      border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
      border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
      border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
      border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
      var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
      presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
      presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
      border.AppendChild(presenter);
      indicator.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
      var panel = new StackPanel { Orientation = Orientation.Horizontal };
      for (int i = 0; i < dots.Length; i++)
      {
        dots[i] = new Ellipse { Width = 4, Height = 4, Margin = new Thickness(2, 0, 2, 0) };
        dots[i].SetResourceReference(Shape.FillProperty, EnvironmentColors.SystemGrayTextBrushKey);
        panel.Children.Add(dots[i]);
      }
      indicator.Content = panel;
    }

    public void Show(object request)
    {
      if (view.IsClosed) return;
      owner = request;
      if (!subscribed)
      {
        UpdateBackground();
        VSColorTheme.ThemeChanged += ThemeChanged;
        view.LayoutChanged += LayoutChanged;
        view.ViewportLeftChanged += ViewChanged;
        view.ViewportWidthChanged += ViewChanged;
        view.ViewportHeightChanged += ViewChanged;
        view.ZoomLevelChanged += ZoomChanged;
        subscribed = true;
      }
      Position();
    }

    public void Hide(object request)
    {
      // A cancelled request may finish after a newer one starts generating.
      if (!ReferenceEquals(owner, request)) return;
      Dispose();
    }

    private void Position()
    {
      if (owner == null || view.IsClosed) return;
      if (view.InLayout) return;
      var line = view.Caret.ContainingTextViewLine;
      if (line == null || line.Bottom <= view.ViewportTop || line.Top >= view.ViewportBottom ||
          view.Caret.Left < view.ViewportLeft || view.Caret.Left > view.ViewportRight ||
          view.ViewportWidth < indicator.Width + 4 || view.ViewportHeight < indicator.Height + 4)
      {
        Remove();
        return;
      }
      double left = Math.Max(view.ViewportLeft + 2, Math.Min(view.Caret.Left + 8, view.ViewportRight - indicator.Width - 2));
      double top = line.Top - indicator.Height - 3;
      if (top < view.ViewportTop + 2) top = line.Bottom + 3;
      top = Math.Max(view.ViewportTop + 2, Math.Min(top, view.ViewportBottom - indicator.Height - 2));
      Canvas.SetLeft(indicator, left); Canvas.SetTop(indicator, top);
      if (!attached)
      {
        attached = layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, this, indicator, null);
        if (attached) Animate();
      }
    }

    private void Animate()
    {
      // Respect Windows' reduced-animation preference and high-contrast mode.
      if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;
      for (int i = 0; i < dots.Length; i++)
        dots[i].BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(450))
        { BeginTime = TimeSpan.FromMilliseconds(i * 140), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }
    private void Remove()
    {
      foreach (var dot in dots) dot.BeginAnimation(UIElement.OpacityProperty, null);
      if (attached) layer.RemoveAdornment(indicator);
      attached = false;
    }
    private void LayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Position();
    private void UpdateBackground()
    {
      // Editor adornments may not resolve the IDE's dynamic brush resource.
      // Use the theme color directly and force an opaque fill over source text.
      var color = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
      indicator.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    }
    private void ThemeChanged(ThemeChangedEventArgs e) => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      UpdateBackground();
    }).FireAndForget();
    private void ViewChanged(object sender, EventArgs e) => Position();
    private void ZoomChanged(object sender, ZoomLevelChangedEventArgs e) => Position();
    public void Dispose()
    {
      owner = null;
      Remove();
      if (!subscribed) return;
      VSColorTheme.ThemeChanged -= ThemeChanged;
      view.LayoutChanged -= LayoutChanged;
      view.ViewportLeftChanged -= ViewChanged;
      view.ViewportWidthChanged -= ViewChanged;
      view.ViewportHeightChanged -= ViewChanged;
      view.ZoomLevelChanged -= ZoomChanged;
      subscribed = false;
    }
  }

  internal sealed class InlineCompletionProgressLayer
  {
    [Export(typeof(AdornmentLayerDefinition))]
    [Name(InlineCompletionProgress.LayerName)]
    [Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Caret)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal AdornmentLayerDefinition Definition = null;
  }
}
