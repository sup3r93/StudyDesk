using System.IO;
using System.Runtime.InteropServices;
using CefSharp;
using Microsoft.Win32.SafeHandles;

namespace StudyDesk.Desktop.Editor;

/// <summary>Preserves every CEF dependency check while retaining the Windows-provided optional FXC module.</summary>
internal sealed partial class WindowsCefBootstrap : SafeHandleZeroOrMinusOneIsInvalid
{
    private const string CompilerName = "d3dcompiler_47.dll";

    /// <summary>Owns a verified system module until Chromium has completed shutdown.</summary>
    private WindowsCefBootstrap(nint module) : base(ownsHandle: true)
    {
        SetHandle(module);
    }

    /// <summary>Checks the installed CEF files and loads only the compiler supplied by Windows.</summary>
    internal static WindowsCefBootstrap Prepare(CefSettingsBase settings)
    {
        return Prepare(settings, LoadSystemCompiler);
    }

    /// <summary>Lets native tests reject an unavailable or incorrectly resolved system module without renderer flags.</summary>
    internal static WindowsCefBootstrap Prepare(CefSettingsBase settings, Func<string, nint> systemLoader)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(systemLoader);
        // Mirror DependencyChecker.AssertAllDependenciesPresent's normalization and public checker call.
        string managedDirectory = Path.GetDirectoryName(typeof(DependencyChecker).Assembly.Location)
            ?? throw new InvalidOperationException("The packaged CEF assembly directory is unavailable.");
        string subprocess = settings.BrowserSubprocessPath;
        string nativeDirectory = Uri.TryCreate(subprocess, UriKind.Absolute, out Uri? subprocessUri) && subprocessUri.IsAbsoluteUri
            ? Path.GetDirectoryName(subprocess) ?? throw new InvalidOperationException("The CEF subprocess directory is unavailable.")
            : managedDirectory;
        string locale = string.IsNullOrEmpty(settings.Locale) ? "en-US" : settings.Locale;
        string localesDirectory = string.IsNullOrEmpty(settings.LocalesDirPath) ? Path.Combine(nativeDirectory, "locales") : settings.LocalesDirPath;
        string resourcesDirectory = string.IsNullOrEmpty(settings.ResourcesDirPath) ? nativeDirectory : settings.ResourcesDirPath;
        string appLocalCompiler = Path.GetFullPath(Path.Combine(nativeDirectory, CompilerName));
        if (File.Exists(appLocalCompiler) || File.Exists(Path.Combine(AppContext.BaseDirectory, CompilerName)))
        {
            throw new InvalidOperationException("An app-local optional D3D compiler must not be redistributed.");
        }

        // CefSharp 156.0.300's native initializer passes false for the retired pack-loading setting.
        List<string> missing = DependencyChecker.CheckDependencies(true, false,
            managedDirectory, nativeDirectory, resourcesDirectory, subprocess, Path.Combine(localesDirectory, locale + ".pak"));
        // No basename-only exception: even an identically named file elsewhere must fail the check.
        if (missing.Any(candidate => !string.Equals(Path.GetFullPath(candidate), appLocalCompiler, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A bundled Chromium dependency is missing.");
        }

        string systemCompiler = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, CompilerName));
        nint module = systemLoader(systemCompiler);
        if (module == nint.Zero)
        {
            throw new InvalidOperationException("The Windows-provided D3D compiler is unavailable.");
        }

        WindowsCefBootstrap result = new(module);
        try
        {
            if (!string.Equals(GetLoadedModulePath(module), systemCompiler, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The loaded D3D compiler is outside the Windows system directory.");
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>Uses an absolute system path and a restricted native search policy, never PATH or the current directory.</summary>
    private static nint LoadSystemCompiler(string path)
    {
        return NativeLibrary.TryLoad(path, typeof(WindowsCefBootstrap).Assembly, DllImportSearchPath.System32, out nint module)
            ? module : nint.Zero;
    }

    /// <summary>Checks the operating system's actual module path rather than trusting the requested filename.</summary>
    private static unsafe string GetLoadedModulePath(nint module)
    {
        char[] buffer = new char[32768];
        fixed (char* pointer = buffer)
        {
            uint length = GetModuleFileName(module, pointer, (uint)buffer.Length);
            if (length == 0 || length >= buffer.Length)
            {
                throw new InvalidOperationException("The Windows module path could not be verified.");
            }
            return Path.GetFullPath(new string(buffer, 0, checked((int)length)));
        }
    }

    /// <summary>Releases the system module only after the caller has shut Chromium down.</summary>
    protected override bool ReleaseHandle()
    {
        NativeLibrary.Free(handle);
        return true;
    }

    /// <summary>Reads the loaded module path through the Windows system library exclusively.</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial uint GetModuleFileName(nint module, char* filename, uint size);
}
