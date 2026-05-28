using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TinyChaos.Gui;

/// <summary>
/// Runs the firmware build/flash subprocess and streams stdout+stderr line
/// by line to the supplied callback.
///
/// The implementation is intentionally minimal: it shells out to <c>make</c>
/// in the firmware directory. The actual build commands live in the
/// firmware Makefile so they stay testable from the terminal and identical
/// across the CLI, the GUI, and CI.
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

    /// <summary>
    /// Run <c>make</c> with the given Makefile target in the firmware
    /// directory. Streams every line of stdout and stderr to
    /// <paramref name="onLine"/>. Returns the exit code; -1 if the process
    /// could not be started.
    ///
    /// Output lines from stderr are prefixed with the literal <c>"! "</c>
    /// so the GUI can colour them red if it wants.
    /// </summary>
    public async Task<int> RunMakeAsync(
        string firmwareDir,
        string target,
        Action<string> onLine,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(firmwareDir))
        {
            onLine($"! firmware directory not found: {firmwareDir}");
            return -1;
        }

        IsRunning = true;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "make",
                WorkingDirectory = firmwareDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrEmpty(target)) psi.ArgumentList.Add(target);

            onLine($"$ make {target} (in {firmwareDir})");

            Process proc;
            try
            {
                proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Process.Start returned null");
            }
            catch (Exception ex)
            {
                onLine($"! could not start make: {ex.Message}");
                onLine("! is `make` on PATH? See firmware/README.md for OS-specific install.");
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

            onLine($"--- make exited with code {proc.ExitCode} ---");
            return proc.ExitCode;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
