using System.Windows;
namespace System69.App;
public partial class ReviewWindow : Window
{
    public ReviewWindow(IReadOnlyList<ReviewRow> rows, bool advanced)
    {
        InitializeComponent();
        Heading.Text = $"Uninstall {rows.Count} app{(rows.Count == 1 ? "" : "s")}?";
        ReviewGrid.ItemsSource = rows;
        Warning.Text = advanced ? "Advanced cleanup permanently removes only the verified folders shown above. It does not remove every registry entry or personal file. Close these apps and save your work first." : "Close these apps and save your work. Some uninstallers may request administrator permission. No leftover folders will be deleted.";
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
