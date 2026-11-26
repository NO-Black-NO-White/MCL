using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MCL.Models;
using MCL.Services;

internal static class SavePreviewValidation
{
    internal static void Run(string root, GameVersion pack)
    {
        var vanilla = new GameVersion("26.3", "", "", "原版", null, true, null);
        var saves = Path.Combine(root, "saves");
        var old = Directory.CreateDirectory(Path.Combine(saves, "old")).FullName;
        File.WriteAllText(Path.Combine(old, "level.dat"), "incomplete write");
        WriteNbt(Path.Combine(old, "level.dat_old"), new Tag("Data", new[]
        {
            new Tag("Player", new[] { new Tag("Count", (byte)42) }),
            new Tag("LevelName", Encoding.UTF8.GetBytes("樱花").Concat(new byte[] { 0xed, 0xa0, 0xbc, 0xed, 0xbc, 0xb8 }).ToArray()),
            new Tag("LastPlayed", 1_700_000_000_000L), new Tag("GameType", 1),
            new Tag("hardcore", (byte)0), new Tag("Difficulty", (byte)1),
            new Tag("DayTime", 49_000L), new Tag("Time", 900L)
        }));

        var newer = Directory.CreateDirectory(Path.Combine(saves, "modern")).FullName;
        WriteNbt(Path.Combine(newer, "level.dat"), new Tag("Data", new[]
        {
            new Tag("LevelName", "峡谷"), new Tag("LastPlayed", 1_800_000_000_000L),
            new Tag("GameType", 0), new Tag("Time", 150L),
            new Tag("difficulty_settings", new[] { new Tag("difficulty", "hard"), new Tag("hardcore", (byte)1) })
        }));
        var clockPath = Path.Combine(Directory.CreateDirectory(Path.Combine(newer, "data", "minecraft")).FullName, "world_clocks.dat");
        WriteNbt(clockPath, new Tag("data", new[]
        {
            new Tag("minecraft:the_end", new[] { new Tag("total_ticks", 24_000L) }),
            new Tag("minecraft:overworld", new[] { new Tag("total_ticks", 144_040L) })
        }));
        var unreadable = Directory.CreateDirectory(Path.Combine(saves, "unreadable")).FullName;
        File.WriteAllText(Path.Combine(unreadable, "level.dat"), "damaged");
        Directory.CreateDirectory(Path.Combine(saves, "not-a-world"));

        var result = SavePreviewReader.Read(root, vanilla, default);
        if (result is not
            [{ Name: "峡谷", Mode: "极限", Difficulty: "困难", Days: 6, CreatedAt: not null },
             { Name: "樱花🌸", Mode: "创造", Difficulty: "简单", Days: 2 },
             { Name: "unreadable", Mode: "—", Days: null }])
            throw new InvalidOperationException("Save preview must read both clock formats, order by play time, and tolerate a damaged level.dat.");

        if (SavePreviewReader.Read(root, pack, default) is not [{ Folder: "TestWorld" }]
            || SavePreviewReader.Read(Path.Combine(root, "unused"), vanilla, default).Length != 0)
            throw new InvalidOperationException("Save preview must use the game's runtime directory and allow an empty saves folder.");
        Console.WriteLine("PASS Save preview reads legacy and 26.x data, retains unreadable worlds and isolates modpack saves.");
    }

    private sealed record Tag(string Name, object Value);

    private static void WriteNbt(string path, params Tag[] tags)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        WriteTag(gzip, new Tag("", tags));
    }

    private static void WriteTag(Stream stream, Tag tag)
    {
        stream.WriteByte(tag.Value switch { byte => 1, int => 3, long => 4, string or byte[] => 8, Tag[] => 10, _ => throw new InvalidOperationException() });
        WriteString(stream, Encoding.UTF8.GetBytes(tag.Name));
        Span<byte> number = stackalloc byte[8];
        switch (tag.Value)
        {
            case byte value: stream.WriteByte(value); break;
            case int value: BinaryPrimitives.WriteInt32BigEndian(number, value); stream.Write(number[..4]); break;
            case long value: BinaryPrimitives.WriteInt64BigEndian(number, value); stream.Write(number); break;
            case string value: WriteString(stream, Encoding.UTF8.GetBytes(value)); break;
            case byte[] value: WriteString(stream, value); break;
            case Tag[] children:
                foreach (var child in children) WriteTag(stream, child);
                stream.WriteByte(0);
                break;
        }
    }

    private static void WriteString(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)value.Length));
        stream.Write(length);
        stream.Write(value);
    }
}
