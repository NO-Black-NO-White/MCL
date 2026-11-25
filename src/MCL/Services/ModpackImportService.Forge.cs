using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

public sealed partial class ModpackImportService
{
    private async Task<string> InstallForgeAsync(string minecraft, string forge, string baseDirectory, string root,
        string staging, string? javaPath, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        Report(progress, "安装 Forge", $"正在准备 Forge {forge}");
        var discovery = new JavaDiscovery();
        var java = string.IsNullOrWhiteSpace(javaPath) ? null : await discovery.InspectAsync(javaPath, ct).ConfigureAwait(false);
        if (java is not { Is64Bit: true, MajorVersion: >= 17 })
            java = (await discovery.FindAsync(ct).ConfigureAwait(false)).FirstOrDefault(candidate => candidate is { Is64Bit: true, MajorVersion: >= 17 });
        if (java is null) throw new InvalidDataException("安装 Forge 需要 64 位 Java 17 或更高版本。请在启动设置中选择 Java。");

        var forgeRoot = ModpackPaths.SafeTarget(staging, ".mcl-forge");
        var baseCopy = ModpackPaths.SafeTarget(forgeRoot, $"versions/{minecraft}");
        Directory.CreateDirectory(baseCopy);
        File.Copy(ModpackPaths.SafeTarget(root, $"{baseDirectory}/{minecraft}.jar"), Path.Combine(baseCopy, minecraft + ".jar"));
        File.Copy(ModpackPaths.SafeTarget(root, $"{baseDirectory}/{minecraft}.json"), Path.Combine(baseCopy, minecraft + ".json"));
        var launcherProfiles = ModpackPaths.SafeTarget(forgeRoot, "launcher_profiles.json");
        await File.WriteAllTextAsync(launcherProfiles, "{\"profiles\":{}}", ct).ConfigureAwait(false);

        var installer = ModpackPaths.SafeTarget(staging, "forge-installer.jar");
        var url = new Uri($"https://maven.minecraftforge.net/net/minecraftforge/forge/{minecraft}-{forge}/forge-{minecraft}-{forge}-installer.jar");
        using var checksum = await GetSmallAsync(new Uri(url.AbsoluteUri + ".sha1"), 256, ct, IsForgeMaven).ConfigureAwait(false);
        using var reader = new StreamReader(checksum);
        var digest = (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
        if (!ModpackPaths.IsHex(digest, 40)) throw new InvalidDataException("Forge 官方安装器的校验信息无效。");
        await DownloadAsync([url], installer, null, 32 * 1024 * 1024, digest, null, IsForgeMaven, ct).ConfigureAwait(false);

        var start = new ProcessStartInfo(java.Path)
        {
            WorkingDirectory = forgeRoot, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Remove("MCL_CURSEFORGE_API_KEY");
        foreach (var argument in new[] { "-Djava.awt.headless=true", "-jar", installer, "--installClient", forgeRoot })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Forge 安装器无法启动。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(12));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new IOException("Forge 安装超时。请检查能否连接 Forge 和 Minecraft 文件服务器。");
        }
        var profilePath = ForgeProfilePath(forgeRoot, minecraft, forge);
        if (process.ExitCode != 0 || !File.Exists(profilePath))
        {
            var detail = (stderr.Result + "\n" + stdout.Result).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("exception", StringComparison.OrdinalIgnoreCase));
            throw new IOException($"Forge 安装器未完成客户端安装（退出码 {process.ExitCode}）。{detail?[..Math.Min(detail.Length, 240)]}");
        }

        var bytes = File.ReadAllBytes(profilePath);
        using var profile = JsonDocument.Parse(bytes);
        ValidateForgeProfile(profile.RootElement, minecraft, forge);
        File.Delete(installer);
        File.Delete(launcherProfiles);
        File.Delete(Path.Combine(baseCopy, minecraft + ".jar"));
        File.Delete(Path.Combine(baseCopy, minecraft + ".json"));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static string ForgeProfilePath(string root, string minecraft, string forge)
    {
        var id = $"{minecraft}-forge-{forge}";
        return ModpackPaths.SafeTarget(root, $"versions/{id}/{id}.json");
    }

    internal static void ValidateForgeProfile(JsonElement profile, string minecraft, string forge)
    {
        if (profile.GetProperty("id").GetString() != $"{minecraft}-forge-{forge}"
            || profile.GetProperty("inheritsFrom").GetString() != minecraft
            || profile.GetProperty("mainClass").GetString() != ModpackPaths.ForgeMain)
            throw new InvalidDataException("Forge 客户端配置与所选版本不一致。");
    }

    private static bool IsForgeMaven(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Host == "maven.minecraftforge.net";
}
