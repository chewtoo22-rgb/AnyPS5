using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace AnyPS5Studio;

/// <summary>The AnyPS5 relinker and the system libraries that go with it.</summary>
public sealed record EngineInfo(string Folder, string Version)
{
    public string RelinkerPath => Path.Combine(Folder, "relinker.exe");
    public string LibsDir => Path.Combine(Folder, "libs");
}

public static class EngineLocator
{
    public static bool IsEngineFolder(string dir) =>
        File.Exists(Path.Combine(dir, "relinker.exe")) && File.Exists(Path.Combine(dir, "libs", "libkernel.prx"));

    public static EngineInfo? Find(string? preferred = null)
    {
        var candidates = new List<string?> { preferred, Path.GetDirectoryName(Environment.ProcessPath),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AnyPS5") };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try { if (IsEngineFolder(c)) return new EngineInfo(Path.GetFullPath(c), ReadVersion(c)); }
            catch (ArgumentException) { /* a malformed PATH entry */ }
        }
        return null;
    }

    private static string ReadVersion(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(dir, "relinker.exe"), "--version")
            { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var read = p.StandardOutput.ReadToEndAsync();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(5000)) { p.Kill(true); return "unknown"; }
            string v = read.GetAwaiter().GetResult().Trim();
            if (v.StartsWith("relinker ", StringComparison.OrdinalIgnoreCase)) v = v["relinker ".Length..].Trim();
            return v.Length == 0 ? "unknown" : v;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return "unknown";
        }
    }
}

public sealed record ConvertRequest(GameScan Game, string OutputDir, EngineInfo Engine);
public sealed record ConvertResult(string OutputDir, string ExePath);
public sealed record ConvertProgress(string Stage, double? Fraction);

/// <summary>A failure with a message that is meant to be shown to the user as it is.</summary>
public sealed class ConvertException : Exception
{
    public ConvertException(string message) : base(message) { }
}

public static class Converter
{
    private const string MarkerName = ".anyps5studio.json";
    private const string WorkDirName = ".anyps5-prepare";
    private const long SpareBytes = 200L * 1024 * 1024;

    public static bool HostIsIntel()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("VendorIdentifier") as string)?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static string SafeFileName(string title)
    {
        // Ascii only: the relinker needs ASCII for its library search path.
        var sb = new StringBuilder();
        foreach (char c in title)
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_') sb.Append(c);
        string name = sb.ToString().Trim();
        return name.Length == 0 ? "game" : name;
    }

    /// <summary>A patch for this game, kept as patches\&lt;title id&gt;*.toml beside the relinker.</summary>
    public static string? FindPatch(EngineInfo engine, string titleId)
    {
        string dir = Path.Combine(engine.Folder, "patches");
        if (titleId.Length == 0 || !ExFatImage.IsSafeName(titleId) || !Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, titleId + "*.toml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    public static List<string> BuildArguments(GameScan game, string inputPath, string exePath, bool intelHost, string? patchFile = null)
    {
        var args = new List<string> { "--windows" };
        if (intelHost) args.Add("--to-intel");
        if (patchFile != null) { args.Add("--patches"); args.Add(patchFile); }
        if (game.UseCurrentDirAsModules) { args.Add("--module-dir"); args.Add("."); }
        foreach (var dir in game.ExtraModuleDirs) { args.Add("--module-dir"); args.Add(dir); }
        args.Add(inputPath);
        args.Add(exePath);
        return args;
    }

    private static string WithSeparator(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;

    private static bool IsInside(string path, string parent) =>
        WithSeparator(path).StartsWith(WithSeparator(parent), StringComparison.OrdinalIgnoreCase);

    public static void CheckOutputFolder(GameScan game, string outputDir)
    {
        if (IsInside(outputDir, game.SourcePath) || IsInside(game.SourcePath, outputDir))
            throw new ConvertException("The save folder can't be inside the game folder, or the other way round. Pick a separate folder.");

        if (!Directory.Exists(outputDir) || !Directory.EnumerateFileSystemEntries(outputDir).Any()) return;

        string marker = Path.Combine(outputDir, MarkerName);
        if (!File.Exists(marker))
            throw new ConvertException($"{outputDir} already has files in it. Pick an empty folder so nothing gets overwritten.");

        string? otherId = null;
        try { otherId = (JsonNode.Parse(File.ReadAllText(marker)) as JsonObject)?["titleId"]?.GetValue<string>(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        if (!string.Equals(otherId ?? "", game.TitleId, StringComparison.OrdinalIgnoreCase))
            throw new ConvertException($"{outputDir} holds a different game. Pick another folder.");
    }

    private static long TreeSize(string dir) => !Directory.Exists(dir) ? 0 :
        Directory.EnumerateFiles(dir, "*", GameScanner.TreeOptions).Sum(f => new FileInfo(f).Length);

    private static void WriteMarker(GameScan game, string outputDir, bool complete)
    {
        var o = new JsonObject { ["title"] = game.Title, ["titleId"] = game.TitleId, ["complete"] = complete };
        File.WriteAllText(Path.Combine(outputDir, MarkerName), o.ToJsonString());
    }

    public static async Task<ConvertResult> RunAsync(ConvertRequest req, IProgress<ConvertProgress> progress, Action<string> log, CancellationToken ct)
    {
        try
        {
            return await RunCoreAsync(req, progress, log, ct);
        }
        catch (InvalidDataException e)
        {
            throw new ConvertException($"The disk image couldn't be read: {e.Message}");
        }
    }

    private static async Task<ConvertResult> RunCoreAsync(ConvertRequest req, IProgress<ConvertProgress> progress, Action<string> log, CancellationToken ct)
    {
        var game = req.Game;
        if (!game.CanConvert) throw new ConvertException("This game can't be converted yet.");
        var source = game.Source!;
        string outDir = Path.GetFullPath(req.OutputDir);
        CheckOutputFolder(game, outDir);

        // The relinker reads plain files, so game code inside a disk image or
        // a SELF container goes through a temporary folder first.
        bool prepare = source is not FolderSource || game.NeedsUnwrap;

        progress.Report(new ConvertProgress("Checking disk space", null));
        await Task.Run(() =>
        {
            long libs = TreeSize(req.Engine.LibsDir);
            long code = !prepare ? 0 : source.Files.Where(f => f.Path == game.ExecutableRel || game.ModuleFiles.Contains(f.Path)).Sum(f => f.Size);
            long need = game.TotalBytes + libs + code + SpareBytes - TreeSize(outDir);
            long free = new DriveInfo(Path.GetPathRoot(outDir)!).AvailableFreeSpace;
            if (need > free)
                throw new ConvertException($"Not enough disk space: needs {FormatBytes(need)}, {FormatBytes(free)} free on {Path.GetPathRoot(outDir)}");
        }, ct);

        Directory.CreateDirectory(outDir);
        string exe = Path.Combine(outDir, SafeFileName(game.Title) + ".exe");
        WriteMarker(game, outDir, complete: false);

        string workDir = Path.Combine(outDir, WorkDirName);
        try
        {
            string input = Path.Combine(source.Location, game.ExecutableRel!.Replace('/', '\\'));
            if (prepare)
            {
                progress.Report(new ConvertProgress("Preparing the game code", null));
                input = await Task.Run(() => PrepareInput(game, workDir, log, ct), ct);
            }
            progress.Report(new ConvertProgress("Converting the game", null));
            await RelinkAsync(req, input, exe, log, ct);
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try { Directory.Delete(workDir, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"Couldn't remove {workDir}: {e.Message}"); }
            }
        }

        var libsSource = req.Engine.LibsDir;
        long libTotal = TreeSize(libsSource);
        long grand = Math.Max(1, libTotal + game.TotalBytes);
        long done = 0;
        void Advance(string stage, long bytes)
        {
            long now = Interlocked.Add(ref done, bytes);
            progress.Report(new ConvertProgress(stage, Math.Min(1.0, (double)now / grand)));
        }

        log($"Copying system libraries to {Path.Combine(outDir, "libs")}");
        foreach (var file in Directory.EnumerateFiles(libsSource, "*", GameScanner.TreeOptions))
        {
            ct.ThrowIfCancellationRequested();
            var from = new FileInfo(file);
            var to = new FileInfo(Path.Combine(outDir, "libs", Path.GetRelativePath(libsSource, file)));
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc) { Advance("Copying system libraries", from.Length); continue; }
            await CopyFileAsync(() => new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true),
                to.FullName, from.LastWriteTimeUtc, n => Advance("Copying system libraries", n), ct);
        }

        // Skipping files that exist protects what the relinker wrote into app0.
        string app0 = Path.Combine(outDir, "app0");
        log($"Copying game files to {app0}");
        foreach (var entry in source.Files)
        {
            ct.ThrowIfCancellationRequested();
            string dest = Path.Combine(app0, entry.Path.Replace('/', '\\'));
            if (File.Exists(dest)) { Advance("Copying game files", entry.Size); continue; }
            await CopyFileAsync(() => source.OpenRead(entry.Path), dest, null, n => Advance("Copying game files", n), ct);
        }

        // Games test /app0/eboot.bin to find their own folder.
        string eboot = Path.Combine(app0, "eboot.bin");
        if (!File.Exists(eboot))
            await CopyFileAsync(() => source.OpenRead(game.ExecutableRel!), eboot, null, _ => { }, ct);

        WriteMarker(game, outDir, complete: true);
        progress.Report(new ConvertProgress("Done", 1));
        return new ConvertResult(outDir, exe);
    }

    /// <summary>Writes the game code as plain ELF files and returns the executable's path.</summary>
    public static string PrepareInput(GameScan game, string workDir, Action<string> log, CancellationToken ct)
    {
        var source = game.Source!;
        var files = new List<string> { game.ExecutableRel! };
        files.AddRange(game.ModuleFiles);
        if (source.Exists("relinker-patches.toml")) files.Add("relinker-patches.toml");

        foreach (var rel in files)
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes = source.ReadAll(rel);
            if (SelfUnwrapper.HasSelfMagic(bytes))
            {
                try
                {
                    bytes = SelfUnwrapper.Unwrap(bytes);
                }
                catch (SelfRefused e)
                {
                    if (rel == game.ExecutableRel) throw new ConvertException($"{rel} can't be used: {e.Message}");
                    log($"Skipping {rel}: {e.Message}");
                    continue;
                }
            }
            string dest = Path.Combine(workDir, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
        }
        log($"Prepared {files.Count} game code files");
        return Path.Combine(workDir, game.ExecutableRel!.Replace('/', '\\'));
    }

    private static async Task RelinkAsync(ConvertRequest req, string input, string exe, Action<string> log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(req.Engine.RelinkerPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(input)!,
        };
        string? patch = FindPatch(req.Engine, req.Game.TitleId);
        if (patch != null) log($"Applying game patch {Path.GetFileName(patch)}");
        foreach (var a in BuildArguments(req.Game, input, exe, HostIsIntel(), patch)) psi.ArgumentList.Add(a);
        log("relinker " + string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

        string lastError = "", reason = "";
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            log(e.Data);
            string line = e.Data.Trim();
            // The relinker prints "FAIL: <reason>", then context such as
            // "Input: <path>", so the reason isn't always the last line.
            if (line.StartsWith("FAIL:", StringComparison.OrdinalIgnoreCase)) reason = line["FAIL:".Length..].Trim();
            else if (line.Length > 0 && !line.StartsWith("Input:", StringComparison.OrdinalIgnoreCase)) lastError = line;
        };
        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ConvertException($"Couldn't start the relinker: {e.Message}");
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using (ct.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } }))
            await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        if (p.ExitCode != 0)
        {
            string what = p.ExitCode == 1 ? "The relinker rejected the request" : "The relinker couldn't convert this game";
            string detail = reason.Length > 0 ? reason : lastError;
            throw new ConvertException(detail.Length > 0 ? $"{what}: {detail}" : $"{what} (exit code {p.ExitCode}).");
        }
        if (!File.Exists(exe))
            throw new ConvertException("The relinker finished but didn't write a program.");
    }

    private static async Task CopyFileAsync(Func<Stream> open, string dest, DateTime? modifiedUtc, Action<long> advance, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        // Writing to .part means a cancelled copy is never mistaken for a
        // finished file on the next run.
        string part = dest + ".part";
        try
        {
            await using (var input = open())
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    advance(n);
                }
            }
            if (modifiedUtc is DateTime stamp) File.SetLastWriteTimeUtc(part, stamp);
            File.Move(part, dest, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = Math.Max(0, bytes);
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} {units[u]}" : $"{v:0.0} {units[u]}";
    }
}

public sealed class LibraryEntry
{
    public string Title { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string Folder { get; set; } = "";
    public string ExePath { get; set; } = "";
    public DateTime ConvertedAt { get; set; }
    public string EngineVersion { get; set; } = "";
    /// <summary>Engine switches for this game, such as APS5_OUTPUT_RESOLUTION=1920x1080.</summary>
    public string EngineSwitches { get; set; } = "";
    /// <summary>Test mode: skip this game's heaviest shader to see what it does.</summary>
    public bool SkipHeaviestShader { get; set; }
    public string Subtitle => TitleId.Length > 0 ? TitleId : Path.GetFileName(Folder);
}

public sealed class AppSettings
{
    public string? EngineFolder { get; set; }
    public bool DetailedRecording { get; set; }
}

/// <summary>The list of converted games and the app's settings, stored per user.</summary>
public sealed class AppStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _dir;

    public AppStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnyPS5 Studio");
        Directory.CreateDirectory(_dir);
    }

    private string LibraryPath => Path.Combine(_dir, "library.json");
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public List<LibraryEntry> LoadLibrary() => Load<List<LibraryEntry>>(LibraryPath);
    public void SaveLibrary(IEnumerable<LibraryEntry> entries) => Save(LibraryPath, entries.ToList());
    public AppSettings LoadSettings() => Load<AppSettings>(SettingsPath);
    public void SaveSettings(AppSettings settings) => Save(SettingsPath, settings);

    private static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new T();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
        }
        catch (JsonException)
        {
            // Keep the damaged file so a bad write doesn't silently lose it.
            File.Move(path, path + ".damaged", overwrite: true);
            return new T();
        }
    }

    private static void Save<T>(string path, T value)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Runs one converted game and records the run.</summary>
public sealed class GameRunner : IDisposable
{
    // Long enough for a game running at a few frames per second to notice.
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(10);

    private Process? _process;
    private RunSession? _session;
    private volatile bool _stopping;
    private readonly ManualResetEventSlim _finished = new(true);

    public bool IsRunning { get; private set; }
    public event Action<string>? Line;
    public event Action<int, Diagnosis?>? Exited;
    public event Action<string>? ShaderSkipped;

    /// <summary>The shader test mode picked in this run, once it has picked one.</summary>
    public string? SkippedProgram => IsRunning ? _session?.SkippedProgram : null;
    public bool ShaderIsOff { get; private set; }

    public void SetShaderOff(bool off)
    {
        _session?.SetShaderOff(off);
        ShaderIsOff = off;
    }

    /// <summary>Reads KEY=VALUE pairs. Only engine switches are accepted, nothing else is passed on.</summary>
    public static Dictionary<string, string> ParseSwitches(string text, out List<string> rejected)
    {
        var result = new Dictionary<string, string>();
        rejected = new List<string>();
        foreach (string item in text.Split(new[] { ' ', ';', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = item.IndexOf('=');
            string key = equals > 0 ? item[..equals] : item;
            if (key.StartsWith("APS5_", StringComparison.Ordinal) || key.StartsWith("ANYPS5_", StringComparison.Ordinal))
                result[key] = equals > 0 ? item[(equals + 1)..] : "1";
            else rejected.Add(item);
        }
        return result;
    }

    public void Start(LibraryEntry game, bool detailed = false)
    {
        if (IsRunning) throw new InvalidOperationException("A game is already running.");
        if (!File.Exists(game.ExePath))
            throw new ConvertException($"{game.ExePath} is missing. It may have been moved or deleted.");

        var psi = new ProcessStartInfo(game.ExePath)
        {
            // Paths such as app0/ are relative to the working directory.
            WorkingDirectory = Path.GetDirectoryName(game.ExePath)!,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        var session = new RunSession(game, detailed, ParseSwitches(game.EngineSwitches, out _), game.SkipHeaviestShader);
        session.ApplyTo(psi);
        session.ShaderChosen += program =>
        {
            ShaderIsOff = true;
            ShaderSkipped?.Invoke(program);
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            session.Line(e.Data);
            Line?.Invoke(e.Data);
        }
        p.OutputDataReceived += OnData;
        p.ErrorDataReceived += OnData;
        p.Exited += (_, _) =>
        {
            int code = p.ExitCode;
            p.WaitForExit(); // lets the output readers finish
            var diagnosis = session.Finish(code, _stopping);
            session.Dispose();
            IsRunning = false;
            _finished.Set();
            Exited?.Invoke(code, diagnosis);
        };

        try { p.Start(); }
        catch (System.ComponentModel.Win32Exception e)
        {
            session.Dispose();
            throw new ConvertException($"Couldn't start the game: {e.Message}");
        }
        session.Began(p);
        IsRunning = true;
        ShaderIsOff = false;
        _stopping = false;
        _finished.Reset();
        _process = p;
        _session = session;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    /// <summary>
    /// Asks the game to close, because the engine only writes its report for a
    /// run it sees end. A game that doesn't respond is ended after a grace period.
    /// </summary>
    public void Stop()
    {
        var p = _process;
        if (!IsRunning || p == null) return;
        _stopping = true;
        try
        {
            p.Refresh();
            if (p.CloseMainWindow())
                Task.Delay(CloseGrace).ContinueWith(_ => Kill(p), TaskScheduler.Default);
            else
                Kill(p);
        }
        catch (InvalidOperationException) { }
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        Stop();
        // Gives the game time to close and the run time to be analyzed.
        if (!_finished.Wait(CloseGrace + TimeSpan.FromSeconds(5))) AppLog.Warn("The running game didn't finish closing.");
        _session?.Dispose();
        _process?.Dispose();
    }
}
