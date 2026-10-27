using System.IO.Compression;
using SnmpBrowser.Models;

namespace SnmpBrowser.Plugins;

/// <summary>
/// Plugin installati da una cartella o da un file .zip (nessuna rete necessaria).
/// Vengono copiati in %AppData%\SnmpBrowser\local-plugins\&lt;id&gt; e seguono le stesse regole di approvazione degli altri.
/// </summary>
public static class LocalPluginStore
{
    private const string KeyPrefix = "plugin-local:";
    private const long MaxBytes = 5_000_000;
    private const int MaxFiles = 300;

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnmpBrowser", "local-plugins");

    private static IEnumerable<string> InstalledDirs() =>
        Directory.Exists(Root)
            ? Directory.GetDirectories(Root).Where(d => !Path.GetFileName(d).StartsWith('.'))
            : Enumerable.Empty<string>();

    public static void LoadInstalled()
    {
        foreach (var dir in InstalledDirs())
            PluginManager.Instance.Load(KeyPrefix + Path.GetFileName(dir), dir);
    }

    /// <summary>Righe da mostrare nella tabella dei plugin, accanto a quelle dei repository.</summary>
    public static List<MibPackageItem> ListItems() =>
        InstalledDirs()
            .Select(dir =>
            {
                var id = Path.GetFileName(dir);
                var key = KeyPrefix + id;
                var name = PluginManager.Instance.Find(key)?.DisplayName ?? id;
                return new MibPackageItem(name, dir, "") { ActivationKey = key, IsLocal = true, InstalledVersion = "locale" };
            })
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string? Remove(MibPackageItem item)
    {
        if (item.ActivationKey is null) return "plugin non valido";
        try
        {
            PluginManager.Instance.Unload(item.ActivationKey);
            if (Directory.Exists(item.Path)) Directory.Delete(item.Path, recursive: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static Task<string?> ImportFolderAsync(string folder) => Task.Run(() => Import(stage => CopyFolder(folder, stage)));

    public static Task<string?> ImportZipAsync(string zipFile) => Task.Run(() => Import(stage => ExtractZip(zipFile, stage)));

    /// <summary>Prepara i file in una cartella temporanea, li verifica e solo allora li installa. Restituisce l'errore, o null.</summary>
    private static string? Import(Action<string> fill)
    {
        var stage = Path.Combine(Root, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            fill(stage);

            var pluginRoot = FindPluginRoot(stage);
            if (pluginRoot is null) return "nel file/cartella non c'e' nessun plugin.json";

            var probe = PluginInstance.Read("probe", pluginRoot);
            var failed = probe.State == PluginState.Error;
            var error = probe.ErrorText;
            var id = probe.Manifest.Id;
            probe.Dispose();
            if (failed) return $"plugin non valido: {error}";

            var key = KeyPrefix + id;
            var target = Path.Combine(Root, id);

            PluginManager.Instance.Unload(key);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(pluginRoot, target);

            PluginManager.Instance.Load(key, target);
            var loaded = PluginManager.Instance.Find(key);
            return loaded is { State: PluginState.Error } ? $"installato ma non utilizzabile: {loaded.ErrorText}" : null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            }
            catch
            {
            }
        }
    }

    /// <summary>plugin.json puo' stare nella radice oppure in un'unica cartella (tipico di uno zip scaricato da GitHub).</summary>
    private static string? FindPluginRoot(string stage)
    {
        if (File.Exists(Path.Combine(stage, "plugin.json"))) return stage;

        var dirs = Directory.GetDirectories(stage);
        var files = Directory.GetFiles(stage);
        if (dirs.Length == 1 && files.Length == 0 && File.Exists(Path.Combine(dirs[0], "plugin.json"))) return dirs[0];
        return null;
    }

    private static void CopyFolder(string source, string stage)
    {
        var root = Path.GetFullPath(source);
        long total = 0;
        var count = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            var norm = rel.Replace('\\', '/');
            if (norm == ".git" || norm.StartsWith(".git/", StringComparison.Ordinal)) continue;

            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

            total += info.Length;
            if (total > MaxBytes || ++count > MaxFiles) throw new InvalidDataException("plugin troppo grande");

            var dest = Path.Combine(stage, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
    }

    private static void ExtractZip(string zipFile, string stage)
    {
        var stageRoot = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
        long total = 0;
        var count = 0;

        using var zip = ZipFile.OpenRead(zipFile);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.Name.Length == 0) continue; // cartella

            var norm = entry.FullName.Replace('\\', '/');
            if (norm.Split('/').Contains(".git")) continue;

            var dest = Path.GetFullPath(Path.Combine(stage, norm));
            if (!dest.StartsWith(stageRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("lo zip contiene percorsi non validi");

            if (++count > MaxFiles) throw new InvalidDataException("plugin troppo grande");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var input = entry.Open();
            using var output = File.Create(dest);

            // la dimensione dichiarata nello zip puo' mentire: conto i byte realmente scritti
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxBytes) throw new InvalidDataException("plugin troppo grande");
                output.Write(buffer, 0, read);
            }
        }
    }
}
