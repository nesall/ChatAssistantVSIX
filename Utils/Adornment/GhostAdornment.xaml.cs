using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace ChatAssistantVSIX.Utils.Adornment
{
  /// <summary>
  /// Interaction logic for GhostAdornment.xaml
  /// </summary>
  public partial class GhostAdornment : Border
  {
    public event Action AcceptClicked;
    public event Action RejectClicked;

    public GhostAdornment(string text)
    {
      InitializeComponent();
      ContentText.Text = text;
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
      AcceptClicked?.Invoke();
    }

    private void RejectButton_Click(object sender, RoutedEventArgs e)
    {
      RejectClicked?.Invoke();
    }

    public string Text
    {
      get { return ContentText.Text; }
    }
  }
}
