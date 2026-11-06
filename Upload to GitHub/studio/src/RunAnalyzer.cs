using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnyPS5Studio;

public static class Severity
{
    public const string Critical = "critical";
    public const string Warning = "warning";
    public const string Info = "info";
    public const string Good = "good";
}

/// <summary>One conclusion: what was found, the numbers behind it, and what to try.</summary>
public sealed record Finding(string Severity, string Area, string Title, string Evidence, string NextStep = "");

public sealed record ShaderCost(string Program, long Ranges, double TotalMs, double MsPerFrame, double SharePct);
public sealed record GpuClassCost(string Name, double MsPerFrame, double MibPerFrame);
public sealed record ThreadCost(string Name, double AverageCpuPct, double PeakCpuPct);
public sealed record IntervalRow(double Start, double End, int Presents, double P50, double P99, double Max, double GpuMs, double WorkerPct);
public sealed record LogGroup(int Count, double FirstSeen, string Sample);

/// <summary>Everything worked out about one run. Saved as diagnosis.json.</summary>
public sealed class Diagnosis
{
    public string Folder { get; set; } = "";
    public string Title { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public DateTime Started { get; set; }
    public double Seconds { get; set; }
    public int? ExitCode { get; set; }
    public string Outcome { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string Stage { get; set; } = "";
    public bool Detailed { get; set; }
    public bool TestMode { get; set; }
    public string? SkippedShader { get; set; }
    public Dictionary<string, string> Switches { get; set; } = new();
    public string? Gpu { get; set; }
    public string? Resolution { get; set; }
    public double? Fps { get; set; }
    public double? FrameMs { get; set; }
    public double? FrameP99Ms { get; set; }
    public double? WorstFrameMs { get; set; }
    public double? GpuBusyMs { get; set; }
    public double? GpuIdleMs { get; set; }
    public double? WorkerBusyPct { get; set; }
    public double? GpuLoadPct { get; set; }
    public double? PeakVramMb { get; set; }
    public double? VramTotalMb { get; set; }
    public double? PeakRamMb { get; set; }
    public List<Finding> Findings { get; set; } = new();
    public List<ShaderCost> Shaders { get; set; } = new();
    public List<GpuClassCost> GpuClasses { get; set; } = new();
    public Dictionary<string, double> Phases { get; set; } = new();
    public List<ThreadCost> Threads { get; set; } = new();
    public List<IntervalRow> Timeline { get; set; } = new();
    public List<LogGroup> LogProblems { get; set; } = new();
    public List<LogGroup> MissingFiles { get; set; } = new();
    public string CrashExcerpt { get; set; } = "";
    public string Comparison { get; set; } = "";

    public string Label
    {
        get
        {
            string fps = Fps is double f ? $"  ·  {f:0.0} FPS" : "";
            return $"{Started:MMM d, HH:mm}  ·  {Outcome}{fps}{(TestMode ? "  ·  test mode" : "")}";
        }
    }
}

/// <summary>Turns the files of a recorded run into findings.</summary>
public static class RunAnalyzer
{
    public const string DiagnosisJson = "diagnosis.json";
    public const string DiagnosisReport = "diagnosis.md";
    public const string ResolutionSwitch = "APS5_OUTPUT_RESOLUTION";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static readonly Regex GpuHeader = new(@"\[gputime\] ([\d.]+) ms of GPU time in \d+ batches over \d+ s .*?; (\d+) presents;", RegexOptions.Compiled);
    private static readonly Regex GpuProgram = new(@"(0x[0-9a-f]+) x(\d+) ([\d.]+)ms", RegexOptions.Compiled);
    private static readonly Regex GpuClass = new(@"([a-z][a-z0-9-]*) x\d+ ([\d.]+)ms ([\d.]+)MiB \(per present", RegexOptions.Compiled);
    private static readonly Regex PresentLine = new(@"GPU busy ([\d.]+) ms, idle gaps ([\d.]+) ms", RegexOptions.Compiled);
    private static readonly Regex TextureBudget = new(@"sampled texture cache budget (\d+) MiB \(was (\d+) MiB\)", RegexOptions.Compiled);
    private static readonly Regex DiskCache = new(@"\[shader-disk-cache\] \(\d+ s\): \d+ hits, (\d+) misses", RegexOptions.Compiled);
    private static readonly Regex OpenFailed = new(@"open failed path=(\S+)", RegexOptions.Compiled);
    private static readonly Regex Fatal = new(@"FATAL: (?:unhandled exception (0x[0-9a-fA-F]+) on thread \d+ '([^']*)')?", RegexOptions.Compiled);
    private static readonly Regex ProblemWords = new(@"FATAL|\berror\b|\bfailed\b|not implemented|unimplemented|assert|exception|could not|refused|unsupported|terminate", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Numbers = new(@"0x[0-9a-fA-F]+|\d+(\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex Stamp = new(@"^\[(\d+\.\d+)\] ", RegexOptions.Compiled);

    public static Diagnosis AnalyzeAndSave(string folder)
    {
        var d = Analyze(folder);
        CompareWithPrevious(d, folder);
        File.WriteAllText(Path.Combine(folder, DiagnosisJson), JsonSerializer.Serialize(d, Options));
        File.WriteAllText(Path.Combine(folder, DiagnosisReport), RenderReport(d));
        return d;
    }

    public static Diagnosis? Load(string folder)
    {
        try
        {
            string path = Path.Combine(folder, DiagnosisJson);
            return File.Exists(path) ? JsonSerializer.Deserialize<Diagnosis>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>Every analyzed run of a game, newest first.</summary>
    public static List<Diagnosis> LoadRuns(string gameFolder)
    {
        string runs = RunSession.RunsFolder(gameFolder);
        if (!Directory.Exists(runs)) return new();
        return Directory.GetDirectories(runs).OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
            .Select(Load).OfType<Diagnosis>().ToList();
    }

    public static Diagnosis Analyze(string folder)
    {
        var facts = ReadFacts(folder);
        var d = new Diagnosis
        {
            Folder = folder, Title = facts.Title, TitleId = facts.TitleId, EngineVersion = facts.EngineVersion, Started = facts.Started,
            Seconds = (facts.Ended - facts.Started).TotalSeconds, ExitCode = facts.ExitCode, Detailed = facts.Detailed, Switches = facts.Switches,
            TestMode = facts.TestMode, SkippedShader = facts.SkipEvents.FirstOrDefault()?.Program,
        };
        var report = ReadReport(folder);
        var intervals = ReadIntervals(folder);
        var log = ReadLog(folder);
        var machine = MachineStats.Read(Path.Combine(folder, RunSession.MachineName));
        d.Threads = ReadThreads(folder);
        d.Stage = report?["run"]?["stage"]?.GetValue<string>() ?? (intervals.Count > 0 ? "presents" : "unknown");
        d.Gpu = report?["system"]?["gpu"]?["name"]?.GetValue<string>();
        d.GpuLoadPct = machine.AverageGpuPct;
        d.PeakVramMb = machine.PeakVramMb;
        d.VramTotalMb = machine.VramTotalMb;
        d.PeakRamMb = machine.PeakPrivateMb;
        d.LogProblems = log.Problems;
        d.MissingFiles = log.MissingFiles;
        d.CrashExcerpt = log.CrashExcerpt;

        d.Outcome = DescribeOutcome(facts, report, log);
        AddEndFindings(d, facts, report, log);
        AddFrameFindings(d, intervals, log, machine, report, facts);
        AddMachineFindings(d, machine, log);
        d.Findings = d.Findings.OrderBy(f => Array.IndexOf(new[] { Severity.Critical, Severity.Warning, Severity.Info, Severity.Good }, f.Severity)).ToList();
        d.Verdict = d.Findings.FirstOrDefault()?.Title ?? "Nothing was recorded for this run.";
        return d;
    }

    private static RunFacts ReadFacts(string folder)
    {
        string path = Path.Combine(folder, RunSession.FactsName);
        return (File.Exists(path) ? JsonSerializer.Deserialize<RunFacts>(File.ReadAllText(path)) : null) ?? new RunFacts();
    }

    /// <summary>A report marked recovered describes an earlier run that was killed, not this one.</summary>
    private static JsonObject? ReadReport(string folder)
    {
        string path = Path.Combine(folder, RunSession.ReportName);
        if (!File.Exists(path)) return null;
        try
        {
            var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return o?["run"]?["recovered"]?.GetValue<bool>() == true ? null : o;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    public sealed record Interval(double Start, double End, int Presents, double P50, double P99, double Max, double GpuMs, double WorkerPct,
                                  string Resolution, Dictionary<string, double> Phases);

    public static List<Interval> ReadIntervals(string folder)
    {
        var list = new List<Interval>();
        string path = Path.Combine(folder, RunSession.SummaryName);
        if (!File.Exists(path)) return list;
        foreach (string line in ReadShared(path))
        {
            try
            {
                if (JsonNode.Parse(line) is not JsonObject o || o["interval_start_s"] == null || o["frame_ms"] is not JsonObject frame) continue;
                var phases = (o["top_phases_ms_per_frame"] as JsonObject)?.ToDictionary(p => p.Key, p => p.Value!.GetValue<double>()) ?? new();
                double worker = (o["worker_busy_pct"] as JsonObject)?.Select(w => w.Value!.GetValue<double>()).DefaultIfEmpty(0).Max() ?? 0;
                list.Add(new Interval(o["interval_start_s"]!.GetValue<double>(), o["interval_end_s"]!.GetValue<double>(), o["presents"]!.GetValue<int>(),
                    frame["p50"]!.GetValue<double>(), frame["p99"]!.GetValue<double>(), frame["max"]!.GetValue<double>(),
                    o["gpu_busy_ms_per_present"]?.GetValue<double>() ?? 0, worker, o["resolution"]?.GetValue<string>() ?? "", phases));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { /* a line cut off by a crash */ }
        }
        return list;
    }

    private static IEnumerable<string> ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s, Encoding.UTF8);
        while (r.ReadLine() is { } line) yield return line;
    }

    private static List<ThreadCost> ReadThreads(string folder)
    {
        string path = Path.Combine(folder, RunSession.ThreadsName);
        if (!File.Exists(path)) return new();
        var rows = new List<(string Name, double Pct)>();
        foreach (string line in ReadShared(path).Skip(1))
        {
            var cells = line.Split(',');
            if (cells.Length == 3 && double.TryParse(cells[2], NumberStyles.Float, Inv, out double pct)) rows.Add((cells[1], pct));
        }
        int samples = Math.Max(1, ReadShared(path).Skip(1).Select(l => l.Split(',')[0]).Distinct().Count());
        return rows.GroupBy(r => r.Name).Select(g => new ThreadCost(g.Key, g.Sum(r => r.Pct) / samples, g.Max(r => r.Pct)))
            .OrderByDescending(t => t.AverageCpuPct).Take(10).ToList();
    }

    public sealed class LogFacts
    {
        public double GpuTimeTotal;
        public long Presents;
        public readonly Dictionary<string, (long Ranges, double Ms)> Programs = new();
        public readonly Dictionary<string, (double Ms, double Mib)> Classes = new();
        public readonly List<double> GpuBusy = new(), GpuIdle = new();
        public int TextureBudgetCuts;
        public double LastTextureBudget;
        public long ShaderMisses;
        public readonly List<(double Time, long Misses)> ShaderMissTimes = new();
        public int UnpreparedShaders;
        public string? FatalLine;
        public string? ExceptionCode;
        public string? ExceptionThread;
        public string CrashExcerpt = "";
        public string? LastLine;
        public List<LogGroup> Problems = new();
        public List<LogGroup> MissingFiles = new();
    }

    public static LogFacts ReadLog(string folder)
    {
        var f = new LogFacts();
        string path = Path.Combine(folder, RunSession.ConsoleName);
        if (!File.Exists(path)) return f;

        var problems = new Dictionary<string, (int Count, double First, string Sample)>();
        var missing = new Dictionary<string, (int Count, double First)>();
        var excerpt = new StringBuilder();
        int excerptLines = 0;

        foreach (string raw in ReadShared(path))
        {
            var stamp = Stamp.Match(raw);
            double time = stamp.Success ? double.Parse(stamp.Groups[1].Value, Inv) : 0;
            string line = stamp.Success ? raw[stamp.Length..] : raw;
            if (line.Trim().Length > 0) f.LastLine = line;

            if (f.FatalLine != null && excerptLines < 60) { excerpt.AppendLine(Clip(line, 220)); excerptLines++; }

            var gpu = GpuHeader.Match(line);
            if (gpu.Success)
            {
                f.GpuTimeTotal += double.Parse(gpu.Groups[1].Value, Inv);
                f.Presents += long.Parse(gpu.Groups[2].Value, Inv);
                ReadGpuBreakdown(line, f);
                continue;
            }
            var present = PresentLine.Match(line);
            if (present.Success)
            {
                f.GpuBusy.Add(double.Parse(present.Groups[1].Value, Inv));
                f.GpuIdle.Add(double.Parse(present.Groups[2].Value, Inv));
            }
            var budget = TextureBudget.Match(line);
            if (budget.Success)
            {
                double now = double.Parse(budget.Groups[1].Value, Inv);
                if (now < double.Parse(budget.Groups[2].Value, Inv)) f.TextureBudgetCuts++;
                f.LastTextureBudget = now;
            }
            var cache = DiskCache.Match(line);
            if (cache.Success)
            {
                long misses = long.Parse(cache.Groups[1].Value, Inv);
                f.ShaderMisses += misses;
                if (misses > 0) f.ShaderMissTimes.Add((time, misses));
                continue;
            }
            if (line.Contains("could not be prepared", StringComparison.Ordinal)) f.UnpreparedShaders++;

            var open = OpenFailed.Match(line);
            if (open.Success)
            {
                string file = open.Groups[1].Value;
                missing[file] = missing.TryGetValue(file, out var m) ? (m.Count + 1, m.First) : (1, time);
                continue;
            }
            var fatal = Fatal.Match(line);
            if (fatal.Success && f.FatalLine == null)
            {
                f.FatalLine = Clip(line, 300);
                if (fatal.Groups[1].Success) { f.ExceptionCode = fatal.Groups[1].Value.ToLowerInvariant(); f.ExceptionThread = fatal.Groups[2].Value; }
                excerpt.AppendLine(Clip(line, 220));
            }
            if (ProblemWords.IsMatch(line))
            {
                string key = Numbers.Replace(Clip(line, 160), "#");
                problems[key] = problems.TryGetValue(key, out var p) ? (p.Count + 1, p.First, p.Sample) : (1, time, Clip(line, 240));
            }
        }
        f.CrashExcerpt = excerpt.ToString();
        f.Problems = problems.Values.OrderByDescending(p => p.Sample.Contains("FATAL")).ThenByDescending(p => p.Count).Take(25)
            .Select(p => new LogGroup(p.Count, p.First, p.Sample)).ToList();
        f.MissingFiles = missing.OrderByDescending(m => m.Value.Count).Take(20).Select(m => new LogGroup(m.Value.Count, m.Value.First, m.Key)).ToList();
        return f;
    }

    /// <summary>The per-shader GPU times in one [gputime] line.</summary>
    public static IEnumerable<(string Program, long Runs, double Ms)> ProgramCosts(string line)
    {
        int programs = line.IndexOf("by program:", StringComparison.Ordinal);
        if (programs < 0 || !line.Contains("[gputime]", StringComparison.Ordinal)) yield break;
        int classes = line.IndexOf("by class:", StringComparison.Ordinal);
        foreach (Match m in GpuProgram.Matches(classes > programs ? line[programs..classes] : line[programs..]))
            yield return (m.Groups[1].Value, long.Parse(m.Groups[2].Value, Inv), double.Parse(m.Groups[3].Value, Inv));
    }

    private static void ReadGpuBreakdown(string line, LogFacts f)
    {
        foreach (var (program, runs, ms) in ProgramCosts(line))
        {
            f.Programs.TryGetValue(program, out var p);
            f.Programs[program] = (p.Ranges + runs, p.Ms + ms);
        }
        int classes = line.IndexOf("by class:", StringComparison.Ordinal);
        if (classes < 0) return;
        foreach (Match m in GpuClass.Matches(line[classes..]))
        {
            f.Classes.TryGetValue(m.Groups[1].Value, out var c);
            f.Classes[m.Groups[1].Value] = (c.Ms + double.Parse(m.Groups[2].Value, Inv), c.Mib + double.Parse(m.Groups[3].Value, Inv));
        }
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    public static string ExitCodeMeaning(int code) => unchecked((uint)code) switch
    {
        0xC0000005 => "access violation: code touched memory it shouldn't",
        0xC000001D => "illegal instruction: the CPU can't run an instruction in the game",
        0xC00000FD => "stack overflow",
        0xC0000409 => "fast fail: the program stopped itself after detecting damage",
        0xC0000374 => "heap corruption",
        0xC0000135 => "a required DLL is missing",
        0xC0000142 => "a DLL failed to start",
        0xE06D7363 => "an uncaught C++ exception",
        0x80000003 => "a debug breakpoint",
        _ => "",
    };

    private static string DescribeOutcome(RunFacts facts, JsonObject? report, LogFacts log)
    {
        if (log.FatalLine != null) return "Crashed";
        string? exit = report?["run"]?["exit"]?.GetValue<string>();
        if (exit is "crash" or "abort") return "Crashed";
        if (facts.StoppedByUser) return "Stopped by you";
        if (facts.ExitCode == 0) return "Closed";
        return facts.ExitCode == null ? "Unknown" : "Stopped unexpectedly";
    }

    private static void AddEndFindings(Diagnosis d, RunFacts facts, JsonObject? report, LogFacts log)
    {
        string stopKind = report?["stop"]?["kind"]?.GetValue<string>() ?? "none";
        string stopMessage = report?["stop"]?["message"]?.GetValue<string>() ?? "";
        string? source = report?["stop"]?["source"]?.GetValue<string>();
        string where = source != null ? $" ({source})" : "";

        if (report?["missing"]?["functions"] is JsonArray { Count: > 0 } missing)
        {
            var names = missing.OfType<JsonObject>().Take(12).Select(m => $"{m["library"]?.GetValue<string>() ?? "?"}: {m["name"]!.GetValue<string>()} ({m["calls"]!.GetValue<long>()})");
            d.Findings.Add(new Finding(stopKind == "missing-function" ? Severity.Critical : Severity.Warning, "Game support",
                stopKind == "missing-function" ? "The game stopped on a function AnyPS5 doesn't have" : $"The game called {missing.Count} functions AnyPS5 doesn't have",
                string.Join("; ", names), "These are what the engine has to implement for this game. Check AnyPS5's issues for each name, then try a newer engine."));
        }
        if (stopKind == "unsupported-shader" || report?["shaders"]?["unsupported"] is JsonArray { Count: > 0 })
        {
            var list = (report?["shaders"]?["unsupported"] as JsonArray)?.OfType<JsonObject>().Take(10)
                .Select(u => $"{u["encoding"]?.GetValue<string>() ?? "?"} {u["opcode"]?.GetValue<string>() ?? u["opcodeValue"]?.ToString() ?? "?"} x{u["count"]}") ?? Enumerable.Empty<string>();
            d.Findings.Add(new Finding(stopKind == "unsupported-shader" ? Severity.Critical : Severity.Warning, "Graphics",
                stopKind == "unsupported-shader" ? "The game stopped on a shader AnyPS5 can't translate" : "Some shaders use instructions AnyPS5 can't translate",
                string.Join("; ", list) + (stopMessage.Length > 0 ? $". {stopMessage}" : ""), "The shader translator needs these instructions added. Listed shaders may not all be used."));
        }
        if (stopKind == "unsupported-format")
            d.Findings.Add(new Finding(Severity.Critical, "Graphics", "The game stopped on an image format AnyPS5 can't use", stopMessage + where,
                "The GPU layer needs this format added. Search AnyPS5's issues for its name."));
        if (stopKind is "assert" or "other" && report?["stop"]?["reason"]?.GetValue<string>() != "unclean-unknown")
            d.Findings.Add(new Finding(Severity.Critical, "Engine", "AnyPS5 stopped the game", stopMessage + where, "The source line says which part of the engine gave up."));

        if (log.FatalLine != null || stopKind == "crash" || report?["run"]?["exit"]?.GetValue<string>() == "crash")
        {
            string meaning = log.ExceptionCode != null ? ExitCodeMeaning(unchecked((int)Convert.ToUInt32(log.ExceptionCode, 16))) : ExitCodeMeaning(facts.ExitCode ?? 0);
            string thread = !string.IsNullOrEmpty(log.ExceptionThread) ? $" on thread '{log.ExceptionThread}'" : "";
            string next = log.CrashExcerpt.Contains("nvgpucomp", StringComparison.OrdinalIgnoreCase)
                ? "The crash is inside NVIDIA's shader compiler. AnyPS5's guide names driver 610.47 for this, so update the driver."
                : "The crash details are in the report, with registers and the stack if the engine printed them.";
            d.Findings.Add(new Finding(Severity.Critical, "Crash", meaning.Length > 0 ? $"The game crashed: {meaning}" : "The game crashed",
                (log.FatalLine ?? stopMessage) + thread, next));
        }
        else if (!facts.StoppedByUser && facts.ExitCode is int code && code != 0 && d.Findings.All(f => f.Severity != Severity.Critical))
        {
            string meaning = ExitCodeMeaning(code);
            d.Findings.Add(new Finding(Severity.Critical, "Crash", $"The game stopped with exit code 0x{code:x8}" + (meaning.Length > 0 ? $": {meaning}" : ""),
                $"Last line: {log.LastLine ?? "none"}", report == null ? "The engine wrote no report, so it didn't see the end coming." : ""));
        }

        if (d.Stage is "unknown" or "loads" or "starts" or "first-draw" && d.Seconds > 5)
            d.Findings.Add(new Finding(Severity.Warning, "Startup", d.Stage == "first-draw" ? "The game drew but never showed a frame" : "The game never reached its first frame",
                $"Furthest point reached: {d.Stage}, after {d.Seconds:0} s. Last line: {Clip(log.LastLine ?? "none", 200)}",
                "The last lines of the log show what it was doing when it got stuck."));

        if (log.UnpreparedShaders > 0)
            d.Findings.Add(new Finding(Severity.Info, "Graphics", $"{log.UnpreparedShaders} shaders couldn't be prepared ahead of time",
                "These are prepared later or when first used, which can cause a short pause."));
        int absent = log.MissingFiles.Sum(m => m.Count);
        if (absent > 0)
            d.Findings.Add(new Finding(Severity.Info, "Files", $"{log.MissingFiles.Count} paths the game asked for weren't there",
                "Games often test for optional files, so this is only a problem if the game stops soon after one. The report lists them."));
    }

    private static double WeightedMedian(IEnumerable<(double Value, int Weight)> items)
    {
        var sorted = items.OrderBy(i => i.Value).ToList();
        long half = sorted.Sum(i => (long)i.Weight) / 2, seen = 0;
        foreach (var (value, weight) in sorted)
        {
            seen += weight;
            if (seen >= half) return value;
        }
        return sorted.Count > 0 ? sorted[^1].Value : 0;
    }

    private static double Median(IEnumerable<double> items) => WeightedMedian(items.Select(v => (v, 1)));

    /// <summary>Whether a shader was off for the whole of a period, in seconds since the run began.</summary>
    private static bool? SkipStateDuring(RunFacts facts, double from, double to)
    {
        bool at(double t) => facts.SkipEvents.LastOrDefault(e => e.Seconds <= t)?.Off ?? false;
        if (facts.SkipEvents.Any(e => e.Seconds > from && e.Seconds < to)) return null;
        return at(from);
    }

    /// <summary>
    /// A test mode run mixes frames with and without the shader, so it reports
    /// them apart and goes on to judge the frames with the shader off.
    /// </summary>
    private static List<Interval> SplitTestMode(Diagnosis d, List<Interval> steady, RunFacts facts, JsonObject? report)
    {
        if (!facts.TestMode) return steady;
        const string next = "This is a test, not a fix. Compare the picture with the shader on and off, because anything missing is what the shader draws. Turn test mode off for normal play.";
        if (d.SkippedShader == null)
        {
            d.Findings.Add(new Finding(Severity.Info, "Test mode", "Test mode found no shader heavy enough to skip",
                $"A shader has to run at least {ShaderSkipper.MinRuns} times at {ShaderSkipper.MinMsPerRun:0} ms or more each.", "Stay on a busy screen for longer."));
            return steady;
        }
        double firstPresent = report?["run"]?["milestones"]?["firstPresent"]?.GetValue<double>() ?? -1;
        if (firstPresent < 0)
        {
            d.Findings.Add(new Finding(Severity.Warning, "Test mode", $"Test mode skipped shader {d.SkippedShader}",
                "The game didn't close normally, so frames with and without the shader can't be told apart and the numbers here mix both.", next));
            return steady;
        }
        var off = steady.Where(i => SkipStateDuring(facts, i.Start + firstPresent, i.End + firstPresent) == true).ToList();
        var on = steady.Where(i => SkipStateDuring(facts, i.Start + firstPresent, i.End + firstPresent) == false).ToList();
        string withIt = on.Count > 0 ? $"{WeightedMedian(on.Select(i => (i.P50, i.Presents))):0} ms" : "not measured";
        string without = off.Count > 0 ? $"{WeightedMedian(off.Select(i => (i.P50, i.Presents))):0} ms" : "not measured";
        d.Findings.Add(new Finding(Severity.Warning, "Test mode", $"Test mode skipped shader {d.SkippedShader}",
            $"Typical frame with it on: {withIt}. With it off: {without}." + (off.Count > 0 ? " The numbers in this report cover the time it was off." : ""), next));
        return off.Count > 0 ? off : steady;
    }

    private static void AddFrameFindings(Diagnosis d, List<Interval> all, LogFacts log, MachineStats machine, JsonObject? report, RunFacts facts)
    {
        d.Timeline = all.Select(i => new IntervalRow(i.Start, i.End, i.Presents, i.P50, i.P99, i.Max, i.GpuMs, i.WorkerPct)).ToList();
        if (log.GpuTimeTotal > 0)
        {
            long frames = Math.Max(1, log.Presents);
            d.Shaders = log.Programs.OrderByDescending(p => p.Value.Ms).Take(12)
                .Select(p => new ShaderCost(p.Key, p.Value.Ranges, p.Value.Ms, p.Value.Ms / frames, 100 * p.Value.Ms / log.GpuTimeTotal)).ToList();
            d.GpuClasses = log.Classes.OrderByDescending(c => c.Value.Ms).Take(10)
                .Select(c => new GpuClassCost(c.Key, c.Value.Ms / frames, c.Value.Mib / frames)).ToList();
        }
        // An interval with a handful of frames is a loading screen.
        var steady = all.Where(i => i.Presents >= 5).ToList();
        if (steady.Count == 0) steady = all;
        if (steady.Count == 0) return;
        steady = SplitTestMode(d, steady, facts, report);

        double frame = WeightedMedian(steady.Select(i => (i.P50, i.Presents)));
        double gpu = WeightedMedian(steady.Select(i => (i.GpuMs, i.Presents)));
        double worker = Median(steady.Select(i => i.WorkerPct));
        double seconds = steady.Sum(i => i.End - i.Start);
        d.Resolution = steady[^1].Resolution;
        d.Fps = seconds > 0 ? steady.Sum(i => i.Presents) / seconds : null;
        d.FrameMs = frame;
        d.FrameP99Ms = Median(steady.Select(i => i.P99));
        d.WorstFrameMs = all.Max(i => i.Max);
        d.GpuBusyMs = gpu;
        d.WorkerBusyPct = worker;
        d.GpuIdleMs = log.GpuIdle.Count > 0 ? Median(log.GpuIdle) : null;
        var phases = new Dictionary<string, List<double>>();
        foreach (var i in steady)
            foreach (var (k, v) in i.Phases)
            {
                if (!phases.TryGetValue(k, out var l)) phases[k] = l = new();
                l.Add(v);
            }
        d.Phases = phases.OrderByDescending(p => Median(p.Value)).Take(8).ToDictionary(p => p.Key, p => Math.Round(Median(p.Value), 1));

        AddBottleneck(d, frame, gpu, worker, machine);
        if (frame > 18) AddShaderFindings(d, log, frame); // a game at full speed needs no advice
        AddStutterFindings(d, all, steady, frame, log, report);

        if (frame > 18 && d.Resolution is { } res && res.EndsWith("x2160", StringComparison.Ordinal))
            d.Findings.Add(d.Switches.ContainsKey(ResolutionSwitch)
                ? new Finding(Severity.Info, "Performance", $"The game kept its {res} output despite {ResolutionSwitch}",
                    "The switch only changes what the game is told about the display. This game still chose 4K, so the switch didn't lower the output.")
                : new Finding(Severity.Info, "Performance", $"The game is using a {res} output",
                    "Some games lower their work when told the display is smaller, but not all of them do.",
                    $"Add {ResolutionSwitch}=1920x1080 to Engine switches, run again, and read the comparison."));
        if (frame > 18 && !d.Detailed)
            d.Findings.Add(new Finding(Severity.Info, "Performance", "Detailed recording was off",
                "This run shows that frames are slow, but not which shaders cost the most.", "Turn on Detailed recording for one run. It lowers FPS a little while it's on."));
    }

    private static void AddBottleneck(Diagnosis d, double frame, double gpu, double worker, MachineStats machine)
    {
        double fps = 1000 / frame;
        if (frame <= 18)
        {
            d.Findings.Add(new Finding(Severity.Good, "Performance", $"Running smoothly at about {fps:0} FPS", $"Typical frame {frame:0.0} ms."));
            return;
        }
        string severity = frame > 34 ? Severity.Critical : Severity.Warning;
        double share = gpu / frame * 100;
        string load = machine.AverageGpuPct is double g ? $" nvidia-smi saw the GPU at {g:0}% on average." : "";
        var hottest = d.Threads.FirstOrDefault();
        if (share >= 60)
        {
            string doubt = machine.AverageGpuPct < 70 ? " That doesn't match the low GPU load, so overlapping queues may be inflating the GPU figure." : "";
            d.Findings.Add(new Finding(severity, "Performance", $"The GPU is limiting this game to about {fps:0} FPS",
                $"The GPU is busy for {gpu:0} ms of each {frame:0} ms frame ({share:0}%).{load}{doubt}",
                d.Detailed ? "The heaviest shaders below are where this time goes. A faster CPU would help little." : "Detailed recording shows which shaders take this time. A faster CPU would help little."));
        }
        else if (hottest is { PeakCpuPct: >= 90 } && hottest.AverageCpuPct >= 85)
        {
            d.Findings.Add(new Finding(severity, "Performance", $"One CPU thread is limiting this game to about {fps:0} FPS",
                $"Thread '{hottest.Name}' averages {hottest.AverageCpuPct:0}% of a CPU core. The GPU is busy for only {gpu:0} ms of each {frame:0} ms frame.{load}",
                "Find out what that thread is doing with APS5_SAMPLE_THREADS=<file> in Engine switches."));
        }
        else if (worker >= 90)
        {
            d.Findings.Add(new Finding(severity, "Performance", $"AnyPS5's GPU command threads are limiting this game to about {fps:0} FPS",
                $"The busiest queue worker is occupied {worker:0}% of the time, but the GPU is busy for only {gpu:0} ms of each {frame:0} ms frame. This counts waits as well as work.{load}",
                "The largest phases in the report show where those threads spend their time."));
        }
        else
        {
            d.Findings.Add(new Finding(severity, "Performance", $"Running at about {fps:0} FPS with no single clear limit",
                $"The GPU is busy for {gpu:0} ms of each {frame:0} ms frame and the busiest queue worker for {worker:0}% of the time.{load}",
                "Turn on Detailed recording, then compare the largest phases in the report."));
        }
    }

    private static void AddShaderFindings(Diagnosis d, LogFacts log, double frame)
    {
        if (d.Shaders.FirstOrDefault() is { SharePct: >= 40 } top && top.Program != d.SkippedShader)
            d.Findings.Add(new Finding(Severity.Critical, "Performance", $"One shader takes {top.MsPerFrame:0} ms of each frame",
                $"Program {top.Program} used {top.TotalMs:0} ms of {log.GpuTimeTotal:0} ms of measured GPU time ({top.SharePct:0}%), and ran {top.Ranges} times over {log.Presents} frames.",
                "The address changes every run, so it can't be named in advance. To inspect it, add APS5_DUMP_SHADERS=1 to Engine switches, which saves every shader as shader_<address>.req, then find this run's address among them."));

        double upload = d.GpuClasses.Where(c => c.Name.StartsWith("storage-", StringComparison.Ordinal) || c.Name.StartsWith("staging-", StringComparison.Ordinal) || c.Name == "shadow-publish").Sum(c => c.MsPerFrame);
        if (upload >= frame * 0.1)
            d.Findings.Add(new Finding(Severity.Warning, "Performance", $"Copying data around costs {upload:0} ms of GPU time per frame",
                "The GPU is moving storage and shadow data between game memory and its own memory.", "The GPU work by type table in the report shows which kind."));

        double waits = d.Phases.Where(p => p.Key.StartsWith("GpuMutex.wait", StringComparison.Ordinal) || p.Key.EndsWith("memory_wait", StringComparison.Ordinal)).Sum(p => p.Value);
        if (waits >= frame * 0.15)
            d.Findings.Add(new Finding(Severity.Info, "Performance", $"Engine threads wait about {waits:0} ms per frame on locks and memory",
                "Summed across threads, so it can overlap with GPU time. Reducing it needs engine changes, not settings."));

        if (d.GpuIdleMs is double idle && idle >= frame * 0.2)
            d.Findings.Add(new Finding(Severity.Info, "Performance", $"The GPU sits idle for {idle:0} ms of each frame",
                "It's waiting for the CPU side or for a synchronisation, so a faster GPU alone wouldn't remove this time."));
    }

    private static void AddStutterFindings(Diagnosis d, List<Interval> all, List<Interval> steady, double frame, LogFacts log, JsonObject? report)
    {
        var spiky = steady.Where(i => i.Max > Math.Max(frame * 2.5, frame + 50)).ToList();
        if (spiky.Count > 0 && spiky.Count >= steady.Count / 4)
        {
            double first = report?["run"]?["milestones"]?["firstPresent"]?.GetValue<double>() ?? -1;
            int compiling = first < 0 ? 0 : spiky.Count(i => log.ShaderMissTimes.Any(m => m.Time >= first + i.Start - 10 && m.Time <= first + i.End));
            string cause = compiling > 0 ? $" {compiling} of them overlap shader compilation, which usually fades as the shader cache fills." : "";
            d.Findings.Add(new Finding(Severity.Warning, "Stutter", $"Frames spiked in {spiky.Count} of {steady.Count} recorded periods",
                $"Typical frame {frame:0} ms, spikes up to {spiky.Max(i => i.Max):0} ms.{cause}", "Run the same section again. If the spikes go, they were shader compilation."));
        }
        var pauses = all.Where(i => i.Max >= 1000).ToList();
        if (pauses.Count > 0)
            d.Findings.Add(new Finding(Severity.Info, "Stutter", pauses.Count == 1 ? "1 long pause of a second or more" : $"{pauses.Count} long pauses of a second or more",
                $"The longest was {pauses.Max(i => i.Max) / 1000:0.0} s. Loading screens are the usual cause."));
        if (log.ShaderMisses > 0)
            d.Findings.Add(new Finding(Severity.Info, "Stutter", $"{log.ShaderMisses} shaders had to be compiled in this run",
                "A later run reuses them from the shader cache."));
    }

    private static void AddMachineFindings(Diagnosis d, MachineStats m, LogFacts log)
    {
        if (m.PeakVramMb is double vram && m.VramTotalMb is double total && total > 0 && vram >= total * 0.9)
            d.Findings.Add(new Finding(Severity.Warning, "Memory", $"Video memory was nearly full: {vram / 1024:0.0} of {total / 1024:0.0} GB",
                "Near the limit, the driver moves data out of video memory, which slows frames sharply."));
        if (log.TextureBudgetCuts > 2)
            d.Findings.Add(new Finding(Severity.Info, "Memory", $"AnyPS5 cut its texture cache {log.TextureBudgetCuts} times",
                $"It ended at {log.LastTextureBudget:0} MiB. It shrinks this as video memory fills, so textures may be reloaded more often."));
        if (m.MinAvailableRamMb is double ram && ram < 1500)
            d.Findings.Add(new Finding(Severity.Warning, "Memory", $"The PC ran low on memory: {ram / 1024:0.0} GB free at the lowest point",
                "Close other programs. Windows starts paging to disk at this level."));
        if (m.PeakTemp is double temp && temp >= 85)
            d.Findings.Add(new Finding(Severity.Warning, "Hardware", $"The GPU reached {temp:0} °C", "It may lower its clock speed to cool down."));
        if (m.AverageSystemCpuPct is double cpu && m.AverageCoresUsed is double cores && cpu >= 90)
            d.Findings.Add(new Finding(Severity.Warning, "Hardware", $"The whole CPU was {cpu:0}% busy", $"The game used about {cores:0.0} cores, so check for other programs if that's far short of all of them."));
        if (m.LongestStallSeconds >= 30)
            d.Findings.Add(new Finding(Severity.Warning, "Freeze", $"No frames for {m.LongestStallSeconds:0} s, from {m.LongestStallStart:0} s into the run",
                m.UnresponsiveSeconds > 0 ? $"Windows also saw the window stop responding for {m.UnresponsiveSeconds} s." : "The window kept responding, so this may be a long load rather than a hang.",
                "The log around that time shows what the game was doing."));
    }

    /// <summary>What changed since the last comparable run of this game.</summary>
    private static void CompareWithPrevious(Diagnosis d, string folder)
    {
        string? parent = Path.GetDirectoryName(folder);
        if (parent == null || d.Fps == null) return;
        var previous = Directory.GetDirectories(parent).Where(p => string.CompareOrdinal(Path.GetFileName(p), Path.GetFileName(folder)) < 0)
            .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).Select(Load).OfType<Diagnosis>().FirstOrDefault(r => r.Fps != null);
        if (previous == null) return;

        var parts = new List<string>
        {
            $"FPS {previous.Fps:0.0} -> {d.Fps:0.0} ({Percent(previous.Fps!.Value, d.Fps.Value)})",
            $"frame {previous.FrameMs:0} -> {d.FrameMs:0} ms",
            $"slowest 1% {previous.FrameP99Ms:0} -> {d.FrameP99Ms:0} ms",
            $"GPU {previous.GpuBusyMs:0} -> {d.GpuBusyMs:0} ms",
        };
        if (previous.Resolution != d.Resolution) parts.Add($"output {previous.Resolution} -> {d.Resolution}");

        var changes = new List<string>();
        if (previous.Detailed != d.Detailed) changes.Add("detailed recording changed, which can affect FPS by itself");
        if (previous.TestMode != d.TestMode) changes.Add($"test mode {(d.TestMode ? "on" : "off")}");
        if (previous.EngineVersion != d.EngineVersion) changes.Add($"engine {previous.EngineVersion} -> {d.EngineVersion}");
        var keys = previous.Switches.Keys.Union(d.Switches.Keys).OrderBy(k => k, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            previous.Switches.TryGetValue(key, out var a);
            d.Switches.TryGetValue(key, out var b);
            if (a != b) changes.Add($"{key}: {a ?? "off"} -> {b ?? "off"}");
        }
        d.Comparison = $"Compared with {previous.Started:MMM d, HH:mm}: {string.Join(", ", parts)}. " +
                       (changes.Count > 0 ? $"Changed: {string.Join("; ", changes)}." : "No settings changed, so any gap comes from where you were in the game.");
    }

    private static string Percent(double before, double after) =>
        before <= 0 ? "n/a" : (after >= before ? "+" : "") + ((after - before) / before * 100).ToString("0", Inv) + "%";

    public static string RenderReport(Diagnosis d)
    {
        var s = new StringBuilder();
        s.AppendLine($"# {d.Title} run report").AppendLine();
        s.AppendLine($"**{d.Verdict}**").AppendLine();
        s.AppendLine($"- Run: {d.Started:yyyy-MM-dd HH:mm:ss}, {d.Seconds:0} s, {d.Outcome.ToLowerInvariant()}" + (d.ExitCode is int c ? $", exit code {c}" : ""));
        s.AppendLine($"- Game: {d.TitleId}, furthest stage: {d.Stage}");
        s.AppendLine($"- Engine: {d.EngineVersion}" + (d.Gpu != null ? $", GPU: {d.Gpu}" : ""));
        s.AppendLine($"- Recording: {(d.Detailed ? "detailed" : "normal")}");
        if (d.TestMode) s.AppendLine($"- Test mode: on, skipped shader: {d.SkippedShader ?? "none"}");
        if (d.Switches.Count > 0) s.AppendLine($"- Engine switches: {string.Join(" ", d.Switches.Select(k => $"{k.Key}={k.Value}"))}");
        if (d.Comparison.Length > 0) s.AppendLine().AppendLine(d.Comparison);

        if (d.FrameMs != null)
        {
            s.AppendLine().AppendLine("## Numbers").AppendLine().AppendLine("| Measure | Value |").AppendLine("|---|---|");
            Row(s, "Average FPS, excluding loading", d.Fps, "0.0");
            Row(s, "Typical frame", d.FrameMs, "0.0", " ms");
            Row(s, "Slowest 1% of frames", d.FrameP99Ms, "0.0", " ms");
            Row(s, "Worst single frame", d.WorstFrameMs, "0", " ms");
            Row(s, "GPU busy per frame", d.GpuBusyMs, "0.0", " ms");
            Row(s, "GPU idle per frame", d.GpuIdleMs, "0.0", " ms");
            Row(s, "Busiest queue worker", d.WorkerBusyPct, "0", "%");
            Row(s, "GPU load (nvidia-smi)", d.GpuLoadPct, "0", "%");
            Row(s, "Peak video memory", d.PeakVramMb, "0", " MB");
            Row(s, "Peak game memory", d.PeakRamMb, "0", " MB");
            if (d.Resolution != null) s.AppendLine($"| Game output | {d.Resolution} |");
        }

        s.AppendLine().AppendLine("## Findings").AppendLine();
        foreach (var f in d.Findings)
        {
            s.AppendLine($"- **[{f.Severity}] {f.Area}: {f.Title}.** {f.Evidence}" + (f.NextStep.Length > 0 ? $" *Next: {f.NextStep}*" : ""));
        }
        if (d.Findings.Count == 0) s.AppendLine("Nothing stood out.");

        if (d.Shaders.Count > 0)
        {
            s.AppendLine().AppendLine("## Heaviest shaders").AppendLine().AppendLine("| Program | Runs | Total ms | ms per frame | Share of GPU time |").AppendLine("|---|---|---|---|---|");
            foreach (var p in d.Shaders) s.AppendLine($"| {p.Program} | {p.Ranges} | {p.TotalMs:0} | {p.MsPerFrame:0.0} | {p.SharePct:0}% |");
        }
        if (d.GpuClasses.Count > 0)
        {
            s.AppendLine().AppendLine("## GPU work by type").AppendLine().AppendLine("| Type | ms per frame | MiB per frame |").AppendLine("|---|---|---|");
            foreach (var g in d.GpuClasses) s.AppendLine($"| {g.Name} | {g.MsPerFrame:0.0} | {g.MibPerFrame:0} |");
        }
        if (d.Phases.Count > 0)
        {
            s.AppendLine().AppendLine("## Largest engine phases").AppendLine().AppendLine("Summed across threads and nested, so these can add up to more than a frame.").AppendLine().AppendLine("| Phase | ms per frame |").AppendLine("|---|---|");
            foreach (var p in d.Phases) s.AppendLine($"| {p.Key} | {p.Value:0.0} |");
        }
        if (d.Threads.Count > 0)
        {
            s.AppendLine().AppendLine("## Busiest threads").AppendLine().AppendLine("100% is one full CPU core.").AppendLine().AppendLine("| Thread | Average | Peak |").AppendLine("|---|---|---|");
            foreach (var t in d.Threads) s.AppendLine($"| {t.Name} | {t.AverageCpuPct:0}% | {t.PeakCpuPct:0}% |");
        }
        if (d.Timeline.Count > 0)
        {
            s.AppendLine().AppendLine("## Timeline").AppendLine().AppendLine("Seconds count from the first frame.").AppendLine().AppendLine("| From | Frames | Typical ms | Slowest 1% | Worst | GPU ms |").AppendLine("|---|---|---|---|---|---|");
            foreach (var t in d.Timeline) s.AppendLine($"| {t.Start:0} s | {t.Presents} | {t.P50:0} | {t.P99:0} | {t.Max:0} | {t.GpuMs:0} |");
        }
        if (d.CrashExcerpt.Length > 0)
            s.AppendLine().AppendLine("## Crash output").AppendLine().AppendLine("```").Append(d.CrashExcerpt).AppendLine("```");
        if (d.LogProblems.Count > 0)
        {
            s.AppendLine().AppendLine("## Problems in the log").AppendLine().AppendLine("Repeated lines are grouped.").AppendLine().AppendLine("| Times | First at | Line |").AppendLine("|---|---|---|");
            foreach (var l in d.LogProblems) s.AppendLine($"| {l.Count} | {l.FirstSeen:0} s | `{l.Sample.Replace('|', '/').Replace('`', '\'')}` |");
        }
        if (d.MissingFiles.Count > 0)
        {
            s.AppendLine().AppendLine("## Paths the game couldn't open").AppendLine().AppendLine("| Times | First at | Path |").AppendLine("|---|---|---|");
            foreach (var l in d.MissingFiles) s.AppendLine($"| {l.Count} | {l.FirstSeen:0} s | `{l.Sample}` |");
        }
        s.AppendLine().AppendLine("## Files in this folder").AppendLine();
        s.AppendLine("- `console.log`: everything the game printed, with seconds since start");
        s.AppendLine("- `aps5-summary.jsonl`: the engine's frame and GPU figures every 5 s");
        s.AppendLine("- `machine.csv`: CPU, memory, disk and GPU load every second");
        s.AppendLine("- `threads.csv`: the busiest threads every 5 s");
        s.AppendLine("- `anyps5-report.json`: the engine's own run report, when it saw the run end");
        s.AppendLine("- `diagnosis.json`: this report as data");
        return s.ToString();
    }

    private static void Row(StringBuilder s, string name, double? value, string format, string unit = "")
    {
        if (value is double v) s.AppendLine($"| {name} | {v.ToString(format, Inv)}{unit} |");
    }

    /// <summary>What machine.csv adds up to.</summary>
    public sealed class MachineStats
    {
        public double? AverageGpuPct, PeakVramMb, VramTotalMb, PeakPrivateMb, MinAvailableRamMb, PeakTemp, AverageSystemCpuPct, AverageCoresUsed;
        public double LongestStallSeconds, LongestStallStart;
        public int UnresponsiveSeconds;

        public static MachineStats Read(string path)
        {
            var m = new MachineStats();
            if (!File.Exists(path)) return m;
            var lines = ReadShared(path).ToList();
            if (lines.Count < 2) return m;
            var names = lines[0].Split(',');
            int Col(string n) => Array.IndexOf(names, n);
            var rows = lines.Skip(1).Select(l => l.Split(',')).Where(r => r.Length == names.Length).ToList();
            if (rows.Count == 0) return m;

            IEnumerable<double> Values(string name)
            {
                int at = Col(name);
                foreach (var r in rows)
                    if (at >= 0 && double.TryParse(r[at], NumberStyles.Float, Inv, out double v)) yield return v;
            }
            static double? Of(IEnumerable<double> v, Func<IEnumerable<double>, double> f)
            {
                var list = v.ToList();
                return list.Count > 0 ? f(list) : null;
            }
            // Startup would drag this down, so it covers the time since frames began.
            int gpuAt = Col("gpu_pct"), sizeAt = Col("summary_bytes");
            var playing = rows.Where(r => double.TryParse(r[sizeAt], NumberStyles.Float, Inv, out double b) && b > 0).ToList();
            var gpuLoads = (playing.Count > 0 ? playing : rows).Select(r => double.TryParse(r[gpuAt], NumberStyles.Float, Inv, out double v) ? v : -1).Where(v => v >= 0);
            m.AverageGpuPct = Of(gpuLoads, Enumerable.Average);
            m.PeakVramMb = Of(Values("vram_mb"), Enumerable.Max);
            m.VramTotalMb = Of(Values("vram_total_mb"), Enumerable.Max);
            m.PeakPrivateMb = Of(Values("private_mb"), Enumerable.Max);
            m.MinAvailableRamMb = Of(Values("avail_ram_mb"), Enumerable.Min);
            m.PeakTemp = Of(Values("gpu_temp"), Enumerable.Max);
            m.AverageSystemCpuPct = Of(Values("system_cpu_pct"), Enumerable.Average);
            m.AverageCoresUsed = Of(Values("cpu_cores"), Enumerable.Average);
            m.UnresponsiveSeconds = Values("responding").Count(v => v == 0);

            int t = Col("t"), bytes = Col("summary_bytes");
            double lastGrowth = -1, lastBytes = 0;
            foreach (var r in rows)
            {
                if (!double.TryParse(r[t], NumberStyles.Float, Inv, out double time) || !double.TryParse(r[bytes], NumberStyles.Float, Inv, out double size)) continue;
                if (size > lastBytes) { lastBytes = size; lastGrowth = time; }
                else if (lastGrowth >= 0 && time - lastGrowth > m.LongestStallSeconds) { m.LongestStallSeconds = time - lastGrowth; m.LongestStallStart = lastGrowth; }
            }
            return m;
        }
    }
}
