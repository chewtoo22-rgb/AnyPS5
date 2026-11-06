using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnyPS5Studio;

/// <summary>What the app found in a game folder or disk image, before anything is converted.</summary>
public sealed class GameScan
{
    /// <summary>The folder or disk image the game comes from.</summary>
    public required string SourcePath { get; init; }
    public IGameSource? Source { get; set; }
    public bool IsImage { get; set; }
    public string Title { get; set; } = "";
    public string TitleId { get; set; } = "";
    /// <summary>The executable's path inside <see cref="Source"/>.</summary>
    public string? ExecutableRel { get; set; }
    public string? ModuleDirName { get; set; }
    public bool UseCurrentDirAsModules { get; set; }
    /// <summary>Folders of modules besides the standard ones, such as fakelib.</summary>
    public List<string> ExtraModuleDirs { get; } = new();
    public List<string> ModuleFiles { get; } = new();
    /// <summary>Modules that can't be used, and why.</summary>
    public List<(string File, string Reason)> ModuleIssues { get; } = new();
    public int ModuleCount => ModuleFiles.Count;
    /// <summary>True when the game's code is in SELF containers the relinker can't read.</summary>
    public bool NeedsUnwrap { get; set; }
    public long TotalBytes { get; set; }
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool CanConvert => Problems.Count == 0 && ExecutableRel != null && Source != null;
}

public static class GameScanner
{
    private static readonly string[] ExecutableNames = { "eboot.bin", "eboot.elf" };
    private static readonly string[] ModuleDirNames = { "sce_module", "sce_modules", "prx" };
    private static readonly string[] ModuleExtensions = { ".prx", ".sprx", ".suprx" };

    public static readonly EnumerationOptions TreeOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // A link back up the tree would otherwise make the copy endless.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static bool IsImageFile(string path) => path.EndsWith(".exfat", StringComparison.OrdinalIgnoreCase);

    public static GameScan Scan(string path, CancellationToken ct = default)
    {
        string full = Path.GetFullPath(path);
        string? chosenFile = File.Exists(full) ? full : null;
        string dir = chosenFile != null ? Path.GetDirectoryName(full)! : full;
        string? image = chosenFile != null && IsImageFile(chosenFile) ? chosenFile : null;

        // Some dumps are one disk image in a folder of its own.
        if (chosenFile == null && Directory.Exists(dir) && !HasExecutable(dir) && !HasExecutable(Path.Combine(dir, "app0")))
        {
            var images = Directory.GetFiles(dir, "*.exfat");
            if (images.Length == 1) image = images[0];
            else if (images.Length > 1)
            {
                var several = new GameScan { SourcePath = dir, Title = new DirectoryInfo(dir).Name };
                several.Problems.Add($"This folder has {images.Length} .exfat images. Choose one with the Image file button.");
                return several;
            }
        }
        if (chosenFile != null && image == null && RefuseChosenFile(chosenFile) is { } refused) return refused;
        return image != null ? ScanImage(image, ct) : ScanFolder(dir, chosenFile, ct);
    }

    /// <summary>
    /// A file that isn't game code says nothing about the folder it sits in,
    /// so it's turned down here, before that folder is scanned as a game.
    /// </summary>
    private static GameScan? RefuseChosenFile(string file)
    {
        byte[] head = new byte[4];
        int read;
        using (var stream = File.OpenRead(file)) read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (read == head.Length && (IsElf(head) || SelfUnwrapper.HasSelfMagic(head))) return null;

        var scan = new GameScan { SourcePath = file, Title = Path.GetFileNameWithoutExtension(file), TotalBytes = new FileInfo(file).Length };
        scan.Problems.Add(read == head.Length && IsPackage(file, head)
            ? PackageMessage
            : $"{Path.GetFileName(file)} isn't a PS5 executable. AnyPS5 needs a decrypted ELF or SELF file.");
        return scan;
    }

    private const string PackageMessage =
        "This is a .pkg package, not an unpacked game, and AnyPS5 can't open packages. " +
        "It needs a decrypted game: a folder with eboot.bin in it, or an .exfat image.";

    private static bool IsElf(byte[] head) => head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F';

    // PS5 packages start 7F "FIH" and PS4 packages 7F "CNT".
    private static bool IsPackage(string file, byte[] head) =>
        file.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)
        || (head[0] == 0x7F && head[1] == (byte)'F' && head[2] == (byte)'I' && head[3] == (byte)'H')
        || (head[0] == 0x7F && head[1] == (byte)'C' && head[2] == (byte)'N' && head[3] == (byte)'T');

    private static GameScan ScanImage(string image, CancellationToken ct)
    {
        var scan = new GameScan { SourcePath = image, IsImage = true, Title = Path.GetFileNameWithoutExtension(image) };
        ExFatImage source;
        try
        {
            source = new ExFatImage(image, ct);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            scan.Problems.Add($"Couldn't read this disk image: {e.Message}");
            return scan;
        }
        if (source.SkippedNames > 0)
            scan.Warnings.Add($"{source.SkippedNames} items in the image have unsafe names and will be left out.");

        scan.Source = !HasExecutable(source) && HasExecutable(new RootedSource(source, "app0"))
            ? new RootedSource(source, "app0") : source;
        Inspect(scan, null);
        return scan;
    }

    private static GameScan ScanFolder(string dir, string? chosenFile, CancellationToken ct)
    {
        // Some dumps keep the game one level down, in an app0 folder.
        if (chosenFile == null && !HasExecutable(dir) && HasExecutable(Path.Combine(dir, "app0")))
            dir = Path.Combine(dir, "app0");

        var scan = new GameScan { SourcePath = dir, Title = new DirectoryInfo(dir).Name };
        if (!Directory.Exists(dir))
        {
            scan.Problems.Add("That folder doesn't exist.");
            return scan;
        }
        scan.Source = new FolderSource(dir, ct);
        Inspect(scan, chosenFile != null ? Path.GetFileName(chosenFile) : null);
        return scan;
    }

    private static void Inspect(GameScan scan, string? chosenExecutable)
    {
        var source = scan.Source!;
        ReadTitleMetadata(scan);
        scan.TotalBytes = source.Files.Sum(f => f.Size);
        scan.ExecutableRel = chosenExecutable ?? FindExecutable(source);

        if (scan.ExecutableRel == null)
        {
            bool hasPackage = source.Files.Any(f => !f.Path.Contains('/') && f.Path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase));
            scan.Problems.Add(hasPackage ? PackageMessage : "No game executable found. Expected eboot.bin at the top of the game.");
        }
        else
        {
            string name = Path.GetFileName(scan.ExecutableRel);
            switch (Classify(source, scan.ExecutableRel, out string reason))
            {
                case CodeKind.Elf:
                    break;
                case CodeKind.Self:
                    scan.NeedsUnwrap = true;
                    break;
                case CodeKind.Unusable:
                    scan.Problems.Add($"{name} can't be used: {reason}");
                    break;
                default:
                    scan.Problems.Add($"{name} isn't a PS5 executable. AnyPS5 needs a decrypted ELF or SELF file.");
                    break;
            }
        }
        ScanModules(scan);
    }

    private static bool HasExecutable(string dir) =>
        Directory.Exists(dir) && ExecutableNames.Any(n => File.Exists(Path.Combine(dir, n)));

    private static bool HasExecutable(IGameSource source) => ExecutableNames.Any(source.Exists);

    private static string? FindExecutable(IGameSource source)
    {
        foreach (var name in ExecutableNames)
            if (source.Exists(name)) return source.Files.First(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase)).Path;
        var elfs = source.Files.Where(f => !f.Path.Contains('/') && f.Path.EndsWith(".elf", StringComparison.OrdinalIgnoreCase)).ToList();
        return elfs.Count == 1 ? elfs[0].Path : null;
    }

    private static string FolderOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static bool IsModule(string path) =>
        ModuleExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static void ScanModules(GameScan scan)
    {
        var source = scan.Source!;
        var present = ModuleDirNames.Where(n => source.Files.Any(f => FolderOf(f.Path).Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (present.Contains("sce_module") && present.Contains("sce_modules"))
        {
            scan.Problems.Add("This game has both sce_module and sce_modules. AnyPS5 only accepts one of them.");
            return;
        }

        foreach (var name in present)
            scan.ModuleFiles.AddRange(source.Files.Where(f => FolderOf(f.Path).Equals(name, StringComparison.OrdinalIgnoreCase)).Select(f => f.Path));

        if (present.Count == 0)
        {
            // The relinker refuses a folder with none of the standard
            // directories unless it is told where modules are.
            scan.UseCurrentDirAsModules = true;
            scan.ModuleFiles.AddRange(source.Files.Where(f => !f.Path.Contains('/') && IsModule(f.Path)).Select(f => f.Path));
            if (scan.ModuleFiles.Count == 0)
                scan.Warnings.Add("No bundled modules found. Fine for small homebrew, unusual for a full game.");
        }
        else
        {
            scan.ModuleDirName = present[0];
        }

        // Modules in other folders only load if they're relinked too. Two are
        // left out: sce_sys holds console metadata, and fakelib holds console
        // stand-ins that would replace AnyPS5's own system libraries (its
        // libSceAgcDriver looks for the PS5's GPU and stops the game).
        var extras = source.Files.Where(f => IsModule(f.Path)).GroupBy(f => FolderOf(f.Path), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && !present.Contains(g.Key, StringComparer.OrdinalIgnoreCase)
                        && !g.Key.Equals("fakelib", StringComparison.OrdinalIgnoreCase)
                        && !g.Key.Equals("sce_sys", StringComparison.OrdinalIgnoreCase)
                        && !g.Key.StartsWith("sce_sys/", StringComparison.OrdinalIgnoreCase));
        foreach (var group in extras)
        {
            scan.ExtraModuleDirs.Add(group.Key);
            scan.ModuleFiles.AddRange(group.Select(f => f.Path));
        }

        int unusable = 0;
        foreach (var module in scan.ModuleFiles)
        {
            var kind = Classify(source, module, out string reason);
            if (kind == CodeKind.Self) scan.NeedsUnwrap = true;
            else if (kind != CodeKind.Elf)
            {
                unusable++;
                scan.ModuleIssues.Add((module, reason.Length > 0 ? reason : "not an ELF or SELF file"));
            }
        }
        if (unusable > 0)
            scan.Warnings.Add($"{unusable} of {scan.ModuleCount} modules can't be used because they're encrypted or damaged, " +
                              "so the game may fail to start.");
    }

    public enum CodeKind { Elf, Self, Other, Unusable }

    /// <summary>Works out what a game code file is, including whether a SELF can be unwrapped.</summary>
    public static CodeKind Classify(IGameSource source, string path, out string reason)
    {
        reason = "";
        try
        {
            byte[] head = source.ReadHead(path, 4);
            if (head.Length == 4 && IsElf(head)) return CodeKind.Elf;
            if (!SelfUnwrapper.HasSelfMagic(head)) return CodeKind.Other;
            SelfUnwrapper.Check(source.ReadAll(path));
            return CodeKind.Self;
        }
        catch (SelfRefused e)
        {
            reason = e.Message;
            return CodeKind.Unusable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            reason = e.Message;
            return CodeKind.Unusable;
        }
    }

    private static void ReadTitleMetadata(GameScan scan)
    {
        var source = scan.Source!;
        bool found = false;
        try
        {
            if (source.Exists("sce_sys/param.json")) found = ReadParamJson(source.ReadAll("sce_sys/param.json"), scan);
            if (!found && source.Exists("sce_sys/param.sfo")) found = ReadParamSfo(source.ReadAll("sce_sys/param.sfo"), scan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidDataException or ArgumentException)
        {
            found = false;
        }
        if (!found)
            scan.Warnings.Add("No valid sce_sys/param.json or param.sfo. Some games stop at startup without one.");
    }

    public static bool ReadParamJson(byte[] bytes, GameScan scan)
    {
        if (JsonNode.Parse(bytes) is not JsonObject root) return false;
        if (root["titleId"]?.GetValueKind() != JsonValueKind.String) return false;
        string id = root["titleId"]!.GetValue<string>();
        if (id.Length == 0 || root["localizedParameters"] is not JsonObject loc) return false;

        // Same order the AnyPS5 runtime uses to choose a language.
        string? lang = Str(loc["defaultLanguage"]) ?? Str(root["defaultLanguage"]);
        JsonObject? entry = (lang != null ? loc[lang] as JsonObject : null) ?? loc["en-US"] as JsonObject
                            ?? loc.Select(kv => kv.Value).OfType<JsonObject>().FirstOrDefault();
        string? name = entry != null ? Str(entry["titleName"]) : null;
        if (string.IsNullOrWhiteSpace(name)) return false;

        scan.TitleId = id;
        scan.Title = name;
        return true;
    }

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool ReadParamSfo(byte[] b, GameScan scan)
    {
        if (b.Length < 20 || b[0] != 0 || b[1] != 'P' || b[2] != 'S' || b[3] != 'F') return false;
        int keys = BitConverter.ToInt32(b, 8), data = BitConverter.ToInt32(b, 12), count = BitConverter.ToInt32(b, 16);
        if (keys < 0 || data < 0 || count < 0 || count > 4096) return false;

        string? title = null, id = null;
        for (int i = 0; i < count; i++)
        {
            int e = 20 + i * 16;
            if (e + 16 > b.Length) return false;
            int keyAt = keys + BitConverter.ToUInt16(b, e);
            int len = BitConverter.ToInt32(b, e + 4);
            int valueAt = data + BitConverter.ToInt32(b, e + 12);
            if (keyAt < 0 || keyAt >= b.Length || len < 0 || valueAt < 0 || valueAt + len > b.Length) return false;
            int keyEnd = Array.IndexOf(b, (byte)0, keyAt);
            if (keyEnd < 0) return false;
            string key = Encoding.ASCII.GetString(b, keyAt, keyEnd - keyAt);
            if (key is "TITLE" or "TITLE_ID")
            {
                string value = Encoding.UTF8.GetString(b, valueAt, len).TrimEnd('\0');
                if (key == "TITLE") title = value; else id = value;
            }
        }
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id)) return false;
        scan.Title = title;
        scan.TitleId = id;
        return true;
    }
}
