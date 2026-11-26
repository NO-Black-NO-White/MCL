using System.Windows;
using Microsoft.Win32;
using MCL.Models;
using MCL.Services;

namespace MCL;

public partial class MainWindow
{
    private readonly JavaDiscovery _javaDiscovery = new();
    private IReadOnlyList<JavaInstallation> _installedJava = [];
    private JavaInstallation? _configuredJava;
    private bool _autoMatchJava;
    private string _javaPath;

    public bool AutoMatchJava
    {
        get => _autoMatchJava;
        set
        {
            if (!Set(ref _autoMatchJava, value)) return;
            MarkDirty();
            NotifyJava();
        }
    }

    public string JavaPath
    {
        get => _javaPath;
        set
        {
            if (!Set(ref _javaPath, value)) return;
            _configuredJava = null;
            MarkDirty();
            NotifyJava();
        }
    }

    public string JavaSummary => SelectedVersion is { IsValid: true } version
        ? FindMatchingJava(version.RequiredJava) is { } installation ? DescribeJava(installation)
            : AutoMatchJava ? $"需要 Java {version.RequiredJava}" : "检查手动配置"
        : "选择游戏版本后匹配";

    public string JavaDetail
    {
        get
        {
            if (!AutoMatchJava) return _configuredJava is { } configured
                ? $"手动选择：{DescribeJava(configured)}"
                : "选择 java.exe，或开启自动匹配。";
            var majors = _installedJava.Append(_configuredJava).OfType<JavaInstallation>()
                .Where(java => java.Is64Bit).Select(java => java.MajorVersion).Distinct().Order();
            var detected = string.Join("、", majors);
            return detected.Length == 0 ? "尚未检测到 64 位 Java。" : $"已检测到 64 位 Java {detected}。";
        }
    }

    private static string DescribeJava(JavaInstallation java) => $"Java {java.MajorVersion} · {(java.Is64Bit ? "64" : "32")} 位";

    private JavaInstallation? FindMatchingJava(int required) => AutoMatchJava
        ? JavaDiscovery.Matches(_configuredJava, required) ? _configuredJava
            : _installedJava.FirstOrDefault(candidate => JavaDiscovery.Matches(candidate, required))
        : _configuredJava is { } configured && configured.MajorVersion >= required ? configured : null;

    private void NotifyJava()
    {
        Notify(nameof(JavaSummary), nameof(JavaDetail));
        if (_selectedInstallInfo is { } info) RemoteJava = FormatRemoteJava(info.RequiredJava);
    }

    private async Task ScanJavaAsync(CancellationToken ct)
    {
        var scan = _javaDiscovery.FindAsync(ct);
        var configured = _javaDiscovery.InspectAsync(JavaPath, ct);
        await Task.WhenAll(scan, configured);
        _installedJava = await scan;
        _configuredJava = await configured;
        NotifyJava();
    }

    private async Task<JavaInstallation> ResolveJavaForLaunchAsync(GameVersion version, CancellationToken ct)
    {
        if (!AutoMatchJava)
        {
            _configuredJava = await _javaDiscovery.InspectAsync(JavaPath, ct);
            NotifyJava();
            if (FindMatchingJava(version.RequiredJava) is { } manual) return manual;
            throw new InvalidDataException($"「{version.DisplayName}」需要 Java {version.RequiredJava}。请检查手动路径，或在「启动设置」开启自动匹配。");
        }

        var candidate = FindMatchingJava(version.RequiredJava);
        if (candidate is not null && await _javaDiscovery.InspectAsync(candidate.Path, ct) is { } verified
            && JavaDiscovery.Matches(verified, version.RequiredJava))
            return verified;

        await ScanJavaAsync(ct);
        return FindMatchingJava(version.RequiredJava)
            ?? throw new InvalidDataException($"「{version.DisplayName}」需要 64 位 Java {version.RequiredJava}。MCL 未找到该版本，请安装后重试，或在「启动设置」浏览已有的 java.exe。");
    }

    private async void DetectJava_Click(object sender, RoutedEventArgs e) => await RunBusyAsync(async () =>
    {
        StatusMessage = "正在查找本机 Java…";
        await ScanJavaAsync(_lifetime.Token);
        StatusMessage = JavaDetail;
    });

    private async void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择 Java 程序",
            Filter = "Java 程序 (java.exe;javaw.exe)|java.exe;javaw.exe",
            CheckFileExists = true
        };
        if (File.Exists(JavaPath)) dialog.InitialDirectory = Path.GetDirectoryName(JavaPath);
        if (dialog.ShowDialog(this) != true) return;
        await RunBusyAsync(async () =>
        {
            var installation = await _javaDiscovery.InspectAsync(dialog.FileName, _lifetime.Token)
                ?? throw new InvalidDataException("这个程序未能返回有效的 Java 版本，请选择可用的 java.exe。");
            JavaPath = installation.Path;
            _configuredJava = installation;
            NotifyJava();
            StatusMessage = "已检查 Java 路径，点击「保存设置」应用。";
        });
    }
}
