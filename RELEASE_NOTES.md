# PL/Pilot – Release Notes

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
