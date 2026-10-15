using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Crap4CSharp.Core;

// Native SDK resolution runs only in an invocation-owned worker. The verified
// SDK floor uses this path; unsupported layouts retain the independent CLI.
// Its stdout is not the public result channel; failure can print SDK inventory.
internal sealed record SdkIdentityObservation(string Status, int Code, string? Version = null,
    string? Path = null, string? MsBuildVersion = null, string? DotNetRoot = null, string? HostPath = null);

internal static class NativeSdkIdentity
{
    internal static SdkIdentityObservation TryObserve(string directory, string expectedHost, CancellationToken token)
    {
        try { return Observe(directory, expectedHost, token); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or DllNotFoundException)
        { return Unsupported(); }
    }

    internal static SdkIdentityObservation Observe(string directory, string expectedHost, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return Unsupported();
        // Custom command/engine selection retains the independently executed CLI.
        foreach (var name in new[] { "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
            "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER" })
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) return Unsupported();
        var windows = OperatingSystem.IsWindows();
        using var process = Process.GetCurrentProcess();
        var modules = process.Modules.Cast<ProcessModule>().Where(module =>
            System.IO.Path.GetFileName(module.FileName).Equals(windows ? "hostfxr.dll" : "libhostfxr.so",
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (modules.Length != 1) return Unsupported();
        var fxr = modules[0].FileName;
        var root = Directory.GetParent(fxr)?.Parent?.Parent?.Parent?.FullName;
        if (root is null) return Unsupported();
        var host = System.IO.Path.Combine(root, windows ? "dotnet.exe" : "dotnet");
        if (!System.IO.Path.IsPathRooted(expectedHost) || !PathIdentityPolicy.Current.Comparer.Equals(
            System.IO.Path.GetFullPath(expectedHost), host)) return Unsupported();
        if (windows && new DriveInfo(System.IO.Path.GetPathRoot(root)!).DriveType == DriveType.Network) return Unsupported();
        var library = NativeLibrary.Load(fxr);
        var executable = Allocate(root);
        var working = Allocate(System.IO.Path.GetFullPath(directory));
        try
        {
            if (!NativeLibrary.TryGetExport(library, "hostfxr_resolve_sdk2", out var address)) return Unsupported();
            var resolve = Marshal.GetDelegateForFunctionPointer<ResolveSdk>(address);
            string? selected = null;
            Result callback = (key, value) =>
            {
                if (key == 0) selected = windows ? Marshal.PtrToStringUni(value) : Marshal.PtrToStringUTF8(value);
            };
            var code = resolve(executable, working, 0, callback);
            GC.KeepAlive(callback);
            token.ThrowIfCancellationRequested();
            if (code != 0 || string.IsNullOrWhiteSpace(selected)) return new("failed", code);
            // The fast provider is deliberately bounded to the proven SDK floor.
            // Unknown SDK layouts/versions retain the actual CLI, never guesses.
            if (System.IO.Path.GetFileName(selected) != "10.0.103") return Unsupported();
            // SDK commands/Locator may install these standard environment values.
            // Admit only the exact currently selected SDK locations; custom values
            // still use the ordinary CLI, before any consumer targets execute.
            foreach (var pair in new[] { ("MSBUILD_EXE_PATH", "MSBuild.dll"), ("MSBuildSDKsPath", "Sdks") })
            {
                var value = Environment.GetEnvironmentVariable(pair.Item1);
                if (!string.IsNullOrEmpty(value) && !PathIdentityPolicy.Current.Comparer.Equals(
                    System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(value)),
                    System.IO.Path.Combine(selected, pair.Item2))) return Unsupported();
            }
            var manifestPath = System.IO.Path.Combine(selected, ".version");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 4096) return Unsupported();
            var manifest = File.ReadAllLines(manifestPath);
            if (manifest.Length != 5 || manifest[1] != "10.0.103" || manifest[4] != "10.0.100") return Unsupported();
            var cliPath = System.IO.Path.Combine(selected, "dotnet.dll");
            var enginePath = System.IO.Path.Combine(selected, "Microsoft.Build.dll");
            foreach (var name in new[] { "dotnet.dll", "dotnet.deps.json", "dotnet.runtimeconfig.json",
                "MSBuild.dll", "Microsoft.Build.dll", "Microsoft.Build.Framework.dll" })
                if (!File.Exists(System.IO.Path.Combine(selected, name))) return Unsupported();
            // Cross-check the SDK's declared version/build against its actual CLI
            // product metadata; do not derive SDK identity from assembly version.
            var cli = FileVersionInfo.GetVersionInfo(cliPath);
            if (cli.ProductVersion != manifest[3] + "+" + manifest[0] ||
                !manifest[3].StartsWith(manifest[1] + "-", StringComparison.Ordinal)) return Unsupported();
            using var runtime = JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(selected, "dotnet.runtimeconfig.json")));
            if (!SupportsRuntimeConfig(runtime.RootElement, Environment.Version)) return Unsupported();
            var fileVersion = FileVersionInfo.GetVersionInfo(enginePath).FileVersion;
            if (!Version.TryParse(fileVersion, out var engine) || engine.Revision < 0) return Unsupported();
            token.ThrowIfCancellationRequested();
            return new("resolved", 0, manifest[1], selected, engine.ToString(), root, host);
        }
        finally
        {
            if (windows) { Marshal.FreeHGlobal(executable); Marshal.FreeHGlobal(working); }
            else { Marshal.FreeCoTaskMem(executable); Marshal.FreeCoTaskMem(working); }
            NativeLibrary.Free(library);
        }
        IntPtr Allocate(string value) => windows ? Marshal.StringToHGlobalUni(value) : Marshal.StringToCoTaskMemUTF8(value);
    }

    // Unknown but valid JSON shapes are unsupported native layouts, not worker
    // failures. The caller must retain its independently executed CLI fallback.
    internal static bool SupportsRuntimeConfig(JsonElement runtime, Version availableRuntime)
    {
        return runtime.ValueKind == JsonValueKind.Object &&
            runtime.TryGetProperty("runtimeOptions", out var options) && options.ValueKind == JsonValueKind.Object &&
            options.TryGetProperty("framework", out var framework) && framework.ValueKind == JsonValueKind.Object &&
            framework.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
            name.GetString() == "Microsoft.NETCore.App" &&
            framework.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String &&
            Version.TryParse(version.GetString(), out var required) && required.Major == 10 && availableRuntime >= required;
    }

    private static SdkIdentityObservation Unsupported() => new("unsupported", 0);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Result(int key, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ResolveSdk(IntPtr executableDirectory, IntPtr workingDirectory, int flags, Result callback);
}
