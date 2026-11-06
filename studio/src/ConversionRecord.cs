using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnyPS5Studio;

/// <summary>
/// Records one conversion attempt. It lives with the app rather than the game
/// because a failed conversion often leaves no game folder behind.
/// </summary>
public sealed class ConversionRecord : IDisposable
{
    public const string LogName = "conversion.log";
    public const string ReportName = "diagnosis.md";
    private const int AttemptsKept = 30;

    private readonly StreamWriter _log;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _recent = new();
    private readonly GameScan _game;
    private string _stage = "Starting";

    public string Folder { get; }

    public static string Root => Path.Combine(AppLog.Folder, "conversions");

    public ConversionRecord(GameScan game, string outputDir, EngineInfo engine)
    {
        _game = game;
        Directory.CreateDirectory(Root);
        string name = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss} {Converter.SafeFileName(game.Title)}";
        Folder = Path.Combine(Root, name);
        Directory.CreateDirectory(Folder);
        Prune();
        _log = new StreamWriter(Path.Combine(Folder, LogName), false, new UTF8Encoding(false)) { AutoFlush = true };

        Line($"Game: {game.Title} {game.TitleId}");
        Line($"Source: {game.SourcePath}{(game.IsImage ? " (exFAT image)" : "")}, {Converter.FormatBytes(game.TotalBytes)}");
        Line($"Output: {outputDir}");
        Line($"Engine: {engine.Version} in {engine.Folder}");
        Line($"Executable: {game.ExecutableRel}, {game.ModuleCount} modules, unwrap: {game.NeedsUnwrap}");
        foreach (var dir in game.ExtraModuleDirs) Line($"Extra module folder: {dir}");
        foreach (var (file, why) in game.ModuleIssues) Line($"Unusable module: {file}: {why}");
        foreach (var w in game.Warnings) Line($"Warning: {w}");
    }

    private static void Prune()
    {
        try
        {
            foreach (var d in new DirectoryInfo(Root).GetDirectories().OrderByDescending(d => d.Name, StringComparer.Ordinal).Skip(AttemptsKept))
                d.Delete(recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Error("Removing old conversion records", e); }
    }

    public void Line(string text)
    {
        lock (_gate)
        {
            _log.WriteLine($"[{_clock.Elapsed.TotalSeconds,8:0.0}] {text}");
            _recent.Add(text);
            if (_recent.Count > 400) _recent.RemoveRange(0, 200);
        }
    }

    public void Stage(string stage)
    {
        lock (_gate)
        {
            if (stage == _stage) return;
            _stage = stage;
        }
        Line($"--- {stage}");
    }

    /// <summary>Writes the outcome. Returns findings for a failure, none for a success.</summary>
    public List<Finding> Finish(string outcome, string? failure)
    {
        var findings = new List<Finding>();
        List<string> tail;
        lock (_gate) tail = _recent.ToList();
        if (failure != null) findings.Add(ConversionDiagnosis.Classify(failure, tail, _stage));

        Line($"Finished: {outcome}, {_clock.Elapsed.TotalMinutes:0.0} minutes" + (failure != null ? $": {failure}" : ""));
        var s = new StringBuilder();
        s.AppendLine($"# {_game.Title} conversion").AppendLine();
        s.AppendLine($"**{outcome}** after {_clock.Elapsed.TotalMinutes:0.0} minutes, at stage: {_stage}.").AppendLine();
        if (failure != null) s.AppendLine("```").AppendLine(failure).AppendLine("```").AppendLine();
        foreach (var f in findings)
            s.AppendLine($"- **{f.Area}: {f.Title}.** {f.Evidence}" + (f.NextStep.Length > 0 ? $" *Next: {f.NextStep}*" : ""));
        if (findings.Count > 0) s.AppendLine();
        s.AppendLine("The full output is in `conversion.log` in this folder.");
        try
        {
            File.WriteAllText(Path.Combine(Folder, ReportName), s.ToString());
            File.WriteAllText(Path.Combine(Folder, "diagnosis.json"), JsonSerializer.Serialize(new { outcome, failure, stage = _stage, findings }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Error("Saving a conversion record", e); }
        AppLog.Info($"Conversion of {_game.Title}: {outcome}" + (failure != null ? $": {failure}" : ""));
        return findings;
    }

    public void Dispose()
    {
        lock (_gate) _log.Dispose();
    }
}

/// <summary>Turns a conversion failure into what it means and what to do about it.</summary>
public static class ConversionDiagnosis
{
    private sealed record Rule(Regex Pattern, string Area, string Title, string Evidence, string Next);

    private static Rule R(string pattern, string area, string title, string evidence, string next) =>
        new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), area, title, evidence, next);

    // The first match wins, so the most specific come first.
    private static readonly Rule[] Rules =
    {
        R(@"not enough disk space|no space left", "Disk", "The drive ran out of space",
            "The game, libraries and temporary files all have to fit on the save drive.", "Free some space or save to another drive."),
        R(@"\bsyscall\b|system call", "Game code", "The game code makes a direct system call AnyPS5 doesn't allow",
            "PS5 code normally goes through system libraries. A direct call can't be translated to Windows automatically.",
            "Note the file and address in the message. A newer engine may handle it, or a patch in the patches folder can replace it, as Tokyo Xtreme Racer's does."),
        R(@"patch|expected bytes|does not match the expected", "Patch", "A game patch doesn't match this game's code",
            "Patches target exact bytes, so they break when the game version differs.", "Update or remove this title's file in the patches folder."),
        R(@"decrypt|encrypted|not an? (valid )?elf|bad magic|invalid elf|expected .*elf|isn't a ps5 executable", "Game files", "The game code is encrypted or isn't a program",
            "AnyPS5 only works on decrypted game code.", "Use a decrypted dump. A .pkg file or an encrypted dump won't work."),
        R(@"unresolved|undefined symbol|missing (module|library|import|symbol|dependency)|no module provides|cannot find|not found", "Game code", "A library or symbol the game needs wasn't found",
            "The game depends on something that isn't in the game files or AnyPS5's libraries.", "Check that sce_module was included in the dump. If it was, the name in the message is what the engine lacks."),
        R(@"instruction|opcode|disassembl|decode|unsupported relocation|relocation", "Game code", "The converter met code it can't translate",
            "This points at a gap in the relinker, not at your files.", "Try a newer engine. If it still fails, the address in the message is what to report."),
        R(@"ascii|path .*(too long|invalid)|filename", "Paths", "A path the converter can't use",
            "The relinker needs short, plain ASCII paths.", "Save to a simple folder such as C:\\Games\\Name."),
        R(@"access is denied|unauthorized|permission", "Disk", "Windows refused access to a file",
            "Antivirus or a protected folder can do this.", "Save somewhere under your user folder, and check antivirus history."),
        R(@"being used by another process|sharing violation", "Disk", "Another program has a file open",
            "Often the game itself, still running, or antivirus scanning it.", "Close the game and try again."),
        R(@"disk image|exfat", "Game files", "The disk image couldn't be read",
            "The image may be incomplete or damaged.", "Check its size against the original, and make it again if it's short."),
        R(@"exit code -?\d{6,}|0xc0", "Engine", "The relinker itself crashed",
            "That's a bug in the converter, not a problem with your game files.", "Try a newer engine. The record folder holds the full output."),
    };

    public static Finding Classify(string failure, IReadOnlyList<string> recentLines, string stage)
    {
        foreach (var rule in Rules)
            if (rule.Pattern.IsMatch(failure))
                return new Finding(Severity.Critical, rule.Area, rule.Title, rule.Evidence, rule.Next);

        // The reason sometimes sits in the output and not in the final message.
        foreach (string line in recentLines.Reverse().Take(30))
            foreach (var rule in Rules)
                if (rule.Pattern.IsMatch(line))
                    return new Finding(Severity.Critical, rule.Area, rule.Title, $"{rule.Evidence} Output: {line}", rule.Next);

        return new Finding(Severity.Critical, "Unknown", "This failure isn't one the app recognises yet",
            $"It happened at stage: {stage}.", "The record folder holds the full output for working out the cause.");
    }
}
