using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PlsqlDevAgent.Agent.Tools;

namespace PlsqlDevAgent.Agent
{
    /// <summary>
    /// Company rules: Markdown files that are added to the system prompt. Loaded from
    ///  1. the "rules" folder next to the plug-in (so a team can ship its rules together with the plug-in) and
    ///  2. the file or folder configured in the settings (e.g. a network share), %VARIABLES% are expanded.
    /// </summary>
    public static class RulesLoader
    {
        public const int MaxTotalChars = 60000;

        public sealed class RuleFile
        {
            public string Path;
            public string Text;
            public string Error;
        }

        public static string PluginRulesDir =>
            Path.Combine(Path.GetDirectoryName(typeof(RulesLoader).Assembly.Location) ?? "", "rules");

        /// <summary>Suggested location for a personal rules file when no path is configured.</summary>
        public static string DefaultRulesFile =>
            Path.Combine(Path.GetDirectoryName(AgentConfig.FilePath) ?? "", "company-rules.md");

        public static string Expand(string path) => Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));

        /// <summary>The file the rules editor opens: the configured file, company-rules.md in a configured folder, or the default.</summary>
        public static string EditableFile(string configuredPath)
        {
            var p = Expand(configuredPath);
            if (string.IsNullOrEmpty(p)) return DefaultRulesFile;
            if (Directory.Exists(p) || p.EndsWith("\\") || p.EndsWith("/")) return Path.Combine(p, "company-rules.md");
            return p;
        }

        public static List<RuleFile> Load(AgentConfig cfg)
        {
            var files = new List<RuleFile>();
            AddFolder(files, PluginRulesDir, optional: true);
            var configured = Expand(cfg.CompanyRulesPath);
            if (!string.IsNullOrEmpty(configured))
            {
                if (Directory.Exists(configured)) AddFolder(files, configured, optional: false);
                else if (File.Exists(configured)) files.Add(Read(configured));
                else files.Add(new RuleFile { Path = configured, Error = "not found" });
            }

            int total = 0;
            foreach (var f in files.Where(f => f.Text != null))
            {
                if (total + f.Text.Length > MaxTotalChars)
                {
                    f.Text = f.Text.Substring(0, Math.Max(0, MaxTotalChars - total));
                    f.Error = $"truncated: the rules may contain at most {MaxTotalChars} characters in total";
                }
                total += f.Text.Length;
            }
            foreach (var f in files.Where(f => f.Error != null)) Util.Log.Warn($"Company rules {f.Path}: {f.Error}");
            return files;
        }

        /// <summary>One line per source for the settings page.</summary>
        public static string Describe(AgentConfig cfg)
        {
            var files = Load(cfg);
            if (files.Count == 0) return "No company rules loaded.";
            return string.Join("\n", files.Select(f =>
                f.Text == null ? $"✖ {f.Path}: {f.Error}" : $"✔ {f.Path} ({f.Text.Length:N0} characters){(f.Error != null ? " – " + f.Error : "")}"));
        }

        private static void AddFolder(List<RuleFile> files, string dir, bool optional)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    if (!optional) files.Add(new RuleFile { Path = dir, Error = "folder not found" });
                    return;
                }
                foreach (var f in Directory.GetFiles(dir, "*.md").OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) files.Add(Read(f));
            }
            catch (Exception ex)
            {
                files.Add(new RuleFile { Path = dir, Error = ex.Message });
            }
        }

        private static RuleFile Read(string path)
        {
            try { return new RuleFile { Path = path, Text = TextFile.Read(path, out _).Trim() }; }
            catch (Exception ex) { return new RuleFile { Path = path, Error = ex.Message }; }
        }

        public static void Save(string path, string text)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        }

        public const string TemplateDe = @"# Firmenregeln für die PL/SQL-Entwicklung

<!-- Vorlage: an das Team anpassen und Nichtzutreffendes löschen.
     Der Agent hält sich bei jedem Code, den er schreibt oder ändert, an diese Regeln. -->

## Namenskonventionen
- Packages: `PKG_<THEMA>` (z. B. `PKG_AUFTRAG`); ein Package pro fachlichem Thema.
- Eigenständige Prozeduren / Funktionen vermeiden; Code gehört in Packages.
- Parameter: `p_` (z. B. `p_auftrag_id`); OUT-Parameter `po_`, IN-OUT-Parameter `pio_`.
- Lokale Variablen: `l_`; Package-Variablen (Body): `g_`; Konstanten: `c_` bzw. `gc_` für Package-Konstanten.
- Cursor: `cur_`; Records: `r_`; Collection-Typen: `t_<name>_tab`; Record-Typen: `t_<name>_rec`; Exceptions: `e_`.
- Tabellen: Singular, Großbuchstaben mit Unterstrichen (`AUFTRAG_POSITION`); Primärschlüssel `<TABELLE>_PK`,
  Fremdschlüssel `<TABELLE>_<REF_TABELLE>_FK`, Unique Keys `<TABELLE>_<SPALTEN>_UK`, Indizes `<TABELLE>_<SPALTEN>_IDX`,
  Sequenzen `<TABELLE>_SEQ`, Trigger `<TABELLE>_<B|A><I|U|D>_TRG`.

## Formatierung
- Schlüsselwörter und Bezeichner in Kleinbuchstaben; 2 Leerzeichen Einrückung; max. 120 Zeichen pro Zeile.
- Eine Anweisung pro Zeile; `end <name>;` für jede Programmeinheit.
- Kommentare auf Deutsch.

## Programmierregeln
- Jedes Package, jede Prozedur und Funktion hat einen Kopfkommentar: Zweck, Parameter, Rückgabewert, ausgelöste Exceptions.
- Typen mit `%TYPE` / `%ROWTYPE` verankern; keine fest codierten Schemanamen.
- Bind-Variablen verwenden; dynamisches SQL nur wenn unvermeidbar und mit `DBMS_ASSERT` für Bezeichner.
- Kein `SELECT *` in produktivem Code; Spalten immer aufzählen.
- Große Datenmengen mengenorientiert oder mit `BULK COLLECT ... LIMIT` und `FORALL` verarbeiten.
- Niemals `WHEN OTHERS THEN NULL`. Unerwartete Fehler mit unserem Logging-Package (`PKG_LOG.error`) protokollieren und weiterwerfen.
- Kein `COMMIT` / `ROLLBACK` in fachlichen Packages: Die Transaktion steuert der Aufrufer (Ausnahme: autonomes Logging).
- Fachliche Fehler mit `raise_application_error(-20xxx, ...)` auslösen, Nummern aus `PKG_FEHLER`.

## Datenbankänderungen
- Jede neue Tabelle hat die Audit-Spalten `ERSTELLT_VON`, `ERSTELLT_AM`, `GEAENDERT_VON`, `GEAENDERT_AM`.
- DDL für TEST und PROD nur über Deployment-Skripte aus der Versionsverwaltung, nie direkt in der Datenbank.
";

        public const string Template = @"# Company rules for PL/SQL development

<!-- Template: adapt it to your team and delete what does not apply.
     The agent follows these rules for all code it writes or changes. -->

## Naming conventions
- Packages: `PKG_<SUBJECT>` (e.g. `PKG_ORDERS`); one package per business subject.
- Standalone procedures / functions: avoid them; put code into packages.
- Parameters: `p_` (e.g. `p_order_id`); OUT parameters `po_`, IN OUT parameters `pio_`.
- Local variables: `l_`; package variables (body): `g_`; constants: `c_` / `gc_` for package constants.
- Cursors: `cur_`; records: `r_`; collection types: `t_<name>_tab`; record types: `t_<name>_rec`; exceptions: `e_`.
- Tables: singular, upper case with underscores (`ORDER_ITEM`); primary key `<TABLE>_PK`, foreign keys
  `<TABLE>_<REF_TABLE>_FK`, unique keys `<TABLE>_<COLUMNS>_UK`, indexes `<TABLE>_<COLUMNS>_IDX`,
  sequences `<TABLE>_SEQ`, triggers `<TABLE>_<B|A><I|U|D>_TRG`.

## Formatting
- Keywords in lower case, identifiers in lower case; 2 spaces indentation; max. 120 characters per line.
- One statement per line; `end <name>;` for every program unit.
- Comments in English.

## Coding rules
- Every package, procedure and function has a header comment: purpose, parameters, return value, raised exceptions.
- Anchor types with `%TYPE` / `%ROWTYPE`; no hard-coded schema names.
- Use bind variables; dynamic SQL only when unavoidable and with `DBMS_ASSERT` for identifiers.
- No `SELECT *` in production code; always list the columns.
- Process large data sets set-based or with `BULK COLLECT ... LIMIT` and `FORALL`.
- Never `WHEN OTHERS THEN NULL`. Log unexpected errors with our logging package (`PKG_LOG.error`) and re-raise.
- No `COMMIT` / `ROLLBACK` inside business packages: the caller controls the transaction
  (exception: autonomous logging).
- Raise business errors with `raise_application_error(-20xxx, ...)`, numbers from `PKG_ERRORS`.

## Database changes
- Every new table has the audit columns `CREATED_BY`, `CREATED_AT`, `UPDATED_BY`, `UPDATED_AT`.
- DDL for TEST and PROD only via deployment scripts in version control, never directly in the database.
";
    }
}
