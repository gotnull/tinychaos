using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TinyChaos.Gui;

/// <summary>
/// Runs the firmware build/flash subprocess and streams stdout+stderr line by
/// line to the supplied callback.
///
/// Build and flash drive the committed CubeMX/CMake project under
/// <c>firmware/nucleo-h753zi/</c> through its <c>flash.sh</c> (macOS/Linux) or
/// <c>flash.ps1</c> (Windows) wrapper - the exact scripts you run by hand, so
/// the GUI, the terminal, and CI stay identical. The on-host protocol self-test
/// still uses the firmware <c>Makefile</c>'s <c>test</c> target (it needs no
/// STM32 toolchain).
/// </summary>
public sealed class BuildFlashService
{
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Find the firmware directory. Order:
    /// 1. Environment variable <c>TINYCHAOS_FIRMWARE</c>.
    /// 2. Walk up from the executable looking for a <c>firmware</c>
    ///    directory next to a <c>.git</c> entry.
    /// 3. Return null if nothing matched.
    /// </summary>
    public static string? FindFirmwareDirectory()
    {
        var env = Environment.GetEnvironmentVariable("TINYCHAOS_FIRMWARE");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            var firmware = Path.Combine(dir, "firmware");
            var gitEntry = Path.Combine(dir, ".git");
            if (Directory.Exists(firmware) && (Directory.Exists(gitEntry) || File.Exists(gitEntry)))
            {
                return firmware;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>The committed STM32 project directory: <c>firmware/nucleo-h753zi</c>.</summary>
    public static string NucleoProjectDirectory(string firmwareDir)
        => Path.Combine(firmwareDir, "nucleo-h753zi");

    /// <summary>
    /// Build and/or flash the <c>nucleo-h753zi</c> project via its flash script.
    /// <paramref name="scriptArg"/> is the script subcommand: <c>"build"</c>,
    /// <c>"flash"</c>, or <c>"clean"</c>. Picks <c>flash.ps1</c> on Windows and
    /// <c>flash.sh</c> elsewhere. Returns the exit code; -1 if it could not
    /// start, -2 if cancelled.
    /// </summary>
    public Task<int> RunFlashScriptAsync(
        string firmwareDir,
        string scriptArg,
        Action<string> onLine,
        CancellationToken cancellationToken = default)
    {
        string nucleoDir = NucleoProjectDirectory(firmwareDir);
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string scriptName = windows ? "flash.ps1" : "flash.sh";
        string scriptPath = Path.Combine(nucleoDir, scriptName);

        if (!Directory.Exists(nucleoDir) || !File.Exists(scriptPath))
        {
            onLine($"! firmware project not found: {scriptPath}");
            onLine("! expected the committed CubeMX/CMake project at firmware/nucleo-h753zi/.");
            return Task.FromResult(-1);
        }

        ProcessStartInfo psi;
        if (windows)
        {
            psi = new ProcessStartInfo { FileName = "powershell" };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            if (!string.IsNullOrEmpty(scriptArg)) psi.ArgumentList.Add(scriptArg);
        }
        else
        {
            // /bin/bash avoids depending on the script's executable bit.
            psi = new ProcessStartInfo { FileName = "/bin/bash" };
            psi.ArgumentList.Add(scriptPath);
            if (!string.IsNullOrEmpty(scriptArg)) psi.ArgumentList.Add(scriptArg);
        }
        psi.WorkingDirectory = nucleoDir;

        string prefix = windows ? $".\\{scriptName}" : $"./{scriptName}";
        string hint = "! need the Arm GNU toolchain, CMake, Ninja and st-flash on PATH. "
                    + "See firmware/README.md for the per-OS install.";
        return RunProcessAsync(psi, $"$ {prefix} {scriptArg} (in {nucleoDir})", hint, onLine, cancellationToken);
    }

    /// <summary>
    /// Flash a prebuilt firmware <c>.bin</c> with <c>st-flash</c> - no build, no
    /// compiler toolchain. Lets someone flash a downloaded release binary (e.g.
    /// from GitHub Releases) straight onto the board. Returns the exit code; -1
    /// if it could not start. (Needs only <c>st-flash</c>; if that is missing,
    /// the user can still drag the .bin onto the NODE_H753ZI USB drive.)
    /// </summary>
    public Task<int> RunStFlashAsync(
        string binPath,
        Action<string> onLine,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
        {
            onLine($"! firmware .bin not found: {binPath}");
            return Task.FromResult(-1);
        }
        var psi = new ProcessStartInfo { FileName = "st-flash" };
        psi.ArgumentList.Add("--connect-under-reset");
        psi.ArgumentList.Add("--reset");
        psi.ArgumentList.Add("write");
        psi.ArgumentList.Add(binPath);
        psi.ArgumentList.Add("0x08000000");
        string hint = "! need st-flash on PATH (brew install stlink / choco install stlink) - "
                    + "or just drag the .bin onto the NODE_H753ZI USB drive, no tools needed.";
        return RunProcessAsync(
            psi, $"$ st-flash --reset write \"{Path.GetFileName(binPath)}\" 0x08000000",
            hint, onLine, cancellationToken);
    }

    /// <summary>
    /// Run <c>make</c> with the given Makefile target in the firmware directory.
    /// Used for the on-host protocol self-test (<c>make test</c>), which needs
    /// no STM32 toolchain. Returns the exit code; -1 if it could not start.
    /// </summary>
    public Task<int> RunMakeAsync(
        string firmwareDir,
        string target,
        Action<string> onLine,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(firmwareDir))
        {
            onLine($"! firmware directory not found: {firmwareDir}");
            return Task.FromResult(-1);
        }
        var psi = new ProcessStartInfo { FileName = "make", WorkingDirectory = firmwareDir };
        if (!string.IsNullOrEmpty(target)) psi.ArgumentList.Add(target);
        string hint = "! is `make` on PATH? See firmware/README.md for OS-specific install.";
        return RunProcessAsync(psi, $"$ make {target} (in {firmwareDir})", hint, onLine, cancellationToken);
    }

    /// <summary>
    /// Shared subprocess runner: streams stdout/stderr to <paramref name="onLine"/>
    /// (stderr prefixed with "! " so the GUI can colour it), supports
    /// cancellation, and reports the exit code.
    /// </summary>
    private async Task<int> RunProcessAsync(
        ProcessStartInfo psi,
        string banner,
        string startHint,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        IsRunning = true;
        try
        {
            onLine(banner);

            Process proc;
            try
            {
                proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Process.Start returned null");
            }
            catch (Exception ex)
            {
                onLine($"! could not start {psi.FileName}: {ex.Message}");
                onLine(startHint);
                return -1;
            }

            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine($"! {e.Data}"); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* swallow */ }
                onLine("! cancelled");
                return -2;
            }

            onLine($"--- exited with code {proc.ExitCode} ---");
            return proc.ExitCode;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
