# PL/Pilot – Release Notes

## 0.6.0 – 2026-11-25

Fixes from daily use with large packages and helper agents.

### Highlights

- **Packages of any size can be changed:** the model sends only the changed lines (`edit_object_source`) or
  compiles straight from the editor window, instead of writing a whole package into its answer.
- **Switch windows while the agent works:** it stays on the window of your request and changes it in the background.
- **No more frozen IDE without a separate session:** PL/SQL Developer handles input between the agent's statements,
  PL/Pilot tells you once why it is slow, and stalls are logged.
- **No parallel execution:** a new setting (on by default) keeps the agent's SQL serial – a DBA request for
  development databases with few CPU cores.
- **Smooth progress spinner.**

---

### New: setting "No parallel execution"

On a development database with 8 cores, a parallel query of the agent takes CPU from everybody. With the setting
(*Settings → Safety → No Parallel Execution*, on by default):

- the agent's own session gets `ALTER SESSION DISABLE PARALLEL QUERY / DML / DDL` (before its next statement; turned
  off again, Oracle's defaults are restored);
- every query and DML statement of the agent gets a statement-level `NO_PARALLEL` hint, and `PARALLEL`,
  `PARALLEL_INDEX` and `ENABLE_PARALLEL_DML` hints are removed – also in subqueries, but not inside string literals. For
  `WITH` queries the hint goes to the main query. This also covers the SQL that `measure_sql` runs inside PL/SQL,
  `explain_plan` and the runtime estimate before a query;
- `ALTER SESSION ENABLE/FORCE PARALLEL` is refused, and the model is told that parallel execution is off.

If the agent shares your main session (*Separate session* off), PL/Pilot does not alter your session; the hints still
apply. Verified in PL/SQL Developer against Oracle 23ai: the statements Oracle received (from `V$SQL`) all carry
`NO_PARALLEL` and none of the model's `PARALLEL` hints. Oracle Free has no parallel servers (and keeps parallel query
disabled in every session), so actual parallel execution could not be observed there.

### New: changing large packages (`edit_object_source`, `compile_plsql` from the editor or a file)

A 4,000-line package used to fail: to compile it, the model had to write the whole package into one tool call, which
is far more than its output limit (*Max tokens*, 8,192 by default). The cut-off call was not executed, and the text
of it could end up in the chat.

- **`edit_object_source`** changes a stored package, package body, procedure, function, trigger, type or type
  body by exact replacements – only the changed lines. PL/Pilot reads the source from the database, applies them,
  shows the diff for approval (also in PL/SQL Developer's difference viewer) and compiles it like `compile_plsql`
  (backup, compiler settings kept, errors and warnings). Without an object type, a package or type is changed where
  the edits match: specification or body. Wrapped units are refused. Measured: one line in a 4,002-line body,
  0.5 s including compilation.
- **`compile_plsql`** takes the code in one of three ways: `from_editor` (the text of the request's editor window,
  e.g. after `edit_editor`), `file` (a file of the project folder) or `source` (new or short units).
- **The approval of a compile shows a diff** against the version in the database when the unit already exists
  (*Open Diff* shows both versions in PL/SQL Developer). It used to show the complete code – for a 4,000-line package
  the whole package appeared in the chat and stayed there. New units are still shown in full.
- **Cut-off tool calls:** when the output limit cuts off a tool call, it is not executed, the chat shows only the
  beginning of its arguments with a notice, and the model is told to use the tools above. When the server returns
  a cut-off call as plain text (DeepSeek's template tokens `<｜tool▁calls▁begin｜>`, `<tool_call>`), that text is
  hidden instead of being shown as the answer.

### New: the request keeps its editor window

The editor tools (`get_editor_text`, `edit_editor`, `set_editor_text`, `get_selected_text`, `get_result_grid`,
`compile_plsql from_editor`) work on the window that was active when you sent the request – or the one the agent
opened with `open_object`, `open_new_window` or `open_file_in_ide`. If you switch to another window while the agent
plans or edits, it carries on with its window in the background (PL/SQL Developer's `IDE_SelectWindow`) and your
window stays in front. It is found by its editor control, or by the object or file it shows; if it was closed, the
agent says so instead of writing into another window. `get_ide_context` names both windows, and the model is told to
refer to the window by its object or file.

### Fixed: PL/SQL Developer froze while helper agents ran

Measured on the local test database: with the plug-in's own session, helper agents do not slow down the IDE at all
(a real two-helper run: longest main-thread wait 1 ms; 533 tool runs in three parallel loops: 1 ms). The freeze happens
when the agent has **no session of its own** (setting *Separate session* off, or PL/SQL Developer's session mode
*Single session*): then every statement runs on the IDE's main thread, and helpers send them almost without pause.

- Between statements on the main thread PL/SQL Developer now handles keyboard, mouse and painting first (the
  plug-in's queued work used to come before them, so the IDE never got to react). Measured with the main session
  and three helper-like tool loops for 12 s: before, a 50 ms timer on the IDE's main thread fired twice and the IDE
  could not react for **12 s**; now it fired 135 times, longest pause **0.2 s**. A single long statement still
  blocks the IDE for its duration – that needs the separate session.
- A notice in the chat says once that the agent has no session of its own, and how to change that.
- While the agent works, stalls of the main thread longer than 2 s are logged with what caused them (plug-in work,
  e.g. the SQL statement, or PL/SQL Developer itself); main-thread work longer than 1 s is logged too.

### Fixed: the progress spinner kept restarting

The status line under the input was rebuilt with every update (each streamed chunk, every second), which restarted
the spinner's animation. Only the texts change now, so it turns smoothly for the whole request.

### Tests

- In-IDE self test modes `FREEZE-MEASURE` (main-thread latency and input responsiveness while statements, parallel
  tool loops and real helper agents run; `PLSQLDEVAGENT_AUTOTEST_SHARED=1` with the main session,
  `PLSQLDEVAGENT_MAINTHREAD_YIELD_MS` for comparisons), `WINDOW-ONLY` (read and write a window that is not in front,
  tools bound to the request's window, a closed window, `edit_object_source` on a 4,002-line package body and its
  specification), `BIGUNIT-LLM` (the real model changes a 4,002-line body, and edits its window while another
  window is opened in front) and `PARALLEL-ONLY` (session status, the statements Oracle received, the refusal).
- Harness: `HARNESS_ONLY=<check group>` runs a single group of checks.
- Harness: switching windows during a request, `compile_plsql from_editor`, `edit_object_source`, cut-off tool calls
  (as a broken call and as template text).

## 0.5.0 – 2026-11-23

This release is about working in large, complex databases with many schemas and tens of thousands of objects.
The agent should get the picture in fewer, larger steps, stay focused on the task, show what it is doing, and know
about your local source files.

### Highlights

- **Object dossiers:** one tool call (or one `@mention`) gives the model an object together with its neighbourhood.
- **Size limits follow the context window:** models with a large context window (e.g. 1M tokens) can finally use it.
- **Active window chip:** the window you have open no longer confuses the agent, and you can cross it out.
- **Live progress:** a status line with what the agent is doing and for how long, durations per tool call, and a
  summary per answer.
- **Local source files:** the project folder (with sub folders per schema) is indexed; PL/Pilot asks whether a change
  goes to the database, the local files or both.
- **Working method:** fixed rules for goal-oriented work in few, large steps, a strategy for large databases, and a
  reminder when the agent only keeps looking things up.

---

### New: object dossiers (`explore_objects`)

A new tool returns a large, structured dossier of several objects in **one call**, instead of a dozen slow
look-up round trips:

- **PL/SQL units:** the complete specification and body.
- **Tables:** columns with comments, keys, indexes, size, triggers (with source) and the tables that reference
  them (incoming foreign keys).
- **Views:** columns and query.
- **Uses:** the tables and views an object uses (described compactly with columns, keys and comments) and the
  specifications of the packages it calls.
- **Used by:** the objects that use it, with the source lines that name it; lines that write a table are marked
  `(writes)`.
- **Data model:** with `depth` 2–3 the dossier follows foreign keys further.
- **Local files:** the object's files in the project folder and whether they are identical to the database version.

Dictionary queries are batched, so a dossier costs a few dozen small queries however many neighbours there are.
No table data is read. Whatever does not fit the size limit is listed by name at the end.

**`@mentions`** of database objects now attach one combined dossier of all mentioned objects (shared size limit,
related objects described once) instead of only source or a table description.

### New: size limits follow the context window

The old fixed limits (40,000 characters per tool result, 300,000 characters before older results were shortened)
were sized for a 128k model. With a 1M model, large packages needed several reads and earlier results were deleted
with "call the tool again if needed", which made the model read the same things again.

| Limit | 128k model | 1M model |
|---|---|---|
| One tool result | 40,000 characters (as before) | ~147,000 characters |
| Conversation before old results are shortened | 300,000 characters (as before) | ~1.9M characters |
| One object dossier | ~73,000 characters | 400,000 characters |

New settings (Settings → Server): **Limits Follow Context Window** (on by default), **Max Tool Result**,
**Conversation Budget**, **Object Dossier** (0 = automatic). The configured values are the minimum; the settings page
shows the limits in effect.

### New: the active window, and how to exclude it

- The window you have open in PL/SQL Developer is shown above the input.
- The agent is told it **may be unrelated** to the request and reads it only when the request is about it. The default
  prompt no longer tells the agent to read the active editor for every request.
- **Click the window to cross it out:** the agent then does not see it, and the editor tools refuse to read or change
  it. This is remembered per window (also in new chats) until you click it again.

### New: live progress and timers

- **Status line under the input** while the agent works: what it is doing right now (waiting for the model,
  thinking – with its latest thought –, writing, running a tool – with the steps of helper agents –, waiting for your
  approval or answer) and for how long, plus the step, the total time of the request and the tokens generated so far.
- **Every tool call** shows its duration.
- **Every answer** ends with a summary, e.g. *Worked 1:23 · 5 model turns · 7 tool calls*.
- Before tool calls the agent is asked to write one sentence saying what it does next and why.

### New: local source files in the project folder

Point **Settings → Project folder** at the folder with your local source files (the ones you commit to version
control). Sub folders – for example one per schema – are included.

- **Index:** PL/Pilot records which file contains which database object and in which lines, from the `CREATE`
  statements. The schema comes from the statement, or else from the nearest folder named like a schema. Files are read
  again only when they change.
- **`find_object_files`:** lists every local file of an object (e.g. specification and body file) and whether it is
  identical to the database version or how many lines differ; without names it gives an overview per folder.
- **Database, local files or both?** The first time in a chat the agent changes an object that also has local files
  (compiling it, editing its database window, or editing one of its files), PL/Pilot asks you:
  - **Database** – as before.
  - **Local files** – database changes are refused; the agent gets the list of every affected file and changes them
    with `edit_file` (on disk, with diff approval and undo).
  - **Both** – the database change goes through, and the agent is told which files to change as well.

  The choice holds for the chat and is shown above the input (*Changes: Local files*); click it to be asked again.
- **`ask_user`:** a new general tool for questions only you can decide, shown as a card with answer buttons and a
  text field. Your click goes straight back to the running request.

### New: working method and strategy for large databases

New fixed rules (they apply also when you replaced the base prompt):

- **Goal first:** define what "done" means and stop investigating as soon as the task can be completed reliably.
- **Few, large, purposeful steps:** request everything the next decision needs in one response; one broad read instead
  of several narrow ones; complete changes compiled once; one verification per task.
- **Efficient thinking:** short reasoning aimed at the next decision; no re-deriving known facts; a different approach
  after two failures.
- **Large databases:** start from the named objects and follow real references; locate with one targeted search before
  reading; learn the naming conventions; qualify other schemas and restrict queries; hand broad questions to parallel
  helper agents; keep track of what is established; explore only as far as the task needs; mind dynamic SQL.
- **Focus reminder:** after 10 model turns in a row that only looked things up, the agent is reminded to check what is
  still missing and act. A notice in the chat shows when this happens (at most twice per request).

### Other changes

- When a request involves objects the agent has not seen yet, its first database call is `explore_objects` with all of
  them; a pasted error stack still goes to `resolve_error_stack` first, a column impact question to
  `find_dependencies` with `column`.
- Helper agents (`run_subtask`) get the new rules in short form; with their own model they get dossiers within the
  normal tool result limit.
- `list_objects` and `search_source` over all schemas leave out the same list of Oracle's own schemas.

### Upgrade notes

- **Version 0.5.0.** Install as before; settings and chats are kept.
- Existing `config.json` values for the limits stay valid; with *Limits Follow Context Window* on they are the minimum.
- If you **replaced the base system prompt**, remove a sentence like "Read the active editor (get_editor_text)" from it –
  the fixed rules now say the opposite.
- If your model server supports a reasoning setting (e.g. `{"reasoning_effort": "low"}`), you can add it under
  *Settings → Extra request JSON* to shorten thinking further.

### Known limitations

- On a database with cold dictionary statistics, the first query on `ALL_CONSTRAINTS` / `DBA_CONSTRAINTS` takes about
  0.5 s to parse (the plug-in interface has no bind variables). A table dossier then needs a few seconds; the time is
  logged in `agent.log` (`Dossier of …: … ms`).
- Unsaved SQL windows without a file count as one window when crossed out.
- The index reads common SQL/PL/SQL extensions (`.sql`, `.pks`, `.pkb`, `.pck`, `.spc`, `.bdy`, `.prc`, `.fnc`, `.trg`,
  `.typ`, `.tps`, `.tpb`, `.vw` and a few more) up to 5 MB per file; a unit ends at its `/` line, the next `CREATE` or
  the end of the file. The comparison with the database covers PL/SQL units, not table or view DDL.
- Local files are changed on disk only; a file that is open in PL/SQL Developer is not reloaded there.
- Small local models follow some rules only partly (e.g. gemma4:12b does not write the sentence before tool calls and
  sometimes picks `explore_objects` instead of `find_dependencies`); the plug-in-side features (asking about local
  files, the focus reminder, the limits) do not depend on the model.

### Tested

- Harness (simulated IDE, scripted model): 224 checks passed.
- Full in-IDE self test against Oracle 23ai on the final build (including the new dossier checks): 204 checks passed.
- Real model (gemma4:12b via Ollama): 14 of 15 tool-choice checks passed, including "first call is `explore_objects`"
  and the local-files scenario (asked, body file changed, database unchanged); the failing check (column impact
  → `find_dependencies`) fails with this model on 0.4.1 as well.
