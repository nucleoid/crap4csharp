using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Crap4CSharp.Core;

// Same-task input observation. Raw command/unknown option values never enter this
// serializable record. Typed recipe-path DATA is admitted separately below.
internal sealed record CompilerArgumentIdentity(int Ordinal, int Utf8Length, string Sha256);
internal sealed record CompilerScalarOption(string Name, string Type, string Value);
internal sealed record CompilerImageFile(string Role, string Path, long Length, string Sha256);
internal sealed record CompilerImageIdentity(string Role, long Length, string Sha256);
internal sealed record CompilerInvocationObservation(string TaskAssociationSha256, string TaskBinding,
    string CommandBinding, IReadOnlyList<CompilerArgumentIdentity> Arguments,
    IReadOnlyList<CompilerScalarOption> Options, IReadOnlyList<string> UnsupportedReasons)
{
    public IReadOnlyList<CompilerImageIdentity> Images { get; init; } = [];
}

internal sealed class CompilerRecipeObservation(string sdkPath, string? hostPath, string taskKey, string? taskFile)
{
    private readonly List<string> unsupported = [];
    private readonly List<CompilerScalarOption> options = [];
    private readonly HashSet<string> optionNames = new(StringComparer.Ordinal);
    private CompilerArgumentIdentity[]? arguments, commandArguments;
    private int argumentEvents, commandEvents;
    private bool commandPrefix;
    private bool executableRoute;
    internal string CommandRouteDiagnostic { get; private set; } = "diagnostic.compilerRoute.noEvent";
    internal static string? NormalizeCommandRouteDiagnostic(string? value) => value is
        "diagnostic.compilerRoute.selectedHostExec" or "diagnostic.compilerRoute.selectedSdkWindowsApphost" or
        "diagnostic.compilerRoute.selectedSdkUnixApphost" or "diagnostic.compilerRoute.other" or
        "diagnostic.compilerRoute.noEvent" or "diagnostic.compilerRoute.multipleEvents" or
        "diagnostic.compilerRoute.unavailable" or "diagnostic.compilerRoute.hostExecSelectedCscOtherHost" or
        "diagnostic.compilerRoute.hostExecOtherCsc" or "diagnostic.compilerRoute.notSelectedCscLauncher" or
        "diagnostic.compilerRoute.lateEvent" ? value : null;
    internal IReadOnlyList<string> RawArguments { get; private set; } = [];
    private bool closed, late;
    private readonly HashSet<string> emitNames = new(StringComparer.Ordinal);
    internal bool SizeDeclined { get; private set; }
    internal void DeclineSize()
    {
        if (ProvenArgumentMismatch()) Unsupported("recipe.provenCommandArgumentsMismatch");
        SizeDeclined = true; arguments = null; commandArguments = null;
        RawArguments = []; options.Clear(); optionNames.Clear();
        Unsupported("recipe.sizeUnsupported");
    }
    internal static CompilerObservation OmitForSize(CompilerObservation observation)
    {
        var recipe = observation.InvocationRecipe;
        if (recipe is null) return observation;
        return observation with { CommandLineArguments = [], EmitParameters = [], CompilerImages = [],
            InvocationRecipe = recipe with { CommandBinding = "unproven", Arguments = [], Options = [], Images = [],
                UnsupportedReasons = recipe.UnsupportedReasons.Append("recipe.sizeUnsupported").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() } };
    }

    internal void Close()
    {
        if (closed) { late = true; Unsupported("recipe.duplicateTaskTerminalEvent"); }
        closed = true;
    }

    private bool MayObserve()
    {
        if (!closed) return true;
        late = true; Unsupported("recipe.eventAfterTaskTerminal"); return false;
    }

    internal bool BeginEmitParameter(string name)
    {
        if (!MayObserve()) return false;
        if (name is not ("EmbeddedFiles" or "GeneratedFilesOutputPath" or "PdbFile") || !emitNames.Add(name))
        { Unsupported("recipe.duplicateEmitParameter"); return false; }
        return true;
    }

    // Required before optional absence: known malformed task/argv evidence must
    // not disappear merely because optional JSON or artifact capacity is full.
    internal static void RequireKnownIntegrity(CompilerObservation observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (observation.InvocationRecipe is not { } recipe) return;
        static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
        if (!Digest(recipe.TaskAssociationSha256) || recipe.Arguments is null || recipe.Options is null ||
            recipe.Images is null || recipe.UnsupportedReasons is null || recipe.UnsupportedReasons.Count is < 2 or > 16 ||
            !recipe.UnsupportedReasons.Contains("recipe.loadedCompilerAndTaskImageNotAttested", StringComparer.Ordinal) ||
            !recipe.UnsupportedReasons.Contains("recipe.runtimeLoadedImageNotAttested", StringComparer.Ordinal) ||
            recipe.UnsupportedReasons.Count != recipe.UnsupportedReasons.Distinct(StringComparer.Ordinal).Count() ||
            recipe.TaskBinding is not ("selected-sdk-core-target" or "unproven") ||
            recipe.CommandBinding is not ("selected-host-exec-csc-arguments-match" or "selected-sdk-csc-exe-arguments-match" or "selected-sdk-csc-unix-apphost-arguments-match" or "unproven") ||
            recipe.UnsupportedReasons.Any(reason => reason is not ("recipe.loadedCompilerAndTaskImageNotAttested" or
                "recipe.runtimeLoadedImageNotAttested" or "recipe.languageVersionUnsupported" or "recipe.scalarOptionUnsupported" or
                "recipe.scalarOptionAmbiguous" or "recipe.selectedSdkTaskFileUnproven" or "recipe.compilerCommandPrefixUnproven" or
                "recipe.argumentOutputMissing" or "recipe.commandEventMissing" or "recipe.commandArgumentsUnproven" or
                "recipe.sizeUnsupported" or "recipe.compilerImageUnavailable" or "recipe.compilerImagesChanged")) ||
            !SafeOptions(recipe.Options, token) || recipe.Arguments.Count > CompilerRecipeBudget.MaximumItems ||
            recipe.Arguments.Sum(argument => (long)(argument?.Utf8Length ?? 0)) > CompilerRecipeBudget.MaximumCharacters ||
            recipe.Arguments.Where((argument, index) => argument is null || argument.Ordinal != index || argument.Utf8Length <= 0 || !Digest(argument.Sha256)).Any() ||
            (observation.RetainedUnsupportedRecipe is null
                ? !Identities(observation.CommandLineArguments, token).SequenceEqual(recipe.Arguments)
                : observation.CommandLineArguments.Count != 0 || observation.EmitParameters.Count != 0) ||
            recipe.Images.Any(image => image is null || image.Length <= 0 || image.Length > CompilerPhaseSnapshot.MaximumFileBytes || !Digest(image.Sha256)))
            throw new ProjectContextException("compiler.recipeBindingUnproven", "Malformed compiler observation cannot be hidden by optional absence.");
        var bindingUnavailable = recipe.UnsupportedReasons.Any(reason => reason is "recipe.selectedSdkTaskFileUnproven" or
            "recipe.compilerCommandPrefixUnproven" or "recipe.argumentOutputMissing" or "recipe.commandEventMissing" or "recipe.sizeUnsupported");
        if ((recipe.TaskBinding == "unproven") != recipe.UnsupportedReasons.Contains("recipe.selectedSdkTaskFileUnproven", StringComparer.Ordinal) ||
            recipe.CommandBinding != "unproven" && recipe.Arguments.Count == 0 ||
            recipe.CommandBinding == "unproven" && !bindingUnavailable ||
            recipe.CommandBinding != "unproven" && (recipe.TaskBinding == "unproven" || bindingUnavailable ||
                recipe.UnsupportedReasons.Contains("recipe.commandArgumentsUnproven", StringComparer.Ordinal)))
            throw new ProjectContextException("compiler.recipeBindingUnproven", "Contradictory compiler binding cannot be hidden by optional absence.");
        if (recipe.Images.Count != 0)
        {
            if (!HasExpectedImageRoles(recipe) ||
                !observation.CompilerImages.Select(image => new CompilerImageIdentity(image.Role, image.Length, image.Sha256)).SequenceEqual(recipe.Images))
                throw new ProjectContextException("compiler.recipeBindingUnproven", "Malformed compiler image roles cannot be hidden by optional absence.");
        }
    }

    internal static bool HasExpectedImageRoles(CompilerInvocationObservation recipe)
    {
        string[] roles = ["dotnet-host", "csc", "compiler-common", "compiler-csharp", "compiler-deps",
            "compiler-runtimeconfig", "sdk-csc-target", "sdk-csc-task"];
        var withLauncher = recipe.Images.Select(image => image.Role).SequenceEqual(roles.Append("csc-launcher"));
        var ordinary = recipe.Images.Select(image => image.Role).SequenceEqual(roles);
        return (OperatingSystem.IsWindows() ? withLauncher : OperatingSystem.IsLinux() ? ordinary || withLauncher : ordinary) &&
            (recipe.CommandBinding != "selected-sdk-csc-unix-apphost-arguments-match" || OperatingSystem.IsLinux() && withLauncher);
    }

    internal static CompilerObservation WithoutOptionalRecipe(CompilerObservation observation, CancellationToken token)
    {
        RequireKnownIntegrity(observation, token);
        return observation with { InvocationRecipe = null, CommandLineArguments = [], EmitParameters = [],
            CompilerImages = [], RetainedUnsupportedRecipe = null };
    }

    internal void Arguments(IReadOnlyList<string> items, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!MayObserve()) return;
        if (++argumentEvents != 1) { Unsupported("recipe.duplicateArgumentOutput"); return; }
        if (SizeDeclined || !TryIdentities(items, token, out arguments)) { DeclineSize(); return; }
        RawArguments = items.ToArray();
    }

    internal void Command(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!MayObserve()) { CommandRouteDiagnostic = "diagnostic.compilerRoute.lateEvent"; return; }
        if (++commandEvents != 1) { CommandRouteDiagnostic = "diagnostic.compilerRoute.multipleEvents"; Unsupported("recipe.duplicateCommandEvent"); return; }
        if (SizeDeclined || text.Length > CompilerRecipeBudget.MaximumCharacters) { CommandRouteDiagnostic = "diagnostic.compilerRoute.unavailable"; DeclineSize(); return; }
        // Lexing only. Unlike CSharpCommandLineParser.Parse, this never reads a
        // response/ruleset file or resolves a source/reference from the command.
        var items = CommandLineParser.SplitCommandLineIntoArguments(text, removeHashComments: false).Take(32769).ToArray();
        if (items.Length > 32768) { CommandRouteDiagnostic = "diagnostic.compilerRoute.unavailable"; DeclineSize(); return; }
        commandPrefix = items.Length >= 3 && hostPath is not null && EqualPath(items[0], hostPath) &&
            items[1] == "exec" && EqualPath(items[2], Path.Combine(sdkPath, "Roslyn", "bincore", "csc.dll"));
        executableRoute = items.Length >= 1 && (OperatingSystem.IsWindows() &&
            EqualPath(items[0], Path.Combine(sdkPath, "Roslyn", "bincore", "csc.exe")) ||
            OperatingSystem.IsLinux() && EqualPath(items[0], Path.Combine(sdkPath, "Roslyn", "bincore", "csc")));
        // Closed route diagnostics remain DATA; only the exact platform-selected
        // apphost above participates in same-task argument matching.
        CommandRouteDiagnostic = commandPrefix ? "diagnostic.compilerRoute.selectedHostExec"
            : items.Length >= 1 && EqualPath(items[0], Path.Combine(sdkPath, "Roslyn", "bincore", "csc.exe"))
                ? "diagnostic.compilerRoute.selectedSdkWindowsApphost"
                : items.Length >= 1 && EqualPath(items[0], Path.Combine(sdkPath, "Roslyn", "bincore", "csc"))
                    ? "diagnostic.compilerRoute.selectedSdkUnixApphost"
                    : items.Length >= 3 && items[1] == "exec" && (ToolLeaf(items[0], "dotnet") || ToolLeaf(items[0], "dotnet.exe")) &&
                        EqualPath(items[2], Path.Combine(sdkPath, "Roslyn", "bincore", "csc.dll"))
                        ? "diagnostic.compilerRoute.hostExecSelectedCscOtherHost"
                        : items.Length >= 3 && items[1] == "exec" && (ToolLeaf(items[0], "dotnet") || ToolLeaf(items[0], "dotnet.exe")) && ToolLeaf(items[2], "csc.dll")
                            ? "diagnostic.compilerRoute.hostExecOtherCsc"
                            : items.Length >= 1 && (ToolLeaf(items[0], "csc") || ToolLeaf(items[0], "csc.exe") || ToolLeaf(items[0], "csc.dll"))
                                ? "diagnostic.compilerRoute.notSelectedCscLauncher" : "diagnostic.compilerRoute.other";
        if (commandPrefix || executableRoute)
        {
            if (!TryIdentities(items.Skip(executableRoute ? 1 : 3).ToArray(), token, out commandArguments))
            { DeclineSize(); return; }
            commandPrefix = true;
        }
        else Unsupported("recipe.compilerCommandPrefixUnproven");
    }

    internal void Option(string name, IReadOnlyList<string> values, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!MayObserve()) return;
        if (SizeDeclined || !ProjectsOption(name)) return;
        if (!optionNames.Add(name) || values.Count != 1) { Unsupported("recipe.scalarOptionAmbiguous"); return; }
        var value = values[0];
        if (BoolOptions.Contains(name) && bool.TryParse(value, out var boolean))
        { options.Add(new(name, "boolean", boolean ? "true" : "false")); return; }
        if (name == "LangVersion")
        {
            // This tool's Roslyn5.0 parser does not define the selected target
            // compiler's accepted language versions. Keep the known scalar DATA.
            if (SupportedLanguage(value)) options.Add(new(name, "enum", value.ToLowerInvariant()));
            else Unsupported("recipe.languageVersionUnsupported");
            return;
        }
        if (EnumOptions.TryGetValue(name, out var allowed))
        {
            var canonical = allowed.SingleOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null) { options.Add(new(name, "enum", canonical)); return; }
        }
        else if ((name is "FileAlignment" or "WarningLevel" or "CodePage") &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 65536)
        { options.Add(new(name, "integer", number.ToString(CultureInfo.InvariantCulture))); return; }
        Unsupported("recipe.scalarOptionUnsupported"); // Never retain the unknown value.
    }

    internal static bool ProjectsOption(string name) => BoolOptions.Contains(name) || EnumOptions.ContainsKey(name) ||
        name is "FileAlignment" or "WarningLevel" or "CodePage" or "LangVersion";

    internal static bool SupportedLanguage(string value) => value.ToLowerInvariant() is
        "default" or "latest" or "latestmajor" or "preview" or "1" or "1.0" or "2" or "2.0" or "3" or "3.0" or "4" or "4.0" or
        "5" or "5.0" or "6" or "6.0" or "7" or "7.0" or "7.1" or "7.2" or "7.3" or "8" or "8.0" or "9" or "9.0" or
        "10" or "10.0" or "11" or "11.0" or "12" or "12.0" or "13" or "13.0" or "14" or "14.0";

    internal static bool SafeOptions(IReadOnlyList<CompilerScalarOption> values, CancellationToken token)
    {
        if (values.Count > BoolOptions.Count + EnumOptions.Count + 4) return false;
        var check = new CompilerRecipeObservation("", null, "null", null);
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            if (value is null || value.Name is null || value.Type is null || value.Value is null || !ProjectsOption(value.Name)) return false;
            check.Option(value.Name, [value.Value], token);
        }
        return check.unsupported.Count == 0 && check.options.SequenceEqual(values);
    }

    internal CompilerInvocationObservation Finish()
    {
        var taskBound = taskKey != "null" && taskFile is not null && EqualPath(taskFile, Path.Combine(sdkPath, "Roslyn", "Microsoft.CSharp.Core.targets"));
        if (ProvenArgumentMismatch()) Unsupported("recipe.provenCommandArgumentsMismatch");
        if (!taskBound) Unsupported("recipe.selectedSdkTaskFileUnproven");
        if (argumentEvents != 1 || arguments is null) Unsupported("recipe.argumentOutputMissing");
        if (commandEvents != 1 || commandArguments is null) Unsupported("recipe.commandEventMissing");
        if (!closed) Unsupported("recipe.taskTerminalEventMissing");
        var matched = taskBound && closed && !late && argumentEvents == 1 && commandEvents == 1 && commandPrefix && arguments is not null && commandArguments is not null && arguments.SequenceEqual(commandArguments);
        if (!matched) Unsupported("recipe.commandArgumentsUnproven");
        // These are concrete absent protocol roles, not inferred defaults. This
        // observation never changes manifest reuse/coverage applicability policy.
        Unsupported("recipe.loadedCompilerAndTaskImageNotAttested");
        Unsupported("recipe.runtimeLoadedImageNotAttested");
        return new(Hash(taskKey), taskBound ? "selected-sdk-core-target" : "unproven",
            matched ? executableRoute ? OperatingSystem.IsLinux() ? "selected-sdk-csc-unix-apphost-arguments-match" :
                "selected-sdk-csc-exe-arguments-match" : "selected-host-exec-csc-arguments-match" : "unproven",
            Array.AsReadOnly(arguments ?? []), Array.AsReadOnly(options.ToArray()),
            Array.AsReadOnly(unsupported.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
    }

    private static bool TryIdentities(IReadOnlyList<string> values, CancellationToken token, out CompilerArgumentIdentity[]? result)
    {
        result = null;
        if (values.Count > CompilerRecipeBudget.MaximumItems) return false;
        long length = 0;
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            if (value is null || (length += Encoding.UTF8.GetByteCount(value)) > CompilerRecipeBudget.MaximumCharacters) return false;
        }
        result = Identities(values, token); return true;
    }

    internal static CompilerArgumentIdentity[] Identities(IReadOnlyList<string> values, CancellationToken token)
    {
        if (values.Count > 32768) throw Limit();
        var result = new CompilerArgumentIdentity[values.Count]; long bytes = 0;
        for (var i = 0; i < values.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var raw = Encoding.UTF8.GetBytes(values[i]);
            if ((bytes += raw.Length) > 2 * 1024 * 1024) throw Limit();
            result[i] = new(i, raw.Length, Convert.ToHexStringLower(SHA256.HashData(raw)));
        }
        return result;
    }

    private void Unsupported(string reason) => unsupported.Add(reason);
    private bool ProvenArgumentMismatch() => taskKey != "null" && taskFile is not null &&
        EqualPath(taskFile, Path.Combine(sdkPath, "Roslyn", "Microsoft.CSharp.Core.targets")) &&
        !late && argumentEvents == 1 && commandEvents == 1 && commandPrefix && arguments is not null && commandArguments is not null &&
        !arguments.SequenceEqual(commandArguments);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool ToolLeaf(string value, string expected)
    {
        try { return PathIdentityPolicy.Current.Comparer.Equals(Path.GetFileName(value), expected); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static bool EqualPath(string value, string expected)
    {
        try { return Path.IsPathFullyQualified(value) && PathIdentityPolicy.Current.Comparer.Equals(Path.GetFullPath(value), Path.GetFullPath(expected)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static ProjectContextException Limit() => new("compiler.evidenceLimit", "Compiler recipe observation exceeds the existing finite command/input bound.");
    private static readonly HashSet<string> BoolOptions = new(StringComparer.Ordinal)
    {
        "AllowUnsafeBlocks", "CheckForOverflowUnderflow", "Optimize", "Deterministic", "EmitDebugInformation", "HighEntropyVA",
        "Prefer32Bit", "PublicSign", "DelaySign", "RefOnly", "NoWin32Manifest", "EmbedAllSources", "UseSharedCompilation",
        "UseHostCompilerIfAvailable", "SkipCompilerExecution", "ProvideCommandLineArgs", "NoConfig", "NoStandardLib", "NoLogo", "Utf8Output", "GenerateFullPaths"
    };
    private static readonly Dictionary<string, string[]> EnumOptions = new(StringComparer.Ordinal)
    {
        ["DebugType"] = ["none", "portable", "embedded", "full", "pdbonly"],
        ["Nullable"] = ["enable", "disable", "warnings", "annotations"],
        ["TargetType"] = ["library", "exe", "winexe", "module", "appcontainerexe", "winmdobj"],
        ["Platform"] = ["AnyCPU", "AnyCPU32BitPreferred", "x86", "x64", "ARM", "ARM64", "Itanium"],
        ["ChecksumAlgorithm"] = ["SHA1", "SHA256"]
    };
}
