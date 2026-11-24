using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCL.Models;

namespace MCL.Services;

public interface IAccountStore
{
    MicrosoftAccountSession? Load();
    void Save(MicrosoftAccountSession session);
    void Clear();
}

/// <summary>Only Windows CurrentUser DPAPI ciphertext is ever written to disk.</summary>
public sealed class AccountStore : IAccountStore
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MCL Microsoft account v1");
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _path;

    public AccountStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _directory = Path.GetFullPath(dataDirectory);
        _path = Path.Combine(_directory, "account.dat");
    }

    public MicrosoftAccountSession? Load()
    {
        lock (_gate)
        {
            byte[]? plaintext = null;
            try
            {
                VerifyPath();
                if (ExistingAttributes(_path) is null) return null;
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException();
                var encrypted = new byte[checked((int)stream.Length)];
                stream.ReadExactly(encrypted);
                plaintext = Transform(encrypted, protect: false);
                if (plaintext.Length > MaximumFileBytes) throw new InvalidDataException();
                var session = JsonSerializer.Deserialize<MicrosoftAccountSession>(plaintext) ?? throw new InvalidDataException();
                Validate(session);
                return session;
            }
            catch (Exception ex) when (ex is JsonException or CryptographicException or InvalidDataException or NotSupportedException)
            {
                // Parsing and crypto errors must not reveal any credential fragment.
                throw new InvalidDataException("保存的账号信息无法读取。可以退出账号清除本机记录，然后重新登录微软账号。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new IOException("无法读取本机保存的账号，请检查账号文件是否被占用或无权访问。");
            }
            finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    public void Save(MicrosoftAccountSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Validate(session);
        lock (_gate)
        {
            var temporaryPath = Path.Combine(_directory, $"account.{Guid.NewGuid():N}.tmp");
            byte[]? plaintext = null;
            try
            {
                VerifyPath();
                Directory.CreateDirectory(_directory);
                VerifyPath();
                plaintext = JsonSerializer.SerializeToUtf8Bytes(session);
                if (plaintext.Length > MaximumFileBytes) throw new InvalidDataException("账号信息过大，无法保存。");
                var encrypted = Transform(plaintext, protect: true);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(encrypted);
                    stream.Flush(flushToDisk: true);
                }
                VerifyPath();
                if (ExistingAttributes(_path) is { } oldAttributes && (oldAttributes & FileAttributes.Encrypted) != 0
                    && (File.GetAttributes(temporaryPath) & FileAttributes.Encrypted) == 0)
                    File.Encrypt(temporaryPath);
                File.Move(temporaryPath, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or NotSupportedException)
            {
                throw new IOException("无法安全保存账号凭证。请检查 Windows 当前用户的加密功能后重试。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new IOException("账号信息未能保存。原有账号记录已保留，请检查账号文件是否被占用、只读或无权访问。");
            }
            finally
            {
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
                // A failed sign-in/save must not leave a second credential cache behind.
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                VerifyPath();
                if (ExistingAttributes(_path) is not null) File.Delete(_path);
                if (ExistingAttributes(_path) is not null) throw new IOException();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw new IOException("未能清除本机账号记录，退出登录尚未完成。请关闭占用账号文件的程序后重试。");
            }
        }
    }

    private void VerifyPath()
    {
        for (var part = new DirectoryInfo(_directory); part is not null; part = part.Parent)
        {
            if (ExistingAttributes(part.FullName) is { } directoryAttributes && (directoryAttributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("账号文件夹不能使用符号链接或目录联接。");
        }
        if (ExistingAttributes(_path) is { } fileAttributes && (fileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("账号文件路径无效。");
    }

    private static FileAttributes? ExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void Validate(MicrosoftAccountSession session)
    {
        session.Validate();
        if (session.ExpiresAt <= DateTimeOffset.UnixEpoch)
            throw new InvalidDataException("正版账号信息不完整，请重新登录。");
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException();
        var source = new DataBlob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        var entropy = new DataBlob { Size = Entropy.Length, Data = Marshal.AllocHGlobal(Entropy.Length) };
        DataBlob output = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);
            Marshal.Copy(Entropy, 0, entropy.Data, Entropy.Length);
            var success = protect
                ? CryptProtectData(ref source, "MCL account", ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref source, out description, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            if (output.Size is <= 0 or > MaximumFileBytes) throw new CryptographicException();
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            ZeroAndFree(source.Data, source.Size, local: false);
            ZeroAndFree(entropy.Data, entropy.Size, local: false);
            ZeroAndFree(output.Data, Math.Max(0, output.Size), local: true);
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }

    private static void ZeroAndFree(IntPtr pointer, int length, bool local)
    {
        if (pointer == IntPtr.Zero) return;
        for (var i = 0; i < length; i++) Marshal.WriteByte(pointer, i, 0);
        if (local) LocalFree(pointer); else Marshal.FreeHGlobal(pointer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
