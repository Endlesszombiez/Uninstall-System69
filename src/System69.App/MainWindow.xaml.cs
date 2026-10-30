using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System69.App.Services;
using System69.Core;

namespace System69.App;

public partial class MainWindow : Window
{
    public static RoutedUICommand HideAppCommand { get; } = new("Hide application", "HideApp", typeof(MainWindow));
    public static RoutedUICommand UnhideAppCommand { get; } = new("Unhide application", "UnhideApp", typeof(MainWindow));
    private readonly InventoryService inventory = new();
    private readonly CleanupService cleanup = new();
    private readonly ObservableCollection<AppRow> apps = [];
    private readonly ObservableCollection<QueueRow> queue = [];
    private readonly HiddenApplications hiddenApps;
    private readonly string hiddenPreferencesPath;
    private ICollectionView? view;
    private bool scanning;
    private bool running;
    private bool stopRequested;
    private bool rebootRequired;
    private bool inventoryStale;

    public MainWindow()
    {
        hiddenPreferencesPath = Environment.GetCommandLineArgs().Contains("--smoke-test")
            ? Path.Combine(Path.GetTempPath(), "system69-hidden-smoke-" + Guid.NewGuid().ToString("N") + ".json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "System69", "hidden-apps.json");
        hiddenApps = new(hiddenPreferencesPath);
        hiddenApps.Load();
        InitializeComponent();
        view = CollectionViewSource.GetDefaultView(apps);
        view.Filter = MatchesFilter;
        AppsGrid.ItemsSource = view;
        QueueGrid.ItemsSource = queue;
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) { AdminButton.Content = "Administrator session"; AdminButton.IsEnabled = false; }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        if (Environment.GetCommandLineArgs().Contains("--smoke-test"))
        {
            try { RunSmokeChecks(); Environment.ExitCode = 0; }
            catch (Exception ex)
            {
                Directory.CreateDirectory("artifacts");
                File.WriteAllText("artifacts/smoke-error.txt", ex.ToString());
                Environment.ExitCode = 1;
            }
            finally { if (File.Exists(hiddenPreferencesPath)) File.Delete(hiddenPreferencesPath); }
            Close();
        }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (running || scanning) return;
        scanning = true;
        RefreshButton.IsEnabled = AddButton.IsEnabled = StartButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        NoticesText.Text = "";
        try
        {
            var found = await inventory.ScanAsync(BrewOption.IsChecked == true, message => Dispatcher.Invoke(() => StatusText.Text = message), CancellationToken.None);
            apps.Clear();
            foreach (var app in found)
            {
                var row = new AppRow(app) { Hidden = hiddenApps.Contains(app) };
                row.PropertyChanged += RowChanged;
                apps.Add(row);
            }
            inventoryStale = false;
            AppCount.Text = apps.Count.ToString("N0");
            TotalSize.Text = $"{apps.Sum(x => x.App.SizeBytes) / (double)(1L << 30):0.0} GB";
            NoticesText.Text = string.Join("  •  ", inventory.Notices.Concat(hiddenApps.LoadError is { } error ? [error] : Array.Empty<string>()));
            StatusText.Text = $"Last scanned {DateTime.Now:t}. Entries from different sources may describe the same app. Sizes are reported by Windows.";
            ApplyFilter();
        }
        catch (Exception ex) { StatusText.Text = "Inventory scan failed: " + ex.Message; }
        finally
        {
            scanning = false;
            RefreshButton.IsEnabled = true;
            Progress.IsIndeterminate = false;
            Progress.Visibility = Visibility.Collapsed;
            UpdateSelection();
            UpdateQueue();
        }
    }

    private bool MatchesFilter(object value)
    {
        if (value is not AppRow row || row.Hidden && ShowHidden.IsChecked != true) return false;
        var text = SearchBox.Text.Trim();
        var matchesText = text.Length == 0 || row.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || row.Publisher.Contains(text, StringComparison.CurrentCultureIgnoreCase) || row.App.Id.Contains(text, StringComparison.OrdinalIgnoreCase);
        var source = SourceFilter.SelectedIndex;
        return matchesText && (source == 0 || row.App.Source == source switch { 1 => PackageSource.Windows, 2 => PackageSource.Winget, 3 => PackageSource.Chocolatey, 4 => PackageSource.Store, _ => PackageSource.Homebrew });
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Source_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void ApplyFilter()
    {
        if (view is null) return;
        view.Refresh();
        AppCount.Text = apps.Count(x => !x.Hidden || ShowHidden.IsChecked == true).ToString("N0");
        TotalSize.Text = $"{apps.Where(x => !x.Hidden || ShowHidden.IsChecked == true).Sum(x => x.App.SizeBytes) / (double)(1L << 30):0.0} GB";
        EmptyText.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = apps.Count == 0 ? "No removable apps found. Refresh to try again."
            : ShowHidden.IsChecked != true && apps.All(x => x.Hidden) ? "All apps are hidden. Enable Show hidden apps to restore them."
            : "No apps match your search.";
        SelectAllBox.IsChecked = false;
    }

    private void HiddenFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (view is null) return;
        if (ShowHidden.IsChecked != true)
            foreach (var row in apps.Where(x => x.Hidden)) row.Selected = false;
        AppsGrid.UnselectAll();
        ApplyFilter();
        UpdateSelection();
    }
    private void HideSelected_Click(object sender, RoutedEventArgs e) => SetHidden(apps.Where(x => x.Selected && !x.Hidden).ToList(), true);
    private void UnhideSelected_Click(object sender, RoutedEventArgs e) => SetHidden(apps.Where(x => x.Selected && x.Hidden).ToList(), false);
    private void HideApp_Executed(object sender, ExecutedRoutedEventArgs e) { if (e.Parameter is AppRow row) SetHidden([row], true); }
    private void UnhideApp_Executed(object sender, ExecutedRoutedEventArgs e) { if (e.Parameter is AppRow row) SetHidden([row], false); }

    private void SetHidden(IReadOnlyList<AppRow> rows, bool hide)
    {
        if (running || scanning || rows.Count == 0) return;
        try
        {
            // Commit preferences before changing the interface, so failures never silently lose a hide.
            hiddenApps.Set(rows.Select(x => x.App), hide);
            foreach (var row in rows) { row.Hidden = hide; row.Selected = false; }
            var removed = 0;
            if (hide)
                foreach (var queued in queue.Where(x => x.Status == "Ready" && hiddenApps.Contains(x.App)).ToList()) { queue.Remove(queued); removed++; }
            AppsGrid.UnselectAll();
            ApplyFilter();
            UpdateSelection();
            UpdateQueue();
            StatusText.Text = hide
                ? $"Hidden {rows.Count} app(s). Use Show hidden apps to unhide them." + (removed > 0 ? $" Removed {removed} pending queue entry/entries." : "")
                : $"Unhidden {rows.Count} app(s).";
        }
        catch (Exception ex) { StatusText.Text = "Could not save hidden apps: " + ex.Message; }
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(AppRow.Selected)) UpdateSelection(); }
    private void UpdateSelection()
    {
        var selected = apps.Count(x => x.Selected);
        var visibleSelected = apps.Count(x => x.Selected && !x.Hidden);
        HideButton.IsEnabled = visibleSelected > 0 && !running && !scanning;
        UnhideButton.IsEnabled = apps.Any(x => x.Selected && x.Hidden) && !running && !scanning;
        SelectionLabel.Text = $"{selected} app{(selected == 1 ? "" : "s")} selected";
        AddButton.IsEnabled = visibleSelected > 0 && !running && !scanning;
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (view is null) return;
        foreach (var row in view.Cast<AppRow>()) row.Selected = SelectAllBox.IsChecked == true && !row.App.Protected;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var skipped = 0;
        foreach (var row in apps.Where(x => x.Selected && !x.Hidden))
        {
            if (row.App.Protected || queue.Any(x => x.App.Source == row.App.Source && x.App.Id == row.App.Id)) { skipped++; continue; }
            queue.Add(new(row.App));
            row.Selected = false;
        }
        UpdateQueue();
        ShowQueue(true);
        StatusText.Text = skipped == 0 ? "Queue ready. Choose your options and review before starting." : $"Queue ready. {skipped} protected or duplicate entries were skipped.";
    }
    private void UpdateQueue()
    {
        QueueCount.Text = $"{queue.Count} app{(queue.Count == 1 ? "" : "s")}";
        QueueNav.Content = $"☷   Uninstall queue ({queue.Count})";
        StartButton.IsEnabled = queue.Any(x => x.Status == "Ready") && !running && !scanning;
    }
    private void AppsNav_Click(object sender, RoutedEventArgs e) => ShowQueue(false);
    private void QueueNav_Click(object sender, RoutedEventArgs e) => ShowQueue(true);
    private void ShowQueue(bool show)
    {
        AppsPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        QueuePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = show ? "Uninstall queue" : "Installed apps";
        PageSubtitle.Text = show ? "A fresh start, one app at a time." : "Find what you no longer need. We’ll take it from here.";
        AppsNav.Background = new System.Windows.Media.SolidColorBrush(show ? System.Windows.Media.Colors.Transparent : System.Windows.Media.Color.FromRgb(220, 235, 227));
        QueueNav.Background = new System.Windows.Media.SolidColorBrush(show ? System.Windows.Media.Color.FromRgb(220, 235, 227) : System.Windows.Media.Colors.Transparent);
    }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (QueueGrid.SelectedItem is QueueRow row) queue.Remove(row); UpdateQueue(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { queue.Clear(); UpdateQueue(); }
    private void Stop_Click(object sender, RoutedEventArgs e) { stopRequested = true; StopButton.IsEnabled = false; StatusText.Text = "Stopping after the current app finishes. Its uninstaller will not be interrupted."; }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (running || scanning) return;
        if (inventoryStale) { MessageBox.Show(this, "Refresh installed apps before starting another batch.", "Refresh required", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var pending = queue.Where(x => x.Status == "Ready").ToList();
        var silent = SilentOnly.IsChecked == true;
        var advanced = AdvancedOption.IsChecked == true;
        var snapshot = apps.Select(x => x.App).ToList();
        var approved = new Dictionary<QueueRow, CleanupDecision>();
        var commands = new Dictionary<QueueRow, UninstallCommand>();
        var reviews = new List<ReviewRow>();
        foreach (var row in pending)
        {
            try
            {
                var command = CommandPolicy.Create(row.App);
                if (silent && !command.Quiet) { row.Status = "Skipped"; row.Detail = "No documented silent command. Disable silent only and re-add to allow an interactive uninstall."; continue; }
                commands[row] = command;
                var decision = cleanup.Policy.Evaluate(row.App, snapshot);
                approved[row] = decision;
                reviews.Add(new(row.Name, row.Source, command.Quiet ? "Silent requested" : "May show a window", advanced ? decision.Allowed ? decision.CanonicalPath! : "Skipped: " + decision.Reason : "Off"));
            }
            catch (Exception ex) { row.Status = "Blocked"; row.Detail = ex.Message; }
        }
        if (commands.Count == 0) { StatusText.Text = "No eligible apps. Review the queue details."; UpdateQueue(); return; }
        if (new ReviewWindow(reviews, advanced) { Owner = this }.ShowDialog() != true) { UpdateQueue(); return; }
        try { ActivityLog.Write("Queue", "System69", "Started", $"{commands.Count} apps; advanced cleanup: {advanced}"); }
        catch (Exception ex) { MessageBox.Show(this, "Cannot write the activity log. No uninstall has started. " + ex.Message, "Log unavailable"); return; }
        running = true;
        stopRequested = false;
        SetBusy(true);
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = false;
        Progress.Maximum = commands.Count;
        Progress.Value = 0;
        try
        {
            foreach (var (row, command) in commands)
            {
                if (stopRequested || rebootRequired) break;
                row.Status = "Running";
                row.Detail = "Uninstaller is running…";
                StatusText.Text = "Uninstalling " + row.Name + "…";
                QueueGrid.ScrollIntoView(row);
                try
                {
                    if (row.App.Source == PackageSource.Windows && !InventoryService.IsRegistered(row.App))
                    { row.Status = "Skipped"; row.Detail = "Registration is already gone. Refresh the inventory."; }
                    else
                    {
                        var result = await ProcessRunner.RunAsync(command.FileName, command.Arguments, CancellationToken.None, rawArguments: command.RawArguments, quiet: command.Quiet);
                        inventoryStale = true;
                        var supportsRebootCodes = row.App.WindowsInstaller || row.App.Source == PackageSource.Chocolatey;
                        if (result.ExitCode == 0 || supportsRebootCodes && result.ExitCode is 3010 or 1641)
                        {
                            rebootRequired = supportsRebootCodes && result.ExitCode is 3010 or 1641;
                            row.Status = rebootRequired ? "Restart needed" : "Completed";
                            row.Detail = "Uninstaller reported success.";
                            if (rebootRequired) row.Detail = "Restart Windows before continuing. Cleanup was skipped.";
                            else if (advanced && approved[row].Allowed)
                            {
                                row.Detail = "Checking leftover folder…";
                                var fresh = await Task.Run(InventoryService.ReadWindowsInventory);
                                row.Detail = await Task.Run(() => cleanup.Clean(row.App, fresh, approved[row].CanonicalPath!));
                            }
                            else if (advanced) row.Detail += " Cleanup skipped: " + approved[row].Reason;
                            else if (row.App.Source == PackageSource.Windows && InventoryService.IsRegistered(row.App)) { row.Status = "Verify"; row.Detail = "Uninstaller exited successfully but registration remains. Refresh or finish its prompts."; }
                        }
                        else
                        {
                            row.Status = "Failed";
                            var error = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
                            row.Detail = $"Exit {result.ExitCode}. " + (string.IsNullOrWhiteSpace(error) ? "Check the uninstaller or try an administrator session." : error.Trim()[..Math.Min(error.Trim().Length, 500)]);
                        }
                    }
                }
                catch (ProcessStillRunningException ex) { stopRequested = true; row.Status = "Check process"; row.Detail = ex.Message; }
                catch (Exception ex) { row.Status = "Failed"; row.Detail = ex.Message; }
                try { ActivityLog.Write(row.Name, row.Source, row.Status, row.Detail); }
                catch (Exception ex) { stopRequested = true; NoticesText.Text = "Activity log failed; stopping the queue. " + ex.Message; }
                Progress.Value++;
            }
        }
        finally
        {
            running = false;
            SetBusy(false);
            Progress.Visibility = Visibility.Collapsed;
            StatusText.Text = rebootRequired ? "Restart Windows before running another batch." : stopRequested ? "Queue stopped. Review remaining entries before continuing." : "Batch finished. Review results and refresh the app list.";
            UpdateQueue();
            if (rebootRequired) StartButton.IsEnabled = false;
        }
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = RemoveButton.IsEnabled = ClearButton.IsEnabled = SilentOnly.IsEnabled = AdvancedOption.IsEnabled = BrewOption.IsEnabled = !busy;
        AdminButton.IsEnabled = !busy && AdminButton.Content?.ToString() != "Administrator session";
        StartButton.IsEnabled = !busy;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = true;
        UpdateSelection();
        ShowHidden.IsEnabled = !busy;
    }
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(ActivityLog.DirectoryPath); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { ActivityLog.DirectoryPath }, UseShellExecute = false }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot open logs"); }
    }
    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        if (queue.Count > 0 && MessageBox.Show(this, "Restarting as administrator clears the current queue. Continue?", "Restart app", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" }); Close(); }
        catch (Win32Exception ex) { StatusText.Text = "Administrator restart was cancelled or unavailable. " + ex.Message; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (running) { e.Cancel = true; StatusText.Text = "Use Stop after current, then wait for the uninstaller to finish before closing."; }
    }
}
