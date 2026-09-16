using System.Globalization;
using System.Text;
using Crap4CSharp.Core;

using var cancellationSource = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};
Console.CancelKeyPress += cancelHandler;
try
{
    return await App.RunAsync(args, Directory.GetCurrentDirectory(), Console.Out, Console.Error, cancellationSource.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

internal static class App
{
    private const string Help = """
        Crap4CSharp - calculate CRAP metrics for C# methods

        Usage:
          crap4csharp [options] [file-or-directory ...]

        Options:
          --coverage <xml>   Use an OpenCover or Cobertura/Coverlet XML report; repeatable.
                             Supplying coverage skips dotnet test.
          --project <path>   Solution or project passed to dotnet test.
          --threshold <n>    Fail when a known CRAP score is strictly greater than n (default: 8).
          --timeout-seconds <n>
                             Maximum time for each external command (default: 300; max: 86400).
          --allow-missing-coverage
                             Allow N/A methods; known scores still gate. By default any N/A is
                             an operational failure to prevent a false-green quality gate.
          --changed          Analyze changed/untracked C# files from git porcelain status.
          -h, --help         Show help and perform no discovery, tests, or writes.

        Exit codes: 0 success, 1 usage/operational failure, 2 threshold exceeded.
        """;

    public static async Task<int> RunAsync(string[] args, string workingDirectory, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Any(arg => arg is "--help" or "-h")) { await output.WriteLineAsync(Help); return 0; }

        try
        {
            var options = args.Length == 0 ? new Options() : Parse(args);
            return await ExecuteAsync(options, workingDirectory, output, error, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            await error.WriteLineAsync($"error: {exception.Message}");
            await error.WriteLineAsync("Run 'crap4csharp --help' for usage.");
            return 1;
        }
        catch (OperationCanceledException) { await error.WriteLineAsync("error: operation cancelled"); return 1; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            await error.WriteLineAsync($"error: {exception.Message}");
            return 1;
        }
    }

    private static Options Parse(string[] args)
    {
        var options = new Options();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            string Value()
            {
                if (++index >= args.Length) throw new ArgumentException($"Missing value for {arg}.");
                return args[index];
            }
            switch (arg)
            {
                case "--coverage": options.Coverage.Add(Value()); break;
                case "--project": options.Project = Value(); break;
                case "--threshold":
                    if (!double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) ||
                        !double.IsFinite(threshold) || threshold < 0) throw new ArgumentException("Threshold must be a finite non-negative number.");
                    options.Threshold = threshold;
                    break;
                case "--timeout-seconds":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var timeoutSeconds) ||
                        timeoutSeconds is < 1 or > 86400)
                        throw new ArgumentException("Timeout must be a whole number of seconds from 1 through 86400.");
                    options.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                    break;
                case "--changed": options.Changed = true; break;
                case "--allow-missing-coverage": options.AllowMissingCoverage = true; break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option: {arg}");
                    options.Inputs.Add(arg);
                    break;
            }
        }
        if (options.Changed && options.Inputs.Count > 0) throw new ArgumentException("--changed cannot be combined with explicit files or directories.");
        return options;
    }

    private static async Task<int> ExecuteAsync(Options options, string workingDirectory, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var files = options.Changed
            ? await ChangedFilesAsync(workingDirectory, options.Timeout, cancellationToken)
            : SourceDiscovery.Discover(options.Inputs, workingDirectory);
        if (files.Count == 0) { await output.WriteLineAsync("No C# source files found."); return 0; }

        var testFailed = false;
        var reports = options.Coverage.Select(path => Path.GetFullPath(path, workingDirectory)).ToList();
        if (reports.Count == 0)
        {
            var target = ResolveTestTarget(options.Project, workingDirectory);
            var resultsDirectory = Path.Combine(Path.GetTempPath(), "crap4csharp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(resultsDirectory);
            await output.WriteLineAsync($"Running coverage: dotnet test {target}");
            await output.WriteLineAsync($"Coverage results: {resultsDirectory}");
            var result = await ProcessRunner.RunAsync("dotnet",
                ["test", target, "-m:1", "--collect:XPlat Code Coverage", "--results-directory", resultsDirectory,
                 "--", "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover"],
                workingDirectory, options.Timeout, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.StandardOutput)) await output.WriteAsync(result.StandardOutput);
            if (!string.IsNullOrWhiteSpace(result.StandardError)) await error.WriteAsync(result.StandardError);
            testFailed = result.ExitCode != 0;
            reports.AddRange(Directory.EnumerateFiles(resultsDirectory, "*.xml", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("coverage", StringComparison.OrdinalIgnoreCase)));
            if (reports.Count == 0)
            {
                await error.WriteLineAsync("error: dotnet test produced no coverage XML. Add coverlet.collector to the test project or pass --coverage.");
                return 1;
            }
        }

        foreach (var report in reports)
            if (!File.Exists(report)) throw new FileNotFoundException($"Coverage report not found: {report}", report);
        var source = new SourceAnalyzer().AnalyzeFiles(files);
        var coverage = reports.Select(CoverageReader.Read).ToArray();
        var metrics = CoverageMatcher.Apply(source, coverage)
            .OrderBy(metric => metric.Crap is null ? 1 : 0)
            .ThenByDescending(metric => metric.Crap)
            .ThenBy(metric => metric.File, StringComparer.Ordinal)
            .ThenBy(metric => metric.StartLine)
            .ToArray();

        await output.WriteLineAsync("CRAP      CC  Coverage  Method");
        foreach (var metric in metrics)
        {
            var crap = metric.Crap?.ToString("0.00", CultureInfo.InvariantCulture) ?? "N/A";
            var coverageText = metric.Coverage is double value ? value.ToString("P1", CultureInfo.InvariantCulture) : "N/A";
            var relative = Path.GetRelativePath(workingDirectory, metric.File);
            await output.WriteLineAsync($"{crap,8} {metric.Complexity,3} {coverageText,9}  {relative}:{metric.StartLine} {metric.DisplayName}");
        }

        var violations = metrics.Where(metric => metric.Crap > options.Threshold).ToArray();
        var missingCoverage = metrics.Count(metric => metric.Coverage is null);
        await output.WriteLineAsync($"Methods: {metrics.Length}; known coverage: {metrics.Length - missingCoverage}; missing coverage: {missingCoverage}; violations (> {options.Threshold.ToString(CultureInfo.InvariantCulture)}): {violations.Length}");
        if (testFailed) { await error.WriteLineAsync("error: dotnet test failed; report shown from available coverage data."); return 1; }
        if (missingCoverage > 0 && !options.AllowMissingCoverage)
        {
            await error.WriteLineAsync("error: one or more analyzed methods have N/A coverage; pass --allow-missing-coverage to gate only known scores.");
            return 1;
        }
        return violations.Length > 0 ? 2 : 0;
    }

    private static async Task<IReadOnlyList<string>> ChangedFilesAsync(string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var rootResult = await ProcessRunner.RunAsync("git", ["rev-parse", "--show-toplevel"], workingDirectory, timeout, cancellationToken);
        if (rootResult.ExitCode != 0)
            throw new InvalidOperationException($"git rev-parse failed: {rootResult.StandardError.Trim()}");
        var root = rootResult.StandardOutput.TrimEnd('\r', '\n');
        if (root.Length == 0) throw new InvalidOperationException("git rev-parse returned an empty repository root.");
        var statusResult = await ProcessRunner.RunAsync("git",
            ["status", "--porcelain=v1", "--untracked-files=all", "-z"], root, timeout, cancellationToken);
        if (statusResult.ExitCode != 0)
            throw new InvalidOperationException($"git status failed: {statusResult.StandardError.Trim()}");
        return GitChanges.ParsePorcelainV1Z(Encoding.UTF8.GetBytes(statusResult.StandardOutput), root);
    }

    private static string ResolveTestTarget(string? requested, string workingDirectory)
    {
        if (requested is not null)
        {
            var path = Path.GetFullPath(requested, workingDirectory);
            if (!File.Exists(path)) throw new FileNotFoundException($"Project or solution not found: {requested}", path);
            return path;
        }
        var solutions = Directory.EnumerateFiles(workingDirectory, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(workingDirectory, "*.slnx", SearchOption.TopDirectoryOnly)).ToArray();
        if (solutions.Length == 1) return solutions[0];
        if (solutions.Length > 1) throw new InvalidOperationException("Multiple solutions found; specify --project.");
        var projects = Directory.EnumerateFiles(workingDirectory, "*.csproj", SearchOption.TopDirectoryOnly).ToArray();
        if (projects.Length == 1) return projects[0];
        if (projects.Length > 1) throw new InvalidOperationException("Multiple projects found; specify --project.");
        throw new InvalidOperationException("No solution or project found in the working directory; specify --project.");
    }

    private sealed class Options
    {
        public List<string> Coverage { get; } = [];
        public List<string> Inputs { get; } = [];
        public string? Project { get; set; }
        public double Threshold { get; set; } = 8;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(300);
        public bool Changed { get; set; }
        public bool AllowMissingCoverage { get; set; }
    }
}
