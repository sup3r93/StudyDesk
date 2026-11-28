using System.IO;
using System.Runtime.InteropServices;
using CefSharp;
using CefSharp.Wpf;
using StudyDesk.Desktop.Editor;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Exercises real dependency-check failures without initializing Chromium or using live workspace data.</summary>
public sealed class WindowsCefBootstrapTests
{
    /// <summary>Every missing core, optional non-FXC or locale file still fails before any system-library load.</summary>
    [Theory]
    [InlineData("libcef.dll")]
    [InlineData("chrome_elf.dll")]
    [InlineData("locales/en-US.pak")]
    public void MissingOtherDependencyIsRejected(string missingFile)
    {
        using NativeFiles files = new();
        File.Delete(Path.Combine(files.Directory, missingFile));
        using CefSettings settings = files.CreateSettings();
        int loads = 0;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => WindowsCefBootstrap.Prepare(settings, _ =>
        {
            loads++;
            return nint.Zero;
        }));
        Assert.Equal("A bundled Chromium dependency is missing.", error.Message);
        Assert.Equal(0, loads);
    }

    /// <summary>An app-local compiler is refused even if the operating system could otherwise provide one.</summary>
    [Fact]
    public void AppLocalCompilerIsRejected()
    {
        using NativeFiles files = new();
        File.WriteAllText(Path.Combine(files.Directory, "d3dcompiler_47.dll"), "Synthetic marker; never executable.");
        using CefSettings settings = files.CreateSettings();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => WindowsCefBootstrap.Prepare(settings, _ => nint.Zero));
        Assert.Equal("An app-local optional D3D compiler must not be redistributed.", error.Message);
    }

    /// <summary>The bootstrap fails closed when the absolute system module cannot be loaded.</summary>
    [Fact]
    public void UnavailableSystemCompilerIsRejected()
    {
        using NativeFiles files = new();
        using CefSettings settings = files.CreateSettings();
        string? requestedPath = null;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => WindowsCefBootstrap.Prepare(settings, path =>
        {
            requestedPath = path;
            return nint.Zero;
        }));
        Assert.Equal(Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "d3dcompiler_47.dll")), requestedPath);
        Assert.Equal("The Windows-provided D3D compiler is unavailable.", error.Message);
    }

    /// <summary>Native path verification rejects a valid handle whose actual module is not the requested system FXC.</summary>
    [Fact]
    public void DifferentLoadedModuleIsRejected()
    {
        using NativeFiles files = new();
        using CefSettings settings = files.CreateSettings();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => WindowsCefBootstrap.Prepare(settings,
            _ => NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "kernel32.dll"))));
        Assert.Equal("The loaded D3D compiler is outside the Windows system directory.", error.Message);
    }

    /// <summary>Supplies inert filenames for the real upstream file-existence checker, never for execution.</summary>
    private sealed class NativeFiles : IDisposable
    {
        private readonly string fixtureRoot;

        /// <summary>Creates a uniquely owned temporary native package and its locale marker.</summary>
        internal NativeFiles()
        {
            fixtureRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StudyDesk.Bootstrap.Tests"));
            Directory = Path.Combine(fixtureRoot, Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "locales"));
            IEnumerable<string> names = DependencyChecker.CefDependencies.Concat(DependencyChecker.CefResources)
                .Concat(DependencyChecker.CefOptionalDependencies.Where(name => !name.Equals("d3dcompiler_47.dll", StringComparison.OrdinalIgnoreCase)))
                .Concat(DependencyChecker.BrowserSubprocessDependencies).Append("CefSharp.BrowserSubprocess.exe").Append("locales/en-US.pak");
            foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                File.WriteAllText(Path.Combine(Directory, name), "Synthetic marker; never executable.");
            }
        }

        /// <summary>Gets this test's owned synthetic package directory.</summary>
        internal string Directory { get; }

        /// <summary>Uses the actual settings and upstream normalization with a separate synthetic subprocess directory.</summary>
        internal CefSettings CreateSettings()
        {
            return new CefSettings { BrowserSubprocessPath = Path.Combine(Directory, "CefSharp.BrowserSubprocess.exe") };
        }

        /// <summary>Removes only the validated, uniquely owned test fixture.</summary>
        public void Dispose()
        {
            string absolute = Path.GetFullPath(Directory);
            if (!absolute.StartsWith(fixtureRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The synthetic fixture path escaped its test directory.");
            }
            System.IO.Directory.Delete(absolute, recursive: true);
        }
    }
}
