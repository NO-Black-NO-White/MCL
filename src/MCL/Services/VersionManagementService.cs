using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

public interface IVersionRecycleBin
{
    Task MoveDirectoryAsync(string absolutePath, CancellationToken ct = default);
}

public sealed class VersionManagementService
{
    private readonly IVersionRecycleBin _recycleBin;
    private readonly Func<bool> _isGameRunning;
    private readonly SemaphoreSlim _gate = new(1);

    public VersionManagementService(IVersionRecycleBin? recycleBin = null, Func<bool>? isGameRunning = null)
    {
        _recycleBin = recycleBin ?? new WindowsVersionRecycleBin();
        _isGameRunning = isGameRunning ?? (() => false);
    }

    public Task<VersionDeletionInfo> InspectAsync(string gameDirectory, GameVersion version, CancellationToken ct = default)
        => Task.Run(() => Inspect(gameDirectory, version, ct), ct);

    private VersionDeletionInfo Inspect(string gameDirectory, GameVersion version, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureGameStopped();
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Path.IsPathFullyQualified(gameDirectory)) throw new InvalidDataException("请先选择完整的游戏文件夹路径。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        var versionsRoot = Path.Combine(root, "versions");
        var id = version.Id;
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.EndsWith('.') || id.EndsWith(' '))
            throw new InvalidDataException("版本名称无效，已停止删除。");
        var target = Path.GetFullPath(Path.Combine(versionsRoot, id));
        var expectedJson = Path.Combine(target, id + ".json");
        if (!string.Equals(Path.GetDirectoryName(target), versionsRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(target, Path.GetFullPath(version.VersionDirectory), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expectedJson, Path.GetFullPath(version.JsonPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("版本路径与当前游戏目录不一致。请刷新列表后重试。");
        CheckLink(root); CheckLink(versionsRoot); CheckLink(target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("这个版本文件夹已不存在，请刷新版本列表。");
        CheckDependencies(versionsRoot, id, ct);
        CheckTree(target, ct);
        EnsureGameStopped();
        var data = new List<string>();
        foreach (var (folder, label) in new[] { ("saves", "独立存档"), ("mods", "模组"), ("config", "模组配置"), ("resourcepacks", "资源包"), ("shaderpacks", "光影包") })
            if (Directory.Exists(Path.Combine(target, folder))) data.Add(label);
        if (File.Exists(Path.Combine(target, "options.txt"))) data.Add("版本设置");
        return new VersionDeletionInfo(root, version, target, Directory.GetCreationTimeUtc(target), data);
    }

    public async Task DeleteAsync(VersionDeletionInfo confirmed, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("已有一个版本正在删除，请稍候。");
        try
        {
            // Repeat every path and dependency check after the confirmation dialog.
            var current = await InspectAsync(confirmed.GameDirectory, confirmed.Version, ct);
            if (!string.Equals(current.DirectoryPath, confirmed.DirectoryPath, StringComparison.OrdinalIgnoreCase) || current.CreationTimeUtc != confirmed.CreationTimeUtc)
                throw new IOException("版本文件夹在确认期间发生了变化，请重新选择后再删除。");
            EnsureGameStopped();
            ct.ThrowIfCancellationRequested();
            await _recycleBin.MoveDirectoryAsync(current.DirectoryPath, ct);
            if (Directory.Exists(current.DirectoryPath)) throw new IOException("版本文件夹仍然存在，未能完成删除。请检查文件占用情况后重试。");
        }
        finally { _gate.Release(); }
    }

    private void EnsureGameStopped()
    {
        if (_isGameRunning()) throw new InvalidOperationException("游戏正在运行或准备启动，请先退出游戏再删除版本。");
    }

    private static void CheckLink(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("版本路径包含链接。请在资源管理器中确认链接位置后再处理，MCL 已停止删除。");
    }

    private static void CheckTree(string target, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            CheckRecyclePathLength(folder);
            CheckLink(folder);
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                CheckRecyclePathLength(entry.FullName);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("版本文件夹中包含链接，MCL 已停止删除。请先在资源管理器中检查。");
                if ((entry.Attributes & FileAttributes.Directory) != 0) { pending.Push(entry.FullName); continue; }
                try { using var probe = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.None, 1); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
                { throw new IOException($"版本文件正在被使用：{entry.Name}。请退出对应游戏或占用程序后重试。", ex); }
            }
        }
    }

    private static void CheckRecyclePathLength(string path)
    {
        if (path.Length >= 260 || path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            throw new IOException("版本中存在过长或特殊格式的路径，无法保证安全移入 Windows 回收站，MCL 已停止删除。请先在资源管理器中处理该路径。");
    }

    private static void CheckDependencies(string versionsRoot, string selectedId, CancellationToken ct)
    {
        var dependents = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(versionsRoot))
        {
            ct.ThrowIfCancellationRequested();
            var siblingId = Path.GetFileName(directory);
            if (siblingId.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) continue;
            CheckLink(directory);
            var json = Path.Combine(directory, siblingId + ".json");
            if (!File.Exists(json)) continue;
            CheckLink(json);
            try
            {
                using var stream = File.OpenRead(json);
                if (stream.Length > 16 * 1024 * 1024) throw new JsonException("Metadata too large.");
                using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 128 });
                foreach (var key in new[] { "inheritsFrom", "jar" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                        && string.Equals(value.GetString(), selectedId, StringComparison.OrdinalIgnoreCase))
                    { dependents.Add(siblingId); break; }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { throw new IOException($"无法检查版本“{siblingId}”的依赖关系。请先修复或移除它，再重试删除当前版本。", ex); }
        }
        if (dependents.Count > 0)
            throw new InvalidOperationException($"以下版本还依赖“{selectedId}”：{string.Join("、", dependents.Take(8))}。请先移除依赖版本，或保留当前基础版本。");
    }
}
