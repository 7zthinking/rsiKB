using System.Windows;

namespace KBEA.App.Views;

public partial class InputDialog : Window
{
    public InputDialog(string title, string label, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        Value = defaultValue;
        ValueBox.Text = defaultValue;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    public string Value { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Value = ValueBox.Text;
        DialogResult = true;
    }
}
