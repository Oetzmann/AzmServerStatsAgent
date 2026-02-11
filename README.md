# AZM Server Stats Agent

Windows-Dienst, der auf jedem zu überwachenden Server läuft und alle 30 Sekunden (konfigurierbar) Statistikdaten erhebt und in die zentrale SQL-Datenbank (AZMtool) schreibt.

## Ablauf

- **Alle 30 s:** CPU (%), Arbeitsspeicher (MB), Laufwerke (GB frei/gesamt) per WMI sammeln.
- **Current:** Eine Zeile pro Server in `azm_tool_server_stats_current` wird per MERGE aktualisiert.
- **Lokale Datei:** Jeder 30-Sekunden-Wert wird als NDJSON-Zeile in eine stündliche Datei geschrieben (`samples_yyyy-MM-dd_HH.jsonl`).
- **Jede volle Stunde:** Die abgeschlossene Stundendatei wird gelesen, aggregiert (AVG CPU, AVG RAM, Min/Avg pro Laufwerk) und eine Zeile in `azm_tool_server_stats_history` geschrieben inkl. Rohdaten-JSON. Anschließend wird die Datei gelöscht.

## Voraussetzungen

- Windows Server (oder Windows mit WMI).
- .NET Framework 4.8.
- SQL Server mit Datenbank **SPExpert_AZMTool** (oder konfigurierter Name); Tabellen `azm_tool_server_stats_current` und `azm_tool_server_stats_history` müssen existieren (Skripte liegen im AZMtool-Repo unter `Sql/21_*` und `Sql/22_*`).

## Konfiguration (App.config)

- **ConnectionStrings / StatsDb:** Verbindungszeichenfolge zur AZMtool-Datenbank.
- **appSettings / ServerName:** Servername, wie in AZMtool `serverList` und in der DB erwartet. Leer = `Environment.MachineName`.
- **appSettings / IntervalSeconds:** Intervall in Sekunden (z. B. 30).
- **appSettings / SamplesDirectory:** Ordner für die stündlichen NDJSON-Dateien. Leer = `%ProgramData%\AzmServerStats`.

## Installation (als Windows-Dienst)

1. Projekt bauen (Release).
2. Als Administrator in `bin\Release` wechseln und ausführen:
   ```bat
   %SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe AzmServerStatsAgent.exe
   ```
3. Dienst starten (Dienste-Verwaltung oder `net start AzmServerStatsAgent`).

Deinstallation:

```bat
%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe /u AzmServerStatsAgent.exe
```

## Neues Repo anlegen

Dieser Ordner kann als eigenständiges Repository verwendet werden:

```bat
cd e:\dev\azmtool-stats-agent
git init
git add .
git commit -m "Initial: Windows-Dienst für Server-Stats (30s + stündliche Aggregation)"
```

Anschließend auf GitHub/GitLab etc. ein leeres Repo anlegen und per `git remote add origin ...` und `git push -u origin main` verbinden.
