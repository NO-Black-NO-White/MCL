using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MCL.Models;

namespace MCL.Services;

public sealed record SavePreview(string Folder, string Name, string Mode, string Difficulty,
    long? Days, DateTime? CreatedAt, DateTime? LastPlayed)
{
    public string DayText => Days is { } days ? $"{days:N0} 天" : "—";
    public string CreatedText => CreatedAt?.ToString("yyyy-MM-dd") ?? "—";
    public string CreatedDetail => (CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "未记录创建时间")
        + "\n取自本机存档文件夹，复制或恢复后可能变化。";
    public string LastPlayedText => LastPlayed?.ToString("yyyy-MM-dd") ?? "—";
    public string LastPlayedDetail => LastPlayed?.ToString("yyyy-MM-dd HH:mm") ?? "未记录最近游玩时间";
}

internal static class SavePreviewReader
{
    private const int MaximumMetadataBytes = 8 * 1024 * 1024;
    private const int DayLength = 24_000;
    private static readonly NbtFields LevelFields = new(
        "Data.LevelName", "Data.LastPlayed", "Data.GameType", "Data.hardcore", "Data.Difficulty", "Data.DayTime", "Data.Time",
        "Data.difficulty_settings.difficulty", "Data.difficulty_settings.hardcore",
        "Data.world_clocks.minecraft:overworld.total_ticks");
    private static readonly NbtFields ClockFields = new("data.minecraft:overworld.total_ticks", "data.overworld.total_ticks");

    internal static SavePreview[] Read(string root, GameVersion version, CancellationToken ct)
    {
        var saves = new DirectoryInfo(Path.Combine(LocalContentArchive.RuntimeDirectory(root, version), "saves"));
        if (!saves.Exists || (saves.Attributes & FileAttributes.ReparsePoint) != 0) return [];

        var previews = new List<SavePreview>();
        foreach (var folder in saves.EnumerateDirectories("*", new EnumerationOptions
                 { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
        {
            ct.ThrowIfCancellationRequested();
            var preview = ReadSave(folder, ct);
            if (preview is not null) previews.Add(preview);
        }
        return previews.OrderByDescending(save => save.LastPlayed)
            .ThenByDescending(save => save.CreatedAt)
            .ThenBy(save => save.Folder, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SavePreview? ReadSave(DirectoryInfo folder, CancellationToken ct)
    {
        var level = Path.Combine(folder.FullName, "level.dat");
        var previous = Path.Combine(folder.FullName, "level.dat_old");
        if (!File.Exists(level) && !File.Exists(previous)) return null;

        var data = ReadFile(folder.FullName, "level.dat", LevelFields, ct)
            ?? ReadFile(folder.FullName, "level.dat_old", LevelFields, ct) ?? [];
        var name = data.GetValueOrDefault("Data.LevelName") as string;
        var hardcore = data.GetValueOrDefault("Data.difficulty_settings.hardcore") as long?
            ?? data.GetValueOrDefault("Data.hardcore") as long?;
        var mode = hardcore is 1 ? "极限" : data.GetValueOrDefault("Data.GameType") switch
        {
            0L => "生存", 1L => "创造", 2L => "冒险", 3L => "旁观", _ => "—"
        };
        var difficulty = (data.GetValueOrDefault("Data.difficulty_settings.difficulty")
            ?? data.GetValueOrDefault("Data.Difficulty")) switch
        {
            "peaceful" or 0L => "和平", "easy" or 1L => "简单", "normal" or 2L => "普通", "hard" or 3L => "困难", _ => "—"
        };

        var ticks = data.GetValueOrDefault("Data.DayTime") as long?
            ?? data.GetValueOrDefault("Data.world_clocks.minecraft:overworld.total_ticks") as long?;
        if (ticks is null && data.ContainsKey("Data.difficulty_settings.difficulty"))
        {
            var clocks = ReadFile(folder.FullName, "data/minecraft/world_clocks.dat", ClockFields, ct);
            if (clocks is not null)
                ticks = clocks.GetValueOrDefault("data.minecraft:overworld.total_ticks") as long?
                    ?? clocks.GetValueOrDefault("data.overworld.total_ticks") as long?;
        }
        else ticks ??= data.GetValueOrDefault("Data.Time") as long?;

        var lastPlayed = data.GetValueOrDefault("Data.LastPlayed") as long?;
        var createdAt = folder.CreationTime;
        return new SavePreview(folder.Name, string.IsNullOrWhiteSpace(name) ? folder.Name : name, mode, difficulty,
            ticks is >= 0 ? ticks / DayLength : null,
            createdAt.Year > 1601 ? createdAt : null,
            lastPlayed is > 0 and <= 253402300799999L ? DateTimeOffset.FromUnixTimeMilliseconds(lastPlayed.Value).LocalDateTime : null);
    }

    private static Dictionary<string, object>? ReadFile(string folder, string relativePath, NbtFields fields, CancellationToken ct)
    {
        try
        {
            var path = ModpackPaths.SafeTarget(folder, relativePath);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > MaximumMetadataBytes) return null;
            var compressed = file.ReadByte() == 0x1f && file.ReadByte() == 0x8b;
            file.Position = 0;
            using var source = compressed ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file;
            using var decoded = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var count = source.Read(buffer);
                if (count == 0) break;
                if (decoded.Length + count > MaximumMetadataBytes) return null;
                decoded.Write(buffer, 0, count);
            }
            decoded.Position = 0;
            var reader = new NbtReader(decoded, fields, ct);
            var values = reader.Read();
            return values.Count == 0 ? null : values;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    private sealed class NbtFields(params string[] values)
    {
        internal readonly HashSet<string> Values = new(values, StringComparer.Ordinal);
        internal readonly HashSet<string> Compounds = new(values.SelectMany(path =>
        {
            var parts = path.Split('.');
            return Enumerable.Range(1, parts.Length - 1).Select(count => string.Join('.', parts.Take(count)));
        }), StringComparer.Ordinal);
    }

    private sealed class NbtReader(MemoryStream stream, NbtFields fields, CancellationToken ct)
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

        internal Dictionary<string, object> Read()
        {
            if (ReadByte() != 10) throw new InvalidDataException("存档缺少 NBT 根复合标签。");
            Skip(ReadUInt16());
            ReadCompound("", 0);
            return _values;
        }

        private void ReadCompound(string? prefix, int depth)
        {
            CheckDepth(depth);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var type = ReadByte();
                if (type == 0) return;
                if (prefix is null)
                {
                    Skip(ReadUInt16());
                    SkipPayload(type, depth + 1);
                    continue;
                }
                var name = ReadString();
                var path = prefix.Length == 0 ? name : prefix + "." + name;
                if (type == 10 && fields.Compounds.Contains(path)) ReadCompound(path, depth + 1);
                else if (fields.Values.Contains(path) && type is 1 or 2 or 3 or 4 or 8)
                {
                    _values[path] = type switch
                    {
                        1 => (long)(sbyte)ReadByte(),
                        2 => (long)(short)ReadUInt16(),
                        3 => (long)ReadInt32(),
                        4 => ReadInt64(),
                        _ => ReadString()
                    };
                }
                else SkipPayload(type, depth + 1);
            }
        }

        private void SkipPayload(int type, int depth)
        {
            CheckDepth(depth);
            switch (type)
            {
                case 1: Skip(1); break;
                case 2: Skip(2); break;
                case 3 or 5: Skip(4); break;
                case 4 or 6: Skip(8); break;
                case 7: Skip(ReadInt32()); break;
                case 8: Skip(ReadUInt16()); break;
                case 9:
                    var item = ReadByte();
                    var count = ReadInt32();
                    if (count < 0 || count > stream.Length - stream.Position || (count > 0 && item == 0))
                        throw new InvalidDataException("存档包含无效的 NBT 列表。");
                    for (var i = 0; i < count; i++) { ct.ThrowIfCancellationRequested(); SkipPayload(item, depth + 1); }
                    break;
                case 10: ReadCompound(null, depth); break;
                case 11: Skip(4L * ReadInt32()); break;
                case 12: Skip(8L * ReadInt32()); break;
                default: throw new InvalidDataException("存档包含未知的 NBT 标签。");
            }
        }

        private string ReadString()
        {
            var length = ReadUInt16();
            var start = (int)stream.Position;
            Skip(length);
            var bytes = stream.GetBuffer().AsSpan(start, length);
            try { return Utf8.GetString(bytes); }
            catch (DecoderFallbackException)
            {
                // Java's modified UTF-8 encodes NUL and UTF-16 surrogate pairs separately.
                var chars = new char[length];
                var count = 0;
                for (var i = 0; i < bytes.Length; i++)
                {
                    var lead = bytes[i];
                    if (lead < 0x80) chars[count++] = (char)lead;
                    else if (lead is >= 0xc0 and < 0xe0)
                        chars[count++] = (char)(((lead & 0x1f) << 6) | Continuation(bytes, ++i));
                    else if (lead is >= 0xe0 and < 0xf0)
                        chars[count++] = (char)(((lead & 0x0f) << 12) | (Continuation(bytes, ++i) << 6) | Continuation(bytes, ++i));
                    else throw new InvalidDataException("存档包含无效的 NBT 字符串。");
                }
                return new string(chars, 0, count);
            }
        }

        private static int Continuation(ReadOnlySpan<byte> bytes, int index)
        {
            if (index >= bytes.Length || (bytes[index] & 0xc0) != 0x80)
                throw new InvalidDataException("存档包含不完整的 NBT 字符串。");
            return bytes[index] & 0x3f;
        }

        private void Skip(long count)
        {
            if (count < 0 || count > stream.Length - stream.Position) throw new InvalidDataException("存档的 NBT 数据不完整。");
            stream.Position += count;
        }

        private int ReadByte()
        {
            var value = stream.ReadByte();
            return value >= 0 ? value : throw new EndOfStreamException();
        }

        private ushort ReadUInt16() { Span<byte> bytes = stackalloc byte[2]; stream.ReadExactly(bytes); return BinaryPrimitives.ReadUInt16BigEndian(bytes); }
        private int ReadInt32() { Span<byte> bytes = stackalloc byte[4]; stream.ReadExactly(bytes); return BinaryPrimitives.ReadInt32BigEndian(bytes); }
        private long ReadInt64() { Span<byte> bytes = stackalloc byte[8]; stream.ReadExactly(bytes); return BinaryPrimitives.ReadInt64BigEndian(bytes); }

        private static void CheckDepth(int depth)
        {
            if (depth > 64) throw new InvalidDataException("存档的 NBT 结构过深。");
        }
    }
}
