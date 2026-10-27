using System.Diagnostics;
using System.Text;

namespace SnmpBrowser.Services;

public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Prime righe utili dell'errore, per mostrarle all'utente.</summary>
    public string ErrorSummary()
    {
        var text = string.IsNullOrWhiteSpace(Error) ? Output : Error;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? $"git ha restituito il codice {ExitCode}" : string.Join(" ", lines.Take(3));
    }
}

/// <summary>Esegue git (MinGit incluso nell'app, altrimenti quello installato) senza mai lanciare eccezioni.</summary>
public static class GitService
{
    private static string? _gitPath;
    private static bool _searched;

    public static string? GitPath
    {
        get
        {
            if (!_searched)
            {
                _gitPath = FindGit();
                _searched = true;
            }
            return _gitPath;
        }
    }

    public static bool IsBundled => GitPath is { } p && p.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);

    private static string? FindGit()
    {
        var bundled = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "git", "cmd", "git.exe"),
            Path.Combine(AppContext.BaseDirectory, "git", "mingw64", "bin", "git.exe"),
        };
        foreach (var candidate in bundled)
            if (File.Exists(candidate)) return candidate;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "git.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
            }
        }

        var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe");
        return File.Exists(programFiles) ? programFiles : null;
    }

    public static async Task<GitResult> RunAsync(IEnumerable<string> args, string? workDir = null, int timeoutSeconds = 120)
    {
        var git = GitPath;
        if (git is null)
        {
            return new GitResult(-1, "",
                "Git non trovato: copia MinGit nella cartella 'git' accanto all'app (vedi tools/get-mingit.ps1) oppure installa Git for Windows.");
        }

        try
        {
            var psi = new ProcessStartInfo(git)
            {
                WorkingDirectory = workDir ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            // niente prompt di credenziali e solo https: blocca file://, ssh, ext:: ecc. anche nei submodule
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_ALLOW_PROTOCOL"] = "https";

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new GitResult(-1, "", $"git non ha risposto entro {timeoutSeconds} secondi (controlla la connessione).");
            }

            return new GitResult(process.ExitCode, (await stdout).Trim(), (await stderr).Trim());
        }
        catch (Exception ex)
        {
            return new GitResult(-1, "", ex.Message);
        }
    }
}
