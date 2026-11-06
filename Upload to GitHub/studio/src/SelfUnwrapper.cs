using System.Text;

namespace AnyPS5Studio;

/// <summary>Raised when a SELF container can't be turned into an ELF.</summary>
public sealed class SelfRefused : Exception
{
    public SelfRefused(string message) : base(message) { }
}

/// <summary>
/// Turns a decrypted SELF or fSELF container into a clean ELF. This is a C#
/// port of tools/unwrap_self.py from the AnyPS5 project (GPL-2.0), kept close
/// to the original so the two can be compared byte for byte. It does no
/// decryption: a container whose load segments all look encrypted is refused.
/// Int128 stands in for Python's unbounded integers.
/// </summary>
public static class SelfUnwrapper
{
    private const uint SelfMagic = 0x1D3D154F;
    private const uint SelfMagicKernel = 0xEEF51454;
    private const int RecordOffset = 0x40;
    private const int RecordSize = 0x20;
    private const ulong RecordHasSegment = 0x800;
    private const int RecordSegmentShift = 20;
    private const int FirstRegion = 0x400;
    private const long MaxAlignmentPadding = 64L * 1024 * 1024;
    private const long MaxOutputFactor = 4;
    private const long MinOutputLimit = 256L * 1024 * 1024;
    private const uint PtNull = 0, PtLoad = 1, PtDynamic = 2, PtNote = 4, PtPhdr = 6, PtTls = 7;
    private const double EntropyLimit = 7.5;
    private const int EntropySample = 0x10000;

    private sealed class Segment
    {
        public int Index;
        public uint Type, Flags;
        public Int128 Offset, Vaddr, Size, Memory, Align, Source, New;
        public Segment? Host;
    }

    private sealed class Region
    {
        public Int128 Old, Size, Vaddr, Align, New;
    }

    private readonly record struct Record(ulong Flags, Int128 Segment, Int128 Size);

    public static bool HasSelfMagic(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4) return false;
        uint magic = BitConverter.ToUInt32(head);
        return magic is SelfMagic or SelfMagicKernel;
    }

    private static Int128 Mod(Int128 a, Int128 b) => ((a % b) + b) % b;

    private static double Entropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;
        Span<int> counts = stackalloc int[256];
        foreach (byte b in data) counts[b]++;
        double total = 0;
        foreach (int count in counts)
        {
            if (count == 0) continue;
            double share = (double)count / data.Length;
            total -= share * Math.Log2(share);
        }
        return total;
    }

    private static List<Record> ReadRecords(byte[] data)
    {
        var records = new List<Record>();
        for (int offset = RecordOffset; offset + RecordSize <= FirstRegion; offset += RecordSize)
        {
            ulong flags = BitConverter.ToUInt64(data, offset);
            Int128 segment = BitConverter.ToUInt64(data, offset + 8);
            Int128 size = BitConverter.ToUInt64(data, offset + 16);
            Int128 again = BitConverter.ToUInt64(data, offset + 24);
            if (size == 0 || size != again) break;
            if (segment >= data.Length || segment < 0x350 || segment % 0x10 != 0) break;
            if (size > data.Length) break;
            records.Add(new Record(flags, segment, size));
        }
        return records;
    }

    private static (int Inner, byte[] Header, List<Segment> Segments) ParseElf(byte[] data)
    {
        int inner = data.AsSpan().IndexOf(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' });
        if (inner < 0) throw new SelfRefused("no inner ELF header");
        if (inner + 0x40 > data.Length) throw new SelfRefused("inner ELF header is truncated");
        byte[] header = data[inner..(inner + 0x40)];
        if (header[4] != 2 || header[5] != 1) throw new SelfRefused("inner ELF is not 64-bit little-endian");
        uint version = BitConverter.ToUInt32(header, 0x14);
        if (header[6] != 1 || version != 1) throw new SelfRefused($"unsupported inner ELF version (identification {header[6]}, object {version})");
        Int128 programOffset = BitConverter.ToUInt64(header, 0x20);
        ushort machine = BitConverter.ToUInt16(header, 0x12);
        if (machine != 62) throw new SelfRefused($"unsupported inner ELF machine {machine} (expected 62)");
        ushort headerSize = BitConverter.ToUInt16(header, 0x34);
        if (headerSize != 64) throw new SelfRefused($"invalid inner ELF header size {headerSize} (expected 64)");
        ushort entrySize = BitConverter.ToUInt16(header, 0x36);
        ushort count = BitConverter.ToUInt16(header, 0x38);
        if (entrySize != 56 || count == 0) throw new SelfRefused($"program header table is {count} entries of {entrySize} bytes");
        if (programOffset < header.Length) throw new SelfRefused("ELF program header table overlaps the ELF header");
        if (inner + programOffset + count * entrySize > data.Length) throw new SelfRefused("ELF program header table does not fit the file");

        var segments = new List<Segment>(count);
        for (int index = 0; index < count; index++)
        {
            int at = (int)(inner + programOffset + index * 56);
            uint kind = BitConverter.ToUInt32(data, at);
            uint flags = BitConverter.ToUInt32(data, at + 4);
            Int128 fileOffset = BitConverter.ToUInt64(data, at + 8);
            Int128 vaddr = BitConverter.ToUInt64(data, at + 16);
            Int128 fileSize = BitConverter.ToUInt64(data, at + 32);
            Int128 memorySize = BitConverter.ToUInt64(data, at + 40);
            Int128 alignment = BitConverter.ToUInt64(data, at + 48);
            if (kind != PtNull && alignment != 0 && (alignment & (alignment - 1)) != 0)
                throw new SelfRefused($"segment {index} alignment is not a power of two");
            if (kind is PtLoad or PtTls && fileSize > memorySize)
                throw new SelfRefused($"segment {index} file size exceeds memory size");
            if (kind == PtTls)
            {
                if (alignment > 8192) throw new SelfRefused($"segment {index} TLS alignment exceeds 8192");
                if (memorySize > 0x7FFF0000) throw new SelfRefused($"segment {index} TLS memory size exceeds 0x7fff0000");
            }
            if (kind == PtLoad && memorySize > (Int128.One << 64) - vaddr)
                throw new SelfRefused($"segment {index} memory range exceeds ELF64");
            segments.Add(new Segment
            {
                Index = index, Type = kind, Flags = flags, Offset = fileOffset, Vaddr = vaddr, Size = fileSize,
                Memory = memorySize, Align = alignment, Source = fileOffset,
            });
        }
        return (inner, header, segments);
    }

    private static void PlaceSegments(byte[] data, List<Segment> headers, List<Record> records)
    {
        foreach (var h in headers)
        {
            if (h.Type != PtNull) continue;
            h.Flags = 0;
            h.Offset = h.Vaddr = h.Size = h.Memory = h.Align = h.Source = 0;
        }
        var loads = headers.Where(h => h.Type == PtLoad).ToList();
        var used = new HashSet<int>();
        var placed = new HashSet<int>();
        for (int index = 0; index < records.Count; index++)
        {
            var (flags, segment, size) = records[index];
            ulong target = flags >> RecordSegmentShift;
            if ((flags & RecordHasSegment) != 0 && target < (ulong)headers.Count && !placed.Contains((int)target) && headers[(int)target].Size == size)
            {
                headers[(int)target].Source = segment;
                placed.Add((int)target);
                used.Add(index);
            }
        }

        var recorded = headers.Where(h => placed.Contains(h.Index) && h.Vaddr == 0 && h.Type != PtLoad).ToList();
        if (recorded.Count > 0)
        {
            var last = recorded.MaxBy(h => h.Offset)!;
            Int128 cursor = last.Source + last.Size;
            var trailing = headers.Where(h => !placed.Contains(h.Index) && h.Vaddr == 0 && h.Type != PtLoad && h.Size != 0 && h.Offset > last.Offset)
                .OrderBy(h => h.Offset).ToList();
            foreach (var h in trailing)
            {
                placed.Add(h.Index);
                if (h.Type == PtNote)
                {
                    h.Source = 0;
                    h.Size = 0;
                    continue;
                }
                h.Source = cursor;
                cursor += h.Size;
                if (cursor > data.Length)
                {
                    h.Source = 0;
                    h.Size = 0;
                }
            }
        }

        foreach (var h in loads)
        {
            if (h.Size == 0 || placed.Contains(h.Index)) continue;
            for (int index = 0; index < records.Count; index++)
            {
                if (used.Contains(index)) continue;
                if (records[index].Size == h.Size)
                {
                    used.Add(index);
                    h.Source = records[index].Segment;
                    break;
                }
            }
        }

        Segment? LoadFor(Int128 vaddr, Int128 size)
        {
            Segment? found = null;
            foreach (var h in loads)
            {
                Int128 end = h.Vaddr + h.Memory;
                if (h.Memory != 0 && h.Vaddr <= vaddr && (vaddr < end || (size == 0 && vaddr == end))) found = h;
            }
            return found;
        }

        (Int128 Offset, Int128 Shift)? tail = null;
        foreach (var h in headers)
        {
            if (h.Type == PtLoad) continue;
            if (placed.Contains(h.Index))
            {
                var host = LoadFor(h.Vaddr, h.Size);
                if (host != null)
                {
                    Int128 inHost = h.Vaddr - host.Vaddr;
                    if (host.Size != 0 && inHost <= host.Size && h.Size <= host.Size - inHost && h.Source == host.Source + inHost)
                        h.Host = host;
                }
                if (h.Size != 0 && (tail == null || h.Offset > tail.Value.Offset))
                    tail = (h.Offset, h.Source - h.Offset);
                continue;
            }
            if (h.Vaddr != 0 || h.Type is PtPhdr or PtDynamic or PtTls)
            {
                var host = LoadFor(h.Vaddr, h.Size);
                if (host == null)
                {
                    if (h.Size == 0)
                    {
                        h.Source = 0;
                        continue;
                    }
                    throw new SelfRefused($"segment {h.Index} vaddr 0x{h.Vaddr:x} is in no PT_LOAD");
                }
                Int128 inHost = h.Vaddr - host.Vaddr;
                if (h.Size != 0 && (inHost > host.Size || h.Size > host.Size - inHost))
                    throw new SelfRefused($"segment {h.Index} does not fit the file-backed range of PT_LOAD {host.Index}");
                if (host.Size == 0 || inHost > host.Size)
                {
                    h.Source = 0;
                    continue;
                }
                h.Host = host;
                h.Source = host.Source + inHost;
                if (tail == null || h.Offset > tail.Value.Offset)
                    tail = (h.Offset, h.Source - h.Offset);
            }
            else if (h.Size != 0)
            {
                h.Source = h.Offset + (tail?.Shift ?? 0);
                if (h.Source > data.Length) h.Source = data.Length;
                Int128 keep = Int128.Max(0, data.Length - h.Source);
                h.Size = Int128.Min(h.Size, keep);
                h.Memory = Int128.Min(h.Memory, keep);
            }
        }

        foreach (var h in headers)
        {
            if (h.Type == PtLoad && h.Size == 0) continue;
            if (h.Source < 0 || h.Source > data.Length || h.Size > data.Length - h.Source)
                throw new SelfRefused($"segment {h.Index} does not fit the file");
        }
    }

    private static void CheckDecrypted(byte[] data, List<Segment> headers)
    {
        var encrypted = new List<string>();
        int loads = 0;
        foreach (var h in headers)
        {
            if (h.Type != PtLoad || h.Size == 0) continue;
            loads++;
            int start = (int)h.Source;
            int length = (int)Int128.Min(h.Size, EntropySample);
            double value = Entropy(data.AsSpan(start, length));
            if (value >= EntropyLimit) encrypted.Add($"segment {h.Index} at 0x{start:x} ({value:0.00} bits per byte)");
        }
        if (encrypted.Count > 0 && encrypted.Count == loads)
            throw new SelfRefused("every load segment looks encrypted: " + string.Join(", ", encrypted));
    }

    /// <summary>Runs every check without building the output.</summary>
    public static void Check(byte[] data) => Process(data, build: false);

    public static byte[] Unwrap(byte[] data) => Process(data, build: true)!;

    private static byte[]? Process(byte[] data, bool build)
    {
        if (data.Length < FirstRegion) throw new SelfRefused("file is smaller than the SELF header area");
        if (!HasSelfMagic(data)) throw new SelfRefused($"not a SELF container (magic 0x{BitConverter.ToUInt32(data, 0):x})");

        var records = ReadRecords(data);
        var (inner, elfHeader, headers) = ParseElf(data);
        var phdrs = headers.Where(h => h.Type == PtPhdr).ToList();
        if (phdrs.Count > 1) throw new SelfRefused("more than one PT_PHDR segment");
        var phdr = phdrs.FirstOrDefault();
        if (phdr != null)
        {
            if (phdr.Size < headers.Count * 56 || phdr.Memory < headers.Count * 56)
                throw new SelfRefused("PT_PHDR does not cover the program header table");
            if (headers.Any(h => h.Type == PtLoad && h.Index < phdr.Index))
                throw new SelfRefused("PT_PHDR must precede the load segments");
        }
        PlaceSegments(data, headers, records);
        if (phdr != null)
        {
            if (phdr.Host == null) throw new SelfRefused("PT_PHDR is not in a file-backed load segment");
            int table = inner + (int)BitConverter.ToUInt64(elfHeader, 0x20);
            int tableSize = headers.Count * 56;
            if (!data.AsSpan((int)phdr.Source, tableSize).SequenceEqual(data.AsSpan(table, tableSize)))
                throw new SelfRefused("PT_PHDR does not map the program header table");
        }
        CheckDecrypted(data, headers);
        if (!build) return null;

        var regions = new List<Region>();
        var loadRegions = new Dictionary<int, Region>();
        foreach (var h in headers)
        {
            if (h.Type != PtLoad || h.Size == 0) continue;
            var region = new Region { Old = h.Source, Size = h.Size, Vaddr = h.Vaddr, Align = h.Align };
            regions.Add(region);
            loadRegions[h.Index] = region;
        }

        Region? RegionFor(Int128 offset, Int128 size) =>
            regions.FirstOrDefault(r => r.Old <= offset && offset + size <= r.Old + r.Size);

        foreach (var h in headers)
        {
            if (h.Type == PtLoad || h.Size == 0) continue;
            if (RegionFor(h.Source, h.Size) == null)
                regions.Add(new Region { Old = h.Source, Size = h.Size, Vaddr = h.Vaddr, Align = h.Align });
        }
        regions = regions.OrderBy(r => r.Old).ToList();

        Region? OutputRegion(Segment h)
        {
            var host = h.Type == PtLoad ? h : h.Host;
            if (host != null && loadRegions.TryGetValue(host.Index, out var region)) return region;
            return RegionFor(h.Source, h.Size);
        }

        static bool PowerOfTwo(Int128 v) => v != 0 && (v & (v - 1)) == 0;

        foreach (var h in headers)
        {
            if (h.Size == 0 && (h.Type == PtLoad || h.Source == 0)) continue;
            var region = OutputRegion(h);
            Int128 alignment = h.Align;
            if (region == null || alignment <= 1 || !PowerOfTwo(alignment)) continue;
            Int128 current = PowerOfTwo(region.Align) ? region.Align : 1;
            Int128 address = h.Vaddr - (h.Source - region.Old);
            if (Mod(address - region.Vaddr, Int128.Min(current, alignment)) != 0)
                throw new SelfRefused($"segment {h.Index} has incompatible alignment within its output region");
            if (alignment > current)
            {
                region.Align = alignment;
                region.Vaddr = address;
            }
        }

        Int128 programOffset = BitConverter.ToUInt64(elfHeader, 0x20);
        Int128 cursor = Int128.Max(FirstRegion, programOffset + headers.Count * 56);
        Int128 padding = 0;
        foreach (var region in regions)
        {
            Int128 alignment = PowerOfTwo(region.Align) ? region.Align : 1;
            Int128 place = cursor + Mod(region.Vaddr - cursor, alignment);
            padding += place - cursor;
            region.New = place;
            cursor = place + region.Size;
        }

        Int128 emptyEnd = cursor;
        foreach (var h in headers)
            if (h.Type == PtLoad && h.Size == 0)
                emptyEnd = Int128.Max(emptyEnd, Mod(h.Vaddr, Int128.Max(h.Align, 1)));
        padding += emptyEnd - cursor;
        if (padding > MaxAlignmentPadding)
            throw new SelfRefused($"segment alignment requires more than {MaxAlignmentPadding} bytes of output padding");
        cursor = emptyEnd;
        Int128 outputLimit = Int128.Max(MaxOutputFactor * data.Length, MinOutputLimit);
        if (cursor > outputLimit)
            throw new SelfRefused($"the load segments need {cursor} bytes of output, more than the {outputLimit} allowed for a {data.Length} byte container");
        if (cursor > Array.MaxLength) throw new SelfRefused("the output is too large");

        var result = new byte[(int)cursor];
        foreach (var region in regions)
            Array.Copy(data, (long)region.Old, result, (long)region.New, (long)region.Size);

        foreach (var h in headers)
        {
            if (h.Size == 0 && (h.Type == PtLoad || h.Source == 0))
            {
                h.New = h.Type == PtLoad ? Mod(h.Vaddr, Int128.Max(h.Align, 1)) : 0;
                continue;
            }
            var region = OutputRegion(h);
            h.New = region == null ? h.Source : region.New + (h.Source - region.Old);
            if (h.New + h.Size > result.Length) throw new SelfRefused($"segment {h.Index} escaped the output");
        }

        if (phdr != null)
        {
            programOffset = phdr.New;
            BitConverter.TryWriteBytes(elfHeader.AsSpan(0x20), (ulong)programOffset);
        }
        if (programOffset + headers.Count * 56 > result.Length) throw new SelfRefused("the program header table escaped the output");

        elfHeader.CopyTo(result, 0);
        BitConverter.TryWriteBytes(result.AsSpan(0x28), 0UL);
        BitConverter.TryWriteBytes(result.AsSpan(0x3C), (ushort)0);
        BitConverter.TryWriteBytes(result.AsSpan(0x3E), (ushort)0);
        foreach (var h in headers)
        {
            var slot = result.AsSpan((int)(programOffset + h.Index * 56), 56);
            BitConverter.TryWriteBytes(slot, h.Type);
            BitConverter.TryWriteBytes(slot[4..], h.Flags);
            BitConverter.TryWriteBytes(slot[8..], (ulong)h.New);
            BitConverter.TryWriteBytes(slot[16..], (ulong)h.Vaddr);
            BitConverter.TryWriteBytes(slot[24..], (ulong)h.Vaddr);
            BitConverter.TryWriteBytes(slot[32..], (ulong)h.Size);
            BitConverter.TryWriteBytes(slot[40..], (ulong)h.Memory);
            BitConverter.TryWriteBytes(slot[48..], (ulong)h.Align);
        }
        return result;
    }
}
