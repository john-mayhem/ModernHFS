using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace ModernHFS;

/// <summary>
/// A folder streamed as a ZIP. Files are stored as-is (no compression), so the exact archive size is known
/// before the first byte: the browser shows real progress and time left, nothing is buffered on disk.
/// ZIP64 is used automatically past 4 GB / 65k entries.
/// </summary>
sealed class ZipPlan
{
    public sealed record Item(string Name, string? Path, long Size, DateTime Modified)   // Path null = folder
    {
        public bool IsDir => Path == null;
        public readonly byte[] NameBytes = Encoding.UTF8.GetBytes(Name);
    }

    public const int MaxEntries = 200_000;

    public List<Item> Items { get; } = [];
    public bool Zip64 { get; private set; }
    public long Length { get; private set; }
    public int FileCount { get; private set; }
    public long FileBytes { get; private set; }

    /// <summary>Everything a visitor of <paramref name="folder"/> may see, recursively. Null = too many entries.</summary>
    public static ZipPlan? Build(string folder, string root, CancellationToken ct)
    {
        var plan = new ZipPlan();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RealPath(new DirectoryInfo(folder)) };
        var todo = new Stack<(string Dir, string Prefix, int Depth)>();
        todo.Push((folder, "", 0));
        while (todo.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, prefix, depth) = todo.Pop();
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch { continue; }
            var subdirs = new List<(string, string, int)>();
            foreach (var fsi in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                FileAttributes a;
                try { a = fsi.Attributes; } catch { continue; }
                if (!FileServer.Visible(a)) continue;
                if ((a & FileAttributes.ReparsePoint) != 0 && !FileServer.LinkStaysInside(fsi.FullName, a, root)) continue;
                var name = prefix + fsi.Name;
                if ((a & FileAttributes.Directory) != 0)
                {
                    var real = RealPath((DirectoryInfo)fsi);
                    // A link to somewhere inside this same folder: its files come through the real path anyway.
                    if ((a & FileAttributes.ReparsePoint) != 0 && FileServer.IsUnder(real, Path.GetFullPath(folder))) continue;
                    // ...and a link pointing back up would loop forever: every real folder only once.
                    if (depth >= 64 || !seen.Add(real)) continue;
                    plan.Items.Add(new Item(name + "/", null, 0, fsi.LastWriteTime));
                    subdirs.Add((fsi.FullName, name + "/", depth + 1));
                }
                else
                {
                    long length;
                    try { length = ((FileInfo)fsi).Length; } catch { continue; }
                    plan.Items.Add(new Item(name, fsi.FullName, length, fsi.LastWriteTime));
                    plan.FileCount++;
                    plan.FileBytes += length;
                }
                if (plan.Items.Count > MaxEntries) return null;
            }
            for (int i = subdirs.Count - 1; i >= 0; i--) todo.Push(subdirs[i]);
        }
        plan.Measure();
        return plan;
    }

    static string RealPath(DirectoryInfo d)
    {
        try { return d.LinkTarget != null ? d.ResolveLinkTarget(true)?.FullName ?? d.FullName : d.FullName; }
        catch { return d.FullName; }
    }

    int LocalExtra(Item i) => Zip64 && !i.IsDir ? 20 : 0;   // 0x0001: id, size, uncompressed, compressed
    int CentralExtra => Zip64 ? 28 : 0;                      // 0x0001: id, size, uncompressed, compressed, offset
    int DescriptorSize => Zip64 ? 24 : 16;

    void Measure()
    {
        long overhead = Items.Sum(i => 30L + 46 + 2 * i.NameBytes.Length + 28 + 24);
        Zip64 = Items.Count >= 0xFFFF || Items.Any(i => i.Size >= 0xFFFF_FFFFL) || FileBytes + overhead >= 0xFFFF_FFFFL;
        long length = 0;
        foreach (var i in Items)
        {
            length += 30 + i.NameBytes.Length + LocalExtra(i) + i.Size + (i.IsDir ? 0 : DescriptorSize);
            length += 46 + i.NameBytes.Length + CentralExtra;
        }
        if (Zip64) length += 56 + 20;
        Length = length + 22;
    }

    /// <summary>Writes exactly <see cref="Length"/> bytes. A file that shrank or vanished meanwhile is padded with zeros.</summary>
    public async Task WriteAsync(Stream output, Action<string> warn, CancellationToken ct)
    {
        var central = new List<(Item Item, long Offset, uint Crc)>(Items.Count);
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        long pos = 0;
        try
        {
            foreach (var item in Items)
            {
                long offset = pos;
                var header = LocalHeader(item);
                await output.WriteAsync(header, ct);
                pos += header.Length;
                uint crc = 0;
                if (!item.IsDir)
                {
                    var hash = new Crc32();
                    long left = item.Size;
                    try
                    {
                        await using var fs = new FileStream(item.Path!, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        while (left > 0)
                        {
                            int n = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
                            if (n == 0) break;
                            hash.Append(buffer.AsSpan(0, n));
                            await output.WriteAsync(buffer.AsMemory(0, n), ct);
                            left -= n;
                            pos += n;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
                    {
                        // unreadable now (locked, deleted): padded below so the promised size still holds
                    }
                    if (left > 0)
                    {
                        warn($"\"{item.Name}\" changed or couldn't be read while zipping, the rest is filled with zeros");
                        Array.Clear(buffer);
                        while (left > 0)
                        {
                            int n = (int)Math.Min(buffer.Length, left);
                            hash.Append(buffer.AsSpan(0, n));
                            await output.WriteAsync(buffer.AsMemory(0, n), ct);
                            left -= n;
                            pos += n;
                        }
                    }
                    crc = hash.GetCurrentHashAsUInt32();
                    var descriptor = Descriptor(item, crc);
                    await output.WriteAsync(descriptor, ct);
                    pos += descriptor.Length;
                }
                central.Add((item, offset, crc));
            }

            long cdStart = pos;
            foreach (var (item, offset, crc) in central)
            {
                var entry = CentralEntry(item, offset, crc);
                await output.WriteAsync(entry, ct);
                pos += entry.Length;
            }
            var end = End(central.Count, pos - cdStart, cdStart, pos);
            await output.WriteAsync(end, ct);
            pos += end.Length;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        if (pos != Length) throw new InvalidOperationException($"ZIP size mismatch: wrote {pos}, promised {Length}");
    }

    // ---- records (all little-endian) -----------------------------------------------------------

    const ushort FlagUtf8 = 0x0800, FlagDescriptor = 0x0008;

    ushort Version(Item i) => (ushort)(Zip64 ? 45 : i.IsDir ? 20 : 20);

    byte[] LocalHeader(Item i)
    {
        var b = new byte[30 + i.NameBytes.Length + LocalExtra(i)];
        var w = new Writer(b);
        var (time, date) = DosTime(i.Modified);
        w.U32(0x04034b50); w.U16(Version(i)); w.U16((ushort)(FlagUtf8 | (i.IsDir ? 0 : FlagDescriptor))); w.U16(0);
        w.U16(time); w.U16(date); w.U32(0);
        uint size32 = i.IsDir ? 0 : Zip64 ? 0xFFFF_FFFF : (uint)i.Size;
        w.U32(size32); w.U32(size32);
        w.U16((ushort)i.NameBytes.Length); w.U16((ushort)LocalExtra(i)); w.Bytes(i.NameBytes);
        if (LocalExtra(i) > 0) { w.U16(0x0001); w.U16(16); w.U64((ulong)i.Size); w.U64((ulong)i.Size); }
        return b;
    }

    byte[] Descriptor(Item i, uint crc)
    {
        var b = new byte[DescriptorSize];
        var w = new Writer(b);
        w.U32(0x08074b50); w.U32(crc);
        if (Zip64) { w.U64((ulong)i.Size); w.U64((ulong)i.Size); }
        else { w.U32((uint)i.Size); w.U32((uint)i.Size); }
        return b;
    }

    byte[] CentralEntry(Item i, long offset, uint crc)
    {
        var b = new byte[46 + i.NameBytes.Length + CentralExtra];
        var w = new Writer(b);
        var (time, date) = DosTime(i.Modified);
        w.U32(0x02014b50); w.U16(Version(i)); w.U16(Version(i)); w.U16((ushort)(FlagUtf8 | (i.IsDir ? 0 : FlagDescriptor))); w.U16(0);
        w.U16(time); w.U16(date); w.U32(crc);
        w.U32(Zip64 ? 0xFFFF_FFFF : (uint)i.Size); w.U32(Zip64 ? 0xFFFF_FFFF : (uint)i.Size);
        w.U16((ushort)i.NameBytes.Length); w.U16((ushort)CentralExtra); w.U16(0); w.U16(0); w.U16(0);
        w.U32(i.IsDir ? 0x10u : 0x20u);
        w.U32(Zip64 ? 0xFFFF_FFFF : (uint)offset);
        w.Bytes(i.NameBytes);
        if (Zip64) { w.U16(0x0001); w.U16(24); w.U64((ulong)i.Size); w.U64((ulong)i.Size); w.U64((ulong)offset); }
        return b;
    }

    byte[] End(int count, long cdSize, long cdStart, long zip64RecordAt)
    {
        var b = new byte[(Zip64 ? 76 : 0) + 22];
        var w = new Writer(b);
        if (Zip64)
        {
            w.U32(0x06064b50); w.U64(44); w.U16(45); w.U16(45); w.U32(0); w.U32(0);
            w.U64((ulong)count); w.U64((ulong)count); w.U64((ulong)cdSize); w.U64((ulong)cdStart);
            w.U32(0x07064b50); w.U32(0); w.U64((ulong)zip64RecordAt); w.U32(1);
        }
        w.U32(0x06054b50); w.U16(0); w.U16(0);
        w.U16(Zip64 ? (ushort)0xFFFF : (ushort)count); w.U16(Zip64 ? (ushort)0xFFFF : (ushort)count);
        w.U32(Zip64 ? 0xFFFF_FFFF : (uint)cdSize); w.U32(Zip64 ? 0xFFFF_FFFF : (uint)cdStart);
        w.U16(0);
        return b;
    }

    static (ushort Time, ushort Date) DosTime(DateTime t)
    {
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        if (t.Year > 2107) t = new DateTime(2107, 12, 31, 23, 59, 58);
        return ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)), (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
    }

    ref struct Writer(Span<byte> span)
    {
        Span<byte> s = span;
        int p;
        public void U16(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(s[p..], v); p += 2; }
        public void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(s[p..], v); p += 4; }
        public void U64(ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(s[p..], v); p += 8; }
        public void Bytes(ReadOnlySpan<byte> v) { v.CopyTo(s[p..]); p += v.Length; }
    }
}
