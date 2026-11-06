using System.Text;

// Writes a small exFAT volume. Clusters start at 2; 2 is the root directory.
sealed class ExBuilder
{
    public const int ExSector = 512, ExCluster = 4096, ExFat = 24, ExHeap = 64;

    public byte[] Image = new byte[ExHeap * ExSector + ExCluster * 64];
    public uint NextCluster = 3;

    public ExBuilder()
    {
        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(Image, 3);
        BitConverter.TryWriteBytes(Image.AsSpan(80), (uint)ExFat);
        BitConverter.TryWriteBytes(Image.AsSpan(84), (uint)8);
        BitConverter.TryWriteBytes(Image.AsSpan(88), (uint)ExHeap);
        BitConverter.TryWriteBytes(Image.AsSpan(92), 64u);
        BitConverter.TryWriteBytes(Image.AsSpan(96), 2u);
        Image[108] = 9;
        Image[109] = 3;
        SetFat(2, 0xFFFFFFFF);
    }

    public int Offset(uint cluster) => ExHeap * ExSector + (int)(cluster - 2) * ExCluster;
    public void SetFat(uint c, uint next) => BitConverter.TryWriteBytes(Image.AsSpan(ExFat * ExSector + (int)c * 4), next);

    public void Entry(ref int at, string name, bool dir, uint first, ulong size, bool contiguous, ulong? valid = null)
    {
        int nameEntries = (name.Length + 14) / 15;
        Image[at] = 0x85;
        Image[at + 1] = (byte)(1 + nameEntries);
        BitConverter.TryWriteBytes(Image.AsSpan(at + 4), (ushort)(dir ? 0x10 : 0x20));
        int s = at + 32;
        Image[s] = 0xC0;
        Image[s + 1] = (byte)(1 | (contiguous ? 2 : 0));
        Image[s + 3] = (byte)name.Length;
        BitConverter.TryWriteBytes(Image.AsSpan(s + 8), valid ?? size);
        BitConverter.TryWriteBytes(Image.AsSpan(s + 20), first);
        BitConverter.TryWriteBytes(Image.AsSpan(s + 24), size);
        for (int i = 0; i < nameEntries; i++)
        {
            int e = s + 32 * (i + 1);
            Image[e] = 0xC1;
            string part = name.Substring(i * 15, Math.Min(15, name.Length - i * 15));
            Encoding.Unicode.GetBytes(part).CopyTo(Image, e + 2);
        }
        at += 32 * (2 + nameEntries);
    }

    public uint Data(byte[] data)
    {
        uint first = NextCluster;
        data.CopyTo(Image, Offset(first));
        NextCluster += (uint)((data.Length + ExCluster - 1) / ExCluster);
        return first;
    }
}
