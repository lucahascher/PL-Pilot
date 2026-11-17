# PL/Pilot – Kurzanleitung

PL/Pilot ist der KI-Copilot direkt in PL/SQL Developer. Er liest und bearbeitet Ihre
Editorfenster, untersucht die Datenbank (Tabellen, Packages, Quelltexte), führt Abfragen aus, kompiliert
PL/SQL und behebt Kompilierfehler. **Jede Änderung braucht Ihre Zustimmung.**

Er arbeitet mit dem Sprachmodell, das in den Einstellungen eingetragen ist (z. B. ein intern betriebener Server) –
Daten gehen nur an diesen Server. Diese Kurzanleitung richtet sich an Anwender; Details stehen in der
[README](README.md) (englisch).

## Installation (ohne Administratorrechte)

1. `PLPilot.zip` aus dem [neuesten Release](https://github.com/xZeenos/PL-Agent/releases/latest) herunterladen und entpacken.
2. Rechtsklick auf `install.ps1` → *Mit PowerShell ausführen*
   (oder: `powershell -ExecutionPolicy Bypass -File .\install.ps1`).
   Das Plug-in wird nach `%LOCALAPPDATA%\PlsqlDevAgent\PlugIns` kopiert.
3. PL/SQL Developer starten: **Konfigurieren → Einstellungen → Optionen** (je nach Version *Verzeichnisse*) →
   **Plug-Ins-Verzeichnis** auf den vom Skript angezeigten Ordner setzen, PL/SQL Developer neu starten.
4. Unter **Konfigurieren → Plug-Ins** erscheint *PL/Pilot*; im Register **Plug-Ins** gibt es die
   Gruppe **PL-Pilot** (im Menüband steht ein Bindestrich, ein „/“ ist dort nicht möglich).

## Einrichtung

**Plug-Ins → PL-Pilot → Einstellungen**:

- **Basis-URL** und **Modell** Ihres LLM-Servers eintragen (ggf. von der IT), **Testen** klicken, **Speichern**.
  Beim Testen (und beim Wechsel des Modells) fragt PL/Pilot den Server nach dem **Kontextfenster** des Modells
  und trägt es ein; bei Ollama wird es nach der ersten Antwort des Modells noch einmal geprüft. Wer einen eigenen
  Wert behalten will, schaltet **Kontextfenster ermitteln** aus.
- **Sprache**: *Automatisch* übernimmt die Sprache von PL/SQL Developer bzw. Windows; *Deutsch* erzwingt Deutsch.
  Die Menüs von PL/SQL Developer wechseln nach einem Neustart.

## Bedienung

| Was | Wie |
|---|---|
| Chat öffnen | Plug-Ins → PL-Pilot → **Chat** |
| Zu markiertem Code fragen | Code markieren → **Zur Auswahl fragen** oder Rechtsklick im Editor → **PL/Pilot fragen** |
| Zu einem Datenbankobjekt fragen | Rechtsklick im Objekt-Browser → **PL/Pilot zu diesem Objekt fragen** |
| Befehle | `/` tippen: `/explain`, `/review`, `/document`, `/test`, `/optimize`, `/fix`, `/compact`, `/new`, `/help` |
| Objekte oder Dateien anhängen | `@` tippen, z. B. `@PKG_AUFTRAG` – Quelltext bzw. Tabellenbeschreibung wird mitgeschickt. Die Vorschläge beim Tippen fragen die Datenbank ab; abschaltbar mit der Einstellung *Objekte nach @ vorschlagen* |
| Frühere Eingaben | `↑` / `↓` im leeren Eingabefeld |
| Gespeicherte Chats | Uhr-Symbol im Chat-Kopf |

**Größere Aufgaben:** Der Agent schreibt sich eine **Aufgabenliste** (über dem Eingabefeld sichtbar) und
arbeitet sie selbstständig ab. Im **Plan-Modus** (Schalter *Agent / Plan* im Eingabefeld) untersucht er nur
und schlägt einen Plan vor; **Plan ausführen** setzt ihn um. Für breite Recherchen startet er
**Hilfsagenten** (`run_subtask`); mehrere davon laufen gleichzeitig. Unter **Einstellungen → Agent → Modell für
Teilaufgaben** kann ein kleineres, schnelleres Modell für die Hilfsagenten eingetragen werden.

**Zustimmungen:** Vor jeder Änderung erscheint eine Karte mit Diff bzw. SQL:
**Zulassen**, **Immer zulassen** (für diesen Chat), **Diff öffnen** (Vergleichsfenster von PL/SQL Developer)
oder **Ablehnen** – optional mit einer Rückmeldung an den Agent.

**Rückgängig:** Jede Editor- oder Dateiänderung hat eine Schaltfläche **Rückgängig**. Bevor der Agent ein
Package, eine Prozedur, Funktion, einen Trigger, Typ oder View ersetzt oder löscht, wird die bisherige
Datenbankversion gesichert – **Vorherige Version wiederherstellen** spielt sie zurück.

**Zurückspulen:** Merkt man erst am Ende, dass etwas nicht stimmt, macht das Uhr-Symbol an einer eigenen
Nachricht (erscheint beim Darüberfahren) **alle Änderungen seit dieser Nachricht** rückgängig – Editorfenster,
Dateien und Packages/Prozeduren/Views in der Datenbank (auch neu angelegte oder gelöschte). **Ab hier rückgängig**
an einem einzelnen Schritt macht diesen und alle späteren Schritte rückgängig. Vorher wird angezeigt, was passiert.
Mit **Rückgängig machen und Nachricht bearbeiten** verschwindet der Chat ab dieser Stelle und die Nachricht steht
wieder im Eingabefeld. Was Sie oder andere danach selbst geändert haben, wird nicht überschrieben (wird übersprungen
und angezeigt). Noch nicht festgeschriebene Datenänderungen (INSERT/UPDATE/DELETE) werden ebenfalls
zurückgenommen; nach einem Commit – auch dem automatischen durch DDL wie CREATE OR REPLACE – geht das nicht mehr,
ebenso wenig bei sonstigem DDL. Die Informationen gelten, bis PL/SQL Developer geschlossen wird.

**Fehler und Abhängigkeiten:**

- **Fehler-Stack einfügen:** Einen Fehler aus einem Log oder Aufruf (Zeilen mit `ORA-06512: in "…", Zeile …`,
  auch englisch oder als `DBMS_UTILITY.FORMAT_CALL_STACK`) in den Chat kopieren – PL/Pilot zeigt zu jeder Zeile
  den Quelltext und die Prozedur, in der sie steht (bei Triggern mit umgerechneter Zeilennummer).
- **Wer verwendet das?** Fragen wie „Was ist betroffen, wenn ich die Spalte BETRAG in AUFTRAG ändere?“
  beantwortet er aus dem Data Dictionary (`find_dependencies`), bis hinunter zu den Quelltextzeilen einer Spalte.
  Werden durch eine Änderung andere Objekte ungültig, nennt er sie und kann sie neu kompilieren.
- **Compiler-Warnungen:** Beim Kompilieren sieht der Agent die Warnungen von Oracle (z. B. PLW-06009
  *OTHERS-Handler ohne RAISE*). Einstellungen wie `PLSQL_CCFLAGS`, Optimierungsstufe, native Kompilierung und
  Debug-Informationen behält jede Einheit dabei. Abschaltbar unter **Einstellungen → Agent → Compiler-Warnungen**.
- PL/Pilot kennt die **Oracle-Version** und die **NLS-Einstellungen** (z. B. Dezimalkomma) der Verbindung und
  schreibt Code passend dazu.
- **Große Packages** liest der Agent abschnittsweise: Ist eine Einheit zu lang für eine Antwort, sieht er eine
  Liste der übrigen Prozeduren und Funktionen mit Zeilennummern und liest gezielt weiter.
- **Messen statt raten:** Bei Fragen wie „Ist diese Abfrage schneller?“ führt PL/Pilot beide Varianten aus,
  vergleicht Laufzeit und CPU-Zeit und prüft, dass beide dieselben Zeilen liefern. Mit Leserecht auf die V$-Views
  (z. B. `SELECT_CATALOG_ROLE`, beim DBA beantragen) zeigt er zusätzlich den tatsächlichen Ausführungsplan
  (geschätzte gegen tatsächliche Zeilen) und die gelesenen Blöcke. INSERT/UPDATE/DELETE misst er nur nach Ihrer
  Zustimmung und rollt sie danach zurück. `/optimize` nutzt das.
- **Unit-Tests:** `/test` schreibt Tests für eine Programmeinheit. Ist utPLSQL in der Datenbank installiert,
  legt PL/Pilot ein Test-Package an, führt es aus (nach Zustimmung), zeigt fehlgeschlagene Tests und die
  Code-Abdeckung und korrigiert die Tests, bis sie durchlaufen. Ohne utPLSQL öffnet er einen Testblock im
  Test-Fenster.

## Sicherheit

- **Geschützte Verbindungen:** Auf Verbindungen, deren Name zu einem Muster passt (Standard `*PROD*;*PRD*`),
  darf der Agent nur lesen. Das Schild-Symbol im Chat-Kopf zeigt das an; ein Klick erlaubt Änderungen nur für
  die laufende Sitzung. Jede andere Verbindung lässt sich mit dem Schloss-Symbol sperren.
- `DROP`, `DELETE` und `TRUNCATE` werden immer einzeln abgefragt.
- Der Agent arbeitet in einer **eigenen Datenbanksitzung** und berührt Ihre offene Transaktion nicht.
  Er führt kein `COMMIT` aus, wenn Sie es nicht ausdrücklich verlangen. Seine Abfragen laufen im Hintergrund:
  PL/SQL Developer bleibt auch bei langen Abfragen bedienbar. **Stoppen** (Esc) beendet das Warten sofort; eine
  bereits laufende Anweisung kann ein Plug-in aber nicht abbrechen – sie läuft in der Sitzung des Agents zu Ende.
  Deshalb fragt PL/Pilot vorher, wenn Oracle eine Abfrage des Agents auf eine Minute oder mehr schätzt
  (**Einstellungen → Agent → Langsame Abfragen bestätigen**; die Schätzung führt die Abfrage nicht aus).
- Der Agent arbeitet in Ihrem Schema und qualifiziert Objekte anderer Schemas (`HR.EMPLOYEES`). Muss er das Schema
  der Sitzung doch einmal umstellen, stellt PL/Pilot es am Ende der Anfrage zurück (auch nach **Stoppen**).
- **Nicht festgeschriebene Änderungen:** Datenänderungen des Agents (INSERT/UPDATE/DELETE) werden nicht automatisch
  festgeschrieben. Solange es welche gibt, erscheint über dem Eingabefeld ein Hinweis mit den Anweisungen und den
  Schaltflächen **Commit** und **Rollback**. Beim Schließen von PL/SQL Developer fragt PL/Pilot, ob sie
  festgeschrieben oder verworfen werden sollen.
- **DBA-Views verwenden** (Einstellungen → Sicherheit): Alle Abfragen des Agents lesen `DBA_TABLES`,
  `DBA_OBJECTS` … statt der `ALL_`-Views – auch die Abfragen, die das Modell selbst schreibt. Benötigt die Rolle
  `SELECT_CATALOG_ROLE`; ohne sie werden automatisch die `ALL_`-Views verwendet.
- Das Passwort der Verbindung wird nie gelesen.

## Firmenregeln und Konventionen

Unter **Einstellungen → Anweisungen & Regeln**:

- **Firmenregeln:** Markdown-Datei oder -Ordner mit den Namens- und Programmierkonventionen Ihres Teams (z. B. auf
  dem Netzlaufwerk). **Bearbeiten…** legt eine Datei aus einer Vorlage an. Zusätzlich werden alle `*.md`-Dateien
  im Ordner `PlugIns\PlsqlDevAgent\rules` geladen.
- **System-Prompt:** die Grundanweisungen des Agents; anpassbar, **Auf Standard zurücksetzen** stellt sie wieder her.
- **Persönliche Anweisungen:** eigene Ergänzungen.
- `AGENTS.md` im Projektordner: Regeln für ein bestimmtes Projekt.
- **Vollständigen System-Prompt anzeigen** zeigt, was das Modell tatsächlich erhält.

## Was immer nachgefragt wird

Auch mit *Automatisch zulassen* oder *Immer zulassen* fragt PL/Pilot immer nach bei: DROP/TRUNCATE/DELETE (auch in
PL/SQL-Blöcken), dynamischem SQL, GRANT/REVOKE und Benutzer-/Systemänderungen, Jobs, UPDATE ohne WHERE, Abfragen mit
PL/SQL in der WITH-Klausel, Triggern auf Datenbank-/Schemaereignisse (z. B. Anmeldung) und beim Schreiben von Git-,
IDE- oder Agent-Konfiguration bzw. Skripten. Eine Zustimmungskarte nimmt dem Eingabefeld nie den Fokus und reagiert in
den ersten Sekundenbruchteilen nicht – Tippen kann also nichts versehentlich zulassen.

## Lokale Daten (Datenschutz)

Unter **Einstellungen → Lokale Daten**:

- **Chatverlauf speichern** an/aus. Aus: Es wird nichts gespeichert; bereits gespeicherte Chats und der Eingabeverlauf
  werden beim Speichern der Einstellung gelöscht.
- **Gespeicherter Inhalt**: *Ohne Werkzeugergebnisse* (Standard – keine Abfrageergebnisse, Datei- oder Objektinhalte)
  oder vollständig. Die Antworten des Agents bleiben wie geschrieben und können Daten zitieren.
- **Aufbewahrung** in Tagen für Chats (Standard 30), Quelltext-Sicherungen (90) und das Log (30).
- **Gespeicherte Chats jetzt löschen**.

## HTTPS

Die Basis-URL darf mit `https://` beginnen. In der URL den Servernamen verwenden, für den das Zertifikat ausgestellt
ist (nicht die IP-Adresse). Lehnt der Verbindungstest das Zertifikat ab, steht in der Meldung der Grund und – falls
das Firmen-Stammzertifikat auf dem PC fehlt – ein Fingerabdruck. Nach Rücksprache mit der IT kann er unter
**Einstellungen → Server: Vertrauenswürdige Zertifikate** eingetragen werden (keine Administratorrechte nötig).

## Hilfe bei Problemen

- **Einstellungen → Log öffnen** zeigt die Protokolldatei (`%LOCALAPPDATA%\PlsqlDevAgent\agent.log`).
- Chat bleibt leer: Die *Microsoft Edge WebView2 Runtime* fehlt – bitte die IT ansprechen.
- „Server nicht erreichbar“: Basis-URL prüfen und **Testen** klicken; ggf. *System-Proxy verwenden* ausschalten.
- Sonderzeichen: Umlaute, ß, € und typografische Zeichen funktionieren. Zeichen außerhalb des Windows-Zeichensatzes
  (z. B. „Ł“) kann PL/SQL Developer nicht an Plug-ins übergeben. Pfeile u. Ä. ersetzt PL/Pilot automatisch (→ wird ->),
  andere lehnt es ab, statt sie zu „?“ zu verfälschen. Enthält ein Editor solche Zeichen, ändert PL/Pilot ihn nicht.

---
Erstellt von Luca Hascher.
