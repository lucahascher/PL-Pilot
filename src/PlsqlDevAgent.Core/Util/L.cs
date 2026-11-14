using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlsqlDevAgent.Util
{
    /// <summary>
    /// Localization of user-visible texts (English keys, German translations). Texts that are sent to the model
    /// (system prompt, tool results) stay in English on purpose: models follow English instructions most reliably.
    /// </summary>
    public static class L
    {
        public static string Lang { get; private set; } = "en";
        public static bool German => Lang == "de";

        /// <summary>setting: "auto", "en" or "de"; ideLanguage: PL/SQL Developer's translation language (may be empty).</summary>
        public static string Resolve(string setting, string ideLanguage)
        {
            setting = (setting ?? "auto").Trim().ToLowerInvariant();
            if (setting == "de" || setting == "en") return setting;
            var ide = (ideLanguage ?? "").ToLowerInvariant();
            if (ide.Contains("german") || ide.Contains("deutsch")) return "de";
            if (ide.Length > 0) return "en";
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en";
        }

        public static void Set(string lang) => Lang = lang == "de" ? "de" : "en";

        /// <summary>Translates (if a German text exists) and formats.</summary>
        public static string T(string english, params object[] args)
        {
            var text = German && De.TryGetValue(english, out var de) ? de : english;
            return args.Length == 0 ? text : string.Format(text, args);
        }

        private static readonly Dictionary<string, string> De = new Dictionary<string, string>
        {
            // agent run
            ["Stopped."] = "Gestoppt.",
            // security / local data
            ["This statement runs dynamic SQL whose effect cannot be checked beforehand."] = "Diese Anweisung führt dynamisches SQL aus, dessen Wirkung vorher nicht geprüft werden kann.",
            ["This statement changes privileges, users, roles or system settings."] = "Diese Anweisung ändert Berechtigungen, Benutzer, Rollen oder Systemeinstellungen.",
            ["This statement creates or changes jobs that run later."] = "Diese Anweisung legt Jobs an oder ändert Jobs, die später laufen.",
            ["This query contains PL/SQL (WITH FUNCTION) that can change data."] = "Diese Abfrage enthält PL/SQL (WITH FUNCTION), das Daten ändern kann.",
            ["This UPDATE has no WHERE clause and changes every row."] = "Dieses UPDATE hat keine WHERE-Bedingung und ändert jede Zeile.",
            ["This trigger runs automatically on database or schema events (e.g. at every logon)."] = "Dieser Trigger läuft automatisch bei Datenbank- oder Schemaereignissen (z. B. bei jeder Anmeldung).",
            ["This file belongs to the configuration of git, an IDE or the agent (e.g. git hooks run code on the next commit)."] = "Diese Datei gehört zur Konfiguration von Git, einer IDE oder des Agents (z. B. führen Git-Hooks beim nächsten Commit Code aus).",
            ["This file contains instructions for the agent or for git."] = "Diese Datei enthält Anweisungen für den Agent oder für Git.",
            ["This is a script or program file that can run code."] = "Dies ist eine Skript- oder Programmdatei, die Code ausführen kann.",
            ["(not stored on this PC)"] = "(auf diesem PC nicht gespeichert)",
            ["{0} saved chat(s) and the prompt history were deleted."] = "{0} gespeicherte(r) Chat(s) und der Eingabeverlauf wurden gelöscht.",
            ["Chat history is off: saved chats and the prompt history are deleted and nothing is saved any more."] = "Der Chatverlauf ist ausgeschaltet: Gespeicherte Chats und der Eingabeverlauf werden gelöscht, es wird nichts mehr gespeichert.",
            // uncommitted changes of the agent's session
            ["Stop the agent before committing or rolling back."] = "Stoppen Sie den Agent vor Commit oder Rollback.",
            ["The agent uses the main session of PL/SQL Developer: commit or roll back there."] = "Der Agent nutzt die Hauptsitzung von PL/SQL Developer: Commit oder Rollback bitte dort.",
            ["Committed the changes of the agent."] = "Änderungen des Agents festgeschrieben (Commit).",
            ["Rolled back the changes of the agent."] = "Änderungen des Agents verworfen (Rollback).",
            ["The connection changed while the agent session had uncommitted changes ({0}). PL/SQL Developer has closed that session: check whether they were saved."] =
                "Die Verbindung wurde gewechselt, während die Sitzung des Agents nicht festgeschriebene Änderungen hatte ({0}). PL/SQL Developer hat diese Sitzung geschlossen: Bitte prüfen, ob sie gespeichert wurden.",
            ["The database session of PL/Pilot has uncommitted changes:\n\n{0}\n\nCommit them?\nYes = Commit, No = Rollback, Cancel = do not close PL/SQL Developer"] =
                "Die Datenbanksitzung von PL/Pilot hat nicht festgeschriebene Änderungen:\n\n{0}\n\nFestschreiben?\nJa = Commit, Nein = Rollback, Abbrechen = PL/SQL Developer nicht schließen",
            ["(open transaction)"] = "(offene Transaktion)",
            ["Roll back the data changes of: {0}"] = "Datenänderungen zurücknehmen: {0}",
            ["the same transaction contains later changes that are not part of this rewind (e.g. from another chat)"] = "dieselbe Transaktion enthält spätere Änderungen, die nicht zurückgespult werden (z. B. aus einem anderen Chat)",
            ["already committed (by a COMMIT or a DDL statement afterwards)"] = "bereits festgeschrieben (durch ein späteres COMMIT oder eine DDL-Anweisung)",
            // HTTPS
            ["The HTTPS certificate of {0} is not accepted: {1}"] = "Das HTTPS-Zertifikat von {0} wird nicht akzeptiert: {1}",
            ["Could not establish a secure (TLS) connection to {0}. The server may require a TLS version or cipher that Windows on this PC does not offer. ({1})"] =
                "Keine sichere (TLS-)Verbindung zu {0} möglich. Der Server verlangt eventuell eine TLS-Version oder Verschlüsselung, die Windows auf diesem PC nicht anbietet. ({1})",
            ["the server sent no certificate"] = "der Server hat kein Zertifikat gesendet",
            ["it is issued for {0}, not for {1}: use that name in the Base URL"] = "es ist für {0} ausgestellt, nicht für {1}: diesen Namen in der Basis-URL verwenden",
            ["it is expired or not yet valid (valid from {0:d} to {1:d})"] = "es ist abgelaufen oder noch nicht gültig (gültig von {0:d} bis {1:d})",
            ["it was revoked"] = "es wurde widerrufen",
            ["Windows on this PC does not trust its issuer \"{0}\" (self-signed, or the company's root certificate is not installed here)"] =
                "Windows vertraut auf diesem PC dem Aussteller „{0}“ nicht (selbst signiert, oder das Stammzertifikat der Firma ist hier nicht installiert)",
            ["After checking with your IT that it is the right certificate, you can trust it on this PC (no admin rights needed) under Settings → Trusted Certificates with one of these thumbprints: {0}"] =
                "Nachdem Sie mit der IT geklärt haben, dass es das richtige Zertifikat ist, können Sie ihm auf diesem PC (ohne Administratorrechte) unter Einstellungen → Vertrauenswürdige Zertifikate mit einem dieser Fingerabdrücke vertrauen: {0}",
            // rewind
            ["Stop the agent before rewinding."] = "Stoppen Sie den Agent, bevor Sie zurückspulen.",
            ["Editor {0}: put the previous text back"] = "Editor {0}: vorherigen Text wiederherstellen",
            ["the editor text was changed afterwards or the window was closed"] = "der Editortext wurde danach geändert oder das Fenster geschlossen",
            ["File {0}: delete (created by the agent)"] = "Datei {0}: löschen (vom Agent angelegt)",
            ["File {0}: restore the previous content"] = "Datei {0}: vorherigen Inhalt wiederherstellen",
            ["the file was changed afterwards"] = "die Datei wurde danach geändert",
            ["{0}: drop (created by the agent)"] = "{0}: löschen (vom Agent angelegt)",
            ["{0}: create again (dropped by the agent)"] = "{0}: wieder anlegen (vom Agent gelöscht)",
            ["{0}: compile the previous version"] = "{0}: vorherige Version kompilieren",
            ["it belongs to connection {0}"] = "gehört zur Verbindung {0}",
            ["the agent is read-only on this connection (unlock it in the chat header)"] = "der Agent darf auf dieser Verbindung nur lesen (im Chat-Kopf entsperren)",
            ["it was changed in the database afterwards"] = "wurde danach in der Datenbank geändert",
            ["not undone: {0}"] = "nicht rückgängig gemacht: {0}",
            ["Not undone (cannot be undone automatically): {0}"] = "Nicht rückgängig gemacht (automatisch nicht möglich): {0}",
            ["Stopped. The last database statement is still running in the agent's session (it cannot be interrupted from a plug-in); the agent's next database access waits until it has finished."] =
                "Gestoppt. Die letzte Datenbankanweisung läuft in der Sitzung des Agenten noch weiter (ein Plug-in kann sie nicht abbrechen); der nächste Datenbankzugriff des Agenten wartet, bis sie fertig ist.",
            ["Reached {0} steps, continuing automatically ({1}/{2})."] = "{0} Schritte erreicht, wird automatisch fortgesetzt ({1}/{2}).",
            ["Stopped after {0} steps. Send \"continue\" to let the agent go on."] = "Nach {0} Schritten angehalten. Mit „weiter“ arbeitet der Agent weiter.",
            ["{0} open task(s), continuing automatically ({1}/{2})."] = "{0} offene Aufgabe(n), wird automatisch fortgesetzt ({1}/{2}).",
            ["The answer was cut off, continuing automatically ({0}/{1})."] = "Die Antwort wurde abgeschnitten, wird automatisch fortgesetzt ({0}/{1}).",
            ["The answer was cut off because it reached the max tokens limit (Settings, Max tokens)."] = "Die Antwort wurde abgeschnitten, weil das Token-Limit erreicht wurde (Einstellungen, Max. Tokens).",
            ["The model returned an empty answer."] = "Das Modell hat eine leere Antwort geliefert.",
            ["The agent stopped with {0} open task(s). Send \"continue\" to let it go on."] = "Der Agent hat mit {0} offenen Aufgabe(n) angehalten. Mit „weiter“ arbeitet er weiter.",
            ["Compacting the conversation…"] = "Unterhaltung wird zusammengefasst…",
            ["Nothing to compact yet."] = "Noch nichts zum Zusammenfassen.",
            ["Could not reach {0}: {1}"] = "{0} ist nicht erreichbar: {1}",
            ["The request to {0} timed out."] = "Zeitüberschreitung bei der Anfrage an {0}.",
            ["LLM server returned {0} {1}: {2}"] = "Der LLM-Server meldet {0} {1}: {2}",
            ["Timed out after 20 s."] = "Zeitüberschreitung nach 20 s.",
            // chat window
            ["Please configure the LLM server and model first (Settings)."] = "Bitte zuerst LLM-Server und Modell konfigurieren (Einstellungen).",
            ["The agent is still working. Stop it first or wait."] = "Der Agent arbeitet noch. Bitte stoppen oder warten.",
            ["The agent is busy."] = "Der Agent ist beschäftigt.",
            ["Nothing to retry."] = "Nichts zum Wiederholen.",
            ["Stop the agent before opening another chat."] = "Bitte den Agent stoppen, bevor ein anderer Chat geöffnet wird.",
            ["Chat not found."] = "Chat nicht gefunden.",
            ["PL/Pilot is working…"] = "PL/Pilot arbeitet…",
            ["PL/Pilot finished."] = "PL/Pilot ist fertig.",
            ["Open an editor window (SQL, Program, Test or Command window) first."] = "Bitte zuerst ein Editorfenster öffnen (SQL-, Programm-, Test- oder Befehlsfenster).",
            ["Replaced the selection in the editor."] = "Auswahl im Editor ersetzt.",
            ["Inserted at line {0} in the editor."] = "In Zeile {0} im Editor eingefügt.",
            ["Select the code to replace in the editor first."] = "Bitte zuerst den zu ersetzenden Code im Editor markieren.",
            ["The selected text could not be located in the editor (block selections are not supported)."] = "Der markierte Text wurde im Editor nicht gefunden (Blockmarkierungen werden nicht unterstützt).",
            ["Undo information is no longer available (it is kept until PL/SQL Developer is closed)."] = "Die Informationen zum Rückgängigmachen sind nicht mehr vorhanden (sie bleiben bis zum Schließen von PL/SQL Developer erhalten).",
            ["Not reverted: the editor content changed after the edit, or a different window is active. Activate the window or use Undo (Ctrl+Z) in the editor."] = "Nicht rückgängig gemacht: Der Editorinhalt wurde danach geändert oder ein anderes Fenster ist aktiv. Bitte das Fenster aktivieren oder im Editor Strg+Z verwenden.",
            ["The file was changed after the edit; not reverted."] = "Die Datei wurde danach geändert; nicht rückgängig gemacht.",
            ["Not reverted: {0}"] = "Nicht rückgängig gemacht: {0}",
            ["Reverted."] = "Rückgängig gemacht.",
            ["Backup not found."] = "Sicherung nicht gefunden.",
            ["Not restored: the agent is read-only on this connection. Unlock the connection in the chat header first."] = "Nicht wiederhergestellt: Der Agent ist auf dieser Verbindung schreibgeschützt. Bitte die Verbindung zuerst im Chat-Kopf entsperren.",
            ["Not restored: the backup belongs to {0}, but PL/SQL Developer is connected to {1}."] = "Nicht wiederhergestellt: Die Sicherung gehört zu {0}, PL/SQL Developer ist aber mit {1} verbunden.",
            ["Restore failed: {0}"] = "Wiederherstellung fehlgeschlagen: {0}",
            ["Base URL must be an http(s) URL, e.g. http://server:8000/v1"] = "Die Basis-URL muss eine http(s)-URL sein, z. B. http://server:8000/v1",
            ["The project folder does not exist."] = "Der Projektordner existiert nicht.",
            ["Extra request JSON is invalid: {0}"] = "Das zusätzliche Anfrage-JSON ist ungültig: {0}",
            ["Project folder for the agent (SQL / PL/SQL files, AGENTS.md)"] = "Projektordner für den Agent (SQL-/PL/SQL-Dateien, AGENTS.md)",
            ["Company rules file (Markdown)"] = "Datei mit Firmenregeln (Markdown)",
            ["Could not read {0}: {1}"] = "{0} konnte nicht gelesen werden: {1}",
            ["Could not save {0}: {1}"] = "{0} konnte nicht gespeichert werden: {1}",
            ["PL/Pilot is still working. Close PL/SQL Developer anyway?"] = "PL/Pilot arbeitet noch. PL/SQL Developer trotzdem schließen?",
            ["PL/Pilot is still executing a database statement. Close PL/SQL Developer anyway?"] = "PL/Pilot führt noch eine Datenbankanweisung aus. PL/SQL Developer trotzdem schließen?",
            ["The Microsoft Edge WebView2 Runtime is not installed.\n\nIt is part of current Windows 10/11 installations; ask IT to install the \"Evergreen WebView2 Runtime\"."] =
                "Die Microsoft Edge WebView2 Runtime ist nicht installiert.\n\nSie ist Teil aktueller Windows-10/11-Installationen; bitte die IT, die „Evergreen WebView2 Runtime“ zu installieren.",
            ["Could not start the chat view:\n{0}\n\nSee {1}"] = "Die Chat-Ansicht konnte nicht gestartet werden:\n{0}\n\nSiehe {1}",
            ["Allowed"] = "Zugelassen",
            ["Allowed (always for this chat)"] = "Zugelassen (immer in diesem Chat)",
            ["Rejected"] = "Abgelehnt",
            ["Cancelled"] = "Abgebrochen",
            // approvals (titles, warnings, diff captions)
            ["Edit editor text"] = "Editortext ändern",
            ["Replace editor text"] = "Editortext ersetzen",
            ["Current editor text"] = "Aktueller Editortext",
            ["Proposed by agent"] = "Vorschlag des Agents",
            ["Compile into database ({0}): {1}"] = "In die Datenbank kompilieren ({0}): {1}",
            ["Execute in database ({0})"] = "In der Datenbank ausführen ({0})",
            ["This statement drops, truncates or deletes data/objects."] = "Diese Anweisung löscht Daten oder Objekte (DROP/TRUNCATE/DELETE).",
            ["Create file {0}"] = "Datei {0} anlegen",
            ["Modify file {0}"] = "Datei {0} ändern",
            ["{0} (current)"] = "{0} (aktuell)",
            ["{0} (proposed)"] = "{0} (Vorschlag)",
            ["Restore {0} from backup {1} ({2})"] = "{0} aus Sicherung {1} wiederherstellen ({2})",
            ["Recompile {0} invalid objects ({1})"] = "{0} ungültige Objekte neu kompilieren ({1})",
            ["Current database version"] = "Aktuelle Datenbankversion",
            ["Backup {0}"] = "Sicherung {0}",
            ["{0}/{1} done"] = "{0}/{1} erledigt",
            // mentions
            ["not found"] = "nicht gefunden",
            ["{0}, {1} lines"] = "{0}, {1} Zeilen",
            // /help
            ["**Commands** (type `/` in the input box):"] = "**Befehle** (im Eingabefeld `/` tippen):",
            ["Own commands: Settings → Custom commands, or `.plsqlagent/commands/<name>.md` in the project folder. Mention objects or files with `@NAME` to attach their source."] =
                "Eigene Befehle: Einstellungen → Eigene Befehle oder `.plsqlagent/commands/<name>.md` im Projektordner. Mit `@NAME` werden Objekte oder Dateien angehängt.",
            ["Explain what the selected code / active editor does"] = "Erklären, was der markierte Code bzw. der aktive Editor macht",
            ["Code review: bugs, performance, security, style"] = "Code-Review: Fehler, Performance, Sicherheit, Stil",
            ["Add header comments / documentation"] = "Kopfkommentare / Dokumentation ergänzen",
            ["Write a test block for the program unit"] = "Testblock für die Programmeinheit schreiben",
            ["Analyse and improve performance (uses explain plan)"] = "Performance analysieren und verbessern (mit Explain Plan)",
            ["Compile the active editor and fix all errors"] = "Aktiven Editor kompilieren und alle Fehler beheben",
            ["Start a new chat"] = "Neuen Chat beginnen",
            ["Summarize the conversation to free context"] = "Unterhaltung zusammenfassen, um Kontext freizugeben",
            ["List the available commands"] = "Verfügbare Befehle anzeigen",
            // prefill (becomes the user's message)
            ["Regarding this code{0}:"] = "Zu diesem Code{0}:",
            ["Regarding the active editor ({0}): "] = "Zum aktiven Editor ({0}): ",
            ["Regarding {0}: "] = "Zu {0}: ",
            [" from the {0} window"] = " aus dem {0}-Fenster",
        };
    }
}
