using System.Collections;
using System.Windows;

namespace KBEA.App.Views;

public partial class RecordListWindow : Window
{
    public RecordListWindow(string title, IEnumerable records)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        RecordsGrid.ItemsSource = records;
    }
}
