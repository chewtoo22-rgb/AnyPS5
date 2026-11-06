using System.Diagnostics;
using System.Text;
using AnyPS5Studio;

// args[0]: folder of the real AnyPS5 install
// args[1]: path of the stand-in relinker built for these tests
string realEngineDir = args[0];
string fakeRelinker = args[1];

// Dump mode, for comparing against the official tool on a real image:
// Tests <engine> <fake> <image.exfat> <outDir>
if (args.Length == 4)
{
    var image = new ExFatImage(args[2]);
    Directory.CreateDirectory(args[3]);
    using var list = new StreamWriter(Path.Combine(args[3], "hashes.txt"));
    const long Whole = 300L * 1024 * 1024;
    foreach (var file in image.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var input = image.OpenRead(file.Path);
        var buffer = new byte[1 << 20];
        long at = 0;
        int n;
        // Whole file when small; first and last 1 MiB of large ones.
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (file.Size <= Whole) sha.TransformBlock(buffer, 0, n, null, 0);
            else
            {
                long end = at + n;
                if (at < (1 << 20) || end > file.Size - (1 << 20)) sha.TransformBlock(buffer, 0, n, null, 0);
                at = end;
            }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        list.WriteLine($"{Convert.ToHexString(sha.Hash!).ToLowerInvariant()} {file.Size} {file.Path}");

        string low = file.Path.ToLowerInvariant();
        if (low == "eboot.bin" || low.EndsWith(".prx") || low.EndsWith(".sprx"))
            File.WriteAllBytes(Path.Combine(args[3], file.Path.Replace('/', '_') + ".elf"), SelfUnwrapper.Unwrap(image.ReadAll(file.Path)));
    }
    Console.WriteLine($"dumped {image.Files.Count} files, skipped names {image.SkippedNames}");
    return 0;
}

int passed = 0, failed = 0;
string root = Path.Combine(Path.GetTempPath(), "anyps5 studio tests " + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
var noProgress = new Progress<ConvertProgress>(_ => { });

async Task Test(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine($"PASS  {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL  {name}\n      {e.GetType().Name}: {e.Message}"); }
}

void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

async Task Throws<T>(Func<Task> body, string contains) where T : Exception
{
    try { await body(); }
    catch (T e)
    {
        Check(e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase), $"message was: {e.Message}");
        return;
    }
    throw new Exception($"expected {typeof(T).Name}");
}

byte[] Elf(int size = 64) { var b = new byte[size]; b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F'; return b; }
byte[] Self() => new byte[] { 0x54, 0x14, 0xF5, 0xEE, 1, 2, 3, 4 };

string NewDir(string name) { var d = Path.Combine(root, Guid.NewGuid().ToString("N")[..6], name); Directory.CreateDirectory(d); return d; }

void Put(string path, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }

const string ParamJson = """{"titleId":"PPSA00001","localizedParameters":{"defaultLanguage":"fr-FR","en-US":{"titleName":"Test Game"},"fr-FR":{"titleName":"Jeu Test"}}}""";

string MakeGame(string moduleDir = "sce_module", bool param = true)
{
    string g = NewDir("Game Source");
    Put(Path.Combine(g, "eboot.bin"), Elf(4096));
    if (moduleDir.Length > 0)
    {
        Put(Path.Combine(g, moduleDir, "libgame.prx"), Elf(128));
        Put(Path.Combine(g, moduleDir, "shared.prx"), Elf(128));
    }
    Put(Path.Combine(g, "data", "level1.dat"), Encoding.UTF8.GetBytes("level one"));
    if (param) File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(g, "sce_sys")).FullName, "param.json"), ParamJson);
    return g;
}

EngineInfo FakeEngine(string? script = null)
{
    string e = NewDir("engine");
    File.Copy(fakeRelinker, Path.Combine(e, "relinker.exe"));
    foreach (var n in new[] { "libkernel.prx", "libc.prx", "libgcc_s_seh-1.dll" })
        Put(Path.Combine(e, "libs", n), Encoding.UTF8.GetBytes("system library " + n));
    if (script != null) File.WriteAllText(Path.Combine(e, "fake-mode.txt"), script);
    return new EngineInfo(e, "test");
}

byte[] Sfo(string title, string id)
{
    var keys = Encoding.ASCII.GetBytes("TITLE\0TITLE_ID\0\0");
    var t = Encoding.UTF8.GetBytes(title + "\0");
    var i = Encoding.UTF8.GetBytes(id + "\0");
    int count = 2, keyStart = 20 + 16 * count, dataStart = keyStart + keys.Length;
    var ms = new MemoryStream();
    var w = new BinaryWriter(ms);
    w.Write(new byte[] { 0, (byte)'P', (byte)'S', (byte)'F' }); w.Write(0x101); w.Write(keyStart); w.Write(dataStart); w.Write(count);
    w.Write((ushort)0); w.Write((ushort)0x204); w.Write(t.Length); w.Write(128); w.Write(0);
    w.Write((ushort)6); w.Write((ushort)0x204); w.Write(i.Length); w.Write(12); w.Write(t.Length);
    w.Write(keys); w.Write(t); w.Write(i);
    return ms.ToArray();
}

// --- scanning

await Test("scan: reads a normal game, picking the default language", async () =>
{
    var s = GameScanner.Scan(MakeGame());
    Check(s.CanConvert, string.Join(";", s.Problems));
    Check(s.Title == "Jeu Test" && s.TitleId == "PPSA00001", $"{s.Title}/{s.TitleId}");
    Check(s.ModuleCount == 2 && s.ModuleDirName == "sce_module" && !s.UseCurrentDirAsModules, "modules");
    Check(s.TotalBytes > 4096, "size");
    Check(s.Warnings.Count == 0, string.Join(";", s.Warnings));
    await Task.CompletedTask;
});

await Test("scan: refuses an encrypted executable", async () =>
{
    string g = MakeGame();
    Put(Path.Combine(g, "eboot.bin"), Self());
    var s = GameScanner.Scan(g);
    Check(!s.CanConvert && s.Problems.Any(p => p.Contains("can't be used")), string.Join(";", s.Problems));
    await Task.CompletedTask;
});

await Test("scan: refuses a folder with no executable", async () =>
{
    var s = GameScanner.Scan(NewDir("empty"));
    Check(!s.CanConvert && s.Problems.Any(p => p.Contains("eboot.bin")), "should refuse");
    await Task.CompletedTask;
});

await Test("scan: refuses sce_module and sce_modules together", async () =>
{
    string g = MakeGame();
    Put(Path.Combine(g, "sce_modules", "x.prx"), Elf());
    Check(!GameScanner.Scan(g).CanConvert, "should refuse");
    await Task.CompletedTask;
});

await Test("scan: warns about modules that are still wrapped", async () =>
{
    string g = MakeGame();
    Put(Path.Combine(g, "sce_module", "wrapped.prx"), Self());
    var s = GameScanner.Scan(g);
    Check(s.CanConvert && s.Warnings.Any(w => w.Contains("1 of 3")), string.Join(";", s.Warnings));
    await Task.CompletedTask;
});

await Test("scan: no module folder uses --module-dir .", async () =>
{
    string g = MakeGame(moduleDir: "");
    var s = GameScanner.Scan(g);
    Check(s.UseCurrentDirAsModules, "flag");
    var a = Converter.BuildArguments(s, "input", @"C:\o\g.exe", false);
    Check(string.Join(' ', a).Contains("--module-dir ."), string.Join(' ', a));
    await Task.CompletedTask;
});

await Test("scan: finds a game one level down in app0", async () =>
{
    string outer = NewDir("dump");
    Put(Path.Combine(outer, "app0", "eboot.bin"), Elf());
    var s = GameScanner.Scan(outer);
    Check(s.SourcePath.EndsWith("app0") && s.ExecutableRel != null, s.SourcePath);
    await Task.CompletedTask;
});

await Test("scan: reads param.sfo when param.json is missing", async () =>
{
    string g = MakeGame(param: false);
    Put(Path.Combine(g, "sce_sys", "param.sfo"), Sfo("Sfo Game", "PPSA00002"));
    var s = GameScanner.Scan(g);
    Check(s.Title == "Sfo Game" && s.TitleId == "PPSA00002", $"{s.Title}/{s.TitleId}");
    await Task.CompletedTask;
});

await Test("scan: damaged metadata warns instead of crashing", async () =>
{
    foreach (var junk in new[] { new byte[] { 0, (byte)'P', (byte)'S', (byte)'F', 9, 9 }, Encoding.UTF8.GetBytes("{not json") })
    {
        string g = MakeGame(param: false);
        Put(Path.Combine(g, "sce_sys", junk[0] == 0 ? "param.sfo" : "param.json"), junk);
        var s = GameScanner.Scan(g);
        Check(s.CanConvert && s.Warnings.Any(w => w.Contains("param")), "warning expected");
    }
    await Task.CompletedTask;
});

// --- arguments and names

await Test("args: Intel hosts get --to-intel, others don't", async () =>
{
    var s = GameScanner.Scan(MakeGame());
    Check(Converter.BuildArguments(s, "i", "o.exe", true).Contains("--to-intel"), "intel");
    Check(!Converter.BuildArguments(s, "i", "o.exe", false).Contains("--to-intel"), "amd");
    Check(Converter.BuildArguments(s, "i", "o.exe", false)[0] == "--windows", "windows first");
    await Task.CompletedTask;
});

await Test("names: non-ASCII and reserved characters are dropped", async () =>
{
    Check(Converter.SafeFileName("Jeu: Évasion?") == "Jeu vasion", Converter.SafeFileName("Jeu: Évasion?"));
    Check(Converter.SafeFileName("ゲーム") == "game", "fallback");
    await Task.CompletedTask;
});

// --- converting

await Test("convert: builds the exact runtime layout", async () =>
{
    var scan = GameScanner.Scan(MakeGame());
    string o = Path.Combine(NewDir("out"), "Jeu Test");
    var r = await Converter.RunAsync(new ConvertRequest(scan, o, FakeEngine()), noProgress, _ => { }, default);

    Check(File.Exists(r.ExePath) && Path.GetDirectoryName(r.ExePath) == o, "exe beside libs");
    Check(File.Exists(Path.Combine(o, "libs", "libkernel.prx")), "libs/");
    Check(File.Exists(Path.Combine(o, "libs", "libgcc_s_seh-1.dll")), "libs dll");
    Check(File.Exists(Path.Combine(o, "app0", "eboot.bin")), "app0/eboot.bin");
    Check(File.Exists(Path.Combine(o, "app0", "data", "level1.dat")), "resources");
    Check(File.Exists(Path.Combine(o, "app0", "sce_sys", "param.json")), "param");
    Check(File.ReadAllText(Path.Combine(o, "app0", "sce_module", "libgame.prx.guest.prx")) == "converted", "converted module");
    Check(File.ReadAllBytes(Path.Combine(o, "app0", "sce_module", "libgame.prx")).Length == 3, "module the relinker wrote must not be overwritten by the source copy");
    Check(!Directory.EnumerateFiles(o, "*.part", SearchOption.AllDirectories).Any(), "no .part left");
});

await Test("convert: eboot.elf still lands as app0/eboot.bin", async () =>
{
    string g = MakeGame();
    File.Move(Path.Combine(g, "eboot.bin"), Path.Combine(g, "eboot.elf"));
    var scan = GameScanner.Scan(g);
    string o = Path.Combine(NewDir("out"), "x");
    await Converter.RunAsync(new ConvertRequest(scan, o, FakeEngine()), noProgress, _ => { }, default);
    Check(File.Exists(Path.Combine(o, "app0", "eboot.bin")), "eboot.bin");
});

await Test("convert: running again finishes cleanly and refreshes libs", async () =>
{
    var scan = GameScanner.Scan(MakeGame());
    string o = Path.Combine(NewDir("out"), "x");
    var engine = FakeEngine();
    await Converter.RunAsync(new ConvertRequest(scan, o, engine), noProgress, _ => { }, default);
    Put(Path.Combine(engine.LibsDir, "libkernel.prx"), Encoding.UTF8.GetBytes("newer system library, longer"));
    await Converter.RunAsync(new ConvertRequest(scan, o, engine), noProgress, _ => { }, default);
    Check(File.ReadAllText(Path.Combine(o, "libs", "libkernel.prx")).StartsWith("newer"), "libs refreshed");
});

await Test("convert: won't touch a folder that already has other files", async () =>
{
    var scan = GameScanner.Scan(MakeGame());
    string o = NewDir("my documents");
    Put(Path.Combine(o, "precious.txt"), new byte[] { 1 });
    await Throws<ConvertException>(() => Converter.RunAsync(new ConvertRequest(scan, o, FakeEngine()), noProgress, _ => { }, default), "already has files");
    Check(File.ReadAllBytes(Path.Combine(o, "precious.txt")).Length == 1 && Directory.GetFiles(o).Length == 1, "folder untouched");
});

await Test("convert: won't mix two games in one folder", async () =>
{
    string o = Path.Combine(NewDir("out"), "x");
    var engine = FakeEngine();
    await Converter.RunAsync(new ConvertRequest(GameScanner.Scan(MakeGame()), o, engine), noProgress, _ => { }, default);
    string other = MakeGame(param: false);
    Put(Path.Combine(other, "sce_sys", "param.sfo"), Sfo("Other", "PPSA00009"));
    await Throws<ConvertException>(() => Converter.RunAsync(new ConvertRequest(GameScanner.Scan(other), o, engine), noProgress, _ => { }, default), "different game");
});

await Test("convert: refuses saving inside the game folder, and the reverse", async () =>
{
    string g = MakeGame();
    var scan = GameScanner.Scan(g);
    await Throws<ConvertException>(() => Converter.RunAsync(new ConvertRequest(scan, Path.Combine(g, "out"), FakeEngine()), noProgress, _ => { }, default), "inside");
    await Throws<ConvertException>(() => Converter.RunAsync(new ConvertRequest(scan, Path.GetDirectoryName(g)!, FakeEngine()), noProgress, _ => { }, default), "inside");
});

await Test("convert: a relinker failure reports its real error", async () =>
{
    var scan = GameScanner.Scan(MakeGame());
    string o = Path.Combine(NewDir("out"), "x");
    await Throws<ConvertException>(() => Converter.RunAsync(new ConvertRequest(scan, o, FakeEngine("fail")), noProgress, _ => { }, default), "unsupported syscall in eboot");
    Check(!Directory.Exists(Path.Combine(o, "libs")), "stops before copying");
});

await Test("convert: exit 0 with no program written is a failure", async () =>
{
    var scan = GameScanner.Scan(MakeGame());
    await Throws<ConvertException>(() => Converter.RunAsync(new ConvertRequest(scan, Path.Combine(NewDir("out"), "x"), FakeEngine("nooutput")), noProgress, _ => { }, default), "didn't write");
});

await Test("convert: cancel stops the relinker quickly and can be resumed", async () =>
{
    var scan = GameScanner.Scan(MakeGame());
    string o = Path.Combine(NewDir("out"), "x");
    var engine = FakeEngine("hang");
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    var sw = Stopwatch.StartNew();
    try
    {
        await Converter.RunAsync(new ConvertRequest(scan, o, engine), noProgress, _ => { }, cts.Token);
        throw new Exception("should have cancelled");
    }
    catch (OperationCanceledException) { }
    Check(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    File.Delete(Path.Combine(engine.Folder, "fake-mode.txt"));
    await Converter.RunAsync(new ConvertRequest(scan, o, engine), noProgress, _ => { }, default);
    Check(File.Exists(Path.Combine(o, "app0", "eboot.bin")), "resumed");
});

await Test("convert: not-enough-space message is in plain numbers", async () =>
{
    var scan = GameScanner.Scan(MakeGame());
    scan.TotalBytes = long.MaxValue / 4;
    await Throws<ConvertException>(() => Converter.RunAsync(new ConvertRequest(scan, Path.Combine(NewDir("out"), "x"), FakeEngine()), noProgress, _ => { }, default), "Not enough disk space");
});

// --- the real relinker

var real = new EngineInfo(realEngineDir, "real");

await Test("real relinker: starts, and a bad ELF fails with a readable reason", async () =>
{
    var scan = GameScanner.Scan(MakeGame());
    string o = Path.Combine(NewDir("out"), "x");
    var lines = new List<string>();
    // The reason, not the "Input: <path>" line printed after it.
    await Throws<ConvertException>(() => Converter.RunAsync(new ConvertRequest(scan, o, real), noProgress, l => { lock (lines) lines.Add(l); }, default), "Expected little-endian ELF64");
    Console.WriteLine("      relinker said: " + string.Join(" | ", lines.Skip(1).Take(3)));
    Check(lines[0].StartsWith("relinker --windows"), lines[0]);
});

await Test("real engine: located and reports a version", async () =>
{
    var e = EngineLocator.Find(realEngineDir);
    Check(e != null, "not found");
    Console.WriteLine("      version: " + e!.Version);
    await Task.CompletedTask;
});

await Test("engine: a folder without libs is not accepted", async () =>
{
    string d = NewDir("noengine");
    File.Copy(fakeRelinker, Path.Combine(d, "relinker.exe"));
    Check(!EngineLocator.IsEngineFolder(d), "must need libs");
    await Task.CompletedTask;
});

await Test("patches: only this game's patch file is used", async () =>
{
    var e = FakeEngine();
    Put(Path.Combine(e.Folder, "patches", "PPSA00001-wait.toml"), new byte[] { 1 });
    Put(Path.Combine(e.Folder, "patches", "PPSA00002-other.toml"), new byte[] { 1 });
    Check(Path.GetFileName(Converter.FindPatch(e, "PPSA00001")!) == "PPSA00001-wait.toml", "match");
    Check(Converter.FindPatch(e, "PPSA00003") == null, "no match");
    Check(Converter.FindPatch(e, "") == null, "no title id");
    Check(Converter.FindPatch(e, @"..\PPSA00001") == null, "unsafe id");
    var s = GameScanner.Scan(MakeGame());
    var a = string.Join(' ', Converter.BuildArguments(s, "i", "o", false, "p.toml"));
    Check(a.Contains("--patches p.toml"), a);
    await Task.CompletedTask;
});

// --- SELF containers

byte[] MakeSelf(bool encrypted)
{
    var d = new byte[0x2000];
    BitConverter.TryWriteBytes(d.AsSpan(0), 0xEEF51454u);
    // One record: the data of program header 0, at 0x1000.
    BitConverter.TryWriteBytes(d.AsSpan(0x40), 0x800UL);
    BitConverter.TryWriteBytes(d.AsSpan(0x48), 0x1000UL);
    BitConverter.TryWriteBytes(d.AsSpan(0x50), 0x1000UL);
    BitConverter.TryWriteBytes(d.AsSpan(0x58), 0x1000UL);
    int e = 0x100;
    new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 9 }.CopyTo(d, e);
    BitConverter.TryWriteBytes(d.AsSpan(e + 0x10), (ushort)3);
    BitConverter.TryWriteBytes(d.AsSpan(e + 0x12), (ushort)62);
    BitConverter.TryWriteBytes(d.AsSpan(e + 0x14), 1u);
    BitConverter.TryWriteBytes(d.AsSpan(e + 0x20), 0x40UL);
    BitConverter.TryWriteBytes(d.AsSpan(e + 0x34), (ushort)64);
    BitConverter.TryWriteBytes(d.AsSpan(e + 0x36), (ushort)56);
    BitConverter.TryWriteBytes(d.AsSpan(e + 0x38), (ushort)1);
    int p = e + 0x40;
    BitConverter.TryWriteBytes(d.AsSpan(p), 1u);
    BitConverter.TryWriteBytes(d.AsSpan(p + 4), 5u);
    BitConverter.TryWriteBytes(d.AsSpan(p + 8), 0x1000UL);
    BitConverter.TryWriteBytes(d.AsSpan(p + 16), 0x4000UL);
    BitConverter.TryWriteBytes(d.AsSpan(p + 32), 0x1000UL);
    BitConverter.TryWriteBytes(d.AsSpan(p + 40), 0x1000UL);
    BitConverter.TryWriteBytes(d.AsSpan(p + 48), 0x1000UL);
    var code = d.AsSpan(0x1000, 0x1000);
    if (encrypted) new Random(5).NextBytes(code); else code.Fill(0x90);
    return d;
}

await Test("self: a decrypted container becomes an ELF with its code aligned", async () =>
{
    var elf = SelfUnwrapper.Unwrap(MakeSelf(false));
    Check(elf[0] == 0x7F && elf[1] == 'E', "ELF magic");
    Check(elf[0x1000] == 0x90 && elf[0x1FFF] == 0x90, "code at its aligned offset");
    Check(BitConverter.ToUInt64(elf, 0x40 + 8) == 0x1000, "program header offset rewritten");
    await Task.CompletedTask;
});

await Test("self: an encrypted container is refused, never copied", async () =>
{
    await Throws<SelfRefused>(() => { SelfUnwrapper.Unwrap(MakeSelf(true)); return Task.CompletedTask; }, "looks encrypted");
});

await Test("self: truncated and corrupt containers are refused", async () =>
{
    await Throws<SelfRefused>(() => { SelfUnwrapper.Check(new byte[16]); return Task.CompletedTask; }, "smaller");
    var noElf = MakeSelf(false);
    noElf[0x100] = 0;
    await Throws<SelfRefused>(() => { SelfUnwrapper.Check(noElf); return Task.CompletedTask; }, "no inner ELF");
    var tooBig = MakeSelf(false);
    BitConverter.TryWriteBytes(tooBig.AsSpan(0x100 + 0x38), (ushort)60000);
    await Throws<SelfRefused>(() => { SelfUnwrapper.Check(tooBig); return Task.CompletedTask; }, "does not fit");
});

await Test("convert: a folder of SELF files is unwrapped and the temp folder removed", async () =>
{
    string g = MakeGame();
    Put(Path.Combine(g, "eboot.bin"), MakeSelf(false));
    Put(Path.Combine(g, "sce_module", "libgame.prx"), MakeSelf(false));
    var scan = GameScanner.Scan(g);
    Check(scan.CanConvert && scan.NeedsUnwrap, string.Join(";", scan.Problems));
    string o = Path.Combine(NewDir("out"), "x");
    var r = await Converter.RunAsync(new ConvertRequest(scan, o, FakeEngine()), noProgress, _ => { }, default);
    Check(File.Exists(r.ExePath), "exe");
    Check(SelfUnwrapper.HasSelfMagic(File.ReadAllBytes(Path.Combine(o, "app0", "eboot.bin"))), "app0 keeps the title's original eboot.bin");
    Check(!Directory.Exists(Path.Combine(o, ".anyps5-prepare")), "temp folder removed");
});

// --- exFAT images

const int ExCluster = ExBuilder.ExCluster;

string SaveImage(ExBuilder b)
{
    string p = Path.Combine(NewDir("img"), "PPSA09999.exfat");
    File.WriteAllBytes(p, b.Image);
    return p;
}

ExBuilder GameImage()
{
    var b = new ExBuilder();
    int at = b.Offset(2);
    uint eboot = b.Data(Elf(6000));
    b.Entry(ref at, "eboot.bin", false, eboot, 6000, true);

    uint dir = b.NextCluster++;
    int d = b.Offset(dir);
    uint mod = b.Data(Elf(100));
    b.Entry(ref d, "libgame.prx", false, mod, 100, true);
    b.Entry(ref at, "sce_module", true, dir, ExCluster, true);

    uint sys = b.NextCluster++;
    int y = b.Offset(sys);
    uint param = b.Data(Encoding.UTF8.GetBytes(ParamJson));
    b.Entry(ref y, "param.json", false, param, (ulong)ParamJson.Length, true);
    b.Entry(ref at, "sce_sys", true, sys, ExCluster, true);

    // A fragmented file: two clusters out of order, joined by the FAT.
    uint second = b.NextCluster++, first = b.NextCluster++;
    new byte[ExCluster].AsSpan().Fill(0xAA);
    b.Image.AsSpan(b.Offset(first), ExCluster).Fill(0xAA);
    b.Image.AsSpan(b.Offset(second), 10).Fill(0xBB);
    b.SetFat(first, second);
    b.SetFat(second, 0xFFFFFFFF);
    b.Entry(ref at, "a-rather-long-file-name-over-fifteen.dat", false, first, ExCluster + 10, false);
    return b;
}

await Test("exfat: reads names, folders, plain and fragmented files", async () =>
{
    var img = new ExFatImage(SaveImage(GameImage()));
    Check(img.Files.Count == 4, string.Join(",", img.Files.Select(f => f.Path)));
    Check(img.Exists("sce_module/libgame.prx"), "nested file");
    var frag = img.ReadAll("a-rather-long-file-name-over-fifteen.dat");
    Check(frag.Length == ExCluster + 10 && frag[0] == 0xAA && frag[ExCluster - 1] == 0xAA && frag[ExCluster] == 0xBB && frag[^1] == 0xBB, "chain order");
    await Task.CompletedTask;
});

await Test("exfat: bytes past the valid length read as zero", async () =>
{
    var b = new ExBuilder();
    int at = b.Offset(2);
    var data = new byte[3000];
    Array.Fill(data, (byte)7);
    b.Entry(ref at, "partial.bin", false, b.Data(data), 3000, true, valid: 1000);
    var read = new ExFatImage(SaveImage(b)).ReadAll("partial.bin");
    Check(read.Length == 3000 && read[999] == 7 && read[1000] == 0 && read[2999] == 0, "zero fill");
    await Task.CompletedTask;
});

await Test("exfat: names that could escape the folder are left out", async () =>
{
    var b = new ExBuilder();
    int at = b.Offset(2);
    uint c = b.Data(Elf());
    b.Entry(ref at, "..", false, c, 64, true);
    b.Entry(ref at, @"..\evil.dll", false, c, 64, true);
    b.Entry(ref at, "C:evil", false, c, 64, true);
    b.Entry(ref at, "good.bin", false, c, 64, true);
    var img = new ExFatImage(SaveImage(b));
    Check(img.Files.Count == 1 && img.Files[0].Path == "good.bin" && img.SkippedNames == 3, string.Join(",", img.Files.Select(f => f.Path)));
    await Task.CompletedTask;
});

await Test("exfat: damaged images fail with a message instead of hanging", async () =>
{
    var loop = new ExBuilder();
    loop.SetFat(2, 2);
    await Throws<InvalidDataException>(() => { _ = new ExFatImage(SaveImage(loop)); return Task.CompletedTask; }, "looping");

    var past = new ExBuilder();
    int at = past.Offset(2);
    past.Entry(ref at, "huge.bin", false, 60, 1UL << 40, true);
    await Throws<InvalidDataException>(() => { new ExFatImage(SaveImage(past)).OpenRead("huge.bin"); return Task.CompletedTask; }, "past its end");

    var self = new ExBuilder();
    int r = self.Offset(2);
    self.Entry(ref r, "again", true, 2, ExCluster, true);
    await Throws<InvalidDataException>(() => { _ = new ExFatImage(SaveImage(self)); return Task.CompletedTask; }, "loop");

    var noMagic = new ExBuilder();
    noMagic.Image[3] = 0;
    await Throws<InvalidDataException>(() => { _ = new ExFatImage(SaveImage(noMagic)); return Task.CompletedTask; }, "isn't an exFAT");
});

await Test("scan: reads an image directly and from a folder holding one", async () =>
{
    string path = SaveImage(GameImage());
    foreach (var pick in new[] { path, Path.GetDirectoryName(path)! })
    {
        var s = GameScanner.Scan(pick);
        Check(s.CanConvert && s.IsImage, string.Join(";", s.Problems));
        Check(s.Title == "Jeu Test" && s.TitleId == "PPSA00001" && s.ModuleCount == 1, $"{s.Title} {s.ModuleCount}");
        Check(s.SourcePath == path, s.SourcePath);
    }
    await Task.CompletedTask;
});

await Test("scan: a broken image is reported, not fatal", async () =>
{
    string p = Path.Combine(NewDir("img"), "bad.exfat");
    File.WriteAllBytes(p, new byte[4096]);
    var s = GameScanner.Scan(p);
    Check(!s.CanConvert && s.Problems.Any(x => x.Contains("isn't an exFAT")), string.Join(";", s.Problems));
    await Task.CompletedTask;
});

await Test("scan: a .pkg is refused without scanning the folder around it", async () =>
{
    string g = MakeGame();
    Put(Path.Combine(g, "UP9000-PPSA00001_00-GAME.pkg"), new byte[] { 0x7F, (byte)'F', (byte)'I', (byte)'H', 1, 0, 3, 0 });
    var s = GameScanner.Scan(Path.Combine(g, "UP9000-PPSA00001_00-GAME.pkg"));
    Check(!s.CanConvert && s.Problems.Count == 1 && s.Problems[0].Contains("can't open packages"), string.Join(";", s.Problems));
    Check(s.ModuleCount == 0 && s.Warnings.Count == 0 && s.TotalBytes == 8, $"{s.ModuleCount} {s.Warnings.Count} {s.TotalBytes}");

    // Told apart by its header too, whatever it's called.
    string renamed = Path.Combine(NewDir("pkg"), "game.bin");
    File.WriteAllBytes(renamed, new byte[] { 0x7F, (byte)'C', (byte)'N', (byte)'T' });
    Check(GameScanner.Scan(renamed).Problems.Single().Contains("can't open packages"), "header");
    await Task.CompletedTask;
});

await Test("scan: a chosen file that isn't code is refused, even a tiny one", async () =>
{
    string g = MakeGame();
    Put(Path.Combine(g, "notes.txt"), new byte[] { 1 });
    var s = GameScanner.Scan(Path.Combine(g, "notes.txt"));
    Check(!s.CanConvert && s.Problems.Single().Contains("isn't a PS5 executable") && s.ModuleCount == 0, string.Join(";", s.Problems));
    await Task.CompletedTask;
});

await Test("scan: a folder holding only a .pkg says so", async () =>
{
    string d = NewDir("pkg-only");
    Put(Path.Combine(d, "game.pkg"), new byte[16]);
    Check(GameScanner.Scan(d).Problems.Single().Contains("can't open packages"), "folder");
    await Task.CompletedTask;
});

await Test("scan: a folder with a .pkg and an image still uses the image", async () =>
{
    string path = SaveImage(GameImage());
    Put(Path.Combine(Path.GetDirectoryName(path)!, "game.pkg"), new byte[16]);
    var s = GameScanner.Scan(Path.GetDirectoryName(path)!);
    Check(s.CanConvert && s.IsImage, string.Join(";", s.Problems));
    await Task.CompletedTask;
});

await Test("scan: choosing an ELF directly still works", async () =>
{
    string g = MakeGame();
    var s = GameScanner.Scan(Path.Combine(g, "eboot.bin"));
    Check(s.CanConvert && s.ExecutableRel == "eboot.bin", string.Join(";", s.Problems));
    await Task.CompletedTask;
});

string MakeRun(string runs, string name, RunFacts facts, string[] summary, string[] console, string? report = null, string? machine = null)
{
    string dir = Path.Combine(runs, name);
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, RunSession.FactsName), System.Text.Json.JsonSerializer.Serialize(facts));
    File.WriteAllLines(Path.Combine(dir, RunSession.SummaryName), summary);
    File.WriteAllLines(Path.Combine(dir, RunSession.ConsoleName), console);
    if (report != null) File.WriteAllText(Path.Combine(dir, RunSession.ReportName), report);
    if (machine != null) File.WriteAllText(Path.Combine(dir, RunSession.MachineName), machine);
    return dir;
}

// The shape of a line of the engine's aps5-summary.jsonl.
string Interval(double start, int presents, double p50, double p99, double max, double gpu, double worker = 99.8, string res = "3840x2160") =>
    FormattableString.Invariant($$"""{"resolution":"{{res}}","interval_start_s":{{start}},"interval_end_s":{{start + 5}},"presents":{{presents}},"frame_ms":{"p50":{{p50}},"p90":{{p50}},"p99":{{p99}},"max":{{max}}},"gpu_busy_ms_per_present":{{gpu}},"worker_busy_pct":{"queue worker 0x0":{{worker}},"queue worker 0x20":50},"top_phases_ms_per_frame":{"Driver.Packet":270,"GpuMutex.wait.dispatch":35,"Driver.Packet.memory_wait":37},"trace":""}""");

RunFacts Facts(int? code = 0, bool stopped = true, bool detailed = false) => new()
{
    Title = "Test", TitleId = "PPSA00001", EngineVersion = "v1", Started = new DateTime(2026, 1, 1, 10, 0, 0),
    Ended = new DateTime(2026, 1, 1, 10, 2, 0), ExitCode = code, StoppedByUser = stopped, Detailed = detailed,
};

const string GpuLine = "[000030.000] [gputime] 4737 ms of GPU time in 3109 batches over 10 s (batch 5627 ms first to last command; classes 698 ms, all ranges 5434 ms, untimed 193 ms outside every range; 53 presents; 0 ranges dropped at the 512 cap); by program: 0x16c00c6c00 x53 4277ms 0x1ac0107b00 x54 177ms; by class: draw x18 2.8ms 0.0MiB (per present x9.0 1.38ms 0.00MiB) storage-upload x87 1060.0ms 768.6MiB (per present x43.5 12.10ms 384.29MiB)";

await Test("diagnosis: names the GPU as the limit and the shader behind it", async () =>
{
    var d = RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", Facts(detailed: true),
        new[] { Interval(0, 2, 12528, 12528, 12528, 103), Interval(5, 36, 136, 147, 147, 103), Interval(10, 37, 136, 150, 150, 103), "{\"cut off by a cra" },
        new[] { GpuLine, "[000031.000] [present] 73 presents over 10 s; per frame, presented or dropped: GPU busy 103.0 ms, idle gaps 35.2 ms (0 batches)" }));
    Check(d.Fps is > 7 && d.Fps < 7.5 && d.FrameMs == 136 && d.GpuBusyMs == 103 && d.GpuIdleMs == 35.2, $"{d.Fps} {d.FrameMs} {d.GpuBusyMs} {d.GpuIdleMs}");
    Check(d.Verdict.Contains("One shader") || d.Verdict.Contains("GPU is limiting"), d.Verdict);
    Check(d.Findings.Any(f => f.Title.Contains("GPU is limiting this game to about 7 FPS")), "bottleneck");
    Check(d.Shaders[0].Program == "0x16c00c6c00" && Math.Abs(d.Shaders[0].MsPerFrame - 4277.0 / 53) < 0.01 && Math.Round(d.Shaders[0].SharePct) == 90, d.Shaders[0].ToString());
    Check(d.Findings.Any(f => f.Title.Contains("One shader takes 81 ms") && f.NextStep.Contains("APS5_DUMP_SHADERS=1")), "shader finding");
    Check(d.Findings.Any(f => f.NextStep.Contains("APS5_OUTPUT_RESOLUTION=1920x1080")), "resolution hint");
    Check(d.Findings.Any(f => f.Title.Contains("Copying data around costs 20 ms")), "copies");
    Check(d.Findings.Any(f => f.Title.Contains("1 long pause of")), "the startup gap counts as a pause, not as a frame rate");
    await Task.CompletedTask;
});

await Test("diagnosis: a fast run is called good and gets no advice", async () =>
{
    var d = RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", Facts(), new[] { Interval(0, 300, 16.6, 17, 18, 9), Interval(5, 300, 16.6, 17, 18, 9) }, Array.Empty<string>()));
    Check(d.Findings[0].Severity == Severity.Good && d.Findings[0].Title.Contains("60 FPS"), d.Verdict);
    Check(!d.Findings.Any(f => f.NextStep.Length > 0), "no advice");
    await Task.CompletedTask;
});

await Test("diagnosis: a busy worker with an idle GPU isn't blamed on the GPU", async () =>
{
    var d = RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", Facts(), new[] { Interval(0, 30, 100, 110, 110, 20), Interval(5, 30, 100, 110, 110, 20) }, Array.Empty<string>()));
    Check(d.Verdict.Contains("command threads"), d.Verdict);
    await Task.CompletedTask;
});

await Test("diagnosis: a crash gives the exception, its meaning and the output", async () =>
{
    var d = RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", Facts(unchecked((int)0xC0000005), false), Array.Empty<string>(),
        new[] { "[000010.000] normal line", "[000011.000] ", "[000011.000] FATAL: unhandled exception 0xc0000005 on thread 1234 'GameThread'", "[000011.000] rip=0x1000 in nvgpucomp64.dll" }));
    Check(d.Outcome == "Crashed" && d.Verdict.Contains("access violation"), d.Verdict);
    var f = d.Findings[0];
    Check(f.Evidence.Contains("GameThread") && f.NextStep.Contains("NVIDIA"), f.ToString());
    Check(d.CrashExcerpt.Contains("rip=0x1000") && RunAnalyzer.RenderReport(d).Contains("Crash output"), d.CrashExcerpt);
    await Task.CompletedTask;
});

await Test("diagnosis: a missing function is named as work for the engine", async () =>
{
    const string report = """{"run":{"exit":"clean","stage":"starts","recovered":false},"stop":{"kind":"missing-function","reason":"runtime-stop","message":"","source":null},"missing":{"functions":[{"library":"libSceNet","name":"sceNetFoo","calls":1}]},"shaders":{"unsupported":[]}}""";
    var d = RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", Facts(1, false), Array.Empty<string>(), new[] { "[000001.000] x" }, report));
    Check(d.Findings[0].Severity == Severity.Critical && d.Findings[0].Evidence.Contains("libSceNet: sceNetFoo (1)"), d.Verdict);
    Check(d.Findings.Any(f => f.Title.Contains("never reached its first frame")), "stage");
    await Task.CompletedTask;
});

await Test("diagnosis: a report recovered from an earlier run isn't read as this run", async () =>
{
    const string report = """{"run":{"exit":"killed","stage":"unknown","recovered":true},"stop":{"kind":"other","reason":"unclean-unknown","message":"old","source":null},"missing":{"functions":[{"library":"x","name":"old","calls":1}]}}""";
    var d = RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", Facts(), new[] { Interval(0, 300, 16.6, 17, 18, 9) }, Array.Empty<string>(), report));
    Check(!d.Findings.Any(f => f.Evidence.Contains("old")), d.Verdict);
    await Task.CompletedTask;
});

await Test("diagnosis: hardware samples are read, and empty GPU columns mean no claim", async () =>
{
    const string header = "t,cpu_cores,system_cpu_pct,working_set_mb,private_mb,read_mb_s,write_mb_s,avail_ram_mb,threads,responding,summary_bytes,gpu_pct,vram_mb,vram_total_mb,gpu_mhz,gpu_max_mhz,gpu_watts,gpu_limit_watts,gpu_temp\n";
    string withGpu = header + string.Join("\n", Enumerable.Range(1, 40).Select(i => $"{i}.0,4.00,30.0,1000,2000,1.0,0.0,1000,100,1,{(i < 3 ? i : 3)},99,11500,12000,2600,3000,200,200,88"));
    var d = RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", Facts(), new[] { Interval(0, 30, 100, 110, 110, 90) }, Array.Empty<string>(), null, withGpu));
    Check(d.GpuLoadPct == 99 && d.PeakVramMb == 11500, $"{d.GpuLoadPct} {d.PeakVramMb}");

    // Load before the first frame isn't held against the GPU.
    string startup = header + "1.0,4.00,30.0,1000,2000,1.0,0.0,9000,100,1,0,0,500,12000,0,0,0,0,40\n2.0,4.00,30.0,1000,2000,1.0,0.0,9000,100,1,5,80,500,12000,0,0,0,0,40";
    Check(RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", Facts(), new[] { Interval(0, 30, 100, 110, 110, 90) }, Array.Empty<string>(), null, startup)).GpuLoadPct == 80, "startup");
    foreach (var want in new[] { "Video memory was nearly full", "ran low on memory", "reached 88", "No frames for 37 s" })
        Check(d.Findings.Any(f => f.Title.Contains(want)), want);

    string noGpu = header + "1.0,4.00,30.0,1000,2000,1.0,0.0,9000,100,1,3,,,,,,,,";
    d = RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", Facts(), new[] { Interval(0, 30, 100, 110, 110, 90) }, Array.Empty<string>(), null, noGpu));
    Check(d.GpuLoadPct == null && !d.Findings.Any(f => f.Evidence.Contains("nvidia-smi")), "no gpu data");
    await Task.CompletedTask;
});

await Test("diagnosis: a run is compared with the one before, with what changed", async () =>
{
    string runs = NewDir("runs");
    var before = Facts();
    string a = MakeRun(runs, "2026-01-01_10-00-00", before, new[] { Interval(0, 36, 136, 147, 147, 103) }, Array.Empty<string>());
    RunAnalyzer.AnalyzeAndSave(a);
    var after = Facts();
    after.Switches["APS5_OUTPUT_RESOLUTION"] = "1920x1080";
    string b = MakeRun(runs, "2026-01-01_11-00-00", after, new[] { Interval(0, 100, 50, 60, 60, 30, res: "1920x1080") }, Array.Empty<string>());
    var d = RunAnalyzer.AnalyzeAndSave(b);
    Check(d.Comparison.Contains("FPS 7.2 -> 20.0 (+178%)") && d.Comparison.Contains("APS5_OUTPUT_RESOLUTION: off -> 1920x1080"), d.Comparison);
    Check(File.ReadAllText(Path.Combine(b, RunAnalyzer.DiagnosisReport)).Contains("Compared with"), "report");

    // A switch that changed nothing is reported, not suggested again.
    var ignored = Facts();
    ignored.Switches["APS5_OUTPUT_RESOLUTION"] = "1920x1080";
    var kept = RunAnalyzer.Analyze(MakeRun(runs, "2026-01-01_12-00-00", ignored, new[] { Interval(0, 36, 136, 147, 147, 103) }, Array.Empty<string>()));
    Check(kept.Findings.Any(f => f.Title.Contains("kept its 3840x2160 output")) && !kept.Findings.Any(f => f.NextStep.Contains("1920x1080")), kept.Verdict);
    var listed = RunAnalyzer.LoadRuns(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(a) ?? "") ?? "") ?? "");
    Check(listed.Count == 0, "only a real game folder layout is listed");
    await Task.CompletedTask;
});

await Test("diagnosis: a run with nothing recorded still gives a report", async () =>
{
    string dir = NewDir("empty-run");
    File.WriteAllText(Path.Combine(dir, RunSession.FactsName), "{}");
    var d = RunAnalyzer.AnalyzeAndSave(dir);
    Check(d.Verdict.Length > 0 && File.Exists(Path.Combine(dir, RunAnalyzer.DiagnosisReport)), d.Verdict);
    await Task.CompletedTask;
});

await Test("test mode: picks a shader that's heavy every frame, never a one-off", async () =>
{
    string Gpu(string programs) => $"[gputime] 300 ms of GPU time in 303 batches over 10 s (batch 304 ms first to last command; 2 presents; 0 ranges dropped at the 512 cap); by program: {programs}; by class: draw x16 6.5ms 0.0MiB (per present x8.0 3.25ms 0.00MiB)";
    var s = new ShaderSkipper();
    // Real lines: a single 112 ms job at load, then light shaders.
    Check(!s.Observe(Gpu("0x1740043a00 x1 112ms 0x1e4010a000 x1 7ms")), "a one-off");
    Check(!s.Observe(Gpu("0x1740034b00 x62 221ms")), "busy but cheap each time");
    Check(!s.Observe("[present] 73 presents over 10 s"), "other lines");
    Check(!s.Observe(Gpu("0x16c00b0a00 x2 216ms 0x1e4010a000 x2 13ms")), "two runs aren't enough");
    Check(s.Observe(Gpu("0x16c00b0a00 x3 283ms")) && s.Program == "0x16c00b0a00", s.Program ?? "none");
    Check(!s.Observe(Gpu("0x9999990000 x90 9000ms")) && s.Program == "0x16c00b0a00", "stays on the first choice");
    await Task.CompletedTask;
});

await Test("test mode: frames with and without the shader are reported apart", async () =>
{
    var f = Facts();
    f.TestMode = true;
    f.SkipEvents.Add(new SkipEvent(31, true, "0x16c00b0a00"));
    f.SkipEvents.Add(new SkipEvent(62, false, "0x16c00b0a00"));
    const string report = """{"run":{"exit":"clean","stage":"presents","recovered":false,"milestones":{"firstPresent":10}},"stop":{"kind":"none","reason":"quit-requested","message":"","source":null},"missing":{"functions":[]}}""";
    // Frames start 10 s into the run, so 20 s here is 30 s into it.
    var d = RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", f,
        new[] { Interval(0, 24, 128, 140, 140, 103), Interval(5, 24, 128, 140, 140, 103), Interval(10, 24, 128, 140, 140, 103),
                Interval(20, 24, 128, 140, 140, 103), Interval(25, 61, 48, 60, 60, 22), Interval(30, 61, 48, 60, 60, 22), Interval(35, 61, 48, 60, 60, 22),
                Interval(50, 24, 128, 140, 140, 103), Interval(55, 24, 128, 140, 140, 103) },
        new[] { GpuLine.Replace("0x16c00c6c00", "0x16c00b0a00") }, report));
    var note = d.Findings.Single(x => x.Area == "Test mode");
    Check(note.Evidence.Contains("with it on: 128 ms") && note.Evidence.Contains("With it off: 48 ms"), note.Evidence);
    Check(d.FrameMs == 48 && d.GpuBusyMs == 22 && d.SkippedShader == "0x16c00b0a00" && d.Label.Contains("test mode"), $"{d.FrameMs} {d.GpuBusyMs}");
    Check(!d.Findings.Any(x => x.Title.Contains("One shader takes")), "the skipped shader isn't blamed for frames it wasn't in");
    await Task.CompletedTask;
});

await Test("test mode: says so when frames can't be separated, or nothing qualified", async () =>
{
    var f = Facts(code: -1);
    f.TestMode = true;
    f.SkipEvents.Add(new SkipEvent(31, true, "0x16c00b0a00"));
    var d = RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", f, new[] { Interval(0, 24, 128, 140, 140, 103) }, Array.Empty<string>()));
    Check(d.Findings.Single(x => x.Area == "Test mode").Evidence.Contains("numbers here mix both"), d.Verdict);

    var none = Facts();
    none.TestMode = true;
    d = RunAnalyzer.Analyze(MakeRun(NewDir("runs"), "a", none, new[] { Interval(0, 24, 128, 140, 140, 103) }, Array.Empty<string>()));
    Check(d.Findings.Any(x => x.Title.Contains("found no shader heavy enough")), d.Verdict);
    await Task.CompletedTask;
});

await Test("test mode: the engine's skip list holds the shader only while it's off", async () =>
{
    string g = NewDir("tm-game");
    var entry = new LibraryEntry { Title = "x", ExePath = Path.Combine(g, "x.exe"), SkipHeaviestShader = true };
    using var session = new RunSession(entry, false, new Dictionary<string, string>(), entry.SkipHeaviestShader);
    string chosen = "";
    session.ShaderChosen += p => chosen = p;
    var psi = new ProcessStartInfo("x");
    session.ApplyTo(psi);
    string file = Path.Combine(session.Folder, RunSession.SkipFileName);
    Check(psi.Environment["APS5_SKIP_SHADER_FILE"] == file && psi.Environment["APS5_PROFILE_GPU"] == "1", "switches");
    Check(!File.ReadAllText(file).Contains("16c0"), "nothing skipped at the start");

    session.Line("[gputime] 1 ms of GPU time in 1 batches over 10 s (x; 2 presents; 0); by program: 0x16c00b0a00 x3 283ms; by class:");
    Check(chosen == "0x16c00b0a00" && File.ReadAllText(file).Trim() == "16c00b0a00", File.ReadAllText(file));
    session.SetShaderOff(false);
    Check(!File.ReadAllText(file).Contains("16c0"), "back on");
    session.SetShaderOff(true);
    Check(File.ReadAllText(file).Trim() == "16c00b0a00", "off again");
    await Task.CompletedTask;
});

await Test("switches: only engine switches are passed to a game", async () =>
{
    var s = GameRunner.ParseSwitches("APS5_OUTPUT_RESOLUTION=1920x1080; ANYPS5_FPS_LIMIT=30 APS5_NO_GPL PATH=C:\\evil =x", out var rejected);
    Check(s.Count == 3 && s["APS5_OUTPUT_RESOLUTION"] == "1920x1080" && s["APS5_NO_GPL"] == "1", string.Join(",", s));
    Check(rejected.SequenceEqual(new[] { "PATH=C:\\evil", "=x" }), string.Join(",", rejected));
    await Task.CompletedTask;
});

await Test("conversion: failures are explained, and unknown ones say so", async () =>
{
    foreach (var (message, title) in new[]
    {
        ("The relinker couldn't convert this game: forbidden syscall 454 in sce_module/libc.prx at 0x116c84", "direct system call"),
        ("Not enough disk space: needs 40 GB, 3 GB free on C:\\", "ran out of space"),
        ("The relinker rejected the request: patch mismatch at 0x10: expected bytes differ", "game patch"),
        ("The relinker couldn't convert this game: not an ELF file", "encrypted"),
        ("The relinker couldn't convert this game: Expected little-endian ELF64 version 1", "encrypted"),
    })
        Check(ConversionDiagnosis.Classify(message, Array.Empty<string>(), "Converting").Title.Contains(title), message);

    Check(ConversionDiagnosis.Classify("The relinker couldn't convert this game (exit code 2).", new[] { "scanning", "found syscall in libc" }, "Converting").Title.Contains("system call"), "output");
    Check(ConversionDiagnosis.Classify("something new", Array.Empty<string>(), "Copying").Evidence.Contains("Copying"), "unknown");
    await Task.CompletedTask;
});

await Test("conversion: an attempt leaves a record, with the failure in it", async () =>
{
    string saved = AppLog.Folder;
    AppLog.Folder = NewDir("applog");
    try
    {
        var scan = GameScanner.Scan(MakeGame());
        using var record = new ConversionRecord(scan, "C:\\out", new EngineInfo("C:\\engine", "v9"));
        record.Stage("Converting the game");
        record.Line("FAIL: forbidden syscall");
        var findings = record.Finish("Failed", "forbidden syscall");
        record.Dispose();
        Check(findings.Single().Title.Contains("system call"), findings.Single().Title);
        string log = File.ReadAllText(Path.Combine(record.Folder, ConversionRecord.LogName));
        Check(log.Contains("Engine: v9") && log.Contains("--- Converting the game") && log.Contains("FAIL: forbidden syscall") && log.Contains("Finished: Failed"), log);
        Check(File.ReadAllText(Path.Combine(record.Folder, ConversionRecord.ReportName)).Contains("direct system call"), "report");
        Check(File.ReadAllText(AppLog.FilePath).Contains("forbidden syscall"), "app log");
    }
    finally { AppLog.Folder = saved; }
    await Task.CompletedTask;
});

await Test("scan: other module folders are relinked, but not fakelib or sce_sys", async () =>
{
    string g = MakeGame();
    Put(Path.Combine(g, "plugins", "libPlugin.prx"), Elf());
    Put(Path.Combine(g, "fakelib", "libSceAgcDriver.sprx"), Elf());
    Put(Path.Combine(g, "sce_sys", "about", "right.sprx"), Elf());
    var s = GameScanner.Scan(g);
    Check(s.ExtraModuleDirs.SequenceEqual(new[] { "plugins" }), string.Join(",", s.ExtraModuleDirs));
    Check(!s.ModuleFiles.Any(f => f.StartsWith("fakelib")), "fakelib left out");
    var a = string.Join(' ', Converter.BuildArguments(s, "i", "o", false));
    Check(a.Contains("--module-dir plugins") && !a.Contains("fakelib"), a);
    await Task.CompletedTask;
});

await Test("convert: an image converts end to end and is left unchanged", async () =>
{
    string path = SaveImage(GameImage());
    string before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    var scan = GameScanner.Scan(path);
    string o = Path.Combine(NewDir("out"), "x");
    var r = await Converter.RunAsync(new ConvertRequest(scan, o, FakeEngine()), noProgress, _ => { }, default);
    Check(File.Exists(r.ExePath), "exe");
    Check(File.Exists(Path.Combine(o, "libs", "libkernel.prx")), "libs");
    Check(File.ReadAllBytes(Path.Combine(o, "app0", "eboot.bin")).Length == 6000, "eboot copied in full");
    Check(File.ReadAllBytes(Path.Combine(o, "app0", "a-rather-long-file-name-over-fifteen.dat"))[ExCluster] == 0xBB, "fragmented file copied");
    Check(File.ReadAllText(Path.Combine(o, "app0", "sce_module", "libgame.prx.guest.prx")) == "converted", "module relinked");
    Check(File.Exists(Path.Combine(o, "app0", "sce_sys", "param.json")), "metadata");
    Check(!Directory.Exists(Path.Combine(o, ".anyps5-prepare")), "temp folder removed");
    Check(before == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), "image unchanged");
});

await Test("convert: a failed relink still removes the temp folder", async () =>
{
    var scan = GameScanner.Scan(SaveImage(GameImage()));
    string o = Path.Combine(NewDir("out"), "x");
    await Throws<ConvertException>(() => Converter.RunAsync(new ConvertRequest(scan, o, FakeEngine("fail")), noProgress, _ => { }, default), "unsupported syscall");
    Check(!Directory.Exists(Path.Combine(o, ".anyps5-prepare")), "temp folder removed");
});

await Test("convert: an image with a bad module still goes ahead without it", async () =>
{
    string g = MakeGame();
    Put(Path.Combine(g, "sce_module", "locked.prx"), MakeSelf(true));
    var scan = GameScanner.Scan(g);
    Check(scan.CanConvert && scan.Warnings.Any(w => w.Contains("1 of 3")), string.Join(";", scan.Warnings));
    var lines = new List<string>();
    string o = Path.Combine(NewDir("out"), "x");
    await Converter.RunAsync(new ConvertRequest(scan, o, FakeEngine()), noProgress, l => { lock (lines) lines.Add(l); }, default);
    Check(lines.Any(l => l.Contains("Skipping sce_module/locked.prx")) || !scan.NeedsUnwrap, "skip is logged");
});

// --- library

await Test("library: saves and reloads", async () =>
{
    var store = new AppStore(NewDir("store"));
    store.SaveLibrary(new[] { new LibraryEntry { Title = "A", TitleId = "B", Folder = "C", ExePath = "D", ConvertedAt = new DateTime(2026, 1, 2) } });
    var back = store.LoadLibrary();
    Check(back.Count == 1 && back[0].Title == "A" && back[0].ConvertedAt.Year == 2026, "roundtrip");
    await Task.CompletedTask;
});

await Test("library: a damaged file is set aside, not fatal", async () =>
{
    string d = NewDir("store");
    File.WriteAllText(Path.Combine(d, "library.json"), "[{oops");
    var store = new AppStore(d);
    Check(store.LoadLibrary().Count == 0 && File.Exists(Path.Combine(d, "library.json.damaged")), "kept aside");
    await Task.CompletedTask;
});

// --- running

await Test("run: starts in the game folder, logs output, reports exit code", async () =>
{
    var scan = GameScanner.Scan(MakeGame());
    string o = Path.Combine(NewDir("out"), "x");
    var r = await Converter.RunAsync(new ConvertRequest(scan, o, FakeEngine()), noProgress, _ => { }, default);

    var entry = new LibraryEntry { Title = "x", ExePath = r.ExePath, Folder = o };
    var lines = new List<string>();
    var done = new TaskCompletionSource<(int Code, Diagnosis? Diagnosis)>();
    using var runner = new GameRunner();
    runner.Line += l => { lock (lines) lines.Add(l); };
    runner.Exited += (c, d) => done.TrySetResult((c, d));
    runner.Start(entry);
    var (code, diagnosis) = await done.Task.WaitAsync(TimeSpan.FromSeconds(20));

    Check(code == 7, $"exit {code}");
    Check(!runner.IsRunning, "not running");
    Check(lines.Any(l => l.Contains("app0 visible")), string.Join("|", lines));

    Check(diagnosis != null && diagnosis.ExitCode == 7 && diagnosis.Outcome == "Stopped unexpectedly", diagnosis?.Outcome ?? "none");
    Check(diagnosis!.Verdict.Contains("exit code 0x00000007"), diagnosis.Verdict);
    string log = File.ReadAllText(Path.Combine(diagnosis.Folder, RunSession.ConsoleName));
    Check(System.Text.RegularExpressions.Regex.IsMatch(log, @"^\[\d{6}\.\d{3}\] ", System.Text.RegularExpressions.RegexOptions.Multiline) && log.Contains("app0 visible"), log);
    Check(RunAnalyzer.LoadRuns(o).Single().Folder == diagnosis.Folder, "listed");
});

await Test("run: a missing program gives a clear message", async () =>
{
    using var runner = new GameRunner();
    await Throws<ConvertException>(() => { runner.Start(new LibraryEntry { ExePath = @"C:\nope\gone.exe" }); return Task.CompletedTask; }, "missing");
});

await Test("run: Stop ends a running game", async () =>
{
    var scan = GameScanner.Scan(MakeGame());
    string o = Path.Combine(NewDir("out"), "x");
    var r = await Converter.RunAsync(new ConvertRequest(scan, o, FakeEngine()), noProgress, _ => { }, default);
    File.WriteAllText(Path.Combine(o, "game-hang.txt"), "x");

    var done = new TaskCompletionSource<Diagnosis?>();
    using var runner = new GameRunner();
    runner.Exited += (_, d) => done.TrySetResult(d);
    runner.Start(new LibraryEntry { Title = "x", ExePath = r.ExePath, Folder = o });
    Check(runner.IsRunning, "running");
    await Task.Delay(300);
    runner.Stop();
    var diagnosis = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(!runner.IsRunning, "stopped");
    Check(diagnosis?.Outcome == "Stopped by you", diagnosis?.Outcome ?? "none");
    Check(!diagnosis!.Findings.Any(f => f.Severity == Severity.Critical), diagnosis.Verdict);
});

try { Directory.Delete(root, true); } catch { /* a slow process may still hold a file */ }
Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
