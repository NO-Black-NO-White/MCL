using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MCL.Models;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private readonly LauncherAppearanceStore _appearanceStore;
    private LauncherAppearance _launcherAppearance;
    private BitmapSource? _versionCover;
    private BitmapSource? _saveCover;
    private int _versionCoverRequest;
    private int _saveCoverRequest;
    private ScreenshotSession? _screenshotSession;
    private (ScreenshotSession Session, string Path)? _latestScreenshot;

    private sealed record ScreenshotSession(string VersionDirectory, string Folder, DateTime StartedUtc);

    public bool IsCherryTheme => _launcherAppearance.Theme == "cherry";
    public bool IsAmethystTheme => _launcherAppearance.Theme == "amethyst";
    public bool ReduceMotion => _launcherAppearance.ReduceMotion;
    private bool AnimatePages => !ReduceMotion && SystemParameters.ClientAreaAnimation;

    public BitmapSource? VersionCover => _versionCover;
    public BitmapSource? SaveCover => _saveCover;
    public bool HasVersionCover => VersionCover is not null;
    public bool HasSaveCover => SaveCover is not null;
    public bool CanPickVersionCover => SelectedVersion is { IsValid: true };
    public bool CanUseLatestScreenshot => HasHomeSave && _latestScreenshot is { } latest
        && string.Equals(latest.Session.VersionDirectory, SelectedVersion?.VersionDirectory, StringComparison.OrdinalIgnoreCase);

    private void ApplyLauncherTheme()
    {
        var name = IsAmethystTheme ? "Amethyst" : "Cherry";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var source = new Uri($"/MCL;component/Themes/{name}.xaml", UriKind.Relative);
        if (dictionaries[0].Source != source) dictionaries[0] = new ResourceDictionary { Source = source };
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string theme })
            SaveLauncherAppearance(_launcherAppearance with { Theme = theme });
    }

    private void ReduceMotion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox option)
            SaveLauncherAppearance(_launcherAppearance with { ReduceMotion = option.IsChecked == true });
    }

    private void SaveLauncherAppearance(LauncherAppearance appearance)
    {
        try
        {
            _appearanceStore.Save(appearance);
            var themeChanged = appearance.Theme != _launcherAppearance.Theme;
            _launcherAppearance = appearance;
            if (themeChanged)
            {
                ApplyLauncherTheme();
                ApplyWindowCaption(this);
            }
            StatusMessage = "启动器外观已保存。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ReportError("外观设置未保存", ex); }
        finally { Notify(nameof(IsCherryTheme), nameof(IsAmethystTheme), nameof(ReduceMotion)); }
    }

    private string? GetCoverPath(bool save)
    {
        if (SelectedVersion is not { IsValid: true } version) return null;
        if (!save) return _appearanceStore.CoverPath("versions", version.VersionDirectory);
        var scope = (GameDirectory, version.ModpackName is null ? null : version.Id);
        if (SelectedHomeSave is not { } world || _homeSaveScope != scope) return null;
        return _appearanceStore.CoverPath("saves", Path.Combine(RuntimeFolder(version), "saves", world.Folder));
    }

    private static string RuntimeFolder(GameVersion version) =>
        version.ModpackName is null ? Path.GetDirectoryName(Path.GetDirectoryName(version.VersionDirectory))!
            : Path.Combine(version.VersionDirectory, "game");

    private async Task RefreshCoverAsync(bool save)
    {
        var request = save ? ++_saveCoverRequest : ++_versionCoverRequest;
        var path = GetCoverPath(save);
        SetCover(save, null);
        if (path is null) return;
        var image = await Task.Run(() => LauncherAppearanceStore.LoadCover(path));
        if (!_closing && request == (save ? _saveCoverRequest : _versionCoverRequest)) SetCover(save, image);
    }

    private void SetCover(bool save, BitmapSource? image)
    {
        if (save)
        {
            _saveCover = image;
            Notify(nameof(SaveCover), nameof(HasSaveCover));
        }
        else
        {
            _versionCover = image;
            Notify(nameof(VersionCover), nameof(HasVersionCover));
        }
    }

    private void HomeCover_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not Border card || e.OriginalSource is not DependencyObject source) return;
        for (var node = source; node is not null && node != card;)
        {
            if (node is ButtonBase or ComboBox) return;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        var save = ReferenceEquals(card, SaveCoverCard);
        if (GetCoverPath(save) is null || IsBusy) return;
        e.Handled = true;
        PickCover(save);
    }

    private void PickVersionCover_Click(object sender, RoutedEventArgs e) => PickCover(save: false);
    private void PickSaveCover_Click(object sender, RoutedEventArgs e) => PickCover(save: true);
    private void ResetVersionCover_Click(object sender, RoutedEventArgs e) => ResetCover(save: false);
    private void ResetSaveCover_Click(object sender, RoutedEventArgs e) => ResetCover(save: true);

    private async void PickCover(bool save)
    {
        if (IsBusy || GetCoverPath(save) is not { } target || SelectedVersion is not { } version) return;
        var screenshots = Path.Combine(RuntimeFolder(version), "screenshots");
        var dialog = new OpenFileDialog
        {
            Title = save ? "选择存档封面" : "选择版本背景",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(screenshots) ? screenshots : ""
        };
        if (dialog.ShowDialog(this) == true) await ImportCoverAsync(dialog.FileName, target, save);
    }

    private async Task ImportCoverAsync(string source, string target, bool save)
    {
        await RunBusyAsync(async () =>
        {
            try
            {
                await Task.Run(() => LauncherAppearanceStore.ImportCover(source, target));
                if (!_closing && target == GetCoverPath(save))
                {
                    await RefreshCoverAsync(save);
                    StatusMessage = save ? "存档封面已保存。" : "版本背景已保存。";
                }
            }
            catch (Exception ex) when (LauncherAppearanceStore.IsImageError(ex))
            { if (!_closing) ReportError("图片未保存", ex); }
        });
    }

    private async void ResetCover(bool save)
    {
        if (IsBusy || GetCoverPath(save) is not { } target) return;
        await RunBusyAsync(async () =>
        {
            File.Delete(target);
            await RefreshCoverAsync(save);
            StatusMessage = save ? "已移除存档封面。" : "已恢复主题背景。";
        });
    }

    private async void UseLatestScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseLatestScreenshot || _latestScreenshot is not { } latest || GetCoverPath(save: true) is not { } target) return;
        await ImportCoverAsync(latest.Path, target, save: true);
    }

    private void BeginScreenshotSession(GameVersion version)
    {
        _latestScreenshot = null;
        _screenshotSession = new(version.VersionDirectory, Path.Combine(RuntimeFolder(version), "screenshots"), DateTime.UtcNow);
        Notify(nameof(CanUseLatestScreenshot));
    }

    private async Task CompleteScreenshotSessionAsync()
    {
        if (_screenshotSession is not { } session) return;
        try
        {
            var path = await Task.Run(() => new DirectoryInfo(session.Folder)
                .EnumerateFiles("*.png", new EnumerationOptions
                {
                    IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
                })
                .Where(file => file.LastWriteTimeUtc >= session.StartedUtc)
                .MaxBy(file => file.LastWriteTimeUtc)?.FullName);
            if (!_closing && ReferenceEquals(session, _screenshotSession) && path is not null)
            {
                _latestScreenshot = (session, path);
                Notify(nameof(CanUseLatestScreenshot));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
