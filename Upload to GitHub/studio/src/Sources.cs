using System.IO;
using System.Text;

namespace AnyPS5Studio;

/// <summary>One file of a game. Paths use forward slashes and are relative to the game's root.</summary>
public sealed record SourceEntry(string Path, long Size);

/// <summary>Somewhere a game's files can be read from: a folder or a disk image.</summary>
public interface IGameSource
{
    string Location { get; }
    IReadOnlyList<SourceEntry> Files { get; }
    Stream OpenRead(string path);
}

public static class GameSourceExtensions
{
    public static bool Exists(this IGameSource source, string path) =>
        source.Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    public static byte[] ReadAll(this IGameSource source, string path)
    {
        using var input = source.OpenRead(path);
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        return memory.ToArray();
    }

    public static byte[] ReadHead(this IGameSource source, string path, int count)
    {
        using var input = source.OpenRead(path);
        var head = new byte[count];
        int got = 0, n;
        while (got < count && (n = input.Read(head, got, count - got)) > 0) got += n;
        return got == count ? head : head[..got];
    }
}

public sealed class FolderSource : IGameSource
{
    public string Location { get; }
    public IReadOnlyList<SourceEntry> Files { get; }

    public FolderSource(string dir, CancellationToken ct = default)
    {
        Location = dir;
        var files = new List<SourceEntry>();
        foreach (var file in Directory.EnumerateFiles(dir, "*", GameScanner.TreeOptions))
        {
            ct.ThrowIfCancellationRequested();
            files.Add(new SourceEntry(Path.GetRelativePath(dir, file).Replace('\\', '/'), new FileInfo(file).Length));
        }
        Files = files;
    }

    public Stream OpenRead(string path) => new FileStream(Path.Combine(Location, path.Replace('/', '\\')),
        FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: false);
}

/// <summary>A view of one subfolder of a source, for dumps that keep the game in app0.</summary>
public sealed class RootedSource : IGameSource
{
    private readonly IGameSource _inner;
    private readonly string _prefix;

    public RootedSource(IGameSource inner, string prefix)
    {
        _inner = inner;
        _prefix = prefix.TrimEnd('/') + "/";
        Files = inner.Files.Where(f => f.Path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            .Select(f => new SourceEntry(f.Path[_prefix.Length..], f.Size)).ToList();
    }

    public string Location => _inner.Location;
    public IReadOnlyList<SourceEntry> Files { get; }
    public Stream OpenRead(string path) => _inner.OpenRead(_prefix + path);
}

/// <summary>
/// Reads the files of an exFAT disk image without mounting it. The image is
/// only ever opened for reading, and its contents are treated as untrusted:
/// every offset is checked and names that could escape a folder are rejected.
/// </summary>
public sealed class ExFatImage : IGameSource
{
    private const int MaxDepth = 64;
    private const int MaxFiles = 1_000_000;
    private const long MaxDirectoryBytes = 256L * 1024 * 1024;

    private sealed record Node(uint FirstCluster, long DataLength, long ValidLength, bool NoFatChain);

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceEntry> _files = new();
    private readonly HashSet<uint> _seenDirectories = new();
    private long _fatOffset, _heapOffset, _imageLength;
    private uint _clusterCount;
    private int _clusterSize;

    public string Location { get; }
    public IReadOnlyList<SourceEntry> Files => _files;
    public int SkippedNames { get; private set; }

    public ExFatImage(string path, CancellationToken ct = default)
    {
        Location = path;
        using var image = OpenImage();
        uint root = ReadBootSector(image);
        var rootChain = FollowChain(image, root, 0, noFatChain: false);
        ct.ThrowIfCancellationRequested();
        Walk(image, rootChain, rootChain.Count * (long)_clusterSize, "", 0, ct);
    }

    private FileStream OpenImage() =>
        new(Location, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: false);

    private uint ReadBootSector(FileStream image)
    {
        _imageLength = image.Length;
        var b = new byte[512];
        image.Position = 0;
        if (image.Read(b, 0, b.Length) < b.Length || Encoding.ASCII.GetString(b, 3, 8) != "EXFAT   ")
            throw new InvalidDataException("This isn't an exFAT disk image.");

        int sectorShift = b[108], clusterShift = b[109];
        if (sectorShift is < 9 or > 12 || clusterShift > 25 || sectorShift + clusterShift > 25)
            throw new InvalidDataException("The disk image's header is damaged.");
        long sector = 1L << sectorShift;
        _clusterSize = 1 << (sectorShift + clusterShift);
        _fatOffset = BitConverter.ToUInt32(b, 80) * sector;
        _heapOffset = BitConverter.ToUInt32(b, 88) * sector;
        _clusterCount = BitConverter.ToUInt32(b, 92);
        uint root = BitConverter.ToUInt32(b, 96);
        if (_clusterCount == 0 || _fatOffset >= _imageLength || _heapOffset >= _imageLength || !ValidCluster(root))
            throw new InvalidDataException("The disk image's header is damaged.");
        return root;
    }

    private bool ValidCluster(uint c) => c >= 2 && c - 2 < _clusterCount;

    private long ClusterOffset(uint cluster) => _heapOffset + (cluster - 2L) * _clusterSize;

    /// <summary>The clusters of a file. A length of 0 follows the chain to its end.</summary>
    private List<uint> FollowChain(FileStream image, uint first, long length, bool noFatChain)
    {
        long wanted = length == 0 ? long.MaxValue : (length + _clusterSize - 1) / _clusterSize;
        var chain = new List<uint>();
        if (noFatChain)
        {
            if (!ValidCluster(first) || wanted > _clusterCount - (first - 2L))
                throw new InvalidDataException("A file in the disk image runs past its end.");
            for (long i = 0; i < wanted; i++) chain.Add(first + (uint)i);
            return chain;
        }

        var entry = new byte[4];
        uint c = first;
        while (chain.Count < wanted)
        {
            if (!ValidCluster(c))
            {
                if (length == 0 && chain.Count > 0) break; // end of chain
                throw new InvalidDataException("A file in the disk image has a broken cluster chain.");
            }
            if (chain.Count >= _clusterCount)
                throw new InvalidDataException("A file in the disk image has a looping cluster chain.");
            chain.Add(c);
            image.Position = _fatOffset + c * 4L;
            if (image.Read(entry, 0, 4) < 4)
                throw new InvalidDataException("The disk image is cut short.");
            c = BitConverter.ToUInt32(entry, 0);
        }
        return chain;
    }

    private void Walk(FileStream image, List<uint> clusters, long length, string parent, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth) throw new InvalidDataException("The disk image's folders are nested too deeply.");
        if (length > MaxDirectoryBytes) throw new InvalidDataException("A folder in the disk image is too large.");

        var data = new byte[length];
        for (int i = 0; i < clusters.Count; i++)
        {
            int want = (int)Math.Min(_clusterSize, length - i * (long)_clusterSize);
            if (want <= 0) break;
            image.Position = ClusterOffset(clusters[i]);
            image.ReadExactly(data, i * _clusterSize, want);
        }

        var subfolders = new List<(string Path, Node Node)>();
        for (int i = 0; i + 32 <= data.Length;)
        {
            ct.ThrowIfCancellationRequested();
            byte type = data[i];
            if (type == 0) break; // end of directory
            if (type != 0x85) { i += 32; continue; }

            int secondary = data[i + 1];
            int setEnd = i + 32 * (secondary + 1);
            if (secondary < 2 || setEnd > data.Length || data[i + 32] != 0xC0) { i += 32; continue; }

            bool isDir = (BitConverter.ToUInt16(data, i + 4) & 0x10) != 0;
            int stream = i + 32;
            int nameLength = data[stream + 3];
            var name = new StringBuilder(nameLength);
            for (int e = stream + 32; e < setEnd && data[e] == 0xC1; e += 32)
                for (int k = 2; k < 32 && name.Length < nameLength; k += 2)
                    name.Append((char)BitConverter.ToUInt16(data, e + k));

            var node = new Node(BitConverter.ToUInt32(data, stream + 20), (long)Math.Min(BitConverter.ToUInt64(data, stream + 24), long.MaxValue),
                (long)Math.Min(BitConverter.ToUInt64(data, stream + 8), long.MaxValue), (data[stream + 1] & 2) != 0);
            i = setEnd;

            string text = name.ToString();
            if (!IsSafeName(text)) { SkippedNames++; continue; }
            string path = parent.Length == 0 ? text : parent + "/" + text;
            if (isDir)
            {
                subfolders.Add((path, node));
            }
            else
            {
                if (_files.Count >= MaxFiles) throw new InvalidDataException("The disk image holds too many files.");
                if (node.ValidLength > node.DataLength) throw new InvalidDataException("A file in the disk image is damaged.");
                if (_nodes.TryAdd(path, node)) _files.Add(new SourceEntry(path, node.DataLength));
            }
        }

        foreach (var (path, node) in subfolders)
        {
            if (node.DataLength == 0) continue;
            if (!_seenDirectories.Add(node.FirstCluster))
                throw new InvalidDataException("The disk image's folders loop back on themselves.");
            var chain = FollowChain(image, node.FirstCluster, node.DataLength, node.NoFatChain);
            Walk(image, chain, node.DataLength, path, depth + 1, ct);
        }
    }

    public static bool IsSafeName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')) return false;
        return name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0 && !name.Any(char.IsControl);
    }

    public Stream OpenRead(string path)
    {
        if (!_nodes.TryGetValue(path, out var node)) throw new FileNotFoundException("Not in the disk image.", path);
        var image = OpenImage();
        try
        {
            var clusters = node.DataLength == 0 ? new List<uint>() : FollowChain(image, node.FirstCluster, node.DataLength, node.NoFatChain);
            return new FileStreamView(image, clusters, _clusterSize, _heapOffset, node.DataLength, node.ValidLength);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>One file inside the image, read cluster by cluster.</summary>
    private sealed class FileStreamView : Stream
    {
        private readonly FileStream _image;
        private readonly List<uint> _clusters;
        private readonly int _clusterSize;
        private readonly long _heapOffset, _length, _valid;
        private long _position;

        public FileStreamView(FileStream image, List<uint> clusters, int clusterSize, long heapOffset, long length, long valid)
        {
            (_image, _clusters, _clusterSize, _heapOffset, _length, _valid) = (image, clusters, clusterSize, heapOffset, length, valid);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length || count <= 0) return 0;
            int inCluster = (int)(_position % _clusterSize);
            int n = (int)Math.Min(Math.Min(count, _clusterSize - inCluster), _length - _position);

            // exFAT doesn't store the bytes past a file's valid length; they read as zero.
            if (_position >= _valid)
            {
                Array.Clear(buffer, offset, n);
            }
            else
            {
                n = (int)Math.Min(n, _valid - _position);
                _image.Position = _heapOffset + (_clusters[(int)(_position / _clusterSize)] - 2L) * _clusterSize + inCluster;
                _image.ReadExactly(buffer, offset, n);
            }
            _position += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _image.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
