using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MCL;

public partial class MainWindow
{
    private const double VersionScrollDurationMs = 120;
    private const double VersionScrollPixelsPerLine = 12;
    private const double WheelDeltaPerNotch = 120;
    private ScrollViewer? _versionScrollViewer;
    private DispatcherTimer? _versionScrollTimer;
    private long _versionScrollStarted;
    private double _versionScrollStart;
    private double _versionScrollTarget;

    private void RevealSelectedVersion()
    {
        if (_currentPage != "Versions" || SelectedVersion is null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (!VersionList.IsVisible || SelectedVersion is not { } selected) return;
            _versionScrollTimer?.Stop();
            VersionList.UpdateLayout();
            VersionList.ScrollIntoView(selected);
            VersionList.UpdateLayout();
        });
    }

    private void VersionList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _versionScrollViewer ??= FindVersionScrollViewer(VersionList);
        if (_versionScrollViewer is not { } viewer || e.Delta == 0)
            return;

        e.Handled = true;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0 || viewer.ScrollableHeight == 0)
            return;

        var distance = -e.Delta / WheelDeltaPerNotch * (lines < 0 ? viewer.ViewportHeight : lines * VersionScrollPixelsPerLine);
        var current = viewer.VerticalOffset;
        // Accumulate repeated wheel steps, but respond immediately when direction reverses.
        var sameDirection = _versionScrollTimer?.IsEnabled == true && (_versionScrollTarget - current) * distance > 0;
        var target = Math.Clamp((sameDirection ? _versionScrollTarget : current) + distance, 0, viewer.ScrollableHeight);

        if (!AnimatePages || target == current)
        {
            _versionScrollTimer?.Stop();
            viewer.ScrollToVerticalOffset(target);
            return;
        }

        if (_versionScrollTimer is null)
        {
            _versionScrollTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _versionScrollTimer.Tick += AdvanceVersionScroll;
        }

        _versionScrollStart = current;
        _versionScrollTarget = target;
        _versionScrollStarted = Environment.TickCount64;
        _versionScrollTimer.Start();
    }

    private void AdvanceVersionScroll(object? sender, EventArgs e)
    {
        if (!VersionList.IsVisible || !VersionList.IsEnabled || _versionScrollViewer is not { } viewer)
        {
            _versionScrollTimer?.Stop();
            return;
        }

        var progress = Math.Min((Environment.TickCount64 - _versionScrollStarted) / VersionScrollDurationMs, 1);
        var eased = progress * (2 - progress);
        viewer.ScrollToVerticalOffset(_versionScrollStart + (_versionScrollTarget - _versionScrollStart) * eased);
        if (progress == 1)
            _versionScrollTimer?.Stop();
    }

    private void VersionList_CancelWheelAnimation(object sender, RoutedEventArgs e) => _versionScrollTimer?.Stop();

    private static ScrollViewer? FindVersionScrollViewer(DependencyObject node)
    {
        if (node is ScrollViewer viewer)
            return viewer;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            if (FindVersionScrollViewer(VisualTreeHelper.GetChild(node, index)) is { } child)
                return child;
        }

        return null;
    }
}
