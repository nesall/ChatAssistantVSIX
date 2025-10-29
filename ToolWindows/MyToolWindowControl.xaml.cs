﻿using ChatAssistantVSIX.ToolWindows;
using ChatAssistantVSIX.Utils;
using Microsoft.VisualStudio.Package;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ChatAssistantVSIX
{
  public partial class MyToolWindowControl : UserControl
  {
    private readonly string testReply = "The increment operator in C++ increases the value of a variable by one. It can be used in two forms: \r\n\r\n1. **Prefix (`++variable`)**: Increments the variable and then returns the new value.\r\n2. **Postfix (`variable++`)**: Returns the current value of the variable and then increments it.\r\n\r\nFor example:\r\n```cpp\r\nint x = 5;\r\nint y = ++x; // y is 6, x is 6\r\nint z = x++; // z is 6, x is 7\r\n```";

    private ItemsControl messageList;
    private TextBox inputBox;
    private Button sendButton;
    public ObservableCollection<ChatMessage> Messages { get; } = new ObservableCollection<ChatMessage>();
    public ToolWindowMessenger ToolWindowMessenger = null;

    public MyToolWindowControl(ToolWindowMessenger toolWindowMessenger)
    {
      toolWindowMessenger ??= new ToolWindowMessenger();
      ToolWindowMessenger = toolWindowMessenger;
      toolWindowMessenger.MessageReceived += OnMessageReceived;
      InitializeComponent();
      BuildUI();
      messageList.ItemsSource = Messages;
    }

    private void BuildUI()
    {
      // Main grid
      var mainGrid = new Grid();
      mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
      mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

      // Messages area
      var scrollViewer = new ScrollViewer
      {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(8)
      };

      messageList = new ItemsControl();
      messageList.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("Messages"));
      messageList.ItemTemplateSelector = new MessageTemplateSelector
      {
        UserTemplate = CreateUserTemplate(),
        AssistantTemplate = CreateAssistantTemplate()
      };

      scrollViewer.Content = messageList;
      Grid.SetRow(scrollViewer, 0);
      mainGrid.Children.Add(scrollViewer);

      // Input area
      var inputBorder = new Border
      {
        BorderThickness = new Thickness(0, 1, 0, 0),
        BorderBrush = this.TryFindResource(VsBrushes.CommandBarBorderKey) as Brush,
        Padding = new Thickness(8)
      };

      var inputGrid = new Grid();
      inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
      inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

      inputBox = new TextBox
      {
        MinHeight = 32,
        MaxHeight = 120,
        MaxLines = 5,
        TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(8, 6, 8, 6),
        Background = this.TryFindResource(VsBrushes.SearchBoxBackgroundKey) as Brush,
        Foreground = this.TryFindResource(VsBrushes.WindowTextKey) as Brush
      };
      inputBox.PreviewKeyDown += InputBox_PreviewKeyDown;
      Grid.SetColumn(inputBox, 0);
      inputGrid.Children.Add(inputBox);

      sendButton = new Button
      {
        Content = "Send",
        MinWidth = 60,
        Height = 32,
        Margin = new Thickness(8, 0, 0, 0),
        Background = this.TryFindResource(VsBrushes.ButtonFaceKey) as Brush,
        Foreground = this.TryFindResource(VsBrushes.ButtonTextKey) as Brush
      };
      sendButton.Click += SendButton_Click;
      Grid.SetColumn(sendButton, 1);
      inputGrid.Children.Add(sendButton);

      inputBorder.Child = inputGrid;
      Grid.SetRow(inputBorder, 1);
      mainGrid.Children.Add(inputBorder);

      Content = mainGrid;
    }

    private DataTemplate CreateUserTemplate()
    {
      var factory = new FrameworkElementFactory(typeof(Border));
      factory.SetValue(Border.MarginProperty, new Thickness(0, 0, 0, 12));
      factory.SetValue(Border.PaddingProperty, new Thickness(12));
      factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
      factory.SetValue(Border.BackgroundProperty, new DynamicResourceExtension(VsBrushes.ToolWindowBackgroundKey));
      factory.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Right);
      var stackPanel = new FrameworkElementFactory(typeof(StackPanel));
      factory.AppendChild(stackPanel);

      var innerBorder = new FrameworkElementFactory(typeof(Border));
      innerBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
      innerBorder.SetValue(Border.PaddingProperty, new Thickness(8));
      innerBorder.SetValue(Border.BackgroundProperty, new DynamicResourceExtension(VsBrushes.HighlightKey));
      stackPanel.AppendChild(innerBorder);

      var textBox = new FrameworkElementFactory(typeof(TextBox));
      textBox.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("Content"));
      textBox.SetValue(TextBox.IsReadOnlyProperty, true);
      textBox.SetValue(TextBox.BorderThicknessProperty, new Thickness(0));
      textBox.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
      textBox.SetValue(TextBox.PaddingProperty, new Thickness(0));
      textBox.SetValue(TextBox.BackgroundProperty, Brushes.Transparent);
      textBox.SetValue(TextBox.ForegroundProperty, new DynamicResourceExtension(VsBrushes.HighlightTextKey));
      innerBorder.AppendChild(textBox);

      return new DataTemplate { VisualTree = factory };
    }

    private DataTemplate CreateAssistantTemplate()
    {
      var factory = new FrameworkElementFactory(typeof(Border));
      factory.SetValue(Border.MarginProperty, new Thickness(0, 0, 0, 12));
      factory.SetValue(Border.PaddingProperty, new Thickness(12));
      factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
      factory.SetValue(Border.BackgroundProperty, new DynamicResourceExtension(VsBrushes.ToolWindowBackgroundKey));

      var stackPanel = new FrameworkElementFactory(typeof(StackPanel));
      factory.AppendChild(stackPanel);

      var innerBorder = new FrameworkElementFactory(typeof(Border));
      innerBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
      innerBorder.SetValue(Border.PaddingProperty, new Thickness(8));
      innerBorder.SetValue(Border.BackgroundProperty, new DynamicResourceExtension(VsBrushes.AccentMediumKey));
      stackPanel.AppendChild(innerBorder);

      // Use FlowDocumentScrollViewer instead of MarkdownScrollViewer
      var viewerFactory = new FrameworkElementFactory(typeof(FlowDocumentScrollViewer));
      viewerFactory.SetValue(FlowDocumentScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
      viewerFactory.SetValue(FlowDocumentScrollViewer.BackgroundProperty, Brushes.Transparent);
      //viewerFactory.SetValue(FlowDocumentScrollViewer.IsDocumentEnabledProperty, true);
      viewerFactory.SetValue(FlowDocumentScrollViewer.IsHitTestVisibleProperty, true);
      viewerFactory.SetValue(FlowDocumentScrollViewer.MarginProperty, new Thickness(0));
      viewerFactory.SetValue(FlowDocumentScrollViewer.PaddingProperty, new Thickness(0));

      // Bind FlowDocument dynamically via Loaded event
      var fontSize = this.FontSize;
      var fontFamily = this.FontFamily.ToString();
      viewerFactory.AddHandler(
          FrameworkElement.LoadedEvent,
          new RoutedEventHandler((sender, _) =>
          {
            if (sender is FlowDocumentScrollViewer fds && fds.DataContext is { } ctx)
            {
              var contentProp = ctx.GetType().GetProperty("Content");
              if (contentProp?.GetValue(ctx) is string markdown)
              {
                fds.Document = MarkdownFlowDocument.Convert(markdown, fontFamily, fontSize);
              }
            }
          })
      );

      innerBorder.AppendChild(viewerFactory);

      return new DataTemplate { VisualTree = factory };
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
      SendMessage();
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
      if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
      {
        e.Handled = true;
        SendMessage();
      }
    }
    private void SendMessage()
    {
      var text = inputBox.Text.Trim();
      if (string.IsNullOrEmpty(text)) return;

      Messages.Add(new ChatMessage { Role = "user", Content = text });
      inputBox.Clear();

      // TODO: Send to your chat backend
      // Messages.Add(new ChatMessage { Role = "Assistant", Content = response });
      Messages.Add(new ChatMessage { Role = "assistant", Content = testReply });
    }

    private void OnMessageReceived(object sender, string e)
    {
      ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        await VS.MessageBox.ShowWarningAsync(e, "Button clicked");
      }).FireAndForget();
    }

  }

  internal class MessageTemplateSelector : DataTemplateSelector
  {
    public DataTemplate UserTemplate { get; set; }
    public DataTemplate AssistantTemplate { get; set; }
    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
      if (item is ChatMessage message)
      {
        return message.Role == "user" ? UserTemplate : AssistantTemplate;
      }
      return base.SelectTemplate(item, container);
    }
  }
}