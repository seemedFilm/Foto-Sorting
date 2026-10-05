using System.Globalization;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using MdDirectory = MetadataExtractor.Directory;

namespace FotoSortierer;

/// <summary>Woher das ermittelte Datum stammt.</summary>
public enum DateSource
{
    Exif,
    VideoMetadaten,
    Dateiname,
    Dateisystem
}

public readonly record struct MediaDate(DateTime Date, DateSource Source);

/// <summary>
/// Ermittelt das "Aufnahmedatum" einer Datei. Reihenfolge:
/// 1. EXIF (DateTimeOriginal → DateTimeDigitized → DateTime) – JPEG, HEIC, TIFF, RAW (CR2, NEF, ARW, DNG …), PNG, WebP
/// 2. Video-Metadaten (MP4/MOV: Apple CreationDate, sonst Movie-Header)
/// 3. Datum im Dateinamen (z. B. IMG_20230514_181500.jpg, IMG-20230514-WA0001.jpg, 2023-05-14 18.15.00.jpg)
/// 4. Dateisystem (das frühere von Erstell- und Änderungsdatum)
/// </summary>
public static class MediaDateReader
{
    private static readonly DateTime MinValid = new(1971, 1, 1);
    private static DateTime MaxValid => DateTime.Now.AddDays(2);

    // Jahr 19xx/20xx, Monat, Tag – optional getrennt durch - _ . ; nicht Teil einer längeren Ziffernfolge
    private static readonly Regex FileNameDateRegex = new(
        @"(?<!\d)(?<y>(?:19|20)\d{2})[-_.]?(?<m>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static MediaDate GetDate(string path)
    {
        var fromMeta = TryReadMetadata(path);
        if (fromMeta is not null)
            return fromMeta.Value;

        if (TryParseFileName(Path.GetFileNameWithoutExtension(path), out var fromName))
            return new MediaDate(fromName, DateSource.Dateiname);

        var fi = new FileInfo(path);
        var fsDate = fi.LastWriteTime < fi.CreationTime ? fi.LastWriteTime : fi.CreationTime;
        return new MediaDate(fsDate, DateSource.Dateisystem);
    }

    private static MediaDate? TryReadMetadata(string path)
    {
        IReadOnlyList<MdDirectory> dirs;
        try
        {
            dirs = ImageMetadataReader.ReadMetadata(path);
        }
        catch
        {
            // Nicht unterstütztes Format, beschädigte Datei, keine Leserechte …
            return null;
        }

        // 1. EXIF
        foreach (var dir in dirs.OfType<ExifSubIfdDirectory>())
        {
            if (TryGet(dir, ExifDirectoryBase.TagDateTimeOriginal, out var d) ||
                TryGet(dir, ExifDirectoryBase.TagDateTimeDigitized, out d))
                return new MediaDate(d, DateSource.Exif);
        }
        foreach (var dir in dirs.OfType<ExifIfd0Directory>())
        {
            if (TryGet(dir, ExifDirectoryBase.TagDateTime, out var d))
                return new MediaDate(d, DateSource.Exif);
        }

        // 2. Video – Apple/Android "com.apple.quicktime.creationdate" (lokale Zeit mit Zeitzone)
        foreach (var dir in dirs.OfType<QuickTimeMetadataHeaderDirectory>())
        {
            if (TryGet(dir, QuickTimeMetadataHeaderDirectory.TagCreationDate, out var d))
                return new MediaDate(d, DateSource.VideoMetadaten);
        }
        // Movie-Header (mvhd) – laut Spezifikation UTC
        foreach (var dir in dirs.OfType<QuickTimeMovieHeaderDirectory>())
        {
            if (dir.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var d))
            {
                var local = DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime();
                if (IsPlausible(local))
                    return new MediaDate(local, DateSource.VideoMetadaten);
            }
        }

        return null;
    }

    private static bool TryGet(MdDirectory dir, int tag, out DateTime date)
    {
        if (dir.TryGetDateTime(tag, out date) && IsPlausible(date))
            return true;
        date = default;
        return false;
    }

    private static bool IsPlausible(DateTime d) => d >= MinValid && d <= MaxValid;

    internal static bool TryParseFileName(string name, out DateTime date)
    {
        foreach (Match m in FileNameDateRegex.Matches(name))
        {
            var text = $"{m.Groups["y"].Value}-{m.Groups["m"].Value}-{m.Groups["d"].Value}";
            if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date) && IsPlausible(date))
                return true;
        }
        date = default;
        return false;
    }
}
