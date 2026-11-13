using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlsqlDevAgent.Agent;
using PlsqlDevAgent.Agent.Tools;
using PlsqlDevAgent.Ide;
using PlsqlDevAgent.Native;
using PlsqlDevAgent.UI;
using PlsqlDevAgent.Util;

namespace PlsqlDevAgent.Testing
{
    /// <summary>
    /// Automated integration test inside PL/SQL Developer. Only runs when the environment variable
    /// PLSQLDEVAGENT_AUTOTEST=&lt;output folder&gt; is set (the shim calls it from AfterStart).
    /// Start PL/SQL Developer with "userid=user/pwd@db" so it is logged on.
    /// Optional: PLSQLDEVAGENT_AUTOTEST_PROMPT = prompt for a real LLM run in the chat window.
    /// Writes report.md (+ transcript.txt) and closes PL/SQL Developer afterwards.
    /// </summary>
    internal static class SelfTest
    {
        private static string _dir;
        private static readonly StringBuilder Report = new StringBuilder();
        private static int _pass, _fail;

        public static void Start(Plugin plugin)
        {
            _dir = Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST");
            Directory.CreateDirectory(_dir);
            var mode = Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_PROMPT");
            if (mode == "ENC-ONLY") _ = EncodingRunAsync(plugin);
            else if (mode == "SQL-ONLY") _ = SqlRunAsync(plugin);
            else if (mode == "REWIND-ONLY") _ = SqlRunAsync(plugin, rewindOnly: true);
            else if (mode == "HTTPS-ONLY") _ = HttpsRunAsync();
            else _ = RunAsync(plugin);
        }

        /// <summary>
        /// HTTPS to LLM servers from inside PL/SQL Developer (CLR hosted by a native program: TLS defaults differ from
        /// a normal .NET program). PLSQLDEVAGENT_AUTOTEST_URLS = base URLs separated by ';',
        /// PLSQLDEVAGENT_AUTOTEST_THUMBPRINT = optional trusted certificate thumbprint.
        /// </summary>
        private static async Task HttpsRunAsync()
        {
            try
            {
                Line("# PL/Pilot – HTTPS self test");
                Line("ServicePointManager.SecurityProtocol before the first request: " + System.Net.ServicePointManager.SecurityProtocol);
                var urls = (Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_URLS") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var url in urls)
                {
                    var cfg = AgentConfig.Load().Clone();
                    cfg.BaseUrl = url;
                    cfg.Model = "https-mock";
                    cfg.ApiKey = "";
                    cfg.UseSystemProxy = false;
                    cfg.ExtraBodyJson = "";
                    var thumbprint = Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_THUMBPRINT");
                    if (thumbprint != null) cfg.GetType().GetProperty("TrustedCertificates")?.SetValue(cfg, thumbprint);
                    using (var client = new OpenAiClient())
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    {
                        try { Line($"{url} models: {string.Join(",", await client.ListModelsAsync(cfg, cts.Token))}"); }
                        catch (Exception ex) { Line($"{url} models FAILED: {ex.Message}"); }
                        try
                        {
                            var turn = await client.StreamChatAsync(cfg, new JArray { new JObject { ["role"] = "user", ["content"] = "hi" } }, null, null, cts.Token);
                            Line($"{url} chat: {turn.Content.Trim()}");
                        }
                        catch (Exception ex) { Line($"{url} chat FAILED: {ex.Message}"); }
                    }
                }
                Line("ServicePointManager.SecurityProtocol afterwards: " + System.Net.ServicePointManager.SecurityProtocol);
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        /// <summary>Only the background SQL / Stop / DBA views checks (quick run).</summary>
        private static async Task SqlRunAsync(Plugin plugin, bool rewindOnly = false)
        {
            try
            {
                Line("# PL/Pilot – background SQL self test");
                for (int i = 0; i < 120 && !plugin.Ide.GetContext().Connected; i++) await Task.Delay(500);
                if (rewindOnly) await RewindChecks(plugin.Ide, AgentConfig.Load());
                else await SqlThreadChecks(plugin.Ide, AgentConfig.Load());
                Line($"\n**Total: {_pass} passed, {_fail} failed**");
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        /// <summary>
        /// Rewind: changes in two editor windows, a created, a changed and a dropped procedure and a DML statement are
        /// undone newest first; changes made afterwards by someone else are detected and left alone.
        /// </summary>
        private static async Task RewindChecks(IIde ide, AgentConfig cfg)
        {
            Line("\n## 9. Rewind");
            await Task.Delay(200);
            var plain = new ToolContext { Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()) };
            string Proc(string name, string body) => $"create or replace procedure {name} is\nbegin\n  {body}\nend;";
            // objects that exist before the recorded changes (not recorded)
            await Tool(plain, "compile_plsql", new JObject { ["source"] = Proc("pda_rw_existing", "null; -- original") }, "compiled successfully");
            await Tool(plain, "compile_plsql", new JObject { ["source"] = Proc("pda_rw_drop", "null; -- will be dropped") }, "compiled successfully");
            await Tool(plain, "execute_sql", new JObject { ["sql"] = "begin execute immediate 'drop procedure pda_rw_new'; exception when others then null; end;" }, null);

            var journal = new ChangeJournal();
            journal.StartCheckpoint("cp1");
            var ctx = new ToolContext
            {
                Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()),
                RecordChange = r => { r.Checkpoint = "cp1"; journal.Add(r); },
            };

            ide.NewWindow(IdeWindowType.Sql, "select 1 a from dual");
            await Task.Delay(300);
            await Tool(ctx, "edit_editor", new JObject { ["old_string"] = "1 a", ["new_string"] = "2 a" }, "Editor updated");
            ide.NewWindow(IdeWindowType.Sql, "select 3 b from dual");
            await Task.Delay(300);
            await Tool(ctx, "edit_editor", new JObject { ["old_string"] = "3 b", ["new_string"] = "4 b" }, "Editor updated");
            await Tool(ctx, "compile_plsql", new JObject { ["source"] = Proc("pda_rw_new", "null; -- created by the agent") }, "compiled successfully");
            await Tool(ctx, "compile_plsql", new JObject { ["source"] = Proc("pda_rw_existing", "null; -- changed by the agent") }, "compiled successfully");
            await Tool(ctx, "execute_sql", new JObject { ["sql"] = "drop procedure pda_rw_drop" }, "Executed successfully");
            await Tool(ctx, "execute_sql", new JObject { ["sql"] = "update emp set sal = sal where 1 = 0" }, "Executed successfully");

            var plan = await Task.Run(() => Rewinder.Plan(journal, journal.StartOfCheckpoint("cp1").Value, plain, null));
            Line("Plan:\n" + string.Join("\n", plan.Steps.Select(st => $"- {st.Text}{(st.Problem == null ? "" : " PROBLEM: " + st.Problem)}")) +
                 "\n" + string.Join("\n", plan.NotUndoable.Select(r => "- not undoable: " + r.Label)));
            Check("rewind plan: 5 undoable steps, 1 statement that cannot be undone", plan.Undoable == 5 && plan.Steps.Count == 5 && plan.NotUndoable.Count == 1);

            var lines = await Task.Run(() => Rewinder.Execute(plan, plain));
            Line("Result:\n" + string.Join("\n", lines));
            var windows = await Task.Run(() => ide.GetAllEditorTexts());
            Check("rewind: first editor window (not the active one) has its old text again", windows.Any(w => w.Trim() == "select 1 a from dual") && !windows.Any(w => w.Trim() == "select 2 a from dual"));
            Check("rewind: second editor window has its old text again", windows.Any(w => w.Trim() == "select 3 b from dual") && !windows.Any(w => w.Trim() == "select 4 b from dual"));
            var objects = await Task.Run(() => ide.Query("select object_name from user_objects where object_name like 'PDA\\_RW\\_%' escape '\\' order by 1", 10));
            var names = string.Join(",", objects.Rows.Select(r => r[0]));
            Line("Procedures now: " + names);
            Check("rewind: the procedure created by the agent is dropped, the dropped one is back", names == "PDA_RW_DROP,PDA_RW_EXISTING");
            var src = await Task.Run(() => Db.CurrentSource(plain, "PROCEDURE", ctx.CurrentSchema, "PDA_RW_EXISTING"));
            Check("rewind: the changed procedure has its original source again", src.Contains("-- original"));
            Check("rewind: everything is marked as undone", journal.OpenFrom(1).All(r => r.Kind == ChangeRecord.NotUndoable));

            // changed afterwards by the user / someone else -> not undone
            var journal2 = new ChangeJournal();
            journal2.StartCheckpoint("cp2");
            ctx.RecordChange = r => { r.Checkpoint = "cp2"; journal2.Add(r); };
            ide.NewWindow(IdeWindowType.Sql, "select 7 c from dual");
            await Task.Delay(300);
            await Tool(ctx, "edit_editor", new JObject { ["old_string"] = "7 c", ["new_string"] = "8 c" }, "Editor updated");
            await Task.Run(() => ide.ReplaceEditorText(ide.GetEditorText(), "select 6 b from dual -- typed by the user"));
            await Tool(ctx, "compile_plsql", new JObject { ["source"] = Proc("pda_rw_existing", "null; -- agent again") }, "compiled successfully");
            await Tool(plain, "compile_plsql", new JObject { ["source"] = Proc("pda_rw_existing", "null; -- someone else") }, "compiled successfully");
            var plan2 = await Task.Run(() => Rewinder.Plan(journal2, 1, plain, null));
            Line("Plan with later changes:\n" + string.Join("\n", plan2.Steps.Select(st => $"- {st.Text}{(st.Problem == null ? "" : " PROBLEM: " + st.Problem)}")));
            Check("rewind: editor text changed by the user afterwards is detected", plan2.Steps.Any(st => st.Record.Kind == ChangeRecord.Editor && st.Problem != null));
            Check("rewind: procedure recompiled by someone else afterwards is detected", plan2.Steps.Any(st => st.Record.Kind == ChangeRecord.Database && st.Problem != null));
            await Task.Run(() => Rewinder.Execute(plan2, plain));
            Check("rewind: the user's editor text is left alone", (await Task.Run(() => ide.GetEditorText())).Contains("typed by the user"));
            src = await Task.Run(() => Db.CurrentSource(plain, "PROCEDURE", ctx.CurrentSchema, "PDA_RW_EXISTING"));
            Check("rewind: the later database version is left alone", src.Contains("-- someone else"));
        }

        /// <summary>A statement that runs <paramref name="seconds"/> seconds in the database.</summary>
        private static string SlowQuery(int seconds) =>
            "with function pda_wait(p_seconds number) return number is begin dbms_session.sleep(p_seconds); return p_seconds; end; " +
            $"select pda_wait({seconds}) waited from dual";

        /// <summary>
        /// Long statements must not block PL/SQL Developer (they run on the SQL thread with the plug-in's own session),
        /// Stop must return at once, and the "Use DBA views" setting must rewrite queries (or fall back without the role).
        /// </summary>
        private static async Task SqlThreadChecks(IIde ide, AgentConfig cfg)
        {
            Line("\n## 8. Long statements in the background, Stop, DBA views");
            Line($"Separate session setting: {cfg.SeparateSession}");
            await Task.Delay(200);

            // 1. the IDE main thread answers while a 4 second statement runs
            var sw = Stopwatch.StartNew();
            var slow = Task.Run(() => ide.Query(SlowQuery(4), 1));
            long slowest = 0;
            int pings = 0;
            while (!slow.IsCompleted)
            {
                var p = Stopwatch.StartNew();
                await Task.Run(() => ide.GetContext()); // a real IDE callback on the main thread
                slowest = Math.Max(slowest, p.ElapsedMilliseconds);
                pings++;
                await Task.Delay(100);
            }
            var waited = (await slow).Rows[0][0];
            Line($"Statement took {sw.ElapsedMilliseconds} ms; {pings} IDE calls meanwhile, slowest {slowest} ms");
            Check("4 s statement returns its result", waited == "4" && sw.ElapsedMilliseconds >= 3500);
            Check($"PL/SQL Developer stays responsive during the statement (slowest IDE call {slowest} ms)", pings >= 20 && slowest < 500);

            // 2. Stop: the caller stops waiting at once; the next statement waits for the running one
            using (var cts = new System.Threading.CancellationTokenSource())
            {
                sw.Restart();
                var stopped = Task.Run(() => { IdeCancellation.Current = cts.Token; return ide.Query(SlowQuery(5), 1); });
                await Task.Delay(1000);
                cts.Cancel();
                bool cancelled = false;
                try { await stopped; } catch (OperationCanceledException) { cancelled = true; }
                Line($"Stop returned after {sw.ElapsedMilliseconds} ms (cancelled: {cancelled})");
                Check("Stop returns at once", cancelled && sw.ElapsedMilliseconds < 2500);
                Check("the stopped statement is reported as still running", ide.IsExecutingSql);
                var next = await Task.Run(() => ide.Query("select 'after' x from dual", 1));
                Line($"Next statement finished after {sw.ElapsedMilliseconds} ms");
                Check("the next statement waits for the stopped one, then works", next.Rows[0][0] == "after" && sw.ElapsedMilliseconds >= 4500);
                Check("nothing running afterwards", !ide.IsExecutingSql);
            }

            // 3. errors are still reported from the background thread
            try { await Task.Run(() => ide.Query("select * from pda_no_such_table", 1)); Check("ORA error from the background thread", false); }
            catch (OracleSqlException ex) { Check($"ORA error from the background thread ({ex.Message.Split('\n')[0]})", ex.Code == 942); }

            // 4. DBA views setting
            var dcfg = cfg.Clone();
            dcfg.UseDbaViews = true;
            var dctx = new ToolContext { Ide = ide, Config = dcfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()) };
            bool hasRole = (await Task.Run(() => ide.Query("select count(*) from session_roles where role = 'SELECT_CATALOG_ROLE'", 1))).Rows[0][0] != "0";
            const string sysTables = "select count(*) n from all_tables where owner = 'SYS'";
            var viaAll = (await Task.Run(() => ide.Query(sysTables, 1))).Rows[0][0];
            var viaSetting = await Tool(dctx, "run_query", new JObject { ["sql"] = sysTables }, null);
            Line($"SELECT_CATALOG_ROLE: {hasRole}; SYS tables via ALL_TABLES: {viaAll}; with the setting: {viaSetting.Replace("\n", " ")}");
            int n1 = int.Parse(viaAll), n2 = int.Parse(viaSetting.Split('\n')[2].Trim()); // header, ---, value
            if (hasRole) Check("DBA views setting: run_query reads DBA_TABLES (sees more SYS tables)", n2 > n1);
            else Check("DBA views setting without SELECT_CATALOG_ROLE: falls back to the ALL_ views", n2 == n1);
            await Tool(dctx, "describe_object", new JObject { ["name"] = "emp" }, "EMP_DEPT_FK");
            await Tool(dctx, "get_object_source", new JObject { ["name"] = "emp_api" }, "PACKAGE BODY");
            await Tool(dctx, "explain_plan", new JObject { ["sql"] = "select ename from emp where deptno = 10" }, "Plan hash value");
            Check("system prompt contains the DBA views rule", SystemPrompt.Build(dcfg, false).Contains("DBA_ data dictionary views"));
        }

        /// <summary>Only the encoding checks (quick run).</summary>
        private static async Task EncodingRunAsync(Plugin plugin)
        {
            try
            {
                Line("# PL/Pilot – encoding self test");
                for (int i = 0; i < 120 && !plugin.Ide.GetContext().Connected; i++) await Task.Delay(500);
                await EncodingChecks(plugin.Ide);
                Line($"\n**Total: {_pass} passed, {_fail} failed**");
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        /// <summary>
        /// Umlauts and other special characters through the plug-in interface: the agent calls the IDE
        /// asynchronously, where PL/SQL Developer always decodes ANSI (not UTF-8).
        /// </summary>
        private static async Task EncodingChecks(IIde ide)
        {
            Line("\n## 7. Special characters (umlauts, €, typographic characters)");
            await Task.Delay(200); // make sure we run from our own message, not inside an IDE event
            var real = ide as PlsqlIde;

            var q = ide.Query("select asciistr('f\u00FCr \u20AC') a, unistr('Gr\\00FC\\00DFe \\20AC') b from dual", 1).Rows[0];
            Line($"Oracle received '{q[0]}', returned '{q[1]}'");
            Check("umlauts and € reach Oracle unchanged", q[0] == "f\\00FCr \\20AC");
            Check("umlauts and € come back from Oracle unchanged", q[1] == "Gr\u00FC\u00DFe \u20AC");

            const string text = "-- Pr\u00FCfung f\u00FCr Umlaute: \u00E4\u00F6\u00FC\u00DF \u00C4\u00D6\u00DC \u20AC \u201Ezitiert\u201C \u2013 Ende\u2026";
            ide.NewWindow(IdeWindowType.Sql, text);
            await Task.Delay(300);
            var shown = real?.ReadEditorUnicode();
            Line("Editor shows: " + shown);
            Check("new window shows umlauts correctly", shown != null && shown.TrimEnd() == text);
            Check("editor text is read back correctly", ide.GetEditorText().TrimEnd() == text);

            var current = ide.GetEditorText();
            ide.ReplaceEditorText(current, current.TrimEnd() + "\r\n-- Gr\u00FC\u00DFe \u2192 Ende");
            shown = real?.ReadEditorUnicode() ?? "";
            Line("After edit: " + shown.Replace("\r\n", " | "));
            Check("edit writes umlauts correctly and replaces the arrow", shown.Contains("-- Gr\u00FC\u00DFe -> Ende") && shown.Contains("\u00E4\u00F6\u00FC\u00DF"));

            var q2 = ide.Query("select 'a \u2192 b' x from dual", 1).Rows[0][0];
            Check("arrow in SQL is sent as ->", q2 == "a -> b");
            try { ide.Query("select '\u0141\u00F3d\u017A' x from dual", 1); Check("SQL with a character outside the code page is refused", false); }
            catch (IdeException ex) { Line("Refused: " + ex.Message); Check("SQL with a character outside the code page is refused", ex.Message.Contains("\u0141")); }

            // a file with a character outside the code page: editing it must be refused instead of destroying it
            var file = Path.Combine(_dir, "polish.sql");
            File.WriteAllText(file, "-- Stadt: \u0141\u00F3d\u017A\r\nselect 1 from dual;\r\n", new UTF8Encoding(true));
            if (ide.OpenFile(file))
            {
                await Task.Delay(500);
                var before = real?.ReadEditorUnicode() ?? "";
                Line("File editor shows: " + before.Replace("\r\n", " | "));
                bool refused = false;
                try { ide.ReplaceEditorText(ide.GetEditorText(), ide.GetEditorText() + "\r\n-- x"); }
                catch (IdeException ex) { refused = true; Line("Refused: " + ex.Message); }
                var after = real?.ReadEditorUnicode() ?? "";
                Check("edit of a text with 'Ł' is refused and the text stays intact", refused && after == before && after.Contains("\u0141"));
            }
            else Check("test file opened", false);
        }

        private static async Task RunAsync(Plugin plugin)
        {
            var ide = plugin.Ide;
            try
            {
                Line("# PL/Pilot – in-IDE self test");
                Line($"Started {DateTime.Now:s}");

                for (int i = 0; i < 120 && !ide.GetContext().Connected; i++) await Task.Delay(500);
                var c = ide.GetContext();
                Line($"IDE version {c.IdeVersion}, connection: {c.ConnectionLabel}, dark mode: {ide.DarkMode}");
                if (!c.Connected) throw new Exception("PL/SQL Developer is not logged on.");

                var cfg = AgentConfig.Load();
                var ctx = new ToolContext
                {
                    Ide = ide,
                    Config = cfg,
                    RequestApproval = r => { Line($"> approval requested ({r.Category}): {r.Title}"); return Task.FromResult(ApprovalResult.Yes()); },
                };

                Line("\n## 1. Setup test schema");
                foreach (var t in new[] { "EMP", "DEPT" })
                    await Tool(ctx, "execute_sql", new JObject { ["sql"] = $"drop table {t} purge" }, null);
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "drop package emp_api" }, null);
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "drop function dept_salary_total" }, null);
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "drop procedure pda_tmp" }, null);
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "drop package dept_api" }, null);
                await Tool(ctx, "execute_sql", new JObject
                {
                    ["sql"] = "begin for r in (select object_name from user_objects where object_type = 'FUNCTION' and object_name like 'FN\\_%' escape '\\') " +
                              "loop execute immediate 'drop function ' || r.object_name; end loop; end;",
                }, null);
                await Tool(ctx, "execute_sql", new JObject
                {
                    ["sql"] = "create table dept (deptno number(2) constraint dept_pk primary key, dname varchar2(30) not null, loc varchar2(30))",
                }, "Executed successfully");
                await Tool(ctx, "execute_sql", new JObject
                {
                    ["sql"] = "create table emp (empno number(4) constraint emp_pk primary key, ename varchar2(30) not null, job varchar2(20), " +
                              "sal number(7,2), deptno number(2) constraint emp_dept_fk references dept)",
                }, "Executed successfully");
                await Tool(ctx, "execute_sql", new JObject
                {
                    ["sql"] = "begin\n  insert into dept values (10, 'ACCOUNTING', 'NEW YORK');\n  insert into dept values (20, 'RESEARCH', 'DALLAS');\n" +
                              "  insert into emp values (7782, 'CLARK', 'MANAGER', 2450, 10);\n  insert into emp values (7839, 'KING', 'PRESIDENT', 5000, 10);\n" +
                              "  insert into emp values (7369, 'Müller-Šebestová', 'CLERK', 800, 20);\n  commit;\nend;",
                }, "Executed successfully");
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "comment on table emp is 'Mitarbeiter – Testdaten (äöü)'" }, "Executed successfully");

                Line("\n## 2. Read tools");
                await Tool(ctx, "get_ide_context", new JObject(), "SCOTT");
                await Tool(ctx, "run_query", new JObject { ["sql"] = "select user, 'äöüß€' umlauts, sysdate from dual" }, "äöüß€");
                await Tool(ctx, "run_query", new JObject { ["sql"] = "select ename from emp where deptno = 20" }, "Müller");
                await Tool(ctx, "run_query", new JObject { ["sql"] = "delete from emp" }, "Not executed");
                await Tool(ctx, "list_objects", new JObject(), "EMP");
                await Tool(ctx, "describe_object", new JObject { ["name"] = "emp" }, "EMP_DEPT_FK");

                Line("\n## 3. Compile with errors, then fixed");
                await Tool(ctx, "compile_plsql", new JObject { ["source"] = PackageSource(broken: true) }, "COMPILED WITH ERRORS");
                await Tool(ctx, "get_compile_errors", new JObject { ["name"] = "EMP_API", ["object_type"] = "PACKAGE BODY" }, "L_CNT");
                await Tool(ctx, "get_compile_errors", new JObject(), "Ungültiger");
                // diagnostics: how does a non-ASCII server message arrive (bytes in the DB vs. text via the IDE)?
                await Tool(ctx, "run_query", new JObject
                {
                    ["sql"] = "select dump(substr(text, instr(text, 'ltig') - 2, 3), 16) db_bytes, substr(text, instr(text, 'ltig') - 3, 12) via_ide, " +
                              "(select value from nls_session_parameters where parameter = 'NLS_LANGUAGE') lang from all_errors " +
                              "where owner = user and name = 'EMP_API' and text like '%ltig%'",
                }, null);
                await Tool(ctx, "compile_plsql", new JObject { ["source"] = PackageSource(broken: false) }, "compiled successfully");
                await Tool(ctx, "describe_object", new JObject { ["name"] = "EMP_API" }, "DEPT_COUNT");
                await Tool(ctx, "get_object_source", new JObject { ["name"] = "emp_api", ["with_line_numbers"] = true }, "PACKAGE BODY");
                await Tool(ctx, "get_object_source", new JObject { ["name"] = "dept", ["object_type"] = "TABLE" }, "CREATE TABLE");
                await Tool(ctx, "search_source", new JObject { ["text"] = "dbms_output" }, "r.ename || ' ('");
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "begin emp_api.list_emps(10); end;" }, "KING");
                await Tool(ctx, "run_query", new JObject { ["sql"] = "select emp_api.dept_count(10) cnt from dual" }, "2");

                Line("\n## 4. Editor tools");
                Line($"Open windows at start (restored desktop): {MainThread.Invoke(() => Callbacks.IDE_GetWindowCount())}");
                CloseAllWindows(); // a restored window would show an outdated version of EMP_API
                await Tool(ctx, "open_object", new JObject { ["name"] = "EMP_API", ["object_type"] = "PACKAGE BODY" }, "matches the version compiled");
                await Task.Delay(1000);
                await Tool(ctx, "get_ide_context", new JObject(), "EMP_API");
                var editorText = await Tool(ctx, "get_editor_text", new JObject { ["start_line"] = 1, ["end_line"] = 6 }, "list_emps");
                await Tool(ctx, "edit_editor", new JObject
                {
                    ["old_string"] = "  procedure list_emps(p_deptno in number) is",
                    ["new_string"] = "  -- edited by the agent self test (ä€)\n  procedure list_emps(p_deptno in number) is",
                }, "Editor updated");
                await Tool(ctx, "get_editor_text", new JObject { ["with_line_numbers"] = true, ["start_line"] = 1, ["end_line"] = 5 }, "self test (ä€)");
                var full = await Task.Run(() => ide.GetEditorText());
                // the database changes while the window is open -> opening it again must warn about the difference
                await Tool(ctx, "compile_plsql", new JObject { ["source"] = PackageSource(broken: false) }, "compiled successfully");
                await Tool(ctx, "open_object", new JObject { ["name"] = "EMP_API", ["object_type"] = "PACKAGE BODY" }, "DIFFERS from the version compiled");
                await Tool(ctx, "compile_plsql", new JObject { ["source"] = full }, "compiled successfully");
                await Tool(ctx, "get_selected_text", new JObject(), null);
                await Tool(ctx, "open_new_window", new JObject { ["text"] = "select * from emp;", ["window_type"] = "sql" }, "Opened");

                await NewFeatureTests(ctx, ide);
                await EncodingChecks(ide);
                await SqlThreadChecks(ide, cfg);
                await RewindChecks(ide, cfg);
                Line($"\n**Tool tests: {_pass} passed, {_fail} failed**");

                var prompt = Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_PROMPT");
                if (!string.IsNullOrWhiteSpace(prompt)) await LlmRun(plugin, prompt);
                Line($"\n**Total: {_pass} passed, {_fail} failed**");
            }
            catch (Exception ex)
            {
                Line("\nFATAL: " + ex);
            }
            finally
            {
                Line($"\nFinished {DateTime.Now:s}");
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        private static async Task NewFeatureTests(ToolContext ctx, IIde ide)
        {
            Line("\n## 6. Explain plan, result grid, backups, guard, cursor, mentions, commands");
            await Tool(ctx, "explain_plan", new JObject { ["sql"] = "select e.ename, d.dname from emp e join dept d on d.deptno = e.deptno where e.deptno = :dno" }, "Plan hash value");

            // result grid: run a query in a real SQL window
            ide.NewWindow(IdeWindowType.Sql, "select empno, ename, sal from emp order by empno");
            MainThread.Invoke(() => Callbacks.IDE_Perform(1)); // Execute
            for (int i = 0; i < 40 && MainThread.Invoke(() => Callbacks.IDE_WindowIsRunning()) != 0; i++) await Task.Delay(250);
            await Task.Delay(500);
            var raw = MainThread.Invoke(() =>
            {
                int rows = Callbacks.IDE_ResultGridRowCount(), cols = Callbacks.IDE_ResultGridColCount();
                var sb = new StringBuilder($"rows={rows} cols={cols}\n");
                for (int r = 0; r <= Math.Min(rows, 5); r++)
                {
                    for (int c = 0; c < cols; c++) sb.Append($"[{Callbacks.Str(Callbacks.IDE_ResultGridCell(c, r))}]");
                    sb.Append('\n');
                }
                return sb.ToString();
            });
            Line("Raw result grid (col, row):\n```\n" + raw + "```");
            var grid = await Tool(ctx, "get_result_grid", new JObject { ["max_rows"] = 10 }, "(3 rows)");
            Check("result grid has titles and all rows incl. the last one", grid.Contains("EMPNO | ENAME | SAL") && grid.Contains("Müller-Šebestová") && grid.Contains("KING"));

            // backups: create, drop (-> backup), restore
            await Tool(ctx, "execute_sql", new JObject { ["sql"] = "create or replace procedure pda_tmp is\nbegin\n  null; -- backup test\nend;" }, "Executed successfully");
            await Tool(ctx, "execute_sql", new JObject { ["sql"] = "drop procedure pda_tmp" }, "backed up");
            var list = await Tool(ctx, "list_backups", new JObject { ["name"] = "pda_tmp" }, "PROCEDURE SCOTT.PDA_TMP");
            var backupId = list.Split('\n').Skip(1).FirstOrDefault()?.Split(' ').FirstOrDefault() ?? "";
            await Tool(ctx, "restore_backup", new JObject { ["id"] = backupId }, "compiled successfully");
            await Tool(ctx, "run_query", new JObject { ["sql"] = "select object_name, status from user_objects where object_name = 'PDA_TMP'" }, "VALID");

            // production guard
            var c = ide.GetContext();
            var label = $"{c.User}@{c.Database}";
            var guard = new ConnectionGuard();
            var gcfg = ctx.Config.Clone();
            gcfg.ProtectedConnections = "*FREEPDB*";
            Check("guard: matching pattern -> read-only", guard.Evaluate(gcfg, c).Mode == GuardMode.Protected && guard.Evaluate(gcfg, c).ReadOnly);
            guard.SetOverride(label, true);
            Check("guard: unlocked for the session", guard.Evaluate(gcfg, c).Mode == GuardMode.ProtectedOverridden && !guard.Evaluate(gcfg, c).ReadOnly);
            guard.SetOverride(label, null);
            gcfg.ProtectedConnections = "*PROD*";
            Check("guard: other connection not protected", guard.Evaluate(gcfg, c).Mode == GuardMode.Normal);
            guard.SetOverride(label, false);
            Check("guard: manual lock", guard.Evaluate(gcfg, c).Mode == GuardMode.Locked && guard.Evaluate(gcfg, c).ReadOnly);

            // cursor semantics for "Insert at cursor"
            ide.NewWindow(IdeWindowType.Sql, "line one\r\nline two\r\nline three");
            MainThread.Invoke(() => Callbacks.IDE_SetCursor(6, 2));
            c = ide.GetContext();
            Check($"cursor position read back (line {c.CursorLine}, col {c.CursorColumn})", c.CursorLine == 2 && c.CursorColumn == 6);
            var before = ide.GetEditorText();
            ide.ReplaceEditorText(before, EditorText.InsertAt(before, c.CursorLine, c.CursorColumn, "XX"));
            Check("insert at cursor", ide.GetEditorText().Replace("\r\n", "\n") == "line one\nline XXtwo\nline three");

            // @-mentions and commands
            var (attach, items) = await Mentions.ResolveAsync("what does @EMP_API do with @dept?", ctx);
            Line("Mention chips: " + items.ToString(Formatting.None));
            Check("mentions attach package source and table description",
                attach.IndexOf("package body", StringComparison.OrdinalIgnoreCase) >= 0 && attach.Contains("Columns:") && items.Count == 2);
            var suggestions = await Task.Run(() => Mentions.Suggest("EMP", ctx));
            Check("mention suggestions: " + string.Join(", ", suggestions.Select(s => (string)s["label"])), suggestions.Any(s => (string)s["label"] == "EMP_API"));
            var cmd = Commands.Find("/review focus on exceptions", ctx.Config, out var cmdArgs);
            Check("command /review expands with arguments", cmd != null && Commands.Expand(cmd, cmdArgs).Contains("focus on exceptions"));
        }

        private static void Check(string name, bool ok)
        {
            if (ok) _pass++; else _fail++;
            Line($"\n### {(ok ? "PASS" : "FAIL")} {name}");
        }

        private static async Task<string> Tool(ToolContext ctx, string name, JObject args, string expect)
        {
            var tool = ToolRegistry.All().First(t => t.Name == name);
            string result;
            var sw = Stopwatch.StartNew();
            try { result = await Task.Run(() => tool.ExecuteAsync(args, ctx)); }
            catch (Exception ex) { result = "EXCEPTION " + ex.GetType().Name + ": " + ex.Message; }
            bool ok = expect == null || result.IndexOf(expect, StringComparison.OrdinalIgnoreCase) >= 0;
            if (expect != null) { if (ok) _pass++; else _fail++; }
            Line($"\n### {(expect == null ? "·" : ok ? "PASS" : "FAIL")} `{name}` {args.ToString(Formatting.None)}  ({sw.ElapsedMilliseconds} ms)");
            if (expect != null && !ok) Line($"expected to contain: `{expect}`");
            Line("```\n" + (result.Length > 3000 ? result.Substring(0, 3000) + "\n…" : result) + "\n```");
            return result;
        }

        /// <summary>Does the model follow a company rule it only knows from the rules file?</summary>
        private static async Task RulesRun(Plugin plugin)
        {
            Line("\n## 7. Company rules with the real model");
            plugin.OnMenuClick(Bootstrap.MiChat);
            var chat = plugin.ChatWindow;
            for (int i = 0; i < 150 && !chat.IsChatReady; i++) await Task.Delay(200);
            await chat.ExecuteScriptAsync("setInterval(() => { const b = document.querySelector('.approval:not(.done) button.primary'); if (b) b.click(); }, 700);");

            var rulesFile = Path.Combine(_dir, "company-rules.md");
            File.WriteAllText(rulesFile,
                "# Test company rules\n" +
                "- Every standalone FUNCTION you create must have a name that starts with the prefix FN_ (for example FN_ORDER_COUNT).\n" +
                "- Local variables start with the prefix v_ (for example v_count).\n", new UTF8Encoding(false));
            chat.ApplyConfigForTest(c => c.CompanyRulesPath = rulesFile);
            try
            {
                await SendAndWait(chat, "Create a standalone function in my schema that returns the number of rows in table DEPT. Compile it and test it with a query.");
                var fns = await Task.Run(() => plugin.Ide.Query(
                    "select object_name, status from user_objects where object_type = 'FUNCTION' and object_name like 'FN\\_%' escape '\\'", 10));
                Line("FN_ functions: " + string.Join(", ", fns.Rows.Select(r => r[0] + " " + r[1])));
                var fn = fns.Rows.FirstOrDefault(r => r[1] == "VALID");
                Check("rule followed: the new function is named FN_… and compiled", fn != null);
                if (fn != null)
                {
                    var src = await Task.Run(() => plugin.Ide.Query(
                        $"select text from user_source where name = '{fn[0]}' and type = 'FUNCTION' order by line", 500));
                    var code = string.Concat(src.Rows.Select(r => r[0]));
                    Line("```sql\n" + code.Trim() + "\n```");
                    Check("rule followed: local variables use the v_ prefix", System.Text.RegularExpressions.Regex.IsMatch(code, @"\bv_\w+", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                }
                await ScreenshotAsync(chat, Path.Combine(_dir, "chat-rules.png"));
            }
            finally
            {
                chat.ApplyConfigForTest(c => c.CompanyRulesPath = "");
            }
        }

        /// <summary>German UI with the real model; screenshot of the IDE with the Plug-Ins ribbon tab.</summary>
        private static async Task GermanRun(Plugin plugin)
        {
            Line("\n## 8. German UI with the real model");
            Line("UI language: " + L.Lang);
            plugin.OnMenuClick(Bootstrap.MiChat);
            var chat = plugin.ChatWindow;
            for (int i = 0; i < 150 && !chat.IsChatReady; i++) await Task.Delay(200);
            await SendAndWait(chat, "/explain");
            await SendAndWait(chat, "Wie viele Zeilen hat die Tabelle EMP? Antworte in einem kurzen Satz.");
            var transcript = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('messages').innerText") ?? "\"\"");
            File.WriteAllText(Path.Combine(_dir, "transcript-de.txt"), transcript, Encoding.UTF8);
            var last = transcript.Length > 600 ? transcript.Substring(transcript.Length - 600) : transcript;
            Line("End of the transcript:\n```\n" + last.Trim() + "\n```");
            var placeholder = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('input').placeholder") ?? "\"\"") ?? "";
            Line("Input placeholder: " + placeholder);
            Check("UI is German", transcript.Contains("Sie") && placeholder.Contains("PL/Pilot fragen"));
            Check("answer is German", System.Text.RegularExpressions.Regex.IsMatch(last, @"\b(Zeilen|Tabelle|enthält|Datensätze)\b"));
            await ScreenshotAsync(chat, Path.Combine(_dir, "chat-de.png"));

            // the model writes a German comment with umlauts into the editor (reported bug: "fÃ¼r")
            plugin.Ide.NewWindow(IdeWindowType.Sql, "select * from emp;");
            await chat.ExecuteScriptAsync("setInterval(() => { const b = document.querySelector('.approval:not(.done) button.primary'); if (b) b.click(); }, 700);");
            await SendAndWait(chat, "Füge im aktiven SQL-Fenster als erste Zeile genau diesen Kommentar ein: " +
                "-- Prüfung für Größe und Gewicht (Änderung €). Sonst nichts ändern.");
            var editorText = (plugin.Ide as PlsqlIde)?.ReadEditorUnicode() ?? "";
            Line("Editor after the model's edit:\n```\n" + editorText.Trim() + "\n```");
            Check("model writes umlauts and € correctly into the editor",
                editorText.Contains("Prüfung für Größe und Gewicht") && editorText.Contains("€") && !editorText.Contains("Ã"));

            // the IDE with the Plug-Ins ribbon tab (shortcut Alt+X) shows the German menu texts
            MainThread.Invoke(() => Callbacks.IDE_KeyPress?.Invoke(0x58, 2));
            await Task.Delay(1000);
            try
            {
                var h = new IntPtr(MainThread.Invoke(() => Callbacks.IDE_GetWindowHandle()));
                if (GetWindowRect(h, out var r))
                {
                    var b = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    using (var bmp = new System.Drawing.Bitmap(b.Width, b.Height))
                    {
                        // PrintWindow also captures the IDE when other windows cover it
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            var hdc = g.GetHdc();
                            bool ok = PrintWindow(h, hdc, 2 /* PW_RENDERFULLCONTENT */);
                            g.ReleaseHdc(hdc);
                            if (!ok) g.CopyFromScreen(b.Location, System.Drawing.Point.Empty, b.Size);
                        }
                        bmp.Save(Path.Combine(_dir, "ide-de.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
            }
            catch (Exception ex) { Line("IDE screenshot failed: " + ex.Message); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

        private static async Task LlmRun(Plugin plugin, string prompt)
        {
            if (prompt == "RULES-ONLY") { await RulesRun(plugin); return; }
            if (prompt == "DE-ONLY") { await GermanRun(plugin); return; }
            Line("\n## 5. Real LLM run in the chat window");
            Line("Prompt: " + prompt);
            plugin.OnMenuClick(Bootstrap.MiChat);
            var chat = plugin.ChatWindow;
            for (int i = 0; i < 150 && !chat.IsChatReady; i++) await Task.Delay(200);
            await chat.ExecuteScriptAsync("setInterval(() => { const b = document.querySelector('.approval:not(.done) button.primary'); if (b) b.click(); }, 700);");
            await chat.ExecuteScriptAsync("document.getElementById('input').value = " + JsonConvert.SerializeObject(prompt) + "; submit();");
            var sw = Stopwatch.StartNew();
            bool sawBusy = false;
            while (sw.Elapsed < TimeSpan.FromMinutes(10))
            {
                await Task.Delay(500);
                if (chat.IsBusy) sawBusy = true;
                else if (sawBusy || sw.Elapsed > TimeSpan.FromSeconds(20)) break;
            }
            await Task.Delay(1500);
            Line($"LLM run finished after {sw.Elapsed.TotalSeconds:0} s (busy now: {chat.IsBusy})");

            var afterMain = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('messages').innerText") ?? "\"\"");
            Line("Task list after the main task: " + JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('tasks').innerText") ?? "\"\"").Replace("\n", " | "));
            Check("agent planned with update_tasks", afterMain.Contains("update_tasks"));
            if (prompt.Contains("DEPT_API"))
            {
                var ctx = plugin.Ide;
                var r = await Task.Run(() => ctx.Query(
                    "select (select status from user_objects where object_name = 'DEPT_API' and object_type = 'PACKAGE BODY') body_status, " +
                    "dept_api.dept_name(10) dname, dept_api.emp_count(10) cnt, dept_api.total_salary(10) total from dual", 1));
                var row = r.Rows.FirstOrDefault() ?? new string[0];
                Line("DEPT_API check: " + string.Join(" | ", row));
                Check("DEPT_API compiled and returns ACCOUNTING / 2 / 7450",
                    row.Length == 4 && row[0] == "VALID" && row[1] == "ACCOUNTING" && row[2] == "2" && row[3].StartsWith("7450"));
            }
            await ScreenshotAsync(chat, Path.Combine(_dir, "chat-main.png"));

            Line("\n### Follow-ups: sub-task, /compact and an @-mention question");
            await SendAndWait(chat, "Use run_subtask to let a helper find out which objects in my schema reference the table EMP. Then summarize its findings in two sentences.");
            var sub = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("[...document.querySelectorAll('.substeps')].map(s => s.innerText).join(' | ')") ?? "\"\"");
            Line("Helper steps: " + sub.Replace("\n", " "));
            Check("helper agent ran its own tool calls", sub.Length > 0);
            await SendAndWait(chat, "/compact");
            await SendAndWait(chat, "@DEPT How many columns does this table have? Answer with the number only.");
            var transcript = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('messages').innerText") ?? "\"\"");
            File.WriteAllText(Path.Combine(_dir, "transcript.txt"), transcript, Encoding.UTF8);
            Check("chat shows the compaction summary", transcript.Contains("Conversation compacted"));
            Check("chat shows the @DEPT attachment chip", transcript.Contains("SCOTT.DEPT"));
            var meter = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('meter').title") ?? "\"\"");
            Line("Token meter: " + meter);
            Check("token meter shows usage", meter.Contains(" of "));
            var saved = ChatStore.List().FirstOrDefault();
            Line($"Newest saved chat: {saved?.Title} ({saved?.Messages} messages, {saved?.Updated:HH:mm:ss})");
            Check("chat was saved", saved != null && saved.Title.StartsWith(prompt.Substring(0, 20)));
            await ScreenshotAsync(chat, Path.Combine(_dir, "chat2.png"));
        }

        private static async Task SendAndWait(ChatForm chat, string text)
        {
            await chat.ExecuteScriptAsync("document.getElementById('input').value = " + JsonConvert.SerializeObject(text) + "; submit();");
            var sw = Stopwatch.StartNew();
            bool sawBusy = false;
            while (sw.Elapsed < TimeSpan.FromMinutes(5))
            {
                await Task.Delay(500);
                if (chat.IsBusy) sawBusy = true;
                else if (sawBusy || sw.Elapsed > TimeSpan.FromSeconds(15)) break;
            }
            await Task.Delay(1000);
            Line($"'{text}' finished after {sw.Elapsed.TotalSeconds:0} s");
        }

        private static async Task ScreenshotAsync(System.Windows.Forms.Form form, string path)
        {
            try
            {
                form.Activate();
                await Task.Delay(700);
                var b = form.Bounds;
                using (var bmp = new System.Drawing.Bitmap(b.Width, b.Height))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, System.Drawing.Point.Empty, b.Size);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            catch (Exception ex) { Line("screenshot failed: " + ex.Message); }
        }

        /// <summary>Closes all windows (so the IDE does not restore test windows next time) and exits the IDE.</summary>
        private static async Task CloseIde()
        {
            try
            {
                Plugin.Instance?.ChatWindow?.Hide();
                CloseAllWindows();
                await Task.Delay(500);
                PostMessage(new IntPtr(Callbacks.IDE_GetWindowHandle()), 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Error("Self test close failed", ex);
            }
        }

        /// <summary>
        /// Activates and closes every child window. Modified windows are first saved to a temp file so that
        /// closing them does not show a "save changes?" dialog.
        /// </summary>
        private static void CloseAllWindows()
        {
            var tmp = Path.Combine(_dir, "closed-windows");
            Directory.CreateDirectory(tmp);
            int before = 0, after = 0;
            MainThread.Invoke(() =>
            {
                before = Callbacks.IDE_GetWindowCount();
                for (int guard = 0; guard < 30; guard++)
                {
                    int count = Callbacks.IDE_GetWindowCount();
                    if (count == 0) break;
                    Callbacks.IDE_ActivateWindow(count - 1);
                    if (Callbacks.IDE_WindowIsModified() != 0)
                    {
                        using (var f = new Callbacks.NativeString(Path.Combine(tmp, $"window{guard}.sql"))) Callbacks.IDE_SetFilename(f.Ptr);
                        Callbacks.IDE_SaveFile();
                    }
                    Callbacks.IDE_CloseFile();
                }
                after = Callbacks.IDE_GetWindowCount();
            });
            Line($"Closed windows: {before} -> {after}");
        }

        private static void Line(string s)
        {
            Report.AppendLine(s);
            Log.Info("SELFTEST " + (s.Length > 200 ? s.Substring(0, 200) : s).Replace("\n", " "));
        }

        private static string PackageSource(bool broken) =>
            "create or replace package emp_api is\n" +
            "  procedure list_emps(p_deptno in number);\n" +
            "  function dept_count(p_deptno in number) return number;\n" +
            "end emp_api;\n" +
            "/\n" +
            "create or replace package body emp_api is\n" +
            "  procedure list_emps(p_deptno in number) is\n" +
            "  begin\n" +
            "    for r in (select * from emp e where e.deptno = p_deptno order by e.empno) loop\n" +
            "      dbms_output.put_line(r.ename || ' (' || r.job || ')');\n" +
            "    end loop;\n" +
            "  end list_emps;\n" +
            "\n" +
            "  function dept_count(p_deptno in number) return number is\n" +
            "    l_count number;\n" +
            "  begin\n" +
            "    select count(*) into " + (broken ? "l_cnt" : "l_count") + " from emp where deptno = p_deptno;\n" +
            "    return l_count;\n" +
            "  end dept_count;\n" +
            "end emp_api;\n" +
            "/\n";

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
