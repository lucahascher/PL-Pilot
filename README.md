# PL/Pilot

*The AI copilot for PL/SQL Developer.* (Technical name: PlsqlDevAgent.)

An agentic AI coding assistant (in the spirit of OpenCode / Claude Code) as a plug-in for
**Allround Automations PL/SQL Developer** (tested with 17, 64 bit). It talks to any
**OpenAI-compatible** chat completions endpoint, e.g. a locally hosted DeepSeek served by vLLM,
Ollama, LM Studio, LiteLLM, …

The agent works *inside* the IDE: it reads and edits your editor windows, browses the data
dictionary, reads object source, runs queries, compiles PL/SQL and fixes compile errors —
every change is shown to you (with a diff) and needs your approval.

## Features

- Chat window (dockable next to the IDE, follows the IDE dark/light theme), streaming answers, "thinking" display
- **Plug-Ins ribbon tab → group PL-Pilot**: *Chat*, *Ask about Selection*, *Settings* (the ribbon cannot show a "/" in group names, so it reads "PL-Pilot" there)
- Right-click in SQL / Program / Test / Command windows → **Ask PL/Pilot**;
  right-click on objects in the Object Browser → **Ask PL/Pilot about this object**
- Tools available to the model:

  | Area | Tools |
  |---|---|
  | Planning | `update_tasks` (task list), `run_subtask` (helper agent) |
  | IDE | `get_ide_context`, `get_editor_text`, `get_selected_text`, `edit_editor` (diff + approval), `set_editor_text`, `open_new_window`, `open_object` (warns if the open window differs from the database), `get_result_grid` |
  | Database (read) | `run_query` (SELECT only), `list_objects`, `describe_object` (columns, keys, indexes, package signatures), `get_object_source`, `search_source`, `get_compile_errors`, `explain_plan`, `list_backups` |
  | Database (write, approval) | `compile_plsql` (CREATE OR REPLACE + error feedback loop), `execute_sql`, `restore_backup` |
  | Project folder (optional) | `list_files`, `read_file`, `search_files`, `write_file`, `edit_file`, `open_file_in_ide` |

- Safety
  - approval cards for every change ("Approve", "Always" for this chat, "Reject" with feedback,
    "IDE diff" opens PL/SQL Developer's difference viewer). A card never takes the focus from the input box
    and ignores clicks/keys in its first 0.7 s, so typing cannot approve it by accident; Enter in its
    feedback field only rejects.
  - **always asked**, also with *Auto approve* and *Always allow*: DROP/TRUNCATE/DELETE (also inside PL/SQL
    blocks and MERGE), dynamic SQL (EXECUTE IMMEDIATE, DBMS_SQL), GRANT/REVOKE, user/role/system changes,
    jobs (DBMS_SCHEDULER/DBMS_JOB), UPDATE without WHERE, queries with PL/SQL in the WITH clause (they can
    change and commit data, so `run_query` refuses them), triggers on database/schema events (e.g. logon), and
    file writes to git/IDE/agent configuration (`.git`, `.vscode`, `.plsqlagent`, `AGENTS.md`) or scripts
  - **production guard**: connections matching patterns (default `*PROD*;*PRD*`) are read-only for the
    agent; the patterns are matched against the logon text *and* the database's own names (DB_NAME,
    DB_UNIQUE_NAME, SERVICE_NAME, CON_NAME, SERVER_HOST, INSTANCE_NAME), so an alias or IP address does not
    bypass it; badge in the chat header to unlock a protected connection or lock any other one for the session.
    Note: "read-only" means the agent issues no changing statements; functions called in a query can still have
    side effects. For a hard guarantee on production use a database account with read-only privileges.
  - the chat page only runs its own script (Content-Security-Policy with a per-load nonce); the Windows login is
    sent to the proxy only, never to the LLM server
  - **local data** (Settings → Local data): chat history on/off (off: nothing is saved, saved chats and the
    prompt history are deleted), saved content *without tool results* (default: no query results, file or
    object contents) or full, retention periods for chats (30 days), source backups (90) and the log (30),
    "Delete saved chats now". Undo information lives in memory only.
  - **backups**: before the agent replaces or drops a PL/SQL unit or view, the database version is saved
    (`%LOCALAPPDATA%\PlsqlDevAgent\backups`); "Restore previous" button in the chat, `restore_backup` tool
  - **undo**: "Revert" button for every editor/file edit the agent (or an Insert button) applied
  - **uncommitted changes**: DML of the agent stays uncommitted in its own session. After every statement the
    plug-in checks the session's transaction (`dbms_transaction.local_transaction_id`, no extra privileges); while
    one is open, a notice above the input lists the statements with **Commit** / **Rollback** buttons, and closing
    PL/SQL Developer asks *Commit / Rollback / Cancel* instead of leaving it to the session's logoff. The agent is told
    what the user decided. EXPLAIN PLAN cleans up its PLAN_TABLE rows so it does not leave a transaction behind.
    (With *Separate session* off, the agent shares the IDE's main session: the notice then only points to
    PL/SQL Developer's own Commit / Rollback.)
  - **rewind**: every change is recorded per chat in order (editor windows, files, PL/SQL units and views incl.
    created/dropped ones). *Rewind* on a user message undoes everything since that message, *Undo from here* on a
    tool step undoes that step and everything after it; a preview lists what happens first. Optionally the
    conversation is cut off there and the message is put back into the input box to edit and resend. A change is
    only undone while its target still looks exactly as the agent left it (edited afterwards → skipped and
    reported); editor edits are found in any open window. Uncommitted DML is rolled back statement by statement
    (savepoints; ROLLBACK for the statement that opened the transaction); once committed – by COMMIT or implicitly
    by DDL – it cannot be undone, nor can other DDL (both are listed).
    The undo information is kept until PL/SQL Developer is closed.
  - separate database session for the agent (does not touch your transaction), read-only mode, the
    connection password is never read
  - the agent's SQL runs on a background thread in that session, so PL/SQL Developer stays usable during long
    queries; *Stop* returns at once (a running statement cannot be interrupted by a plug-in, it finishes in the
    agent's session and the next database access waits for it). With *Separate session* off (or in single session
    mode) the SQL has to run on the IDE main thread as before.
  - **Use DBA views** (Settings → Safety): every query of the agent, including the ones the model writes, reads the
    `DBA_` dictionary views instead of `ALL_` (`ALL_TABLES` → `DBA_TABLES`; literals, comments and other schemas'
    objects are left alone); the model is told so in the system prompt. Without `SELECT_CATALOG_ROLE` the plug-in
    falls back to the `ALL_` views (remembered per connection, logged once). Statements (`execute_sql`,
    `compile_plsql`) are never rewritten.
- Longer tasks
  - **task list**: for multi-step work the agent writes a plan with `update_tasks` and ticks it off; shown as a
    live checklist above the input
  - **auto-continue**: when the agent stops with open tasks, reaches the step limit or its answer is cut off, the
    plug-in prompts it to go on by itself (Settings → Max auto continues; stops if no progress is made)
  - **plan mode**: switch *Agent / Plan* in the input bar; in Plan mode the agent only investigates (read-only)
    and proposes a plan, the *Execute plan* button then lets it carry the plan out
  - **sub-tasks**: `run_subtask` hands a research question to a helper agent with its own fresh context and
    read-only tools (keeps the main conversation small); its steps are shown under the tool call. Several
    `run_subtask` calls in one answer run in parallel; helpers are told to batch independent tool calls into one
    turn; *Settings → Sub-task model* can point them to a smaller, faster model
- Chat comfort
  - saved chats (history button), retry the last answer, ↑ / ↓ for previous prompts
  - quick commands: `/explain`, `/review`, `/document`, `/test`, `/optimize`, `/fix`, `/compact`, `/new`,
    `/help`; own commands in the settings (`/name = prompt`) or as `.plsqlagent/commands/<name>.md`
  - `@NAME` / `@OWNER.NAME` / `@path/file.sql` attaches object source, table descriptions or files (with autocomplete)
  - token meter; long conversations are summarized automatically (or with `/compact`)
  - code blocks: Copy, Insert at cursor, Replace selection, open in a SQL window
- Instructions & rules (Settings → *Instructions & Rules*), added to the system prompt in this order:
  1. **System prompt**: the base instructions (role, way of working, answer style); *Edit…* opens it in a
     full-window editor, *Reset to Default* restores the built-in text. The rules the plug-in's features
     depend on (tools, plan mode, task list) are always added and cannot be edited away.
  2. **Company rules**: every `*.md` file in `PlugIns\PlsqlDevAgent\rules\` (ship your rules with the plug-in)
     plus the file or folder set under *Company Rules*, e.g. `\\fileserver\dev\plsql-agent\company-rules.md`
     so the whole team shares one version. *Edit…* creates a file from a template with typical PL/SQL naming
     and coding conventions.
  3. **Project rules**: `AGENTS.md` in the project folder.
  4. **Personal instructions**.
  *Preview Effective System Prompt* shows exactly what the model receives.

## Languages

English and German (*Deutsch*): Settings → Appearance → *Language*. *Automatic* uses PL/SQL Developer's
language, then the Windows display language. Chat, settings, notices, approval cards, the PL/SQL Developer
menus (after a restart), the company rules template and the installer are translated; in German the agent
answers in German. The system prompt itself stays English (models follow English instructions most
reliably). German quick guide for users: [ANLEITUNG.md](ANLEITUNG.md).

## Installation on a PC without admin rights

PL/SQL Developer looks for plug-ins in this order:
1. the **Plug-Ins directory from the preferences** (`PlugInDir`),
2. `<install dir>\PlugIns\<windows user name>\`,
3. `<install dir>\PlugIns\`.

Option 1 needs no admin rights at all:

1. Copy `PLPilot.zip` to the work PC and extract it.
2. Run `powershell -ExecutionPolicy Bypass -File .\install.ps1`
   (copies to `%LOCALAPPDATA%\PlsqlDevAgent\PlugIns` and unblocks the files).
3. In PL/SQL Developer: **Configure → Preferences → Options → Plug-Ins directory** (the exact page name
   may vary by version; the preference is called `PlugInDir`) → set it to the folder printed by the
   script, restart PL/SQL Developer.
4. **Configure → Plug-Ins** should list *PL/Pilot*. Open **Plug-Ins → PL-Pilot → Settings**,
   enter the base URL of your LLM server (e.g. `http://llm-server:8000/v1`), click **Test**, pick the
   model, **Save**.

If the PL/SQL Developer install folder is writable for you (e.g. it is installed under
`%LOCALAPPDATA%\Programs`), you can also copy the content of `PlugIns\` directly into its `PlugIns`
folder. Keep the structure: `PlsqlDevAgent.dll` in the PlugIns folder, the managed files in the
`PlsqlDevAgent` sub folder.

Requirements on the work PC: Windows 10/11 x64, PL/SQL Developer 64 bit (12+), .NET Framework 4.8
and the WebView2 runtime (both ship with Windows 10/11 and PL/SQL Developer 17 uses WebView2 itself).
Nothing is installed system wide; settings are in `%APPDATA%\PlsqlDevAgent\config.json`
(API key encrypted with Windows DPAPI), logs in `%LOCALAPPDATA%\PlsqlDevAgent\agent.log`.

## Building (on a machine with admin rights)

Requirements: .NET SDK 6 or newer, Visual Studio 2019/2022 (Community is fine) or Build Tools with
the "Desktop development with C++" workload.

```powershell
.\build.ps1                       # -> dist\PLPilot.zip
.\build.ps1 -DeployTo "$env:LOCALAPPDATA\Programs\PLSQL Developer 17\PlugIns"   # build + copy for local testing
```

### Testing

- **Harness** (`src\Harness`): runs the chat window outside PL/SQL Developer with a simulated IDE.
  `--mock-llm` uses a scripted fake OpenAI server; `--llm <baseUrl> <model>` a real one;
  `--autotest <dir> [--prompt "..."] [--offscreen]` drives one conversation and writes transcript/screenshots.
- **In-IDE self test**: set `PLSQLDEVAGENT_AUTOTEST=<output dir>`,
  `PLSQLDEVAGENT_AUTOTEST_CONNECTION=<USER@DATABASE exactly as in the chat header>` (and optionally
  `PLSQLDEVAGENT_AUTOTEST_PROMPT=<task for the LLM>`) and start PL/SQL Developer logged on, e.g.
  `plsqldev.exe userid=scott/tiger@localhost:1521/FREEPDB1`. After start-up the plug-in runs every
  database/editor tool against the real IDE callbacks (creates and drops EMP/DEPT/EMP_API and more in that
  schema – use a test database), optionally one LLM task in the chat window, writes `report.md` and closes the IDE.
  It refuses to run (and changes nothing) unless the connection matches `PLSQLDEVAGENT_AUTOTEST_CONNECTION` and is
  not protected. Quick modes via `PLSQLDEVAGENT_AUTOTEST_PROMPT`: `SQL-ONLY`, `REWIND-ONLY`, `TXN-ONLY`,
  `TXN-CLOSE`, `SEC-ONLY`, `ENC-ONLY`, `HTTPS-ONLY` (no database).
  A local test database: `docker run -d -p 1521:1521 -e ORACLE_PASSWORD=... -e APP_USER=scott -e APP_USER_PASSWORD=tiger gvenzl/oracle-free:23-slim-faststart`.
  PL/SQL Developer needs an Oracle client; the Instant Client (Basic Lite zip) on the `PATH` is enough.

## Architecture

```
PL/SQL Developer (plsqldev.exe, Delphi, 64 bit)
 └─ PlugIns\PlsqlDevAgent.dll            native C++ shim: plug-in exports, menus, callback table,
    │                                    loads the CLR lazily on first use, FPU guard
    └─ PlugIns\PlsqlDevAgent\PlsqlDevAgent.Core.dll   (.NET Framework 4.8, in-process)
         ├─ Ide\PlsqlIde        IDE callbacks (marshalled to the IDE main thread) and SQL callbacks
         │                      (Ide\SqlThread: background thread on the plug-in's own session)
         ├─ Agent\AgentSession  tool calling loop, context management, approvals
         ├─ Agent\OpenAiClient  streaming /chat/completions client (SSE, tool_calls, reasoning)
         ├─ Agent\Tools\*       IDE, database and file tools
         └─ UI\ChatForm         WinForms window + WebView2 (UI\Web\index.html)
```

Why in-process and .NET Framework 4.8: a separate `.exe` in the user profile is frequently blocked by
AppLocker on company PCs, while DLLs loaded by an approved program are not; .NET Framework 4.8 is
present on every Windows 10/11, so nothing has to be installed.

**Character set:** the plug-in interface passes 8-bit strings. PL/SQL Developer honours `CHARMODE=UTF8`
only while it is itself calling into the plug-in; the agent calls the IDE asynchronously, and those
calls are always decoded with the Windows ANSI code page (verified with version 17). The plug-in
therefore uses the ANSI code page (1252: ä ö ü ß € „“ – … included) everywhere. Common symbols outside
it are replaced (→ becomes ->); SQL or editor text with other characters (e.g. Ł) is refused instead of
being silently turned into `?`, and an editor that already contains such characters is not changed.

## HTTPS

`https://` base URLs are supported. Notes for the switch:

- **TLS version**: Windows chooses (TLS 1.2, TLS 1.3 on Windows 11). Inside PL/SQL Developer .NET would otherwise
  run in its legacy mode (SSL 3/TLS 1.0 only) because the CLR is hosted by a native program; the plug-in turns that
  off at start-up (log line `TLS: versions chosen by Windows`).
- **Certificate**: validated by Windows like in Edge/Chrome, using the Windows certificate store. A certificate from
  the company CA works if IT has deployed the CA root to the PCs (usual on managed PCs). Otherwise the connection test
  / chat shows exactly why it was rejected and the thumbprints of the certificate chain; one of them can be entered
  under *Settings → Server: Trusted Certificates* (no admin rights needed; check with IT first). Expired, revoked and
  wrong-host-name certificates are never accepted.
- Use the host name the certificate was issued for in the Base URL (not the IP address).
- The *Use System Proxy* setting applies to HTTPS as well; internal servers are normally in the proxy bypass list.

## Tips for the LLM server

- The server must support OpenAI style **tool calling** (`tools` / `tool_calls`). For vLLM start it
  with `--enable-auto-tool-choice --tool-call-parser <parser for your model>`.
- Reasoning output (`reasoning_content` or `<think>` tags) is shown collapsed as "Thinking".
- The system prompt is stable for the whole chat so the server's prefix cache can be reused.
- Server specific options can be added under *Extra request JSON*, e.g. `{"top_p": 0.95}`.

---
Created by Luca Hascher.
