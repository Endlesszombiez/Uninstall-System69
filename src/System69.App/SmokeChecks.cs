using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace System69.App;

// Explicit opt-in smoke checks; never calls the uninstall executor.
public partial class MainWindow
{
    private void RunSmokeChecks()
    {
        void Assert(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
        Assert(apps.Count > 0, "Read-only Windows inventory returned no apps.");
        Directory.CreateDirectory("artifacts");
        SearchBox.Text = "system69-smoke-query-that-matches-nothing";
        Assert(view!.IsEmpty, "Search filter failed.");
        SearchBox.Text = "";
        Assert(!view.IsEmpty, "Clearing the search failed.");
        for (var i = 1; i <= 5; i++)
        {
            SourceFilter.SelectedIndex = i;
            Assert(view.Cast<AppRow>().All(MatchesFilter), "Source filter failed.");
        }
        SourceFilter.SelectedIndex = 0;
        SaveWindow(this, "artifacts/installed-apps.png");
        var first = apps.First(x => !x.App.Protected);
        var second = apps.Last(x => !x.App.Protected && x != first);
        second.Selected = true;
        first.Selected = true;
        Assert(AddButton.IsEnabled, "Checkbox selection did not enable add.");
        Add_Click(this, new RoutedEventArgs());
        Assert(queue.Count == 2 && QueuePanel.Visibility == Visibility.Visible, "Adding to queue failed.");
        SaveWindow(this, "artifacts/uninstall-queue.png");
        var review = new ReviewWindow([new(first.Name, first.Source, first.Mode, "Off")], false) { Owner = this };
        review.Show();
        SaveWindow(review, "artifacts/review.png");
        review.Close();
        Clear_Click(this, new RoutedEventArgs());
        Assert(queue.Count == 0 && !StartButton.IsEnabled, "Clearing the queue failed.");
        File.WriteAllText("artifacts/smoke-result.txt", $"PASS: read-only scan returned {apps.Count} entries. Search, source filters, multi-select binding, queue addition, review window, clear queue, and WPF rendering passed. No uninstall commands executed.\r\n");
    }

    private static void SaveWindow(Window window, string path)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
