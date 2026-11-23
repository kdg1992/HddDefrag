# HddDefrag

[![Build](https://github.com/kdg1992/HddDefrag/actions/workflows/build.yml/badge.svg)](https://github.com/kdg1992/HddDefrag/actions/workflows/build.yml)
[![Release-Workflow](https://github.com/kdg1992/HddDefrag/actions/workflows/release.yml/badge.svg)](https://github.com/kdg1992/HddDefrag/actions/workflows/release.yml)
[![Aktuelle Version](https://img.shields.io/github/v/release/kdg1992/HddDefrag?label=Version)](https://github.com/kdg1992/HddDefrag/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/kdg1992/HddDefrag/total?label=Downloads)](https://github.com/kdg1992/HddDefrag/releases)
[![Lizenz](https://img.shields.io/github/license/kdg1992/HddDefrag?label=Lizenz)](LICENSE)
![Plattform](https://img.shields.io/badge/Plattform-Windows%2010%20%7C%2011%20x64-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![Sprache](https://img.shields.io/github/languages/top/kdg1992/HddDefrag?label=Sprache)

Grafisches Defragmentierungs-Tool für Windows 11 im Stil des Windows-98-Defrag, mit Clusterkarte. Defragmentiert werden ausschließlich rotierende Festplatten (HDD). SSDs und NVMe-Laufwerke werden angezeigt, aber nie defragmentiert.

> **Benutzung auf eigene Gefahr.** HddDefrag verschiebt Dateidaten direkt auf dem Datenträger. Vor dem ersten Einsatz auf einem Laufwerk gehört eine aktuelle Sicherung dazu. Das Programm wird ohne jede Gewährleistung bereitgestellt, siehe [Lizenz](#lizenz).

## Voraussetzungen

- Windows 10 oder 11, 64 Bit
- Zum Bauen: .NET 10 SDK
- Zum Ausführen: .NET 10 Desktop Runtime
- Administratorrechte zur Laufzeit (für den Echtmodus, siehe unten)

## Build

### Mit JetBrains Rider

`HddDefrag.sln` öffnen. Die Startkonfigurationen liegen in `.run/`:

- „HddDefrag (Admin)“: Echtmodus auf den Laufwerken
- „HddDefrag (Demo, ohne Admin)“: Demo-Modus mit simulierten Laufwerken

Hinweis: Zum Debuggen des Echtmodus muss Rider selbst als Administrator laufen. Sonst startet sich das Programm ohne Debugger mit Adminrechten neu. Der Neustart erzeugt einen neuen Prozess, an den der Debugger nicht angehängt ist.

### Per Kommandozeile

```
dotnet build HddDefrag.sln -c Release
dotnet publish HddDefrag.csproj -c Release -r win-x64 --self-contained false -o publish
```

## Start

```
bin\Release\net10.0-windows\HddDefrag.exe
```

Das Manifest verwendet `asInvoker`. Ohne Adminrechte fordert das Programm beim Start per UAC eine Rechteanhebung an und startet sich danach selbst neu. Wird die Abfrage abgelehnt, zeigt das Programm eine Fehlermeldung und beendet sich.

Demo-Modus:

```
HddDefrag.exe --demo
```

Der Demo-Modus verwendet simulierte Laufwerke und benötigt keine Adminrechte. Mit `--demo-autorun` startet zusätzlich die Analyse des ersten erlaubten Laufwerks sofort, z. B. `HddDefrag.exe --demo --demo-autorun`.

## Bedienung

1. Laufwerk in der Liste wählen. Gesperrte Laufwerke sind ausgegraut; der Grund steht in der Statusspalte.
2. **Analysieren**: liest die Belegungs-Bitmap und prüft alle Dateien. Danach zeigt ein Ergebnisdialog die Kennzahlen der Analyse.
3. **Defragmentieren**: öffnet einen Bestätigungsdialog mit Laufwerk, Modell, Bus und Medientyp. Der Lauf startet erst, wenn die Checkbox „Ich habe verstanden, dass Dateien auf diesem Laufwerk physisch verschoben werden“ aktiviert ist.
4. **Pause** und **Fortsetzen** halten den Lauf an bzw. setzen ihn fort. **Abbrechen** beendet ihn.
5. Das Protokoll im Hauptfenster zeigt die Schritte des Laufs.
6. **Vollbild** (Schaltfläche oder Taste F11) zeigt die Clusterkarte bildschirmfüllend wie die Detailansicht des Windows-98-Defrag; Esc oder F11 beendet den Vollbildmodus. Ein laufender Vorgang läuft dabei weiter.

### Legende der Clusterkarte

| Farbe | Bedeutung |
|---|---|
| Weiß | Frei |
| Blau | Belegt |
| Rot | Fragmentiert |
| Grün | Systemdateien ($MFT und MFT-Zone) |
| Dunkelgrau | Nicht verschiebbar |
| Gelb | Wird gerade verschoben |

## Sicherheitsregeln

- Der Medientyp wird über `IOCTL_STORAGE_QUERY_PROPERTY` mit `StorageDeviceSeekPenaltyProperty` ermittelt. Wählbar sind nur Laufwerke mit Seek-Penalty (rotierende Festplatten).
- Ist der Medientyp unbekannt, bleibt das Laufwerk gesperrt.
- Nur NTFS-Laufwerke werden unterstützt.
- Nur feste Datenträger (`DRIVE_FIXED`) sind zulässig. Wechselmedien, Netzlaufwerke und CD-Laufwerke sind gesperrt.
- Nie verschoben werden:
  - NTFS-Metadateien mit `$`-Präfix im Wurzelverzeichnis (`$MFT`, `$LogFile`, `$Bitmap` usw.)
  - `pagefile.sys`, `hiberfil.sys`, `swapfile.sys`, `DumpStack.log.tmp`, `bootmgr`, `BOOTNXT`
  - Inhalte von `System Volume Information`, `$RECYCLE.BIN`, `Recovery`, `Boot`, `EFI`, `Config.Msi`
- Reparse-Points (Symlinks, Junctions) werden beim Scan übersprungen.
- Die MFT-Zone wird nie als Ziel verwendet.
- Vor dem Verschieben wird der Zielbereich in der Bitmap geprüft. Zusätzlich prüft NTFS selbst. `FSCTL_MOVE_FILE` ist pro Aufruf atomar; bei einem Stromausfall bleibt die Datei konsistent.
- Vor jedem Lauf zeigt der Bestätigungsdialog Laufwerk, Modell, Bus und Medientyp.
- Empfehlung: vor dem Defragmentieren ein Backup anlegen.

## Technik

- C#, .NET 10, WPF
- Win32-APIs: `FSCTL_GET_NTFS_VOLUME_DATA`, `FSCTL_GET_VOLUME_BITMAP`, `FSCTL_GET_RETRIEVAL_POINTERS`, `FSCTL_MOVE_FILE`, `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`
- Verschoben wird in Stücken von 16 MB (4096 Cluster à 4 KiB). Die kleinsten fragmentierten Dateien werden zuerst behandelt. Nach jeder Verschiebung werden die Extents erneut gelesen und geprüft.

Projektstruktur:

- `Core/`: Schnittstellen, Datenmodelle, Sicherheitsregeln
- `Native/`: P/Invoke-Aufrufe (Volume-Zugriff, Laufwerksermittlung)
- `Engine/`: Scan, Analyse, Defragmentierung, Kartenmodell
- `UI/`: WPF-Oberfläche im Win98-Stil, Demo-Engine (`UI/Demo/`)

## Release

Ein Tag der Form `v1.2.3` stößt den Workflow `.github/workflows/release.yml` an. Er baut das Programm mit dem .NET 10 SDK für Windows x64 und erstellt ein Release mit den Anhängen `HddDefrag-vX-win-x64.zip` (braucht die .NET 10 Desktop Runtime) und `HddDefrag-vX-win-x64-eigenstaendig.zip` (bringt .NET mit). Die Versionsnummer der EXE kommt aus dem Tag.

```
git tag v0.2.0
git push origin main --tags
```

## Bekannte Grenzen

- Der Defragmentierer führt nur fragmentierte Dateien zusammen. Der freie Speicher wird nicht konsolidiert.
- Dateien mit Sparse- oder komprimierten Bereichen werden übersprungen.
- Die Dateianalyse läuft über einen Verzeichnisdurchlauf. Bei sehr vielen Dateien dauert sie einige Minuten.
- Nur Laufwerke mit Buchstaben werden unterstützt.

## Lizenz

Copyright (C) 2026 kdg1992

HddDefrag ist freie Software unter der [GNU General Public License](LICENSE), Version 3 oder (nach Ihrer Wahl) jeder späteren Version. Das Programm wird in der Hoffnung verbreitet, dass es nützlich ist, aber OHNE JEDE GEWÄHRLEISTUNG; auch ohne die implizite Gewährleistung der MARKTFÄHIGKEIT oder EIGNUNG FÜR EINEN BESTIMMTEN ZWECK.
