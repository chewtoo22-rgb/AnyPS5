using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AnyPS5Studio;

/// <summary>The facts about one run that only the app knows. Saved as run.json.</summary>
public sealed class RunFacts
{
    public string Title { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime Ended { get; set; }
    public int? ExitCode { get; set; }
    public bool StoppedByUser { get; set; }
    public bool Detailed { get; set; }
    public bool TestMode { get; set; }
    public List<SkipEvent> SkipEvents { get; set; } = new();
    public Dictionary<string, string> Switches { get; set; } = new();
}

/// <summary>A moment in a test mode run when a shader was turned off or back on.</summary>
public sealed record SkipEvent(double Seconds, bool Off, string Program);

/// <summary>
/// Picks the shader test mode skips. It has to run repeatedly and be expensive
/// each time, so a one-off job during loading is never chosen. One shader per
/// run: skipping more would hide which one caused any damage to the picture.
/// </summary>
public sealed class ShaderSkipper
{
    public const int MinRuns = 3;
    public const double MinMsPerRun = 20;

    private readonly Dictionary<string, (long Runs, double Ms)> _totals = new();

    public string? Program { get; private set; }

    /// <summary>Reads a line of the engine's output. True when it settles on a shader.</summary>
    public bool Observe(string line)
    {
        if (Program != null) return false;
        foreach (var (program, runs, ms) in RunAnalyzer.ProgramCosts(line))
        {
            _totals.TryGetValue(program, out var t);
            _totals[program] = (t.Runs + runs, t.Ms + ms);
        }
        var heaviest = _totals.Where(p => p.Value.Runs >= MinRuns && p.Value.Ms / p.Value.Runs >= MinMsPerRun)
            .OrderByDescending(p => p.Value.Ms).Select(p => p.Key).FirstOrDefault();
        if (heaviest == null) return false;
        Program = heaviest;
        return true;
    }
}

/// <summary>Records one run of a game into its own folder under diagnostics\runs.</summary>
public sealed class RunSession : IDisposable
{
    public const string ConsoleName = "console.log";
    public const string MachineName = "machine.csv";
    public const string ThreadsName = "threads.csv";
    public const string FactsName = "run.json";
    public const string SummaryName = "aps5-summary.jsonl";
    public const string ReportName = "anyps5-report.json";
    public const string SkipFileName = "skip-shaders.txt";

    private const long MaxConsoleBytes = 256L * 1024 * 1024;
    private const int RunsKept = 30;
    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(1);
    private const int ThreadSampleEvery = 5;

    private readonly string _gameFolder;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly RunFacts _facts;
    private StreamWriter? _console;
    private long _consoleBytes;
    private Task? _sampling;
    private NvidiaSampler? _nvidia;
    private readonly ShaderSkipper? _skipper;

    public string Folder { get; }

    /// <summary>Test mode found the shader to skip and has turned it off.</summary>
    public event Action<string>? ShaderChosen;

    public string? SkippedProgram => _skipper?.Program;

    public static string RunsFolder(string gameFolder) => Path.Combine(gameFolder, "diagnostics", "runs");

    public RunSession(LibraryEntry game, bool detailed, IReadOnlyDictionary<string, string> switches, bool testMode = false)
    {
        _gameFolder = Path.GetDirectoryName(game.ExePath)!;
        string runs = RunsFolder(_gameFolder);
        string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        Folder = Path.Combine(runs, name);
        for (int n = 2; Directory.Exists(Folder); n++) Folder = Path.Combine(runs, $"{name}_{n}");
        Directory.CreateDirectory(Folder);
        Prune(runs, Folder);

        _facts = new RunFacts
        {
            Title = game.Title, TitleId = game.TitleId, EngineVersion = game.EngineVersion,
            Started = DateTime.Now, Detailed = detailed, TestMode = testMode, Switches = new Dictionary<string, string>(switches),
        };
        if (testMode)
        {
            _skipper = new ShaderSkipper();
            WriteSkipFile("");
        }
        _console = new StreamWriter(Path.Combine(Folder, ConsoleName), append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>Removes the oldest runs. Only folders holding a run.json are ours to delete.</summary>
    private static void Prune(string runs, string current)
    {
        try
        {
            var old = new DirectoryInfo(runs).GetDirectories().Where(d => d.FullName != current && File.Exists(Path.Combine(d.FullName, FactsName)))
                .OrderByDescending(d => d.Name, StringComparer.Ordinal).Skip(RunsKept - 1);
            foreach (var d in old) d.Delete(recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Error("Removing old runs", e); }
    }

    /// <summary>The switches that make the engine record. The player's own switches win.</summary>
    public void ApplyTo(ProcessStartInfo psi)
    {
        psi.Environment["APS5_TRACE_SUMMARY_EVERY"] = "5";
        psi.Environment["APS5_TRACE_DIR"] = Folder;
        if (_facts.Detailed)
        {
            psi.Environment["APS5_PROFILE_GPU"] = "1";
            psi.Environment["APS5_PROFILE_DRAW"] = "1";
        }
        if (_facts.TestMode)
        {
            // The per-shader GPU times are how the heavy one gets found.
            psi.Environment["APS5_PROFILE_GPU"] = "1";
            psi.Environment["APS5_SKIP_SHADER_FILE"] = Path.Combine(Folder, SkipFileName);
        }
        foreach (var (key, value) in _facts.Switches) psi.Environment[key] = value;
    }

    private void WriteSkipFile(string program) =>
        File.WriteAllText(Path.Combine(Folder, SkipFileName), program.Length > 0 ? program[2..] + "\n" : "# nothing skipped\n", Encoding.ASCII);

    /// <summary>Turns the chosen shader off or back on while the game runs. The engine notices within a moment.</summary>
    public void SetShaderOff(bool off)
    {
        if (_skipper?.Program is not { } program) return;
        lock (_gate)
        {
            WriteSkipFile(off ? program : "");
            _facts.SkipEvents.Add(new SkipEvent(_clock.Elapsed.TotalSeconds, off, program));
        }
    }

    public void Began(Process process)
    {
        _clock.Start();
        _facts.Started = DateTime.Now;
        _nvidia = NvidiaSampler.TryStart();
        var token = _stop.Token;
        _sampling = Task.Run(() => SampleAsync(process, token));
    }

    public void Line(string text)
    {
        if (_skipper != null && _skipper.Observe(text))
        {
            SetShaderOff(true);
            ShaderChosen?.Invoke(_skipper.Program!);
        }
        lock (_gate)
        {
            if (_console == null) return;
            if (_consoleBytes > MaxConsoleBytes)
            {
                _console.WriteLine("[log stopped here: the run reached the size limit]");
                _console.Dispose();
                _console = null;
                return;
            }
            _console.Write('[');
            _console.Write(_clock.Elapsed.TotalSeconds.ToString("000000.000", CultureInfo.InvariantCulture));
            _console.Write("] ");
            _console.WriteLine(text);
            _consoleBytes += text.Length + 16;
        }
    }

    /// <summary>Ends the recording and works out what happened.</summary>
    public Diagnosis? Finish(int exitCode, bool stoppedByUser)
    {
        _stop.Cancel();
        try { _sampling?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException e) { AppLog.Error("Sampling the run", e); }
        _nvidia?.Dispose();
        lock (_gate) { _console?.Dispose(); _console = null; }

        _facts.Ended = DateTime.Now;
        _facts.ExitCode = exitCode;
        _facts.StoppedByUser = stoppedByUser;
        try
        {
            File.WriteAllText(Path.Combine(Folder, FactsName), JsonSerializer.Serialize(_facts, new JsonSerializerOptions { WriteIndented = true }));
            CollectEngineReport();
            return RunAnalyzer.AnalyzeAndSave(Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            AppLog.Error($"Finishing the run in {Folder}", e);
            return null;
        }
    }

    /// <summary>The engine writes its report beside the game, once, and only for a run it saw end.</summary>
    private void CollectEngineReport()
    {
        var report = new FileInfo(Path.Combine(_gameFolder, ReportName));
        if (report.Exists && report.LastWriteTime >= _facts.Started)
            report.CopyTo(Path.Combine(Folder, ReportName), overwrite: true);
    }

    private async Task SampleAsync(Process process, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        string summary = Path.Combine(Folder, SummaryName);
        using var machine = new StreamWriter(Path.Combine(Folder, MachineName), false, new UTF8Encoding(false)) { AutoFlush = true };
        using var threads = new StreamWriter(Path.Combine(Folder, ThreadsName), false, new UTF8Encoding(false)) { AutoFlush = true };
        machine.WriteLine("t,cpu_cores,system_cpu_pct,working_set_mb,private_mb,read_mb_s,write_mb_s,avail_ram_mb,threads,responding,summary_bytes," +
                          "gpu_pct,vram_mb,vram_total_mb,gpu_mhz,gpu_max_mhz,gpu_watts,gpu_limit_watts,gpu_temp");
        threads.WriteLine("t,thread,cpu_pct");

        var threadTimes = new Dictionary<int, TimeSpan>();
        var names = new Dictionary<int, string>();
        TimeSpan lastCpu = TimeSpan.Zero;
        var (lastIdle, lastBusy) = Native.SystemTimes();
        var (lastRead, lastWrite) = (0UL, 0UL);
        double last = 0, lastThreads = 0;
        int tick = 0;

        using var timer = new PeriodicTimer(SampleEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                process.Refresh();
                if (process.HasExited) return;
                double now = _clock.Elapsed.TotalSeconds, dt = Math.Max(0.001, now - last);
                last = now;

                var cpu = process.TotalProcessorTime;
                double cores = (cpu - lastCpu).TotalSeconds / dt;
                lastCpu = cpu;

                var (idle, busy) = Native.SystemTimes();
                double total = busy - lastBusy;
                double system = total > 0 ? 100.0 * (1 - (idle - lastIdle) / total) : 0;
                (lastIdle, lastBusy) = (idle, busy);

                var (read, write) = Native.IoBytes(process);
                double readRate = (read - lastRead) / dt / (1 << 20), writeRate = (write - lastWrite) / dt / (1 << 20);
                (lastRead, lastWrite) = (read, write);

                long summaryBytes = File.Exists(summary) ? new FileInfo(summary).Length : 0;
                machine.WriteLine(string.Format(inv, "{0:0.0},{1:0.00},{2:0.0},{3},{4},{5:0.0},{6:0.0},{7},{8},{9},{10},{11}",
                    now, cores, system, process.WorkingSet64 >> 20, process.PrivateMemorySize64 >> 20, readRate, writeRate,
                    Native.AvailableRamMb(), process.Threads.Count, process.Responding ? 1 : 0, summaryBytes, _nvidia?.Latest ?? ",,,,,,,"));

                if (++tick % ThreadSampleEvery != 0) continue;
                double span = Math.Max(0.001, now - lastThreads);
                lastThreads = now;
                var rows = new List<(string Name, double Pct)>();
                foreach (ProcessThread t in process.Threads)
                {
                    try
                    {
                        var used = t.TotalProcessorTime;
                        threadTimes.TryGetValue(t.Id, out var before);
                        threadTimes[t.Id] = used;
                        if (!names.TryGetValue(t.Id, out var label)) names[t.Id] = label = Native.ThreadName(t.Id);
                        rows.Add((label, 100.0 * (used - before).TotalSeconds / span));
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* it just ended */ }
                }
                foreach (var row in rows.OrderByDescending(r => r.Pct).Take(8))
                    threads.WriteLine(string.Format(inv, "{0:0.0},{1},{2:0.0}", now, row.Name.Replace(',', ' '), row.Pct));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            AppLog.Error("Sampling the machine during a run", e);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _nvidia?.Dispose();
        lock (_gate) { _console?.Dispose(); _console = null; }
    }

    /// <summary>GPU load from nvidia-smi. Other GPUs have no equivalent, so those columns stay empty.</summary>
    private sealed class NvidiaSampler : IDisposable
    {
        private readonly Process _process;
        private volatile string _latest = ",,,,,,,";

        public string Latest => _latest;

        private NvidiaSampler(Process process) => _process = process;

        public static NvidiaSampler? TryStart()
        {
            var psi = new ProcessStartInfo("nvidia-smi")
            {
                Arguments = "-i 0 --query-gpu=utilization.gpu,memory.used,memory.total,clocks.gr,clocks.max.gr,power.draw,power.limit,temperature.gpu " +
                            "--format=csv,noheader,nounits -l 1",
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false,
            };
            try
            {
                var p = new Process { StartInfo = psi };
                var sampler = new NvidiaSampler(p);
                p.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    var cells = e.Data.Split(',').Select(c => c.Trim()).Select(c => double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out double _) ? c : "").ToArray();
                    if (cells.Length == 8) sampler._latest = string.Join(',', cells);
                };
                p.ErrorDataReceived += (_, _) => { };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                return sampler;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AppLog.Info("nvidia-smi isn't available, so runs have no GPU load samples.");
                return null;
            }
        }

        public void Dispose()
        {
            try { if (!_process.HasExited) _process.Kill(); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            _process.Dispose();
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint access, bool inherit, int threadId);

        [DllImport("kernel32.dll")]
        private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length, Load;
            public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
        }

        /// <summary>Idle time and all time, kernel time already including idle.</summary>
        public static (double Idle, double All) SystemTimes() =>
            GetSystemTimes(out long idle, out long kernel, out long user) ? (idle, (double)kernel + user) : (0, 0);

        public static (ulong Read, ulong Write) IoBytes(Process p) =>
            GetProcessIoCounters(p.Handle, out var c) ? (c.ReadBytes, c.WriteBytes) : (0, 0);

        public static long AvailableRamMb()
        {
            var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref s) ? (long)(s.AvailPhys >> 20) : 0;
        }

        public static string ThreadName(int id)
        {
            const uint QueryLimitedInformation = 0x0800;
            IntPtr handle = OpenThread(QueryLimitedInformation, false, id);
            if (handle == IntPtr.Zero) return $"thread {id}";
            try
            {
                if (GetThreadDescription(handle, out IntPtr text) < 0 || text == IntPtr.Zero) return $"thread {id}";
                string? name = Marshal.PtrToStringUni(text);
                LocalFree(text);
                return string.IsNullOrWhiteSpace(name) ? $"thread {id}" : name;
            }
            finally { CloseHandle(handle); }
        }
    }
}
