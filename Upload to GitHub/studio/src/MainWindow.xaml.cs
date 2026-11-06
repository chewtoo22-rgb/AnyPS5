using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace AnyPS5Studio;

public partial class MainWindow : Window
{
    private const int MaxLogChars = 200_000;

    private readonly AppStore _store = new();
    private readonly ObservableCollection<LibraryEntry> _games;
    private readonly AppSettings _settings;
    private readonly GameRunner _runner = new();

    private EngineInfo? _engine;
    private GameScan? _scan;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _convertCts;
    private LibraryEntry? _runningGame;
    private bool _outputChosenByUser;
    private bool _stopRequested;

    public MainWindow()
    {
        InitializeComponent();

        _settings = _store.LoadSettings();
        _games = new ObservableCollection<LibraryEntry>(_store.LoadLibrary().OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase));
        GameList.ItemsSource = _games;
        UpdateEmptyLibrary();

        _runner.Line += line => Dispatcher.BeginInvoke(() => AppendLog(RunLog, line));
        _runner.ShaderSkipped += program => Dispatcher.BeginInvoke(() => OnShaderSkipped(program));
        _runner.Exited += (code, diagnosis) => Dispatcher.BeginInvoke(() => OnGameExited(code, diagnosis));

        RefreshEngine();
        OutputFactsUpdate();
        DetailedCheck.IsChecked = _settings.DetailedRecording;

        // Dropping a folder on the app's icon passes it as an argument.
        Loaded += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1) _ = ScanAsync(args[1]);
        };
    }

    // The title bar stays light unless Windows is told otherwise.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        int on = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
    }

    private void RefreshEngine()
    {
        _engine = EngineLocator.Find(_settings.EngineFolder);
        if (_engine != null)
        {
            EngineStatus.Text = _engine.Version == "unknown" ? "AnyPS5 ready" : $"AnyPS5 {_engine.Version} ready";
            EngineButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            EngineStatus.Text = "AnyPS5 not found. Choose the folder with relinker.exe and libs.";
            EngineButton.Visibility = Visibility.Visible;
        }
        UpdateConvertButton();
    }

    private void OnChooseEngine(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the AnyPS5 folder" };
        if (dialog.ShowDialog(this) != true) return;
        if (!EngineLocator.IsEngineFolder(dialog.FolderName))
        {
            MessageBox.Show(this, "That folder needs relinker.exe and libs\\libkernel.prx.", "AnyPS5 Studio",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.EngineFolder = dialog.FolderName;
        _store.SaveSettings(_settings);
        RefreshEngine();
    }

    private bool Converting => _convertCts != null;

    private void UpdateConvertButton()
    {
        ConvertButton.IsEnabled = !Converting && _engine != null && _scan?.CanConvert == true && OutputBox.Text.Length > 0;
    }

    private void ShowConvertView()
    {
        GameList.SelectedItem = null;
        GameView.Visibility = Visibility.Collapsed;
        ConvertView.Visibility = Visibility.Visible;
    }

    private void OnNewClicked(object sender, RoutedEventArgs e) => ShowConvertView();

    private void OnBrowseSource(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the game folder" };
        if (dialog.ShowDialog(this) == true) _ = ScanAsync(dialog.FolderName);
    }

    private void OnBrowseImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the game's disk image", Filter = "exFAT disk images (*.exfat)|*.exfat" };
        if (dialog.ShowDialog(this) == true) _ = ScanAsync(dialog.FileName);
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save the game" };
        if (dialog.ShowDialog(this) != true) return;
        OutputBox.Text = dialog.FolderName;
        _outputChosenByUser = true;
        OutputFactsUpdate();
        UpdateConvertButton();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !Converting && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Converting || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        ShowConvertView();
        _ = ScanAsync(paths[0]);
    }

    private async Task ScanAsync(string path)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();

        SourceBox.Text = path;
        _scan = null;
        ResultBanner.Visibility = Visibility.Collapsed;
        ConvertFindings.Visibility = Visibility.Collapsed;
        ScanPanel.Visibility = Visibility.Collapsed;
        ScanHint.Text = "Looking through the folder...";
        ScanHint.Visibility = Visibility.Visible;
        UpdateConvertButton();

        GameScan scan;
        try
        {
            scan = await Task.Run(() => GameScanner.Scan(path, cts.Token), cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (cts != _scanCts) return;
            ScanHint.Text = $"Couldn't read that folder: {ex.Message}";
            AppLog.Warn($"Couldn't scan {path}: {ex.Message}");
            return;
        }
        if (cts != _scanCts) return; // a newer pick replaced this one

        _scan = scan;
        if (scan.Problems.Count > 0) AppLog.Info($"Scan of {path} refused: {string.Join(" ", scan.Problems)}");
        SourceBox.Text = scan.SourcePath;
        ScanHint.Visibility = Visibility.Collapsed;
        ScanPanel.Visibility = Visibility.Visible;
        GameTitle.Text = scan.Title;

        var facts = new List<string>();
        if (scan.TitleId.Length > 0) facts.Add(scan.TitleId);
        if (scan.ExecutableRel != null) facts.Add(Path.GetFileName(scan.ExecutableRel));
        facts.Add(scan.ModuleCount == 1 ? "1 module" : $"{scan.ModuleCount} modules");
        facts.Add(Converter.FormatBytes(scan.TotalBytes));
        if (scan.IsImage) facts.Add("exFAT image");
        if (scan.NeedsUnwrap) facts.Add("SELF, will be unwrapped");
        GameFacts.Text = string.Join("  ·  ", facts);

        ProblemList.Children.Clear();
        foreach (var p in scan.Problems) ProblemList.Children.Add(Notice(p, "Bad"));
        foreach (var w in scan.Warnings) ProblemList.Children.Add(Notice(w, "Warn"));

        if (!_outputChosenByUser)
            OutputBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "AnyPS5 Games", Converter.SafeFileName(scan.Title));
        OutputFactsUpdate();
        UpdateConvertButton();
    }

    private TextBlock Notice(string text, string brushKey) => new()
    {
        Text = text,
        Foreground = (Brush)FindResource(brushKey),
        Margin = new Thickness(0, 4, 0, 0),
        FontSize = 13,
    };

    private void OutputFactsUpdate()
    {
        string text = "";
        if (OutputBox.Text.Length > 0)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(OutputBox.Text))!);
                text = $"{Converter.FormatBytes(drive.AvailableFreeSpace)} free on drive {drive.Name.TrimEnd('\\', ':')}";
                if (_scan != null && _engine != null)
                    text += $"  ·  this game needs about {Converter.FormatBytes(_scan.TotalBytes + LibsBytes())}";
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
        OutputFacts.Text = text;
        OutputFacts.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private long _libsBytes = -1;

    private long LibsBytes()
    {
        if (_libsBytes < 0)
            _libsBytes = Directory.EnumerateFiles(_engine!.LibsDir).Sum(f => new FileInfo(f).Length);
        return _libsBytes;
    }

    private void SetBusy(bool busy)
    {
        BrowseSource.IsEnabled = BrowseImage.IsEnabled = BrowseOutput.IsEnabled = NewButton.IsEnabled = !busy;
        GameList.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateConvertButton();
    }

    private void Banner(string text, string brushKey)
    {
        var brush = (Brush)FindResource(brushKey);
        ResultBanner.BorderBrush = brush;
        ResultText.Foreground = brush;
        ResultText.Text = text;
        ResultBanner.Visibility = Visibility.Visible;
    }

    private async void OnConvert(object sender, RoutedEventArgs e)
    {
        if (_scan == null || _engine == null || Converting) return;
        var scan = _scan;
        var engine = _engine;
        string output = OutputBox.Text;

        var cts = _convertCts = new CancellationTokenSource();
        ConvertLog.Clear();
        LogExpander.Visibility = Visibility.Visible;
        ResultBanner.Visibility = Visibility.Collapsed;
        ConvertFindings.Visibility = Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        SetBusy(true);

        var record = new ConversionRecord(scan, output, engine);
        var progress = new Progress<ConvertProgress>(p =>
        {
            record.Stage(p.Stage);
            StageText.Text = p.Stage;
            Progress.IsIndeterminate = p.Fraction == null;
            if (p.Fraction is double f) Progress.Value = f;
        });
        void Log(string line)
        {
            record.Line(line);
            Dispatcher.BeginInvoke(() => AppendLog(ConvertLog, line));
        }

        try
        {
            var result = await Task.Run(() => Converter.RunAsync(new ConvertRequest(scan, output, engine), progress, Log, cts.Token));
            var entry = new LibraryEntry
            {
                Title = scan.Title, TitleId = scan.TitleId, Folder = result.OutputDir, ExePath = result.ExePath,
                ConvertedAt = DateTime.Now, EngineVersion = engine.Version,
            };
            var old = _games.FirstOrDefault(g => string.Equals(g.Folder, entry.Folder, StringComparison.OrdinalIgnoreCase));
            if (old != null) _games.Remove(old);
            _games.Add(entry);
            SaveLibrary();
            record.Finish("Converted", null);
            ResetConvertForm();
            GameList.SelectedItem = entry;
        }
        catch (OperationCanceledException)
        {
            record.Finish("Cancelled", null);
            Banner("Cancelled. Convert again with the same folder to finish.", "Warn");
        }
        catch (ConvertException ex)
        {
            Banner(ex.Message, "Bad");
            ShowConvertFindings(record, record.Finish("Failed", ex.Message));
            LogExpander.IsExpanded = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Banner($"Stopped while writing files: {ex.Message}", "Bad");
            ShowConvertFindings(record, record.Finish("Failed", ex.Message));
            LogExpander.IsExpanded = true;
        }
        catch (Exception ex)
        {
            // Recorded here because the record is lost if this reaches the crash handler.
            AppLog.Error("Unexpected error during a conversion", ex);
            record.Finish("Failed", ex.Message);
            throw;
        }
        finally
        {
            record.Dispose();
            _convertCts = null;
            cts.Dispose();
            Progress.IsIndeterminate = false;
            SetBusy(false);
        }
    }

    private string? _convertRecordFolder;

    private void ShowConvertFindings(ConversionRecord record, List<Finding> findings)
    {
        _convertRecordFolder = record.Folder;
        ConvertFindingList.Children.Clear();
        foreach (var f in findings) ConvertFindingList.Children.Add(FindingView(f));
        ConvertFindings.Visibility = Visibility.Visible;
    }

    private void OnOpenConvertRecord(object sender, RoutedEventArgs e) => OpenInExplorer(_convertRecordFolder);

    private static void OpenInExplorer(string? folder)
    {
        if (folder != null && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });
    }

    /// <summary>One finding: a coloured title, the evidence, and what to try next.</summary>
    private StackPanel FindingView(Finding f)
    {
        string brush = f.Severity switch { Severity.Critical => "Bad", Severity.Warning => "Warn", Severity.Good => "Good", _ => "Muted" };
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = f.Title, Foreground = (Brush)FindResource(brush), FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (f.Evidence.Length > 0)
            panel.Children.Add(new TextBlock { Text = f.Evidence, Foreground = (Brush)FindResource("Muted"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        if (f.NextStep.Length > 0)
            panel.Children.Add(new TextBlock { Text = f.NextStep, Foreground = (Brush)FindResource("Text"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        return panel;
    }

    private void ResetConvertForm()
    {
        _scan = null;
        _outputChosenByUser = false;
        SourceBox.Clear();
        OutputBox.Clear();
        ScanPanel.Visibility = Visibility.Collapsed;
        ScanHint.Text = "Or drop a folder or .exfat image anywhere on this window.";
        ScanHint.Visibility = Visibility.Visible;
        LogExpander.Visibility = Visibility.Collapsed;
        LogExpander.IsExpanded = false;
        OutputFactsUpdate();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StageText.Text = "Cancelling...";
        _convertCts?.Cancel();
    }

    private static void AppendLog(TextBox box, string line)
    {
        box.AppendText(line + Environment.NewLine);
        if (box.Text.Length > MaxLogChars) box.Text = box.Text[^(MaxLogChars / 2)..];
        box.ScrollToEnd();
    }

    private void UpdateEmptyLibrary() =>
        EmptyLibrary.Visibility = _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SaveLibrary()
    {
        _store.SaveLibrary(_games);
        UpdateEmptyLibrary();
    }

    private LibraryEntry? Selected => GameList.SelectedItem as LibraryEntry;

    private void OnGameSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } game) return;
        ConvertView.Visibility = Visibility.Collapsed;
        GameView.Visibility = Visibility.Visible;

        DetailTitle.Text = game.Title;
        var facts = new List<string>();
        if (game.TitleId.Length > 0) facts.Add(game.TitleId);
        facts.Add($"Converted {game.ConvertedAt:d}");
        if (game.EngineVersion.Length > 0) facts.Add($"AnyPS5 {game.EngineVersion}");
        DetailFacts.Text = string.Join("  ·  ", facts);

        SwitchesBox.Text = game.EngineSwitches;
        TestModeCheck.IsChecked = game.SkipHeaviestShader;
        ShowSwitchesNote(game);
        ShowRuns(game);
        RunLog.Text = ReadLastRunLog();
        RunLog.ScrollToEnd();
        UpdateRunButtons();
    }

    private Diagnosis? PickedRun => RunPicker.SelectedItem as Diagnosis;

    private void ShowRuns(LibraryEntry game)
    {
        var runs = RunAnalyzer.LoadRuns(game.Folder);
        RunPicker.ItemsSource = runs;
        RunPicker.SelectedIndex = runs.Count > 0 ? 0 : -1;
        RunPanel.Visibility = runs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoRunsText.Visibility = runs.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRunPicked(object sender, SelectionChangedEventArgs e)
    {
        RunFindings.Children.Clear();
        if (PickedRun is not { } d) return;

        var numbers = new List<string>();
        if (d.FrameMs is double frame) numbers.Add($"Typical frame {frame:0} ms");
        if (d.FrameP99Ms is double p99) numbers.Add($"slowest 1% {p99:0} ms");
        if (d.GpuBusyMs is double gpu) numbers.Add($"GPU {gpu:0} ms");
        if (d.GpuLoadPct is double load) numbers.Add($"GPU load {load:0}%");
        if (d.Resolution is { Length: > 0 } res) numbers.Add(res);
        numbers.Add(d.Detailed ? "detailed" : "normal recording");
        RunNumbers.Text = string.Join("  ·  ", numbers);
        RunComparison.Text = d.Comparison;
        RunComparison.Visibility = d.Comparison.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var f in d.Findings.Take(8)) RunFindings.Children.Add(FindingView(f));
    }

    private void OnOpenReport(object sender, RoutedEventArgs e)
    {
        if (PickedRun is not { } d) return;
        string path = Path.Combine(d.Folder, RunAnalyzer.DiagnosisReport);
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path } });
    }

    private void OnOpenRunFolder(object sender, RoutedEventArgs e) => OpenInExplorer(PickedRun?.Folder);

    private void OnDetailedChanged(object sender, RoutedEventArgs e)
    {
        _settings.DetailedRecording = DetailedCheck.IsChecked == true;
        _store.SaveSettings(_settings);
    }

    private void OnTestModeChanged(object sender, RoutedEventArgs e)
    {
        bool on = TestModeCheck.IsChecked == true;
        if (Selected is not { } game || game.SkipHeaviestShader == on) return;
        game.SkipHeaviestShader = on;
        SaveLibrary();
    }

    private void OnShaderSkipped(string program)
    {
        Status($"Test mode: shader {program} is off. Compare the picture with the button above.");
        UpdateRunButtons();
    }

    private void OnToggleShader(object sender, RoutedEventArgs e)
    {
        _runner.SetShaderOff(!_runner.ShaderIsOff);
        Status($"Test mode: shader {_runner.SkippedProgram} is {(_runner.ShaderIsOff ? "off" : "back on")}.");
        UpdateRunButtons();
    }

    private void OnSwitchesChanged(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } game || game.EngineSwitches == SwitchesBox.Text.Trim()) return;
        game.EngineSwitches = SwitchesBox.Text.Trim();
        SaveLibrary();
        ShowSwitchesNote(game);
    }

    private void ShowSwitchesNote(LibraryEntry game)
    {
        GameRunner.ParseSwitches(game.EngineSwitches, out var rejected);
        SwitchesNote.Text = rejected.Count > 0 ? $"Ignored, because only APS5_ and ANYPS5_ switches are passed on: {string.Join(" ", rejected)}" : "";
        SwitchesNote.Visibility = rejected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string ReadLastRunLog()
    {
        try
        {
            if (PickedRun is not { } d) return "";
            string path = Path.Combine(d.Folder, RunSession.ConsoleName);
            if (!File.Exists(path)) return "";
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (s.Length > MaxLogChars) s.Seek(-MaxLogChars, SeekOrigin.End);
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private void UpdateRunButtons()
    {
        bool thisGame = _runner.IsRunning && ReferenceEquals(_runningGame, Selected);
        PlayButton.Visibility = thisGame ? Visibility.Collapsed : Visibility.Visible;
        PlayButton.IsEnabled = !_runner.IsRunning;
        StopButton.Visibility = thisGame ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = !thisGame;
        ShaderToggleButton.Visibility = thisGame && _runner.SkippedProgram != null ? Visibility.Visible : Visibility.Collapsed;
        ShaderToggleButton.Content = _runner.ShaderIsOff ? "Turn shader back on" : "Turn shader off";
        if (!_runner.IsRunning && _runningGame == null) RunStatus.Visibility = Visibility.Collapsed;
    }

    private void Status(string text)
    {
        RunStatus.Text = text;
        RunStatus.Visibility = Visibility.Visible;
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } game) return;
        OnSwitchesChanged(sender, e); // Play can be clicked while the box still has focus
        RunLog.Clear();
        try
        {
            _runningGame = game;
            _runner.Start(game, DetailedCheck.IsChecked == true);
            Status(game.SkipHeaviestShader ? $"{game.Title} is running. Test mode is looking for the heaviest shader." : $"{game.Title} is running.");
        }
        catch (Exception ex) when (ex is ConvertException or IOException or UnauthorizedAccessException)
        {
            _runningGame = null;
            AppLog.Warn($"Couldn't start {game.Title}: {ex.Message}");
            Status(ex.Message);
        }
        UpdateRunButtons();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _stopRequested = true;
        _runner.Stop();
    }

    private void OnGameExited(int code, Diagnosis? diagnosis)
    {
        string title = _runningGame?.Title ?? "The game";
        var game = _runningGame;
        bool stopped = _stopRequested;
        _stopRequested = false;
        _runningGame = null;
        UpdateRunButtons();
        Status(code == 0 || stopped ? $"{title} closed." : $"{title} stopped with exit code {code}. The report below has the reason.");
        AppLog.Info($"{title} ended with code {code}: {diagnosis?.Verdict ?? "no diagnosis"}");
        if (game != null && ReferenceEquals(game, Selected)) ShowRuns(game);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } game) return;
        if (!Directory.Exists(game.Folder))
        {
            Status($"{game.Folder} is missing. It may have been moved or deleted.");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { game.Folder } });
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } game) return;
        var answer = MessageBox.Show(this,
            $"Remove {game.Title} from this list?\n\nThe game's files stay in:\n{game.Folder}",
            "AnyPS5 Studio", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        _games.Remove(game);
        SaveLibrary();
        ShowConvertView();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Converting || _runner.IsRunning)
        {
            string what = Converting ? "A conversion is in progress and will be cancelled." : "Your game is running and will be closed.";
            var answer = MessageBox.Show(this, $"{what}\n\nClose AnyPS5 Studio?", "AnyPS5 Studio",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) { e.Cancel = true; return; }
        }
        _convertCts?.Cancel();
        _runner.Dispose();
    }
}
