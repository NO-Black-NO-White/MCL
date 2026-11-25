using System.Windows;
using Microsoft.Win32;
using MCL.Models;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private readonly ModpackImportService _modpacks;
    private ModpackPreview? _selectedPack;
    private CancellationTokenSource? _packCancellation;
    private string _packStatus = "选择一个文件，查看它需要的游戏版本和模组加载器。";
    private double _packPercent;
    private bool _packIndeterminate;
    private bool _packCompleted;
    private bool _includeOptionalPackFiles;

    public bool CanChoosePack => !IsBusy && !IsGameRunning;
    public bool HasPack => _selectedPack is not null;
    public bool HasOptionalPackFiles => _selectedPack?.OptionalFiles > 0;
    public bool CanImportPack => CanChoosePack && HasPack && !PackCompleted;
    public bool CanCancelPack => _packCancellation is { IsCancellationRequested: false };
    public bool PackInProgress => _packCancellation is not null;
    public string PackName => _selectedPack?.Name ?? "";
    public string PackFileName => _selectedPack is null ? "" : Path.GetFileName(_selectedPack.ArchivePath);
    public string PackDetails => _selectedPack is { } pack
        ? $"Minecraft {pack.Minecraft}  ·  {pack.LoaderText}  ·  整合包版本 {pack.Version}" : "";
    public string PackFileSummary => _selectedPack is { } pack
        ? $"{pack.RequiredFiles:N0} 个必需文件，{pack.OptionalFiles:N0} 个可选文件。"
            + (pack.CurseForge ? "安装时查询 CurseForge 下载大小。" : $"整合包文件最多下载 {FormatBytes(pack.DownloadBytes)}，游戏本体另计。")
        : "";
    public string PackOptionalText => $"同时安装 {_selectedPack?.OptionalFiles ?? 0} 个可选文件";
    public string PackStatus { get => _packStatus; private set => Set(ref _packStatus, value); }
    public double PackPercent { get => _packPercent; private set => Set(ref _packPercent, value); }
    public bool PackIndeterminate { get => _packIndeterminate; private set => Set(ref _packIndeterminate, value); }
    public bool PackCompleted { get => _packCompleted; private set { if (Set(ref _packCompleted, value)) Notify(nameof(CanImportPack)); } }
    public bool IncludeOptionalPackFiles { get => _includeOptionalPackFiles; set => Set(ref _includeOptionalPackFiles, value); }

    private void NotifyPackActions() => Notify(nameof(CanChoosePack), nameof(CanImportPack), nameof(CanCancelPack),
        nameof(PackInProgress), nameof(ActivityText));

    private async void ChoosePack_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChoosePack) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择客户端整合包",
            Filter = "整合包文件 (*.mrpack;*.zip)|*.mrpack;*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await PreviewPackAsync(dialog.FileName);
    }

    private void Pack_DragOver(object sender, DragEventArgs e)
    {
        var paths = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
        e.Effects = CanChoosePack && paths is { Length: 1 }
            && Path.GetExtension(paths[0]).ToLowerInvariant() is ".mrpack" or ".zip"
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Pack_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (CanChoosePack && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths)
            await PreviewPackAsync(paths[0]);
    }

    private async Task PreviewPackAsync(string path)
    {
        if (!CanChoosePack) return;
        IsBusy = true;
        _selectedPack = null;
        PackCompleted = false;
        PackPercent = 0;
        IncludeOptionalPackFiles = false;
        NotifyPackSelection();
        PackStatus = "正在读取整合包清单…";
        try
        {
            _selectedPack = await ModpackImportService.PreviewAsync(path, _lifetime.Token);
            PackStatus = "检查版本和安装位置，点击「安装整合包」开始。只导入你信任的整合包。";
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            if (_closing) return;
            PackStatus = "无法读取整合包。 " + ex.Message;
            _log.Warn(PackStatus);
        }
        finally
        {
            NotifyPackSelection();
            IsBusy = false;
        }
    }

    private void NotifyPackSelection() => Notify(nameof(HasPack), nameof(PackName), nameof(PackDetails), nameof(PackFileSummary),
        nameof(HasOptionalPackFiles), nameof(PackOptionalText), nameof(PackFileName), nameof(CanImportPack));

    private async void InstallPack_Click(object sender, RoutedEventArgs e)
    {
        if (!CanImportPack || _selectedPack is not { } pack) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _packCancellation = cancellation;
        IsBusy = true;
        PackPercent = 0;
        PackIndeterminate = true;
        PackStatus = "准备安装整合包…";
        NotifyPackActions();
        try
        {
            var directory = GameDirectoryResolver.Normalize(GameDirectory);
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("请先选择一个游戏文件夹。");
            GameDirectory = directory;
            var progress = new Progress<InstallProgress>(value =>
            {
                if (_closing || !ReferenceEquals(_packCancellation, cancellation) || cancellation.IsCancellationRequested) return;
                PackIndeterminate = value.TotalBytes == 0;
                PackPercent = value.Percent;
                var count = value.TotalFiles > 0 ? $" · {value.CompletedFiles:N0}/{value.TotalFiles:N0} 个文件" : "";
                PackStatus = $"{value.Stage} · {value.Detail}{count}";
            });
            var result = await _modpacks.InstallAsync(pack, directory, IncludeOptionalPackFiles, progress, cancellation.Token, JavaPath);
            PackCompleted = true;
            PackPercent = 100;
            await ScanVersionsAsync(result.InstanceId);
            var java = _java?.MajorVersion == result.RequiredJava ? "" : $" 此整合包建议使用 Java {result.RequiredJava}。";
            PackStatus = $"「{result.Name}」已安装并选中，可返回首页启动。{java}";
            try { SaveCurrentSettings(); }
            catch (Exception ex)
            {
                PackStatus += " 启动设置未能保存，下次打开时可重新选择这个版本。";
                _log.Warn("整合包已安装，但启动设置未保存：" + ex.Message);
            }
            _log.Info($"已安装整合包 {result.Name}（{result.InstanceId}）。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            PackStatus = "已取消安装。重新安装时可复用已完成的基础游戏文件。";
        }
        catch (Exception ex)
        {
            PackStatus = (PackCompleted ? "整合包已安装，但版本列表未能刷新。 " : "安装未完成。 ") + ex.Message;
            _log.Warn(PackStatus);
        }
        finally
        {
            _packCancellation = null;
            PackIndeterminate = false;
            IsBusy = false;
            NotifyPackActions();
        }
    }

    private void CancelPack_Click(object sender, RoutedEventArgs e)
    {
        _packCancellation?.Cancel();
        NotifyPackActions();
        PackStatus = "正在取消安装…";
    }

    private void ShowPackHome_Click(object sender, RoutedEventArgs e) => HomeNav.IsChecked = true;

    public void OpenModpacks() => ModpacksNav.IsChecked = true;
}
