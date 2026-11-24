using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MCL;

public partial class App : Application
{
    private bool _smokeMode;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        var smokeIndex = Array.IndexOf(e.Args, "--smoke-test");
        var smoke = smokeIndex >= 0;
        _smokeMode = smoke;
        string? dataDirectory = null;
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        if (dataIndex >= 0 && dataIndex + 1 < e.Args.Length) dataDirectory = Path.GetFullPath(e.Args[dataIndex + 1]);
        if (smoke && dataDirectory is null) { Shutdown(2); return; }
        var window = new MainWindow(dataDirectory, offlinePreview: smoke);
        MainWindow = window;
        try
        {
            if (smoke)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -10000;
                window.Top = -10000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
            }
            window.Show();
            await window.InitializeAsync();
            var catalogIndex = Array.IndexOf(e.Args, "--catalog-fixture");
            if (smoke && catalogIndex >= 0 && catalogIndex + 1 < e.Args.Length) window.LoadSmokeCatalog(e.Args[catalogIndex + 1]);
            var detailsIndex = Array.IndexOf(e.Args, "--details-fixture");
            if (smoke && detailsIndex >= 0 && detailsIndex + 1 < e.Args.Length) window.LoadSmokeVersionDetails(e.Args[detailsIndex + 1]);
            if (!smoke) { if (e.Args.Contains("--downloads")) window.OpenDownloads(); else if (e.Args.Contains("--versions")) window.OpenVersions(); else if (e.Args.Contains("--accounts")) window.OpenAccounts(); return; }
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await window.ValidateUiAsync();
            window.Width = 1180;
            window.Height = 800;
            var visual = (FrameworkElement)window.Content;
            visual.Measure(new Size(1180, 760));
            visual.Arrange(new Rect(0, 0, 1180, 760));
            visual.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (smokeIndex + 1 < e.Args.Length)
            {
                var screenshotPath = Path.GetFullPath(e.Args[smokeIndex + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
                foreach (var page in new[] { "Home", "Versions", "Downloads", "Accounts", "Settings", "Logs" })
                {
                    window.SelectSmokePage(page);
                    visual.UpdateLayout();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                    var bitmap = new RenderTargetBitmap(1180, 760, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var path = page == "Home" ? screenshotPath : Path.Combine(dataDirectory ?? Path.GetDirectoryName(screenshotPath)!, "ui-" + page.ToLowerInvariant() + ".png");
                    using var output = File.Create(path);
                    encoder.Save(output);
                }
            }
            window.WriteSmokeResult("UI_SMOKE_OK: navigation, settings persistence, version scan and WPF layout passed.");
            window.Close();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            if (smoke)
            {
                window.WriteSmokeResult("UI_SMOKE_FAILED: " + ex);
                Shutdown(1);
            }
            else window.ReportError("初始化失败", ex);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_smokeMode)
        {
            if (MainWindow is MainWindow smokeWindow) smokeWindow.WriteSmokeResult("UI_SMOKE_FAILED: " + e.Exception);
            e.Handled = true;
            Shutdown(1);
            return;
        }
        if (MainWindow is MainWindow window) window.ReportError("操作未能完成", e.Exception);
        else MessageBox.Show(e.Exception.Message, "MCL", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
