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

namespace ChatAssistantVSIX.Dialogs
{
  public partial class StatsDialog : Window
  {
    public string ApiKey
    {
      get => ApiKeyTextBox.Text;
      set => ApiKeyTextBox.Text = value;
    }

    public bool EnableFeature
    {
      get => EnableFeatureCheckBox.IsChecked == true;
      set => EnableFeatureCheckBox.IsChecked = value;
    }

    public StatsDialog()
    {
      InitializeComponent();
      // Optionally initialize from persisted settings here
      // ApiKey = Properties.Settings.Default.ApiKey;
      // EnableFeature = Properties.Settings.Default.EnableFeature;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
      DialogResult = true;
      Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
      DialogResult = false;
      Close();
    }
  }
}
