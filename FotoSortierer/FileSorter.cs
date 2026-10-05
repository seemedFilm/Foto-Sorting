using System.Globalization;
using System.Security.Cryptography;

namespace FotoSortierer;

public sealed class SortOptions
{
    public required string SourceFolder { get; init; }
    public required string TargetFolder { get; init; }
    public bool Move { get; init; }
    public bool IncludeSubfolders { get; init; } = true;
    public bool DryRun { get; init; }
    public bool DeleteEmptyFolders { get; init; }
}

public sealed record SortProgress(int Processed, int Total, string? LogLine);

public sealed class SortResult
{
    public int Total { get; set; }
    public int Sorted { get; set; }
    public int AlreadyInPlace { get; set; }
    public int Duplicates { get; set; }
    public int Renamed { get; set; }
    public int Errors { get; set; }
    public Dictionary<DateSource, int> BySource { get; } = new();
    public string? LogFile { get; set; }
}

/// <summary>Sortiert Dateien in Unterordner yyyy-MM.</summary>
public static class FileSorter
{
    // Begleitdateien bekommen das Datum ihrer Hauptdatei (IMG_1234.xmp → IMG_1234.CR2)
    private static readonly HashSet<string> SidecarExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".xmp", ".aae", ".thm", ".pp3", ".dop" };

    // System-/Hilfsdateien, die nie angefasst werden
    private static readonly HashSet<string> IgnoredFileNames =
        new(StringComparer.OrdinalIgnoreCase) { "Thumbs.db", "desktop.ini", ".DS_Store" };

    private const string LogFilePrefix = "Sortierprotokoll_";

    public static SortResult Run(SortOptions options, IProgress<SortProgress>? progress, CancellationToken ct)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.SourceFolder));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.TargetFolder));

        if (!System.IO.Directory.Exists(source))
            throw new DirectoryNotFoundException($"Quellordner nicht gefunden: {source}");

        var result = new SortResult();
        var log = new List<string>();
        void Log(string line, int processed, int total)
        {
            log.Add(line);
            progress?.Report(new SortProgress(processed, total, line));
        }

        Log(options.DryRun ? "TESTLAUF – es werden keine Dateien verändert." : (options.Move ? "Modus: Verschieben" : "Modus: Kopieren"), 0, 0);
        Log("Dateien werden gesucht …", 0, 0);

        // Liegt das Ziel innerhalb der Quelle (aber nicht identisch), wird es beim Einlesen übersprungen.
        var excludeTarget = !PathEquals(source, target) && IsSubPathOf(target, source);

        var files = EnumerateFiles(source, options.IncludeSubfolders, excludeTarget ? target : null, ct)
            .Where(f => !IgnoredFileNames.Contains(Path.GetFileName(f)))
            .Where(f => !Path.GetFileName(f).StartsWith(LogFilePrefix, StringComparison.OrdinalIgnoreCase))
            // Hauptdateien zuerst, damit Begleitdateien deren Datum übernehmen können
            .OrderBy(f => IsSidecar(f) ? 1 : 0)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.Total = files.Count;
        Log($"{files.Count} Dateien gefunden.", 0, files.Count);

        var dateByStem = new Dictionary<string, MediaDate>(StringComparer.OrdinalIgnoreCase);
        var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processed = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            processed++;
            var name = Path.GetFileName(file);

            try
            {
                var dir = Path.GetDirectoryName(file)!;
                var stemKey = Path.Combine(dir, Path.GetFileNameWithoutExtension(file));

                MediaDate md;
                if (IsSidecar(file) && dateByStem.TryGetValue(stemKey, out var mainDate))
                {
                    md = mainDate;
                }
                else
                {
                    md = MediaDateReader.GetDate(file);
                    dateByStem.TryAdd(stemKey, md);          // IMG_1234      ← IMG_1234.xmp
                    dateByStem.TryAdd(file, md);             // IMG_1234.CR2  ← IMG_1234.CR2.xmp
                }

                result.BySource[md.Source] = result.BySource.GetValueOrDefault(md.Source) + 1;

                var monthFolder = md.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                var destDir = Path.Combine(target, monthFolder);
                var dest = Path.Combine(destDir, name);

                if (PathEquals(file, dest))
                {
                    result.AlreadyInPlace++;
                    Log($"=  {name} liegt bereits in {monthFolder}", processed, files.Count);
                    continue;
                }

                var renamed = false;
                if (File.Exists(dest) || plannedTargets.Contains(dest))
                {
                    if (File.Exists(dest) && FilesAreEqual(file, dest))
                    {
                        result.Duplicates++;
                        Log($"≡  {name} → {monthFolder}: identische Datei existiert bereits – übersprungen", processed, files.Count);
                        continue;
                    }
                    dest = GetUniquePath(dest, plannedTargets);
                    renamed = true;
                    result.Renamed++;
                }

                plannedTargets.Add(dest);

                if (!options.DryRun)
                {
                    System.IO.Directory.CreateDirectory(destDir);
                    if (options.Move)
                    {
                        File.Move(file, dest, overwrite: false);
                    }
                    else
                    {
                        File.Copy(file, dest, overwrite: false);
                        // Erstelldatum der Kopie an das Original angleichen (Änderungsdatum bleibt bei File.Copy erhalten)
                        try { File.SetCreationTime(dest, File.GetCreationTime(file)); } catch { /* unkritisch */ }
                    }
                }

                result.Sorted++;
                var arrow = options.Move ? "→" : "⇒";
                var suffix = renamed ? $" (umbenannt in {Path.GetFileName(dest)})" : "";
                Log($"{arrow}  {Path.GetRelativePath(source, file)} {arrow} {monthFolder}  [{md.Source}, {md.Date:dd.MM.yyyy}]{suffix}", processed, files.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Errors++;
                Log($"!  FEHLER bei {name}: {ex.Message}", processed, files.Count);
            }
        }

        if (options.Move && options.DeleteEmptyFolders && !options.DryRun && options.IncludeSubfolders)
        {
            var removed = DeleteEmptySubfolders(source, target);
            if (removed > 0)
                Log($"{removed} leere Ordner entfernt.", processed, files.Count);
        }

        Log("", processed, files.Count);
        Log($"Fertig: {result.Sorted} einsortiert, {result.Duplicates} Duplikate übersprungen, " +
            $"{result.AlreadyInPlace} bereits am Ziel, {result.Renamed} umbenannt, {result.Errors} Fehler.", processed, files.Count);
        Log("Datumsquelle: " + string.Join(", ", result.BySource.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")), processed, files.Count);

        if (!options.DryRun && result.Total > 0)
        {
            try
            {
                System.IO.Directory.CreateDirectory(target);
                var logPath = Path.Combine(target, $"{LogFilePrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
                File.WriteAllLines(logPath, log);
                result.LogFile = logPath;
            }
            catch { /* Protokoll ist optional */ }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateFiles(string root, bool recursive, string? excludedFolder, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();

            string[] files;
            try { files = System.IO.Directory.GetFiles(current); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var f in files)
                yield return f;

            if (!recursive)
                continue;

            string[] subDirs;
            try { subDirs = System.IO.Directory.GetDirectories(current); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var d in subDirs)
            {
                if (excludedFolder != null && PathEquals(d, excludedFolder))
                    continue;
                var attr = File.GetAttributes(d);
                if (attr.HasFlag(FileAttributes.System) || attr.HasFlag(FileAttributes.ReparsePoint))
                    continue; // $RECYCLE.BIN, System Volume Information, Junctions
                pending.Push(d);
            }
        }
    }

    private static int DeleteEmptySubfolders(string root, string target)
    {
        var removed = 0;
        string[] dirs;
        try { dirs = System.IO.Directory.GetDirectories(root, "*", SearchOption.AllDirectories); }
        catch { return 0; }

        // tiefste Ordner zuerst
        foreach (var d in dirs.OrderByDescending(d => d.Length))
        {
            if (PathEquals(d, target) || IsSubPathOf(d, target) || IsSubPathOf(target, d))
                continue;
            try
            {
                // Leer = keine Unterordner und höchstens noch Thumbs.db / desktop.ini / .DS_Store
                if (System.IO.Directory.EnumerateDirectories(d).Any())
                    continue;
                var files = System.IO.Directory.GetFiles(d);
                if (files.Any(f => !IgnoredFileNames.Contains(Path.GetFileName(f))))
                    continue;

                foreach (var junk in files)
                {
                    File.SetAttributes(junk, FileAttributes.Normal);
                    File.Delete(junk);
                }
                System.IO.Directory.Delete(d);
                removed++;
            }
            catch { /* Ordner bleibt einfach bestehen */ }
        }
        return removed;
    }

    private static bool IsSidecar(string path) => SidecarExtensions.Contains(Path.GetExtension(path));

    private static string GetUniquePath(string path, HashSet<string> planned)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !planned.Contains(candidate))
                return candidate;
        }
    }

    private static bool FilesAreEqual(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length)
            return false;

        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>true, wenn <paramref name="path"/> innerhalb von <paramref name="folder"/> liegt.</summary>
    private static bool IsSubPathOf(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase) && p.Length > f.Length;
    }
}
