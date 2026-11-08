using ChatAssistantVSIX.ToolWindows;
using ChatAssistantVSIX.Utils;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;

namespace ChatAssistantVSIX
{
  public partial class MyToolWindowControl : UserControl
  {
    private readonly ChatSession session = new ChatSession();
    public ObservableCollection<ChatMessage> Messages => session.Messages;
    public ToolWindowMessenger ToolWindowMessenger;
    private CancellationTokenSource chatCts, providerCts;
    private Action flushChat;
    private readonly DispatcherTimer statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
    private string solutionScope, selectedProviderId;
    private bool activeView, refreshing, updatingProviders, providersReady, followLatest = true, autoScrolling;
    private List<ChatProvider> providerChoices = new List<ChatProvider>();
    public MyToolWindowControl(ToolWindowMessenger messenger)
    {
      ToolWindowMessenger = messenger ?? new ToolWindowMessenger();
      InitializeComponent();
      Messages.CollectionChanged += MessagesChanged;
      statusTimer.Tick += (_, _) => RefreshProvidersAsync(false).FireAndForget();
      Loaded += (_, _) =>
      {
        if (activeView) return;
        activeView = true;
        ToolWindowMessenger.MessageReceived += OnMessageReceived;
        statusTimer.Start();
        RefreshProvidersAsync(true).FireAndForget();
      };
      Unloaded += (_, _) =>
      {
        activeView = false; statusTimer.Stop();
        ToolWindowMessenger.MessageReceived -= OnMessageReceived;
        StopChat(); providerCts?.Cancel();
      };
      UpdateControls();
    }
    private void MessagesChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
      if (e.Action == NotifyCollectionChangedAction.Reset) messageList.Children.Clear();
      if (e.NewItems != null) foreach (ChatMessage message in e.NewItems) messageList.Children.Add(CreateMessage(message));
      emptyState.Visibility = Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
      FollowResponse();
    }
    private FrameworkElement CreateMessage(ChatMessage message)
    {
      bool user = message.Role == "user";
      var bubble = new Border
      {
        CornerRadius = new CornerRadius(6), Padding = new Thickness(10), BorderThickness = new Thickness(1),
        Margin = user ? new Thickness(28, 0, 0, 12) : new Thickness(0, 0, 12, 12)
      };
      bubble.SetResourceReference(Border.BackgroundProperty, user ? VsBrushes.ButtonFaceKey : VsBrushes.WindowKey);
      bubble.SetResourceReference(Border.BorderBrushProperty, VsBrushes.CommandBarBorderKey);
      var panel = new StackPanel(); bubble.Child = panel;
      var copy = new Button
      {
        Content = CopyIcon(), ToolTip = user ? "Copy message" : "Copy response", Width = 24, Height = 24,
        MinWidth = 0, Padding = new Thickness(3), Margin = new Thickness(6, 0, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top
      };
      System.Windows.Automation.AutomationProperties.SetName(copy, user ? "Copy message" : "Copy response");
      copy.Click += (_, _) => CopyText(message.CopyContent);
      var name = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
      name.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.GrayTextKey);
      if (!user)
      {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 5) }; panel.Children.Add(header);
        DockPanel.SetDock(copy, Dock.Right); header.Children.Add(copy); header.Children.Add(name);
      }
      var viewer = new RichTextBox { IsReadOnly = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
      viewer.SetResourceReference(Control.ForegroundProperty, VsBrushes.WindowTextKey);
      if (user)
      {
        var row = new DockPanel(); panel.Children.Add(row);
        DockPanel.SetDock(copy, Dock.Right); row.Children.Add(copy); row.Children.Add(viewer);
      }
      else panel.Children.Add(viewer);
      var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }; panel.Children.Add(state);
      void Render()
      {
        name.Text = string.IsNullOrEmpty(message.Provider) ? "Assistant" : message.Provider;
        state.Text = message.Status;
        state.Visibility = string.IsNullOrEmpty(message.Status) ? Visibility.Collapsed : Visibility.Visible;
        viewer.Visibility = string.IsNullOrEmpty(message.Content) && message.Sources.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        copy.IsEnabled = !string.IsNullOrEmpty(message.Content) || message.Sources.Count > 0;
        viewer.Document = message.Role == "user" ? new FlowDocument(new Paragraph(new Run(message.Content))) : MarkdownFlowDocument.Convert(message.Content, FontFamily.ToString(), FontSize);
        if (!user && message.Sources.Count > 0)
        {
          viewer.Document.Blocks.Add(new Paragraph(new Run("Sources:")) { Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold });
          var labels = message.SourceLabels;
          for (int i = 0; i < labels.Length; i++)
          {
            var path = new Run(labels[i]) { ToolTip = message.Sources[i] };
            viewer.Document.Blocks.Add(new Paragraph(path) { Margin = new Thickness(0, 1, 0, 1), FontStyle = FontStyles.Italic });
          }
        }
        viewer.Document.PagePadding = new Thickness(0);
        viewer.Document.TextAlignment = user ? TextAlignment.Right : TextAlignment.Left;
        viewer.Document.SetResourceReference(FlowDocument.ForegroundProperty, VsBrushes.WindowTextKey);
      }
      Render();
      message.PropertyChanged += (_, e) =>
      {
        if (e.PropertyName == nameof(ChatMessage.Content) || e.PropertyName == nameof(ChatMessage.Sources)) { Render(); FollowResponse(); }
        else if (e.PropertyName == nameof(ChatMessage.Status)) { state.Text = message.Status; state.Visibility = string.IsNullOrEmpty(message.Status) ? Visibility.Collapsed : Visibility.Visible; }
        else name.Text = message.Provider;
      };
      return bubble;
    }
    private static FrameworkElement CopyIcon()
    {
      var icon = new System.Windows.Shapes.Path
      {
        Data = Geometry.Parse("M4,4 L4,1 L13,1 L13,10 L10,10 M1,4 L10,4 L10,13 L1,13 Z"),
        Width = 14, Height = 14, StrokeThickness = 1.2, Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
      };
      icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
      { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
      return icon;
    }
    private void CopyText(string text)
    {
      try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); }
      catch (Exception ex) { SetNotice("Could not copy: " + ex.Message); }
    }
    private void UpdateControls()
    {
      sendButton.Content = session.IsBusy ? "Stop" : "Send";
      sendButton.ToolTip = session.IsBusy ? "Stop generation (Esc)" : "Send message (Enter)";
      var selected = providerBox.SelectedItem as ChatProvider;
      sendButton.IsEnabled = session.IsBusy || (providersReady && selected?.Available == true && !string.IsNullOrWhiteSpace(inputBox.Text));
      retryButton.Visibility = session.RetryRequest == null ? Visibility.Collapsed : Visibility.Visible;
      retryButton.IsEnabled = !session.IsBusy && providersReady;
      placeholder.Visibility = string.IsNullOrEmpty(inputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SetNotice(string text) { notice.Text = text; Diag.OutputMsg("[System] Chat: " + text); }
    private async Task RefreshProvidersAsync(bool force)
    {
      if (refreshing) return;
      refreshing = true;
      var scope = PhenixCodeCoreService.SolutionSettingsPath;
      try
      {
        if (!string.Equals(solutionScope, scope, StringComparison.OrdinalIgnoreCase))
        {
          StopChat(); session.Clear(); selectedProviderId = null; providerChoices.Clear(); providersReady = false;
          updatingProviders = true; providerBox.ItemsSource = null; updatingProviders = false;
          solutionScope = scope;
          scopeLabel.Text = string.IsNullOrEmpty(scope) ? "No solution open" : System.IO.Path.GetFileName(PhenixCodeCoreService.SolutionDir);
        }
        if (string.IsNullOrEmpty(scope) || !PhenixCodeCoreService.IsServiceRunning)
        {
          providersReady = false; providerBox.IsEnabled = false;
          serviceStatus.Text = string.IsNullOrEmpty(scope) ? "Open a solution to chat" : PhenixCodeCoreService.IsServiceStarting ? "Service starting…" : "Service stopped — open Settings to start";
          return;
        }
        // Refresh even while the process remains running: its enabled models or
        // default can change, and a restart may occur between timer ticks.
        if (providerBox.IsDropDownOpen && !force) return;
        providerCts?.Dispose(); providerCts = new CancellationTokenSource();
        var choices = ChatProvider.FromSettings(await PhenixCodeCoreService.GetChatProvidersAsync(providerCts.Token));
        if (!activeView || !string.Equals(scope, PhenixCodeCoreService.SolutionSettingsPath, StringComparison.OrdinalIgnoreCase)) return;
        var selected = selectedProviderId == null ? choices.FirstOrDefault(p => p.IsDefault) : choices.FirstOrDefault(p => p.Id == selectedProviderId);
        if (selected == null && selectedProviderId != null)
        {
          selected = new ChatProvider { Id = selectedProviderId, Label = "Selected model unavailable — choose another", Available = false };
          choices.Add(selected);
        }
        if (!providerChoices.Select(p => (p.Id, p.Label, p.Detail, p.Available)).SequenceEqual(choices.Select(p => (p.Id, p.Label, p.Detail, p.Available))) || (providerBox.SelectedItem as ChatProvider)?.Id != selected?.Id)
        {
          updatingProviders = true;
          providerBox.ItemsSource = choices; providerBox.SelectedItem = selected;
          updatingProviders = false;
        }
        providerChoices = choices;
        providersReady = choices.Any(p => p.Available); providerBox.IsEnabled = true;
        serviceStatus.Text = providersReady ? "Service running" : "No models enabled — open Settings";
      }
      catch (OperationCanceledException) { }
      catch (Exception ex)
      {
        providersReady = false; serviceStatus.Text = "Model list unavailable — open Settings or refresh";
        Diag.OutputMsg("Chat provider refresh: " + ex.Message);
      }
      finally { updatingProviders = false; refreshing = false; UpdateControls(); }
    }
    private void ProviderOpened(object sender, EventArgs e) => RefreshProvidersAsync(true).FireAndForget();
    private void ProviderChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
      if (!updatingProviders && providerBox.SelectedItem is ChatProvider choice) selectedProviderId = choice.Id;
      providerBox.ToolTip = (providerBox.SelectedItem as ChatProvider)?.Detail;
      UpdateControls();
    }
    private void InputChanged(object sender, TextChangedEventArgs e) { if (sendButton != null) UpdateControls(); }
    private void InputKeyDown(object sender, KeyEventArgs e)
    {
      ThreadHelper.ThrowIfNotOnUIThread();
      if (e.Key == Key.Escape && session.IsBusy) { StopChat(); e.Handled = true; }
      else if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
      {
        e.Handled = true;
        if (!session.IsBusy) SendMessage();
      }
    }
    private void SendClicked(object sender, RoutedEventArgs e) { ThreadHelper.ThrowIfNotOnUIThread(); if (session.IsBusy) StopChat(); else SendMessage(); }
    private void SendMessage()
    {
      ThreadHelper.ThrowIfNotOnUIThread();
      if (session.IsBusy || string.IsNullOrWhiteSpace(inputBox.Text)) return;
      if (!string.Equals(solutionScope, PhenixCodeCoreService.SolutionSettingsPath, StringComparison.OrdinalIgnoreCase)) { RefreshProvidersAsync(true).FireAndForget(); return; }
      if (!providersReady || !PhenixCodeCoreService.IsServiceRunning || !(providerBox.SelectedItem is ChatProvider provider) || !provider.Available)
      { SetNotice("Start the service and choose an available model before sending."); RefreshProvidersAsync(true).FireAndForget(); return; }
      var request = session.Begin(inputBox.Text, provider.Id, provider.Label);
      inputBox.Clear(); followLatest = true; notice.Text = "";
      RunChatAsync(request).FireAndForget();
    }
    private void RetryClicked(object sender, RoutedEventArgs e)
    {
      var retry = session.RetryRequest;
      if (retry == null || session.IsBusy) return;
      if (!providersReady || !PhenixCodeCoreService.IsServiceRunning || !providerChoices.Any(p => p.Id == retry.TargetApi && p.Available))
      { SetNotice("The original model is unavailable. Choose a model and send a new message, or restore it in Settings."); return; }
      followLatest = true; notice.Text = "";
      RunChatAsync(session.Retry()).FireAndForget();
    }
    private void StopChat()
    {
      flushChat?.Invoke(); chatCts?.Cancel(); session.Stop(); UpdateControls();
    }
    private async Task RunChatAsync(ChatSession.Request request)
    {
      var cts = new CancellationTokenSource(); chatCts = cts;
      var requestScope = solutionScope;
      var pending = new StringBuilder(); var gate = new object(); string model = null; string[] sources = null;
      void Flush()
      {
        if (!string.Equals(requestScope, PhenixCodeCoreService.SolutionSettingsPath, StringComparison.OrdinalIgnoreCase))
        {
          cts.Cancel();
          if (ReferenceEquals(session.Active, request)) session.Clear();
          return;
        }
        string text; string[] files; string responseModel;
        lock (gate) { text = pending.ToString(); pending.Clear(); files = sources; sources = null; responseModel = model; }
        if (text.Length > 0) session.Append(request, text);
        if (files != null) session.SetSources(request, files);
        if (ReferenceEquals(session.Active, request) && responseModel != null) request.Response.Provider = responseModel;
      }
      flushChat = Flush;
      var render = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
      render.Tick += (_, _) => Flush(); render.Start(); UpdateControls();
      try
      {
        var result = await PhenixCodeCoreService.ChatCompletionStreamAsync(payload =>
        {
          var text = (string)JObject.Parse(payload)["content"];
          lock (gate) pending.Append(text);
        }, request.History, targetApi: request.TargetApi, cancellationToken: cts.Token, propagateErrors: true,
          onMetadata: metadata =>
          {
            lock (gate)
            {
              if (metadata["model"]?.Type == JTokenType.String) model = (string)metadata["model"];
              if ((string)metadata["type"] == "context_sources" && metadata["sources"] is JArray paths)
                sources = paths.Where(p => p.Type == JTokenType.String).Select(p => (string)p).ToArray();
            }
          });
        Flush();
        if (cts.IsCancellationRequested) session.Fail(request, "Stopped. Partial response kept.");
        else if (string.IsNullOrWhiteSpace(result)) session.Fail(request, "The model returned no text. Retry or choose another model.");
        else session.Complete(request);
      }
      catch (Exception ex)
      {
        Flush();
        var error = cts.IsCancellationRequested ? "Stopped. Partial response kept." : ex is OperationCanceledException ? "The response timed out. Retry or choose another model." : "Response failed: " + ex.Message;
        session.Fail(request, error); Diag.OutputMsg("Chat request: " + error);
      }
      finally
      {
        render.Stop(); cts.Dispose();
        if (ReferenceEquals(chatCts, cts)) { chatCts = null; flushChat = null; }
        UpdateControls();
      }
    }
    private void TranscriptScrolled(object sender, ScrollChangedEventArgs e)
    {
      if (!ReferenceEquals(e.OriginalSource, transcript)) return;
      if (!autoScrolling && e.ExtentHeightChange == 0 && e.VerticalChange != 0) followLatest = transcript.ScrollableHeight - transcript.VerticalOffset < 24;
      jumpButton.Visibility = followLatest ? Visibility.Collapsed : Visibility.Visible;
    }
    private void FollowResponse()
    {
      if (!followLatest) { jumpButton.Visibility = Visibility.Visible; return; }
      ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        await Task.Yield();
        if (!followLatest) return;
        autoScrolling = true;
        transcript.UpdateLayout(); transcript.ScrollToEnd(); autoScrolling = false;
      }).FireAndForget();
    }
    private void JumpClicked(object sender, RoutedEventArgs e) { followLatest = true; FollowResponse(); jumpButton.Visibility = Visibility.Collapsed; }
    private void SettingsClicked(object sender, RoutedEventArgs e) => OpenSettingsAsync().FireAndForget();
    private async Task OpenSettingsAsync()
    {
      try
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dialog = new Dialogs.SettingsDialog();
        new WindowInteropHelper(dialog) { Owner = Process.GetCurrentProcess().MainWindowHandle };
        dialog.ShowDialog();
        await RefreshProvidersAsync(true);
      }
      catch (Exception ex) { SetNotice("Could not open Settings: " + ex.Message); }
    }
    private void OnMessageReceived(object sender, string e)
    {
      ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        //await VS.MessageBox.ShowWarningAsync(e, "Button clicked");
        try
        {
          switch (e)
          {
            case "ButtonSettingsCommand":
              break;
            case "cmdidButtonClearChat":
              await ClearChatAsync();
              break;
            default:
              break;
          }
        }
        catch (Exception x)
        {
          Diag.OutputMsg(x.Message);
        }

      }).FireAndForget();
    }

    private async Task ClearChatAsync()
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      StopChat(); session.Clear(); selectedProviderId = null; notice.Text = ""; followLatest = true;
      RefreshProvidersAsync(true).FireAndForget(); UpdateControls();
    }

  }
}
