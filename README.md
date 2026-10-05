# FotoSortierer

Sortiert alle Dateien aus einem Quellordner in Unterordner `yyyy-MM` (z. B. `2023-05`).

## Voraussetzungen
- Visual Studio 2022 (Workload „.NET-Desktopentwicklung“)
- .NET 8 SDK
- Beim ersten Build lädt NuGet automatisch das Paket **MetadataExtractor**.

## Starten
`FotoSortierer.sln` öffnen → F5.

## Datumsermittlung (in dieser Reihenfolge)
1. **EXIF**: DateTimeOriginal → DateTimeDigitized → DateTime (JPEG, HEIC, TIFF, PNG, WebP, RAW wie CR2/NEF/ARW/DNG)
2. **Video-Metadaten**: MP4/MOV (Apple-CreationDate, sonst Movie-Header in UTC → Ortszeit)
3. **Dateiname**: z. B. `IMG_20230514_181500.jpg`, `IMG-20230514-WA0001.jpg`, `2023-05-14 18.15.00.jpg`
4. **Dateisystem**: das frühere von Erstell- und Änderungsdatum

Begleitdateien (`.xmp`, `.aae`, `.thm`, `.pp3`, `.dop`) landen im selben Monat wie ihre Hauptdatei.

## Optionen
- **Kopieren / Verschieben**
- **Unterordner einbeziehen**
- **Testlauf** – zeigt nur an, was passieren würde
- **Leere Quellordner entfernen** (nur beim Verschieben)

## Verhalten bei Konflikten
- Gleicher Name + identischer Inhalt (SHA-256) im Zielordner → übersprungen
- Gleicher Name, anderer Inhalt → wird als `Name (1).jpg` abgelegt
- Liegt der Zielordner im Quellordner, wird er beim Einlesen ignoriert
- Nach jedem echten Lauf wird ein `Sortierprotokoll_….txt` im Zielordner gespeichert
