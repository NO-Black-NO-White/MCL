using System.Diagnostics;
using System.Text.RegularExpressions;
using MCL.Models;

namespace MCL.Services;

public sealed class JavaDiscovery
{
    private static readonly Regex JavaVersion = new("(?:java|openjdk)\\s+version\\s+\"(?<version>[^\"]+)\"|java\\.version\\s*=\\s*(?<version>[^\\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JavaArchitecture = new(@"os\.arch\s*=\s*(?<arch>[^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool Matches(JavaInstallation? java, int requiredMajor) =>
        java is { Is64Bit: true } && java.MajorVersion == requiredMajor;

    public async Task<IReadOnlyList<JavaInstallation>> FindAsync(CancellationToken ct = default)
    {
        var paths = await Task.Run(() => FindCandidates(ct), ct);
        var found = new List<JavaInstallation>();
        using var concurrency = new SemaphoreSlim(2);
        var tasks = paths.Select(async path =>
        {
            await concurrency.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var installation = await InspectAsync(path, ct).ConfigureAwait(false);
                if (installation is not null) lock (found) found.Add(installation);
            }
            finally { concurrency.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return found.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .OrderByDescending(item => item.MajorVersion).ThenByDescending(item => item.Is64Bit).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<JavaInstallation?> InspectAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var executable = NormalizeExecutable(path);
        if (executable is null) return null;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "-XshowSettings:properties -version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            if (!process.Start()) return null;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) return null;
            var versionMatch = JavaVersion.Match(output);
            if (!versionMatch.Success) return null;
            var version = versionMatch.Groups["version"].Value;
            var pieces = version.Split('.', '-', '_', '+');
            var majorPart = pieces.Length > 1 && pieces[0] == "1" ? pieces[1] : pieces[0];
            if (!int.TryParse(majorPart, out var major) || major < 1) return null;
            var archMatch = JavaArchitecture.Match(output);
            var arch = archMatch.Groups["arch"].Value;
            var is64Bit = archMatch.Success
                ? arch.Contains("64", StringComparison.OrdinalIgnoreCase) || arch.Equals("s390x", StringComparison.OrdinalIgnoreCase)
                : output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase);
            return new JavaInstallation(executable, major, version, is64Bit);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            ct.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            TryKill(process);
            return null;
        }
    }

    private static IReadOnlyList<string> FindCandidates(CancellationToken ct)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? candidate)
        {
            ct.ThrowIfCancellationRequested();
            if (candidates.Count >= 48 || string.IsNullOrWhiteSpace(candidate)) return;
            var executable = NormalizeExecutable(candidate);
            if (executable is not null) candidates.Add(executable);
        }
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) Add(Path.Combine(javaHome.Trim('"'), "bin", "java.exe"));
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { Add(Path.Combine(Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')), "java.exe")); }
            catch (ArgumentException) { }
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrWhiteSpace(programFiles)) continue;
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "AdoptOpenJDK", "Microsoft", "BellSoft", "Amazon Corretto", "Zulu", "Azul Systems", "Semeru" }) roots.Add(Path.Combine(programFiles, vendor));
            roots.Add(Path.Combine(programFiles, "Minecraft Launcher", "runtime"));
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        roots.Add(Path.Combine(local, "Programs", "Eclipse Adoptium"));
        roots.Add(Path.Combine(local, "Programs", "Microsoft"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime"));
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((root, 0));
            var visited = 0;
            while (queue.Count > 0 && visited++ < 150 && candidates.Count < 48)
            {
                ct.ThrowIfCancellationRequested();
                var (directory, depth) = queue.Dequeue();
                Add(Path.Combine(directory, "bin", "java.exe"));
                if (depth >= 3) continue;
                try
                {
                    var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var child in Directory.EnumerateDirectories(directory, "*", options).Take(150))
                        queue.Enqueue((child, depth + 1));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
        }
        return candidates.ToArray();
    }

    private static string? NormalizeExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            if (Path.GetFileName(fullPath).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)) fullPath = Path.Combine(Path.GetDirectoryName(fullPath)!, "java.exe");
            if (!Path.GetFileName(fullPath).Equals("java.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return null;
            var file = new FileInfo(fullPath);
            var destination = file.ResolveLinkTarget(returnFinalTarget: true);
            return destination?.FullName ?? fullPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (NotSupportedException) { }
    }
}
