using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

public sealed class VersionCatalog
{
    public Task<IReadOnlyList<GameVersion>> ScanAsync(string gameDirectory, CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<GameVersion>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var gameRoot = Path.GetFullPath(gameDirectory);
            var versionsRoot = Path.Combine(gameRoot, "versions");
            if (!Directory.Exists(versionsRoot)) return Array.Empty<GameVersion>();
            var versions = new List<GameVersion>();
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var directory in Directory.EnumerateDirectories(versionsRoot, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                var id = Path.GetFileName(directory);
                var jsonPath = Path.Combine(directory, id + ".json");
                try
                {
                    if (!File.Exists(jsonPath))
                    {
                        if (File.Exists(Path.Combine(directory, ".mcl-installing"))) continue;
                        versions.Add(new GameVersion(id, jsonPath, directory, "未知", null, false, "缺少版本描述文件"));
                        continue;
                    }
                    using var stream = File.OpenRead(jsonPath);
                    if (stream.Length > 16 * 1024 * 1024) throw new JsonException("Version metadata is too large.");
                    using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 128 });
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Version metadata must be an object.");
                    var metadataId = GetString(root, "id");
                    var inheritsFrom = GetString(root, "inheritsFrom");
                    string? issue = null;
                    if (string.IsNullOrWhiteSpace(metadataId)) issue = "版本描述缺少 id";
                    else if (!string.Equals(metadataId, id, StringComparison.Ordinal)) issue = "版本描述中的 id 与文件夹名称不一致";
                    if (inheritsFrom is not null && !IsSafeVersionId(inheritsFrom)) issue = "继承版本名称无效";
                    if (string.Equals(inheritsFrom, id, StringComparison.OrdinalIgnoreCase)) issue = "版本不能继承自身";
                    if (inheritsFrom is null && string.IsNullOrWhiteSpace(GetString(root, "mainClass"))) issue = "版本描述缺少启动入口";
                    var kind = IdentifyKind(root, id);
                    ModpackInstance? instance = null;
                    if (issue is null && root.TryGetProperty(ModpackPaths.InstanceKey, out var pack))
                    {
                        instance = pack.Deserialize<ModpackInstance>() ?? throw new InvalidDataException("缺少整合包实例配置。");
                        ModpackPaths.CheckInstance(id, inheritsFrom, GetString(root, "mainClass"), instance);
                        kind = instance.Forge is not null ? "Forge 整合包" : instance.Fabric is null ? "原版整合包" : "Fabric 整合包";
                        var baseDirectory = ModpackPaths.BaseDirectory(instance.Minecraft, instance.BaseSha1, instance.UsesCachedBase);
                        if (!Directory.Exists(ModpackPaths.SafeTarget(directory, "game"))
                            || !File.Exists(ModpackPaths.SafeTarget(gameRoot, $"{baseDirectory}/{instance.Minecraft}.json")))
                            issue = "整合包缺少游戏目录或基础版本，请重新导入";
                    }
                    versions.Add(new GameVersion(id, jsonPath, directory, kind, inheritsFrom, issue is null, issue)
                    { ModpackName = instance?.Name, ModpackMinecraft = instance?.Minecraft, ModpackVersion = instance?.PackVersion,
                        ModpackLoader = instance?.Forge is { } forge ? $"Forge {forge}" : instance?.Fabric });
                }
                catch (InvalidDataException ex)
                {
                    versions.Add(new GameVersion(id, jsonPath, directory, "整合包", null, false, ex.Message));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    versions.Add(new GameVersion(id, jsonPath, directory, "未知", null, false, ex is JsonException ? "版本描述文件损坏或格式无效" : "无法读取版本描述文件"));
                }
            }
            return versions.OrderByDescending(version => GetLastWriteTime(version.VersionDirectory)).ThenBy(version => version.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        }, ct);
    }

    private static string? GetString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime GetLastWriteTime(string directory)
    {
        try { return Directory.GetLastWriteTimeUtc(directory); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
        catch (System.Security.SecurityException) { return DateTime.MinValue; }
    }

    private static bool IsSafeVersionId(string value) => !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".." && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !value.Contains('/') && !value.Contains('\\');

    private static string IdentifyKind(JsonElement root, string id)
    {
        var markers = new List<string> { id, GetString(root, "mainClass") ?? "" };
        if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in libraries.EnumerateArray())
                if (library.ValueKind == JsonValueKind.Object) markers.Add(GetString(library, "name") ?? "");
        }
        if (markers.Any(marker => marker.Contains("neoforge", StringComparison.OrdinalIgnoreCase) || marker.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase))) return "NeoForge";
        if (markers.Any(marker => marker.Contains("quilt", StringComparison.OrdinalIgnoreCase))) return "Quilt";
        if (markers.Any(marker => marker.Contains("fabric", StringComparison.OrdinalIgnoreCase))) return "Fabric";
        if (markers.Any(marker => marker.Contains("forge", StringComparison.OrdinalIgnoreCase))) return "Forge";
        return "原版";
    }
}
