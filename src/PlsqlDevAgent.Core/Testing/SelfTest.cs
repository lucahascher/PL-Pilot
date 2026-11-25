using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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
            if (mode == "HTTPS-ONLY") { _ = HttpsRunAsync(); return; } // no database access
            _ = StartOnConfirmedConnection(plugin, mode);
        }

        private static async Task StartOnConfirmedConnection(Plugin plugin, string mode)
        {
            if (!await ConfirmTestConnection(plugin.Ide)) return;
            if (mode == "ENC-ONLY") await EncodingRunAsync(plugin);
            else if (mode == "SQL-ONLY") await SqlRunAsync(plugin);
            else if (mode == "REWIND-ONLY") await SqlRunAsync(plugin, rewindOnly: true);
            else if (mode == "TXN-ONLY") await SqlRunAsync(plugin, transactionOnly: true);
            else if (mode == "TXN-CLOSE") await CloseWithTransactionAsync(plugin);
            else if (mode == "SEC-ONLY") await SqlRunAsync(plugin, securityOnly: true);
            else if (mode == "ANALYSIS-ONLY") await AnalysisRunAsync(plugin);
            else if (mode == "LLM-TOOLS") await LlmToolsRunAsync(plugin);
            else if (mode == "CACHE-MEASURE") await CacheMeasureRunAsync(plugin);
            else if (mode == "FREEZE-MEASURE") await FreezeMeasureRunAsync(plugin);
            else if (mode == "WINDOW-ONLY") await WindowBindingRunAsync(plugin);
            else if (mode == "BIGUNIT-LLM") await BigUnitLlmRunAsync(plugin);
            else if (mode == "PARALLEL-ONLY") await ParallelRunAsync(plugin);
            else if (mode != null && mode.StartsWith("PROMPT-ONLY:")) await PromptOnlyRunAsync(plugin, mode.Substring("PROMPT-ONLY:".Length).Trim());
            else if (mode != null && mode.StartsWith("PLAY:")) await PlayRunAsync(plugin, mode.Substring("PLAY:".Length).Trim());
            else await RunAsync(plugin);
        }

        /// <summary>
        /// The self test creates, changes and drops objects (EMP, DEPT, packages, procedures) in the connected schema.
        /// It only runs when PLSQLDEVAGENT_AUTOTEST_CONNECTION names exactly the connection it is allowed to use
        /// (USER@DATABASE as in the chat header) and that connection is not protected; otherwise nothing happens.
        /// </summary>
        private static async Task<bool> ConfirmTestConnection(IIde ide)
        {
            for (int i = 0; i < 120 && !ide.GetContext().Connected; i++) await Task.Delay(500);
            var c = ide.GetContext();
            var label = $"{c.User}@{c.Database}";
            var allowed = (Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_CONNECTION") ?? "").Trim();
            string refusal = null;
            if (!c.Connected) refusal = "PL/SQL Developer is not logged on";
            else if (allowed.Length == 0) refusal = "PLSQLDEVAGENT_AUTOTEST_CONNECTION is not set";
            else if (!string.Equals(allowed, label, StringComparison.OrdinalIgnoreCase)) refusal = $"logged on as {label}, but PLSQLDEVAGENT_AUTOTEST_CONNECTION is {allowed}";
            else
            {
                var identity = await Task.Run(() => ide.DatabaseIdentity);
                var pattern = ConnectionGuard.MatchingPattern(AgentConfig.Load().ProtectedConnections, c, identity);
                if (pattern != null) refusal = $"the connection is protected (pattern {pattern})";
            }
            if (refusal == null) return true;
            Log.Warn("Self test NOT started: " + refusal + ". It creates and drops objects in the connected schema.");
            Line("# PL/Pilot self test – REFUSED\n" + refusal);
            try { File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8); } catch { }
            return false;
        }

        /// <summary>
        /// Closes PL/SQL Developer while the agent session has an uncommitted UPDATE (SAL of 7839 + 1000) and answers
        /// the plug-in's question automatically: PLSQLDEVAGENT_AUTOTEST_ANSWER = commit | rollback (default).
        /// The result is checked afterwards in the database (the report is written before closing).
        /// </summary>
        private static async Task CloseWithTransactionAsync(Plugin plugin)
        {
            var ide = plugin.Ide;
            Line("# PL/Pilot – closing with uncommitted changes");
            for (int i = 0; i < 120 && !ide.GetContext().Connected; i++) await Task.Delay(500);
            var journal = ChangeJournal.For("selftest-close");
            var ctx = new ToolContext
            {
                Ide = ide, Config = AgentConfig.Load(), RequestApproval = r => Task.FromResult(ApprovalResult.Yes()),
                RecordChange = r => journal.Add(r),
            };
            await Tool(ctx, "execute_sql", new JObject { ["sql"] = "update emp set sal = sal + 1000 where empno = 7839" }, "not committed");
            Line("Open transaction before closing: " + ide.PendingTransaction);
            bool commit = Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_ANSWER") == "commit";
            File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
            QuestionDialog.ShownForTest = d =>
            {
                // a picture of the dialog proves that its text is readable (MessageBox text vanished in the dark theme)
                try
                {
                    using (var bmp = new System.Drawing.Bitmap(d.Width, d.Height))
                    {
                        d.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, d.Width, d.Height));
                        bmp.Save(Path.Combine(_dir, "close-dialog.png"));
                    }
                }
                catch (Exception ex) { Log.Warn("Close dialog picture failed: " + ex.Message); }
                Log.Info($"Self test: answering the close dialog ({d.Question.Replace("\n", " ")}) with " + (commit ? "commit" : "rollback"));
                d.BeginInvoke((Action)(() => d.Choose(commit ? "commit" : "rollback")));
            };
            await CloseIde();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string title);

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

        /// <summary>Only the analysis checks (database facts, compiler settings, dependencies, error stacks).</summary>
        private static async Task AnalysisRunAsync(Plugin plugin)
        {
            try
            {
                Line("# PL/Pilot – analysis self test");
                var c = plugin.Ide.GetContext();
                Line($"IDE version {c.IdeVersion}, connection: {c.ConnectionLabel}");
                var ctx = new ToolContext { Ide = plugin.Ide, Config = AgentConfig.Load(), RequestApproval = r => Task.FromResult(ApprovalResult.Yes()) };
                await AnalysisChecks(ctx, plugin.Ide);
                await ExploreChecks(ctx);
                await MeasureChecks(ctx, plugin.Ide);
                await UnitTestChecks(ctx, plugin.Ide);
                Line($"\n**Total: {_pass} passed, {_fail} failed**");
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        private static string AnalysisPackage(int variant) =>
            "create or replace package pda_an_pkg is\n" +
            (variant == 0 ? "  function total return pda_an_t.amount%type;\n"
             : variant == 1 ? "  function total(p_min in number default null) return pda_an_t.amount%type;\n" : "") +
            "  procedure div(p in number);\n" +
            "  procedure show_stack;\n" +
            "end pda_an_pkg;\n" +
            "/\n" +
            "create or replace package body pda_an_pkg is\n" +
            (variant == 2 ? "" :
            "  function total" + (variant == 1 ? "(p_min in number default null)" : "") + " return pda_an_t.amount%type is\n" +
            "    l_sum pda_an_t.amount%type;\n" +
            "  begin\n" +
            "    select sum(amount) into l_sum from pda_an_t;\n" +
            "    return l_sum;\n" +
            "  end total;\n" +
            "\n") +
            "  procedure div(p in number) is\n" +
            "    l number;\n" +
            "  begin\n" +
            "    l := 1 / p;\n" +
            "  end div;\n" +
            "\n" +
            "  procedure show_stack is\n" +
            "  begin\n" +
            "    dbms_output.put_line(dbms_utility.format_call_stack);\n" +
            "  end show_stack;\n" +
            "end pda_an_pkg;\n" +
            "/\n";

        /// <summary>
        /// Object dossiers (explore_objects) against the real database: a small data model (customer ← order ← item)
        /// with a trigger, a view, a package that reads and writes it and a procedure that calls the package.
        /// </summary>
        private static async Task ExploreChecks(ToolContext ctx)
        {
            Line("\n## 12b. Object dossiers (explore_objects)");
            var schema = await Task.Run(() => ctx.CurrentSchema);
            async Task<string> Exec(string sql, string expect = null) => await Tool(ctx, "execute_sql", new JObject { ["sql"] = sql }, expect);
            var drops = new[] { "drop procedure pda_ex_p", "drop package pda_ex_pkg", "drop view pda_ex_open_v", "drop sequence pda_ex_order_seq",
                                "drop table pda_ex_item purge", "drop table pda_ex_order purge", "drop table pda_ex_customer purge" };
            foreach (var drop in drops) await Exec(drop);
            try
            {
                Line("\n### Setup");
                await Exec("create table pda_ex_customer (id number constraint pda_ex_customer_pk primary key, name varchar2(100) not null, email varchar2(200))", "Executed successfully");
                await Exec("comment on column pda_ex_customer.email is 'Contact address for invoices'", "Executed successfully");
                await Exec("create table pda_ex_order (id number constraint pda_ex_order_pk primary key, " +
                           "customer_id number not null constraint pda_ex_order_customer_fk references pda_ex_customer, amount number(12,2), status varchar2(1) default 'O' not null)",
                           "Executed successfully");
                await Exec("comment on column pda_ex_order.status is 'O = open, C = closed'", "Executed successfully");
                await Exec("create table pda_ex_item (id number constraint pda_ex_item_pk primary key, " +
                           "order_id number not null constraint pda_ex_item_order_fk references pda_ex_order, qty number)", "Executed successfully");
                await Exec("create sequence pda_ex_order_seq", "Executed successfully");
                await Exec("create or replace trigger pda_ex_order_trg\nbefore insert on pda_ex_order\nfor each row\nbegin\n" +
                           "  if :new.id is null then\n    :new.id := pda_ex_order_seq.nextval;\n  end if;\nend;", "Executed successfully");
                await Tool(ctx, "compile_plsql", new JObject
                {
                    ["source"] = "create or replace view pda_ex_open_v as select id, customer_id, amount from pda_ex_order where status = 'O'\n/\n" +
                                 "create or replace package pda_ex_pkg is\n  procedure place(p_customer_id in number, p_amount in number);\n" +
                                 "  function open_total(p_customer_id in number) return number;\nend pda_ex_pkg;\n/\n" +
                                 "create or replace package body pda_ex_pkg is\n  procedure place(p_customer_id in number, p_amount in number) is\n    l_name pda_ex_customer.name%type;\n  begin\n" +
                                 "    select name into l_name from pda_ex_customer where id = p_customer_id;\n" +
                                 "    insert into pda_ex_order (customer_id, amount) values (p_customer_id, p_amount);\n  end place;\n\n" +
                                 "  function open_total(p_customer_id in number) return number is\n    l_sum number;\n  begin\n" +
                                 "    select sum(amount) into l_sum from pda_ex_open_v where customer_id = p_customer_id;\n    return nvl(l_sum, 0);\n  end open_total;\nend pda_ex_pkg;\n/\n" +
                                 "create or replace procedure pda_ex_p is\nbegin\n  pda_ex_pkg.place(1, 10);\nend pda_ex_p;\n/\n",
                }, "compiled successfully");

                Line("\n### Dossiers");
                var sw = Stopwatch.StartNew();
                var pkg = await Tool(ctx, "explore_objects", new JObject { ["names"] = new JArray("pda_ex_pkg") }, "## PACKAGE " + schema + ".PDA_EX_PKG");
                var ms = sw.ElapsedMilliseconds;
                Check("package dossier: specification and body, the used tables described with columns and foreign key, the view, the caller's line",
                    pkg.Contains("procedure place(p_customer_id") && pkg.Contains("insert into pda_ex_order") &&
                    pkg.Contains($"TABLE {schema}.PDA_EX_ORDER – used by PDA_EX_PKG") && pkg.Contains("CUSTOMER_ID NUMBER NOT NULL") &&
                    pkg.Contains($"FK (CUSTOMER_ID) → {schema}.PDA_EX_CUSTOMER") && pkg.Contains($"VIEW {schema}.PDA_EX_OPEN_V") &&
                    pkg.Contains($"PROCEDURE {schema}.PDA_EX_P") && pkg.Contains("line 3: pda_ex_pkg.place(1, 10);"));
                Check($"package dossier in {ms} ms ({pkg.Length} chars)", ms < 10000);

                var order = await Tool(ctx, "explore_objects", new JObject { ["names"] = new JArray("PDA_EX_ORDER") }, "## TABLE " + schema + ".PDA_EX_ORDER");
                Check("table dossier: column comment, incoming foreign key, trigger with source, the package's writing line, the view as a user",
                    order.Contains("O = open, C = closed") && order.Contains($"{schema}.PDA_EX_ITEM (ORDER_ID) → PDA_EX_ORDER") &&
                    order.Contains($"TRIGGER {schema}.PDA_EX_ORDER_TRG") && order.Contains("pda_ex_order_seq.nextval") &&
                    Regex.IsMatch(order, @"line \d+ \(writes\): insert into pda_ex_order") && order.Contains($"VIEW {schema}.PDA_EX_OPEN_V") &&
                    order.Contains($"TABLE {schema}.PDA_EX_CUSTOMER – foreign-key neighbour of PDA_EX_ORDER") && order.Contains("Contact address for invoices"));

                var hop1 = await Tool(ctx, "explore_objects", new JObject { ["names"] = new JArray("pda_ex_customer") }, "PDA_EX_ORDER");
                var hop2 = await Tool(ctx, "explore_objects", new JObject { ["names"] = new JArray("pda_ex_customer"), ["depth"] = 2 }, "PDA_EX_ITEM");
                Check("depth 1 shows the direct neighbour only, depth 2 also the table that references it",
                    !hop1.Contains("PDA_EX_ITEM") && hop2.Contains($"TABLE {schema}.PDA_EX_ITEM – foreign-key neighbour of PDA_EX_ORDER (hop 2)"));

                var both = await Tool(ctx, "explore_objects", new JObject { ["names"] = new JArray("pda_ex_pkg", "pda_ex_order", "pda_ex_pkg.place", "pda_ex_nothing") }, "# Dossier:");
                Check("several names in one call: each object once, PACKAGE.SUBPROGRAM shows the package, unknown names noted",
                    Regex.Matches(both, "^## ", RegexOptions.Multiline).Count >= 2 && Regex.Matches(both, $"^## PACKAGE {schema}.PDA_EX_PKG$", RegexOptions.Multiline).Count == 1 &&
                    both.Contains("pda_ex_nothing: no such object") && !both.Contains($"TABLE {schema}.PDA_EX_ORDER – used by"));

                var dbaCfg = ctx.Config.Clone();
                dbaCfg.UseDbaViews = true;
                var dbaCtx = new ToolContext { Ide = ctx.Ide, Config = dbaCfg, RequestApproval = ctx.RequestApproval };
                var dba = await Tool(dbaCtx, "explore_objects", new JObject { ["names"] = new JArray("pda_ex_pkg") }, "## PACKAGE " + schema + ".PDA_EX_PKG");
                Check("the same package dossier with the DBA views setting", dba == pkg);
            }
            finally
            {
                foreach (var drop in drops) await Exec(drop);
            }
        }

        /// <summary>
        /// Database facts for the system prompt, compiler settings kept and warnings reported, impact analysis,
        /// recompiling invalidated objects and resolving error stacks, against the real database.
        /// </summary>
        private static async Task AnalysisChecks(ToolContext ctx, IIde ide)
        {
            Line("\n## 12. Database facts, compiler settings, impact analysis, error stacks");
            var info = await Task.Run(() => DatabaseInfo.Load(ide));
            var section = info?.PromptSection() ?? "";
            Line("```\n" + section + "```");
            Check($"database facts: version {info?.Version}, NLS settings and character set read",
                info != null && info.Major >= 11 && section.Contains("NLS settings") && section.Contains("Character set"));
            Check("system prompt contains the database section", SystemPrompt.Build(ctx.Config, false, info).Contains("# Connected database"));

            var schema = await Task.Run(() => ctx.CurrentSchema);
            async Task<string> Exec(string sql, string expect = null) => await Tool(ctx, "execute_sql", new JObject { ["sql"] = sql }, expect);
            const string settingsProbe = "begin dbms_output.put_line('session: ccflags=[' || $$PLSQL_CCFLAGS || '] optimize=' || $$PLSQL_OPTIMIZE_LEVEL || ' warnings=' || $$PLSQL_WARNINGS); end;";
            string SessionSettings(string execResult) => execResult.Substring(Math.Max(0, execResult.IndexOf("session:", StringComparison.Ordinal))).Trim();

            foreach (var drop in new[] { "drop trigger pda_an_trg", "drop view pda_an_v2", "drop view pda_an_v", "drop procedure pda_an_p", "drop procedure pda_an_flags",
                                         "drop procedure pda_an_warn", "drop package pda_an_pkg", "drop table pda_an_t purge" })
                await Exec(drop);
            try
            {
                Line("\n### Setup");
                await Exec("create table pda_an_t (id number constraint pda_an_t_pk primary key, amount number(10,2), note varchar2(30))", "Executed successfully");
                await Exec("begin\n  for i in 1 .. 100 loop insert into pda_an_t values (i, i * 10, case when mod(i, 10) = 0 then 'tenth' end); end loop;\n  commit;\n" +
                           "  dbms_stats.gather_table_stats(user, 'PDA_AN_T');\nend;", "Executed successfully");
                await Tool(ctx, "compile_plsql", new JObject { ["source"] = AnalysisPackage(0) }, "compiled successfully");
                await Tool(ctx, "compile_plsql", new JObject
                {
                    ["source"] = "create or replace view pda_an_v as select id, note from pda_an_t\n/\n" +
                                 "create or replace view pda_an_v2 as select id from pda_an_v\n/\n" +
                                 "create or replace procedure pda_an_p is\n  l number;\nbegin\n  l := pda_an_pkg.total;\nend pda_an_p;\n/\n",
                }, "compiled successfully");

                Line("\n### Statistics and impact analysis");
                var stats = await Tool(ctx, "describe_object", new JObject { ["name"] = "pda_an_t", ["include_statistics"] = true }, "CLUSTERING_FACTOR");
                Check("statistics: table row count and column statistics", stats.Contains("Table statistics") && stats.Contains("NUM_DISTINCT") && stats.Contains("100"));
                var usedBy = await Tool(ctx, "find_dependencies", new JObject { ["name"] = "pda_an_t" }, "PDA_AN_V");
                // PDA_AN_P is a direct user as well: it calls a function returning PDA_AN_T.AMOUNT%TYPE
                var level2 = usedBy.IndexOf("level 2:", StringComparison.Ordinal);
                Check("find_dependencies: direct users on level 1, the view on the view on level 2",
                    level2 > 0 && usedBy.IndexOf($"PROCEDURE {schema}.PDA_AN_P", StringComparison.Ordinal) is int p && p > 0 && p < level2 &&
                    usedBy.IndexOf($"VIEW {schema}.PDA_AN_V2", StringComparison.Ordinal) > level2);
                await Tool(ctx, "find_dependencies", new JObject { ["name"] = "pda_an_t", ["column"] = "amount" }, "sum(amount)");
                await Tool(ctx, "find_dependencies", new JObject { ["name"] = "pda_an_p", ["direction"] = "uses" }, $"PACKAGE {schema}.PDA_AN_PKG");

                Line("\n### Compiler warnings and settings");
                await Tool(ctx, "compile_plsql", new JObject
                {
                    ["source"] = "create or replace procedure pda_an_warn is\n  l_x varchar2(10);\nbegin\n  select dummy into l_x from dual where dummy = 1;\n" +
                                 "exception\n  when others then null;\nend pda_an_warn;",
                }, "PLW-06009");
                var before = SessionSettings(await Exec(settingsProbe, "session:"));
                await Exec("create or replace procedure pda_an_flags is\nbegin\n  $if $$pda_flag $then dbms_output.put_line('flag on'); $else dbms_output.put_line('flag off'); $end\nend pda_an_flags;",
                           "Executed successfully");
                await Exec("alter procedure pda_an_flags compile plsql_ccflags = 'pda_flag:true' plsql_optimize_level = 1 reuse settings", "Executed successfully");
                await Exec("begin pda_an_flags; end;", "flag on");
                await Tool(ctx, "compile_plsql", new JObject
                {
                    ["source"] = "create or replace procedure pda_an_flags is\nbegin\n  -- changed by the agent\n  $if $$pda_flag $then dbms_output.put_line('flag on'); $else dbms_output.put_line('flag off'); $end\nend pda_an_flags;",
                }, "compiled successfully");
                await Exec("begin pda_an_flags; end;", "flag on");
                await Tool(ctx, "run_query", new JObject { ["sql"] = "select plsql_ccflags, plsql_optimize_level from user_plsql_object_settings where name = 'PDA_AN_FLAGS'" }, "pda_flag:true");
                var after = SessionSettings(await Exec(settingsProbe, "session:"));
                Check($"the session's compiler settings are restored ({after})", before.Length > 8 && before == after);
                await Tool(ctx, "compile_plsql", new JObject
                {
                    ["source"] = "create or replace procedure pda_an_flags is\nbegin\n  $if $$pda_flag $then dbms_output.put_line('flag on'); $else dbms_output.put_line('flag off'); $end\nend pda_an_flags;",
                    ["keep_settings"] = false,
                }, "compiled successfully");
                await Exec("begin pda_an_flags; end;", "flag off");

                Line("\n### Invalidated dependents and recompiling");
                await Tool(ctx, "compile_plsql", new JObject { ["source"] = AnalysisPackage(1) }, $"PROCEDURE {schema}.PDA_AN_P");
                await Tool(ctx, "recompile_invalid", new JObject { ["objects"] = new JArray($"PROCEDURE {schema}.PDA_AN_P") }, "1 valid now");
                await Tool(ctx, "compile_plsql", new JObject { ["source"] = AnalysisPackage(2) }, "became INVALID");
                await Tool(ctx, "recompile_invalid", new JObject { ["objects"] = new JArray("PDA_AN_P") }, "still INVALID");
                await Tool(ctx, "compile_plsql", new JObject { ["source"] = AnalysisPackage(0) }, "compiled successfully");
                await Tool(ctx, "recompile_invalid", new JObject { ["objects"] = new JArray("PDA_AN_P") }, "valid now");

                Line("\n### Error stacks");
                var divLine = AnalysisPackage(0).Split(new[] { "/\n" }, StringSplitOptions.None)[1].Trim('\n').Split('\n')
                    .Select((l, i) => (l, i)).First(x => x.l.Contains("1 / p")).i + 1;
                var failed = await Exec("begin pda_an_pkg.div(0); end;", "ORA-01476");
                await Tool(ctx, "resolve_error_stack", new JObject { ["error_stack"] = failed }, ">> " + divLine.ToString().PadLeft(5) + " |     l := 1 / p;");
                await Tool(ctx, "resolve_error_stack", new JObject
                {
                    ["error_stack"] = $"FEHLER in Zeile 1:\nORA-01476: Divisor ist Null\nORA-06512: in \"{schema}.PDA_AN_PKG\", Zeile {divLine}\nORA-06512: in Zeile 1",
                }, ", in DIV (nearest declaration above the line)");
                var stack = await Exec("begin pda_an_pkg.show_stack; end;", "PL/SQL Call Stack");
                await Tool(ctx, "resolve_error_stack", new JObject { ["error_stack"] = stack }, "dbms_utility.format_call_stack");
                await Exec("create or replace trigger pda_an_trg\nbefore insert on pda_an_t\nfor each row\ndeclare\n  l number;\nbegin\n  l := 1;\n" +
                           "  if :new.id < 0 then\n    raise_application_error(-20001, 'negative id');\n  end if;\nend;", "Executed successfully");
                var trg = await Exec("insert into pda_an_t (id) values (-1)", "ORA-20001");
                await Tool(ctx, "resolve_error_stack", new JObject { ["error_stack"] = trg }, "= source line 9");
            }
            finally
            {
                foreach (var drop in new[] { "drop trigger pda_an_trg", "drop view pda_an_v2", "drop view pda_an_v", "drop procedure pda_an_p", "drop procedure pda_an_flags",
                                             "drop procedure pda_an_warn", "drop package pda_an_pkg", "drop table pda_an_t purge" })
                    await Exec(drop);
            }
        }

        /// <summary>
        /// The configured real model with everyday requests: does it pick resolve_error_stack for a pasted error,
        /// find_dependencies for an impact question, measure_sql to prove a rewrite, and the Test window without utPLSQL?
        /// </summary>
        private static async Task LlmToolsRunAsync(Plugin plugin)
        {
            var ide = plugin.Ide;
            var ctx = new ToolContext { Ide = ide, Config = AgentConfig.Load(), RequestApproval = r => Task.FromResult(ApprovalResult.Yes()) };
            async Task<string> Exec(string sql, string expect = null) => await Tool(ctx, "execute_sql", new JObject { ["sql"] = sql }, expect);
            try
            {
                Line("# PL/Pilot – real model with the analysis tools");
                Line($"Model: {ctx.Config.Model} at {ctx.Config.BaseUrl}, UI language {L.Lang}");
                foreach (var drop in new[] { "drop view pda_llm_order_v", "drop package pda_llm_pkg", "drop table pda_llm_order purge" }) await Exec(drop);
                await Exec("create table pda_llm_order (id number constraint pda_llm_order_pk primary key, customer_id number not null, amount number(12,2), " +
                           "created date not null, status varchar2(10))", "Executed successfully");
                await Exec("insert into pda_llm_order select level, mod(level, 500), mod(level * 7, 1000) + 0.5, date '2026-01-01' + mod(level, 300), " +
                           "case mod(level, 10) when 0 then 'CANCELLED' else 'OPEN' end from dual connect by level <= 50000", "Executed successfully");
                await Exec("commit");
                await Exec("create index pda_llm_order_created on pda_llm_order (created)", "Executed successfully");
                await Exec("begin dbms_stats.gather_table_stats(user, 'PDA_LLM_ORDER'); end;", "Executed successfully");
                await Exec("create or replace view pda_llm_order_v as select id, customer_id, amount from pda_llm_order where status = 'OPEN'", "Executed successfully");
                await Tool(ctx, "compile_plsql", new JObject
                {
                    ["source"] = "create or replace package pda_llm_pkg is\n  function avg_amount(p_customer_id in number) return number;\n  procedure report(p_customer_id in number);\nend pda_llm_pkg;\n/\n" +
                                 "create or replace package body pda_llm_pkg is\n  function avg_amount(p_customer_id in number) return number is\n    l_sum number;\n    l_cnt number;\n  begin\n" +
                                 "    select sum(amount), count(*) into l_sum, l_cnt from pda_llm_order where customer_id = p_customer_id;\n    return round(nvl(l_sum, 0) / l_cnt, 2);\n  end avg_amount;\n\n" +
                                 "  procedure report(p_customer_id in number) is\n  begin\n    dbms_output.put_line('Average: ' || avg_amount(p_customer_id));\n  end report;\nend pda_llm_pkg;\n/\n",
                }, "compiled successfully");
                var error = (await Exec("begin pda_llm_pkg.report(9999); end;", "ORA-01476")).Replace("FAILED: ", "");

                plugin.OnMenuClick(Bootstrap.MiChat);
                var chat = plugin.ChatWindow;
                for (int i = 0; i < 150 && !chat.IsChatReady; i++) await Task.Delay(200);
                UseTestModel(chat);
                // a careful user: allows normal actions, rejects destructive ones and changes of the package under test
                await chat.ExecuteScriptAsync(CarefulApprover(@"/(PACKAGE|BODY) PDA_LLM_PKG\b/i"));

                async Task<string> Ask(string text, int minutes = 8)
                {
                    var before = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('messages').innerText") ?? "\"\"") ?? "";
                    await chat.ExecuteScriptAsync("document.getElementById('input').value = " + JsonConvert.SerializeObject(text) + "; submit();");
                    var sw = Stopwatch.StartNew();
                    bool sawBusy = false;
                    while (sw.Elapsed < TimeSpan.FromMinutes(minutes))
                    {
                        await Task.Delay(500);
                        if (chat.IsBusy) sawBusy = true;
                        else if (sawBusy || sw.Elapsed > TimeSpan.FromSeconds(20)) break;
                    }
                    if (chat.IsBusy)
                    {
                        // a slow local model may still be working: stop it, so the next prompt and closing are not blocked
                        Line($"(still working after {minutes} minutes: stopped)");
                        chat.StopForTest();
                        for (int i = 0; i < 60 && chat.IsBusy; i++) await Task.Delay(500);
                    }
                    await Task.Delay(1500);
                    var after = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('messages').innerText") ?? "\"\"") ?? "";
                    // from the prompt on (the welcome text disappears with the first message, so the old length is no anchor)
                    var firstLine = text.Split('\n')[0];
                    var anchor = after.LastIndexOf(firstLine.Substring(0, Math.Min(40, firstLine.Length)), StringComparison.Ordinal);
                    var segment = anchor >= 0 ? after.Substring(anchor) : after.Length > before.Length ? after.Substring(before.Length) : after;
                    Line($"\n### Prompt ({sw.Elapsed.TotalSeconds:0} s): {text.Split('\n')[0]}\n```\n{(segment.Length > 2500 ? segment.Substring(0, 1200) + "\n…\n" + segment.Substring(segment.Length - 1200) : segment).Trim()}\n```");
                    return segment;
                }

                var s0 = await Ask("Erkläre mir, was PDA_LLM_PKG macht und welche Tabellen und Views es verwendet. Nichts ändern.");
                int explore = s0.IndexOf("explore_objects", StringComparison.Ordinal);
                var lookups = new[] { "get_object_source", "describe_object", "search_source", "run_query" }.Select(t => s0.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).ToList();
                Check("unknown objects: the model's first database call is explore_objects", explore >= 0 && lookups.All(i => i > explore));
                // a new chat: with the package already in context the model answers the error stack without any tool
                await chat.ExecuteScriptAsync("document.getElementById('btnNew').click()");
                await Task.Delay(1000);
                var s1 = await Ask("Dieser Fehler kommt aus unserem Anwendungs-Log. Was ist die Ursache und wie behebe ich ihn? Noch nichts ändern.\n\n" + error);
                Check("pasted error stack: the model calls resolve_error_stack", s1.Contains("resolve_error_stack"));
                var s2 = await Ask("Wir wollen die Spalte AMOUNT der Tabelle PDA_LLM_ORDER in NET_AMOUNT umbenennen. Welche Objekte wären betroffen? Nichts ändern.");
                Check("impact question: the model calls find_dependencies", s2.Contains("find_dependencies"));
                var s3 = await Ask("Ist diese Umformulierung schneller und liefert sie dasselbe Ergebnis? Bitte messen, nichts ändern.\n" +
                                   "Original: select id, amount from pda_llm_order where trunc(created) = date '2026-03-01'\n" +
                                   "Neu: select id, amount from pda_llm_order where created >= date '2026-03-01' and created < date '2026-03-02'");
                Check("rewrite to prove: the model calls measure_sql", s3.Contains("measure_sql"));
                var utPlsql = await Task.Run(() => UtPlsql.Detect(ide));
                var s4 = await Ask("/test pda_llm_pkg.avg_amount", 20); // writes, compiles and runs a test package: many slow steps
                var rejected = await chat.ExecuteScriptAsync("window.pdaRejected || 0");
                var state = await Task.Run(() => ide.Query(
                    "select (select count(*) from pda_llm_order) || '/' || (select count(*) from user_source where name = 'PDA_LLM_PKG' and type = 'PACKAGE BODY' " +
                    "and text like '%nvl(l_sum, 0) / l_cnt%') from dual", 1));
                Line($"Approvals rejected like a careful user would: {rejected}");
                Check($"/test leaves the table data and the tested package unchanged (rows / original line: {state.Rows[0][0]})", state.Rows[0][0] == "50000/1");
                if (utPlsql != null)
                {
                    Check("/test with utPLSQL: the model compiles a test package and runs it with run_unit_tests", s4.Contains("compile_plsql") && s4.Contains("run_unit_tests"));
                    await Exec("begin for r in (select object_name from user_objects where object_type = 'PACKAGE' and object_name like 'UT\\_%' escape '\\' " +
                               "and object_name like '%PDA_LLM%') loop execute immediate 'drop package ' || r.object_name; end loop; end;");
                }
                else Check("/test without utPLSQL: the model opens a Test window", s4.Contains("open_new_window") || s4.Contains("Test window") || s4.Contains("Test-Fenster"));

                // local source files: the package also exists in the project folder (spec and body in a schema sub folder);
                // the model should ask where to change it (the "user" answers: local files) and edit the body file only
                var project = Path.Combine(_dir, "project");
                if (Directory.Exists(project)) Directory.Delete(project, true);
                var bodyFile = Path.Combine(project, ide.GetContext().User.ToUpperInvariant(), "packages", "pda_llm_pkg.pkb");
                Directory.CreateDirectory(Path.GetDirectoryName(bodyFile));
                var source = await Task.Run(() => Db.CurrentSource(ctx, "PACKAGE BODY", ide.GetContext().User.ToUpperInvariant(), "PDA_LLM_PKG"));
                var spec = await Task.Run(() => Db.CurrentSource(ctx, "PACKAGE", ide.GetContext().User.ToUpperInvariant(), "PDA_LLM_PKG"));
                File.WriteAllText(Path.ChangeExtension(bodyFile, ".pks"), spec + "\r\n/\r\n");
                File.WriteAllText(bodyFile, source + "\r\n/\r\n");
                var original = File.ReadAllText(bodyFile);
                chat.ApplyConfigForTest(c => c.WorkspaceDir = project);
                await chat.ExecuteScriptAsync("document.getElementById('btnNew').click()");
                await Task.Delay(1000);
                await chat.ExecuteScriptAsync(@"setInterval(() => {
                    const q = document.querySelector('.approval.question:not(.done)'); if (!q) return;
                    window.pdaQuestion = q.querySelector('.ahead').innerText;
                    const b = Array.from(q.querySelectorAll('button')).find(x => /lokal|local/i.test(x.innerText));
                    if (b) b.click(); else { const i = q.querySelector('input'); i.value = 'Nur die lokalen Dateien'; i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' })); }
                  }, 1500);");
                var s5 = await Ask("Ändere pda_llm_pkg.avg_amount so, dass für Kunden ohne Bestellungen 0 zurückgegeben wird statt eines Fehlers.", 10);
                var asked = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("window.pdaQuestion || ''") ?? "\"\"");
                var changedLocally = File.ReadAllText(bodyFile) != original;
                var dbUnchanged = await Task.Run(() => ide.Query(
                    "select count(*) from user_source where name = 'PDA_LLM_PKG' and type = 'PACKAGE BODY' and text like '%nvl(l_sum, 0) / l_cnt%'", 1).Rows[0][0] == "1");
                Line($"Question of the model: {asked}");
                Check("local files: the user is asked where to change the package", asked.Length > 0);
                Check($"answer 'local files': the body file is changed ({changedLocally}), the database is not ({dbUnchanged})", changedLocally && dbUnchanged);
                chat.ApplyConfigForTest(c => c.WorkspaceDir = "");

                var transcript = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('messages').innerText") ?? "\"\"");
                File.WriteAllText(Path.Combine(_dir, "transcript-tools.txt"), transcript ?? "", Encoding.UTF8);
                Line($"\n**Total: {_pass} passed, {_fail} failed**");
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                // whatever the model left uncommitted would stop PL/SQL Developer from closing (Commit/Rollback question)
                try { if (ide.PendingTransaction != null) await Task.Run(() => ide.Execute("rollback")); } catch { }
                foreach (var drop in new[] { "drop view pda_llm_order_v", "drop package pda_llm_pkg", "drop table pda_llm_order purge" }) await Exec(drop);
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        /// <summary>
        /// Approves like a careful user: normal actions yes, cards with a destructive warning (and compiles of objects
        /// matching <paramref name="protectedTitle"/>, a JS regex) no, with German feedback; counts window.pdaRejected.
        /// </summary>
        private static string CarefulApprover(string protectedTitle = null) => @"setInterval(() => {
            const c = document.querySelector('.approval:not(.done):not(.question)'); if (!c) return;
            const title = (c.querySelector('.ahead') || c).innerText;
            let reason = null;
            if (c.querySelector('.warn')) reason = 'Nein - keine bestehenden Daten löschen oder ändern.';
            else if (" + (protectedTitle ?? "null") + @" && " + (protectedTitle ?? "/$^/") + @".test(title)) reason = 'Nein - den getesteten Code nicht ändern, nur den Fehler melden.';
            if (reason) { c.querySelector('input').value = reason; window.pdaRejected = (window.pdaRejected || 0) + 1; c.querySelector('button[data-a=""no""]').click(); }
            else c.querySelector('button.primary').click();
          }, 700);";

        /// <summary>
        /// PLSQLDEVAGENT_AUTOTEST_PROMPT = "PROMPT-ONLY:&lt;text&gt;": sends one prompt to the real model like a user would
        /// (careful approver) and records the transcript, the tool calls and the objects it left behind.
        /// </summary>
        private static async Task PromptOnlyRunAsync(Plugin plugin, string prompt)
        {
            var ide = plugin.Ide;
            try
            {
                Line("# PL/Pilot – one prompt with the real model");
                plugin.OnMenuClick(Bootstrap.MiChat);
                var chat = plugin.ChatWindow;
                for (int i = 0; i < 150 && !chat.IsChatReady; i++) await Task.Delay(200);
                UseTestModel(chat);
                Line("Prompt: " + prompt);
                var before = await Task.Run(() => ide.Query("select max(object_id) from user_objects", 1).Rows[0][0]);
                await chat.ExecuteScriptAsync(CarefulApprover());
                await chat.ExecuteScriptAsync("document.getElementById('input').value = " + JsonConvert.SerializeObject(prompt) + "; submit();");
                var sw = Stopwatch.StartNew();
                bool sawBusy = false;
                while (sw.Elapsed < TimeSpan.FromMinutes(20))
                {
                    await Task.Delay(1000);
                    if (chat.IsBusy) sawBusy = true;
                    else if (sawBusy || sw.Elapsed > TimeSpan.FromSeconds(30)) break;
                }
                if (chat.IsBusy) { Line("(still working after 20 minutes: stopped)"); chat.StopForTest(); for (int i = 0; i < 60 && chat.IsBusy; i++) await Task.Delay(500); }
                Line($"Finished after {sw.Elapsed.TotalSeconds:0} s; approvals rejected: {await chat.ExecuteScriptAsync("window.pdaRejected || 0")}");

                var transcript = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('messages').innerText") ?? "\"\"") ?? "";
                File.WriteAllText(Path.Combine(_dir, "transcript-prompt.txt"), transcript, Encoding.UTF8);
                var created = await Task.Run(() => ide.Query(
                    $"select object_type, object_name, status from user_objects where object_id > {before} and object_name not like 'ISEQ$$%' order by object_id", 100));
                Line("Objects created:\n```\n" + string.Join("\n", created.Rows.Select(r => $"{r[0]} {r[1]} {r[2]}")) + "\n```");
                Line("Transcript (end):\n```\n" + (transcript.Length > 6000 ? "…\n" + transcript.Substring(transcript.Length - 6000) : transcript) + "\n```");
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                try { if (ide.PendingTransaction != null) await Task.Run(() => ide.Execute("rollback")); } catch { }
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        /// <summary>
        /// PLSQLDEVAGENT_AUTOTEST_PROMPT = "PLAY:&lt;text&gt;": a run to film (screen recording of a demo). Runs the SQL file
        /// PLSQLDEVAGENT_AUTOTEST_PLAY_SQL (if set) in a SQL window, opens the file PLSQLDEVAGENT_AUTOTEST_PLAY_OPEN (if set,
        /// e.g. a package body to edit), types the prompt visibly into the chat, approves every
        /// card after a pause so it can be read (PLSQLDEVAGENT_AUTOTEST_PLAY_PAUSE_MS, default 3000; destructive cards are
        /// rejected; questions get the option containing PLSQLDEVAGENT_AUTOTEST_PLAY_ANSWER, else the first one), runs the
        /// SQL file again at the end and leaves PL/SQL Developer open.
        /// </summary>
        private static async Task PlayRunAsync(Plugin plugin, string prompt)
        {
            var ide = plugin.Ide;
            int pause = int.TryParse(Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_PLAY_PAUSE_MS"), out var ms) ? ms : 3000;
            var sqlFile = Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_PLAY_SQL");
            try
            {
                Line("# PL/Pilot – play mode (screen recording)");
                CloseAllWindows(); // PL/SQL Developer restores the windows of the last session: start the recording clean
                string sql = string.IsNullOrWhiteSpace(sqlFile) ? null : File.ReadAllText(sqlFile, Encoding.UTF8).Trim();
                if (sql != null) await RunInSqlWindow(ide, sql, pause, closeOthers: true);
                // the file the request is about, e.g. a package body from the version control checkout: it becomes the
                // active window, so the agent edits it visibly in the editor
                var openFile = Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_PLAY_OPEN");
                if (!string.IsNullOrWhiteSpace(openFile))
                {
                    Line($"Opened {openFile}: {ide.OpenFile(openFile)}; windows: {WindowList()}");
                    await Task.Delay(pause);
                }

                plugin.OnMenuClick(Bootstrap.MiChat);
                var chat = plugin.ChatWindow;
                for (int i = 0; i < 150 && !chat.IsChatReady; i++) await Task.Delay(200);
                UseTestModel(chat);
                await Task.Delay(pause);
                const int typingMs = 35;
                await chat.ExecuteScriptAsync("(() => { const el = document.getElementById('input'), t = " + JsonConvert.SerializeObject(prompt)
                    + "; let i = 0; el.focus(); const h = setInterval(() => { el.value = t.slice(0, ++i); el.dispatchEvent(new Event('input')); "
                    + "if (i >= t.length) clearInterval(h); }, " + typingMs + "); })()");
                await Task.Delay(prompt.Length * typingMs + pause);
                await chat.ExecuteScriptAsync(PausedApprover(pause, Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_PLAY_ANSWER")));
                await chat.ExecuteScriptAsync("submit();");
                Line("Prompt: " + prompt);

                var sw = Stopwatch.StartNew();
                bool sawBusy = false;
                while (sw.Elapsed < TimeSpan.FromMinutes(40))
                {
                    await Task.Delay(1000);
                    if (chat.IsBusy) sawBusy = true;
                    else if (sawBusy || sw.Elapsed > TimeSpan.FromSeconds(30)) break;
                }
                Line($"Agent finished after {sw.Elapsed.TotalSeconds:0} s (still busy: {chat.IsBusy}); windows: {WindowList()}");
                var transcript = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('messages').innerText") ?? "\"\"") ?? "";
                File.WriteAllText(Path.Combine(_dir, "transcript-play.txt"), transcript, Encoding.UTF8);

                await Task.Delay(pause);
                if (sql != null) await RunInSqlWindow(ide, sql, pause);
                Line("Done – PL/SQL Developer stays open.");
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
            }
        }

        /// <summary>
        /// Opens a SQL window with the text, executes it (like F8) and waits until it finished. A runtime error shows
        /// PL/SQL Developer's modal error dialog ("View program sources of error stack?"); it stays visible for two
        /// pauses so it can be read and is then closed (= Cancel).
        /// </summary>
        private static async Task RunInSqlWindow(IIde ide, string sql, int pause, bool closeOthers = false)
        {
            ide.NewWindow(IdeWindowType.Sql, sql);
            long sqlWindow = MainThread.Invoke(() => (long)Callbacks.IDE_GetEditorHandle());
            await Task.Delay(Math.Max(500, pause / 2));
            MainThread.Invoke(() => { Callbacks.IDE_Perform(1); }); // Execute
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromMinutes(2))
            {
                await Task.Delay(250);
                var dialog = FindErrorDialog();
                if (dialog != IntPtr.Zero)
                {
                    await Task.Delay(pause * 2);
                    // "Cancel": "Yes" opens the program sources of the error stack, "No" was seen opening the failing unit too
                    var cancel = FindChildButton(dialog, "Cancel", "&Cancel", "Abbrechen", "&Abbrechen");
                    if (cancel != IntPtr.Zero) PostMessage(cancel, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);
                    else PostMessage(dialog, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                    Line($"Error dialog shown and closed ({(cancel != IntPtr.Zero ? "Cancel" : "WM_CLOSE")}); windows: {WindowList()}");
                    await Task.Delay(500);
                    continue;
                }
                if (MainThread.Invoke(() => Callbacks.IDE_WindowIsRunning()) == 0) break;
            }
            Line($"SQL window executed ({sw.Elapsed.TotalSeconds:0} s); windows: {WindowList()}");
            if (closeOthers)
            {
                // windows that PL/SQL Developer restored from the last session or opened for the error stack meanwhile
                await Task.Delay(1000);
                CloseWindowsExcept(sqlWindow);
            }
            await Task.Delay(pause);
        }

        /// <summary>Closes every window except the one with this editor control (modified ones are saved to the log folder first).</summary>
        private static void CloseWindowsExcept(long keepEditor)
        {
            var tmp = Path.Combine(_dir, "closed-windows");
            Directory.CreateDirectory(tmp);
            int closed = MainThread.Invoke(() =>
            {
                int n = 0;
                for (int guard = 0; guard < 30; guard++)
                {
                    int index = -1, count = Callbacks.IDE_GetWindowCount();
                    try
                    {
                        for (int i = 0; i < count && index < 0; i++)
                            if (Callbacks.IDE_SelectWindow(i) != 0 && Callbacks.IDE_GetEditorHandle() != keepEditor) index = i;
                    }
                    finally { Callbacks.IDE_SelectWindow(0); }
                    if (index < 0) break;
                    Callbacks.IDE_ActivateWindow(index);
                    if (Callbacks.IDE_WindowIsModified() != 0)
                    {
                        using (var f = new Callbacks.NativeString(Path.Combine(tmp, $"other{guard}.sql"))) Callbacks.IDE_SetFilename(f.Ptr);
                        Callbacks.IDE_SaveFile();
                    }
                    Callbacks.IDE_CloseFile();
                    n++;
                }
                return n;
            });
            Line($"Closed {closed} other window(s); windows: {WindowList()}");
        }

        /// <summary>A visible top-level window of this process titled "Error" / "Fehler" (PL/SQL Developer's error dialog).</summary>
        private static IntPtr FindErrorDialog()
        {
            uint me = (uint)Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out var pid);
                if (pid != me) return true;
                var title = new StringBuilder(64);
                GetWindowText(h, title, title.Capacity);
                if (title.ToString() != "Error" && title.ToString() != "Fehler") return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>A child control (button) of the dialog whose text is one of the captions.</summary>
        private static IntPtr FindChildButton(IntPtr dialog, params string[] captions)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(dialog, (h, l) =>
            {
                var text = new StringBuilder(64);
                GetWindowText(h, text, text.Capacity);
                if (!captions.Contains(text.ToString())) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>
        /// Approves each card once it has been visible for <paramref name="pauseMs"/>; destructive cards are rejected. A
        /// question of the agent is answered with the option containing <paramref name="answer"/> (else the first option).
        /// </summary>
        private static string PausedApprover(int pauseMs, string answer) => @"(() => { const seen = new WeakMap(), clicked = new WeakSet(), wanted = "
            + JsonConvert.SerializeObject((answer ?? "").ToLowerInvariant()) + @"; setInterval(() => {
            const c = document.querySelector('.approval:not(.done)'); if (!c || clicked.has(c)) return;
            if (!seen.has(c)) { seen.set(c, Date.now()); c.scrollIntoView({ block: 'nearest', behavior: 'smooth' }); return; }
            if (Date.now() - seen.get(c) < " + pauseMs + @") return;
            clicked.add(c);
            if (c.classList.contains('question')) {
              const options = Array.from(c.querySelectorAll('button[data-i]'));
              const pick = (wanted && options.find(b => b.innerText.toLowerCase().includes(wanted))) || options[0];
              if (pick) pick.click();
            }
            else if (c.querySelector('.warn')) { c.querySelector('input').value = 'Nein - keine bestehenden Daten löschen oder ändern.'; c.querySelector('button[data-a=""no""]').click(); }
            else c.querySelector('button.primary').click();
          }, 250); })()";

        /// <summary>
        /// measure_sql: queries fetched in the database, binds, comparison with the same-rows check, DML rolled back
        /// (also inside an open transaction), plan mode, and the actual plan or the note about missing V$ access.
        /// </summary>
        private static async Task MeasureChecks(ToolContext ctx, IIde ide)
        {
            Line("\n## 13. Measuring statements");
            async Task<string> Exec(string sql, string expect = null) => await Tool(ctx, "execute_sql", new JObject { ["sql"] = sql }, expect);
            async Task<string> Measure(JObject args, string expect) => await Tool(ctx, "measure_sql", args, expect);
            await Exec("drop table pda_ms_t purge");
            // several statements in one execute_sql call are refused before anyone is asked to approve them
            int asked = 0;
            var counting = new ToolContext { Ide = ctx.Ide, Config = ctx.Config, RequestApproval = r => { asked++; return Task.FromResult(ApprovalResult.Yes()); } };
            await Tool(counting, "execute_sql", new JObject { ["sql"] = "create table pda_two_a (a number); create table pda_two_b (b number)" }, "exactly ONE statement");
            Check("several statements: refused without an approval card", asked == 0);
            try
            {
                await Exec("create table pda_ms_t as select level id, mod(level, 7) grp, rpad('x', 50, 'x') pad from dual connect by level <= 20000", "Executed successfully");
                await Exec("begin dbms_stats.gather_table_stats(user, 'PDA_MS_T'); end;", "Executed successfully");

                var single = await Measure(new JObject { ["sql"] = "select id from pda_ms_t where grp = :g", ["binds"] = new JObject { ["g"] = 3 } }, "2,857 rows fetched");
                Check("measure_sql: actual plan, or a note that V$ access is missing",
                    single.Contains("A-Rows") || single.Contains("No actual execution plan"));
                await Measure(new JObject
                {
                    ["sql"] = "with x as (select /*+ full(t) */ id from pda_ms_t t where grp = 2) select count(*) from x",
                    ["runs"] = 2,
                }, "statistics of the last run");
                await Measure(new JObject
                {
                    ["sql"] = "select id from pda_ms_t where grp = :g",
                    ["compare_sql"] = "select id from pda_ms_t where grp + 0 = :g",
                    ["binds"] = new JObject { ["g"] = 3 },
                }, "Same rows: YES");
                await Measure(new JObject
                {
                    ["sql"] = "select id from pda_ms_t where grp = :g",
                    ["compare_sql"] = "select id from pda_ms_t where grp = :g and id > 100",
                    ["binds"] = new JObject { ["g"] = 3 },
                }, "Same rows: NO");
                await Measure(new JObject { ["sql"] = "select * from pda_no_such_table" }, "ORA-00942");

                // DML is executed and rolled back; with an open transaction only back to a savepoint
                await Measure(new JObject { ["sql"] = "update pda_ms_t set pad = 'changed' where grp = 1" }, "rows processed (rolled back)");
                await Tool(ctx, "run_query", new JObject { ["sql"] = "select count(*) changed from pda_ms_t where pad = 'changed'" }, "0");
                Check("measure_sql: no transaction left open after the rolled back UPDATE", ide.PendingTransaction == null);
                await Exec("insert into pda_ms_t values (-1, 9, 'keep')", "Executed successfully");
                await Measure(new JObject { ["sql"] = "delete from pda_ms_t where grp = 2" }, "rows processed (rolled back)");
                var rows = await Task.Run(() => ide.Query("select (select count(*) from pda_ms_t where id = -1) || '/' || (select count(*) from pda_ms_t where grp = 2) from dual", 1));
                Check($"measure_sql in an open transaction: the earlier INSERT stays, the DELETE is undone ({rows.Rows[0][0]})",
                    rows.Rows[0][0] == "1/2857" && ide.PendingTransaction != null);
                await Exec("rollback", "Executed successfully");

                var plan = new ToolContext { Ide = ctx.Ide, Config = ctx.Config, RequestApproval = ctx.RequestApproval, PlanMode = true };
                await Tool(plan, "measure_sql", new JObject { ["sql"] = "update pda_ms_t set pad = 'x'" }, "Not executed: the agent is in PLAN MODE");
                await Tool(plan, "measure_sql", new JObject { ["sql"] = "select count(*) from pda_ms_t" }, "1 row fetched");
            }
            finally
            {
                await Exec("drop table pda_ms_t purge");
            }
        }

        /// <summary>run_unit_tests with utPLSQL (a failing and a passing run, coverage) or the message without utPLSQL.</summary>
        private static async Task UnitTestChecks(ToolContext ctx, IIde ide)
        {
            Line("\n## 14. Unit tests (utPLSQL)");
            var ut = await Task.Run(() => UtPlsql.Detect(ide));
            var info = await Task.Run(() => DatabaseInfo.Load(ide));
            if (ut == null)
            {
                Line("utPLSQL is not installed in this database: only the fallback is checked.");
                await Tool(ctx, "run_unit_tests", new JObject(), "utPLSQL is not installed");
                Check("system prompt says utPLSQL is missing and suggests a Test window", info?.PromptSection().Contains("utPLSQL is not installed") == true);
                return;
            }
            Line($"utPLSQL {ut.Version} in schema {ut.Owner}");
            Check("system prompt names the installed utPLSQL version", info?.PromptSection().Contains("utPLSQL " + ut.Version) == true);
            async Task<string> Exec(string sql, string expect = null) => await Tool(ctx, "execute_sql", new JObject { ["sql"] = sql }, expect);
            string TestPackage(string expected) =>
                "create or replace package ut_pda_calc is\n  --%suite(PDA calculations)\n\n  --%test(adds 19 percent VAT)\n  procedure add_vat;\n\n" +
                "  --%test(raises -20001 when dividing by zero)\n  --%throws(-20001)\n  procedure div_zero;\n\n  --%test(VAT of 10)\n  procedure vat_of_ten;\nend ut_pda_calc;\n/\n" +
                "create or replace package body ut_pda_calc is\n  procedure add_vat is\n  begin\n    ut.expect(pda_ut_calc.add_vat(100)).to_equal(119);\n  end add_vat;\n\n" +
                "  procedure div_zero is\n    l number;\n  begin\n    l := pda_ut_calc.safe_div(1, 0);\n  end div_zero;\n\n" +
                $"  procedure vat_of_ten is\n  begin\n    ut.expect(pda_ut_calc.add_vat(10)).to_equal({expected});\n  end vat_of_ten;\nend ut_pda_calc;\n/\n";
            try
            {
                await Tool(ctx, "compile_plsql", new JObject
                {
                    ["source"] = "create or replace package pda_ut_calc is\n  function add_vat(p_net in number) return number;\n  function safe_div(p_a in number, p_b in number) return number;\nend pda_ut_calc;\n/\n" +
                                 "create or replace package body pda_ut_calc is\n  function add_vat(p_net in number) return number is\n  begin\n    return round(p_net * 1.19, 2);\n  end add_vat;\n\n" +
                                 "  function safe_div(p_a in number, p_b in number) return number is\n  begin\n    if p_b = 0 then\n      raise_application_error(-20001, 'division by zero');\n    end if;\n" +
                                 "    return p_a / p_b;\n  end safe_div;\nend pda_ut_calc;\n/\n",
                }, "compiled successfully");
                await Tool(ctx, "compile_plsql", new JObject { ["source"] = TestPackage("12") }, "compiled successfully");
                var failing = await Tool(ctx, "run_unit_tests", new JObject { ["path"] = "ut_pda_calc" }, "3 tests, 1 failed");
                Check("the failed expectation is reported with actual and expected value", failing.Contains("NOT PASSED") && (failing.Contains("11.9") || failing.Contains("11,9"))); // utPLSQL uses the NLS decimal separator
                await Tool(ctx, "compile_plsql", new JObject { ["source"] = TestPackage("11.9") }, "compiled successfully");
                var passing = await Tool(ctx, "run_unit_tests", new JObject { ["path"] = "ut_pda_calc", ["coverage_objects"] = new JArray("pda_ut_calc") }, "ALL PASSED");
                Check("code coverage of the tested package is reported", passing.Contains("PDA_UT_CALC") && passing.Contains("% of"));

                // utPLSQL's "suite package does not exist" (ORA-20204) is explained
                await Tool(ctx, "compile_plsql", new JObject
                {
                    ["source"] = "create or replace package ut_pda_diag is\n  --%suite(No empty line)\n  --%test(one)\n  procedure t1;\nend ut_pda_diag;\n/\n" +
                                 "create or replace package body ut_pda_diag is\n  procedure t1 is\n  begin\n    ut.expect(1).to_equal(1);\n  end t1;\nend ut_pda_diag;\n/\n",
                }, "compiled successfully");
                await Tool(ctx, "run_unit_tests", new JObject { ["path"] = "ut_pda_diag" }, "put an EMPTY LINE after the --%suite line");
                await Tool(ctx, "compile_plsql", new JObject
                {
                    ["source"] = "create or replace package ut_pda_diag is\n  --%suite(Invalid specification)\n\n  --%test(one)\n  procedure t1(p in pda_no_such_type);\nend ut_pda_diag;\n/\n",
                }, "COMPILED WITH ERRORS");
                // utPLSQL reports the tests of an invalid package as errored: the compile errors are named
                await Tool(ctx, "run_unit_tests", new JObject { ["path"] = "ut_pda_diag" }, "PDA_NO_SUCH_TYPE");
                await Tool(ctx, "run_unit_tests", new JObject { ["path"] = "ut_pda_does_not_exist" }, "There is no package");
                // the code under test is invalid: the tests error, and the reason is named
                await Tool(ctx, "compile_plsql", new JObject
                {
                    ["source"] = "create or replace package body pda_ut_calc is\n  function add_vat(p_net in number) return number is\n  begin\n    return round(p_net * l_rate, 2);\n  end add_vat;\n\n" +
                                 "  function safe_div(p_a in number, p_b in number) return number is\n  begin\n    return p_a / p_b;\n  end safe_div;\nend pda_ut_calc;\n/\n",
                }, "COMPILED WITH ERRORS");
                await Tool(ctx, "run_unit_tests", new JObject { ["path"] = "ut_pda_calc" }, "PACKAGE BODY " + ctx.CurrentSchema + ".PDA_UT_CALC used by the tests is INVALID");

                // get_compile_errors for a PACKAGE also shows the errors of its body
                await Tool(ctx, "compile_plsql", new JObject
                {
                    ["source"] = "create or replace package ut_pda_diag is\n  --%suite(Body with errors)\n\n  --%test(one)\n  procedure t1;\nend ut_pda_diag;\n/\n" +
                                 "create or replace package body ut_pda_diag is\n  procedure t1 is\n  begin\n    ut.expect(pda_no_such_function(1)).to_equal(1);\n  end t1;\nend ut_pda_diag;\n/\n",
                }, "COMPILED WITH ERRORS");
                await Tool(ctx, "get_compile_errors", new JObject { ["name"] = "ut_pda_diag", ["object_type"] = "PACKAGE" }, "PDA_NO_SUCH_FUNCTION");
            }
            finally
            {
                await Exec("drop package ut_pda_calc");
                await Exec("drop package pda_ut_calc");
                await Exec("drop package ut_pda_diag");
            }
        }

        /// <summary>Only the background SQL / Stop / DBA views checks (quick run).</summary>
        private static async Task SqlRunAsync(Plugin plugin, bool rewindOnly = false, bool transactionOnly = false, bool securityOnly = false)
        {
            try
            {
                Line("# PL/Pilot – background SQL self test");
                for (int i = 0; i < 120 && !plugin.Ide.GetContext().Connected; i++) await Task.Delay(500);
                if (securityOnly) await SecurityChecks(plugin.Ide, AgentConfig.Load());
                else if (transactionOnly) await TransactionChecks(plugin.Ide, AgentConfig.Load());
                else if (rewindOnly) await RewindChecks(plugin.Ide, AgentConfig.Load());
                else
                {
                    await SqlThreadChecks(plugin.Ide, AgentConfig.Load());
                    await SchemaChecks(plugin.Ide, AgentConfig.Load());
                    await FastReadChecks(plugin.Ide, AgentConfig.Load());
                }
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
            // the UPDATE (even with 0 rows) opens a transaction in the agent session, so it is rolled back as well
            Check("rewind plan: 6 undoable steps incl. rolling back the open UPDATE", plan.Undoable == 6 && plan.Steps.Count == 6 && plan.NotUndoable.Count == 0);

            var lines = await Task.Run(() => Rewinder.Execute(plan, plain, journal));
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
            Check("rewind: everything is marked as undone and no transaction is left open", journal.OpenFrom(1).Count == 0 && ide.PendingTransaction == null);

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
            await Task.Run(() => Rewinder.Execute(plan2, plain, journal2));
            Check("rewind: the user's editor text is left alone", (await Task.Run(() => ide.GetEditorText())).Contains("typed by the user"));
            src = await Task.Run(() => Db.CurrentSource(plain, "PROCEDURE", ctx.CurrentSchema, "PDA_RW_EXISTING"));
            Check("rewind: the later database version is left alone", src.Contains("-- someone else"));
        }

        /// <summary>
        /// Uncommitted changes in the agent's session: detected after DML, rolled back statement by statement through
        /// rewind (savepoints), committed implicitly by DDL, and EXPLAIN PLAN leaves no transaction behind.
        /// </summary>
        private static async Task TransactionChecks(IIde ide, AgentConfig cfg)
        {
            Line("\n## 10. Uncommitted changes of the agent session");
            await Task.Delay(200);
            var plain = new ToolContext { Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()) };
            if (ide.PendingTransaction != null) await Task.Run(() => ide.Execute("rollback"));
            decimal Sal(int empno) => decimal.Parse(ide.Query($"select sal from emp where empno = {empno}", 1).Rows[0][0].Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            decimal king = await Task.Run(() => Sal(7839)), clark = await Task.Run(() => Sal(7782));
            int events = 0;
            Action onChange = () => System.Threading.Interlocked.Increment(ref events);
            ide.TransactionChanged += onChange;
            try
            {
                var journal = new ChangeJournal();
                journal.StartCheckpoint("t1");
                var ctx = new ToolContext
                {
                    Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()),
                    RecordChange = r => { r.Checkpoint = "t1"; journal.Add(r); },
                };
                await Tool(ctx.ForCall("callA"), "execute_sql", new JObject { ["sql"] = "update emp set sal = sal + 100 where empno = 7839" }, "not committed");
                Line("Open transaction: " + ide.PendingTransaction);
                Check("an open transaction is detected after DML", ide.PendingTransaction != null && events >= 1);
                await Tool(ctx.ForCall("callB"), "execute_sql", new JObject { ["sql"] = "update emp set sal = sal + 100 where empno = 7782" }, "not committed");
                var statements = ChangeJournal.StatementsInTransaction(ide.PendingTransaction, journal);
                Check("both statements are listed for the open transaction", statements.Count == 2);

                var plan = await Task.Run(() => Rewinder.Plan(journal, journal.StartOfCall("callB").Value, plain, null));
                Check("rewind plan: roll back the second update", plan.Undoable == 1 && plan.Steps[0].Record.Kind == ChangeRecord.Data);
                Line(string.Join("\n", await Task.Run(() => Rewinder.Execute(plan, plain, journal))));
                Check("rewind rolled back only the second update",
                    await Task.Run(() => Sal(7782)) == clark && await Task.Run(() => Sal(7839)) == king + 100 && ide.PendingTransaction != null);

                plan = await Task.Run(() => Rewinder.Plan(journal, journal.StartOfCheckpoint("t1").Value, plain, null));
                Line(string.Join("\n", await Task.Run(() => Rewinder.Execute(plan, plain, journal))));
                Check("rewinding all data changes restores the data and ends the transaction", await Task.Run(() => Sal(7839)) == king && ide.PendingTransaction == null);

                // DDL commits implicitly: the update can no longer be rolled back
                await Tool(ctx.ForCall("callC"), "execute_sql", new JObject { ["sql"] = "update emp set sal = sal + 1 where empno = 7839" }, "not committed");
                await Tool(plain, "compile_plsql", new JObject { ["source"] = "create or replace procedure pda_tx_ddl is\nbegin\n  null;\nend;" }, "compiled successfully");
                Check("DDL commits the open transaction (the notice disappears)", ide.PendingTransaction == null);
                plan = await Task.Run(() => Rewinder.Plan(journal, journal.StartOfCall("callC").Value, plain, null));
                Line("Plan after DDL: " + string.Join("; ", plan.Steps.Select(st => st.Text + " PROBLEM: " + st.Problem)));
                Check("rewind reports the committed update as no longer undoable", plan.Steps.Count == 1 && plan.Steps[0].Problem != null);
                await Task.Run(() => { ide.Execute("update emp set sal = sal - 1 where empno = 7839"); ide.Execute("commit"); });
                Check("COMMIT ends the transaction", ide.PendingTransaction == null && await Task.Run(() => Sal(7839)) == king);

                // two chats share the session: chat A's first statement opened the transaction, chat B added to it
                var other = ChangeJournal.For("selftest-other-chat");
                other.StartCheckpoint("o1");
                var ctxOther = new ToolContext
                {
                    Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()),
                    RecordChange = r => { r.Checkpoint = "o1"; other.Add(r); },
                };
                await Tool(ctx.ForCall("callD"), "execute_sql", new JObject { ["sql"] = "update emp set sal = sal + 5 where empno = 7839" }, "not committed");
                await Tool(ctxOther.ForCall("callE"), "execute_sql", new JObject { ["sql"] = "update emp set sal = sal + 5 where empno = 7782" }, "not committed");
                plan = await Task.Run(() => Rewinder.Plan(journal, journal.StartOfCall("callD").Value, plain, null));
                Line("Plan with a change of another chat in the same transaction: " + string.Join("; ", plan.Steps.Select(st => st.Text + " PROBLEM: " + st.Problem)));
                Check("rewind does not roll back another chat's changes in the same transaction", plan.Undoable == 0 && plan.Steps.Count == 1);
                Check("the open transaction lists the statements of both chats", ChangeJournal.StatementsInTransaction(ide.PendingTransaction, journal).Count == 2);
                await Task.Run(() => ide.Execute("rollback"));
                ChangeJournal.MarkTransactionReverted(plan.Steps[0].Record.TransactionId);
                Check("ROLLBACK ends it and restores the data", ide.PendingTransaction == null && await Task.Run(() => Sal(7839)) == king && await Task.Run(() => Sal(7782)) == clark);

                await Tool(plain, "explain_plan", new JObject { ["sql"] = "select ename from emp where deptno = 10" }, "Plan hash value");
                Check("explain_plan leaves no open transaction behind", ide.PendingTransaction == null);

                // a block without DML leaves nothing to commit (Oracle: a SAVEPOINT alone would open a transaction)
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "begin emp_api.list_emps(10); end;" }, "Executed successfully");
                Check("a block without DML opens no transaction", ide.PendingTransaction == null);
            }
            finally
            {
                ide.TransactionChanged -= onChange;
                if (ide.PendingTransaction != null) await Task.Run(() => ide.Execute("rollback")); // never leave the close dialog to the test
            }
        }

        /// <summary>
        /// Security fixes against the real database: a "query" with WITH FUNCTION cannot change data unasked, statements
        /// that hide deletions in PL/SQL and logon triggers always ask (also with auto-approve), and the production
        /// guard recognizes the database by its own name.
        /// </summary>
        private static async Task SecurityChecks(IIde ide, AgentConfig cfg)
        {
            Line("\n## 11. Security");
            await Task.Delay(200);
            var plain = new ToolContext { Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()) };
            var requests = new System.Collections.Generic.List<ApprovalRequest>();
            bool approve = false;
            var auto = cfg.Clone();
            auto.AutoApproveSql = true; // the checks must also hold with "Auto approve SQL"
            var ctx = new ToolContext
            {
                Ide = ide, Config = auto,
                RequestApproval = r => { requests.Add(r); return Task.FromResult(approve ? ApprovalResult.Yes() : new ApprovalResult { Approved = false, Feedback = "rejected by the self test" }); },
            };
            async Task<int> ProbeRows() => int.Parse((await Task.Run(() => ide.Query("select count(*) from pda_sec_probe", 1))).Rows[0][0]);

            await Tool(plain, "execute_sql", new JObject { ["sql"] = "begin execute immediate 'drop table pda_sec_probe purge'; exception when others then null; end;" }, null);
            await Tool(plain, "execute_sql", new JObject { ["sql"] = "create table pda_sec_probe (n number)" }, "Executed successfully");
            try
            {
                const string withFunction = "with function pda_w return number is pragma autonomous_transaction; begin insert into pda_sec_probe values (1); commit; return 1; end; " +
                                            "select pda_w from dual";
                await Tool(ctx, "run_query", new JObject { ["sql"] = withFunction }, "Not executed");
                Check("run_query refuses WITH FUNCTION without asking anything", requests.Count == 0 && await ProbeRows() == 0);

                await Tool(ctx, "execute_sql", new JObject { ["sql"] = withFunction }, "rejected");
                Check("execute_sql: WITH FUNCTION asks despite auto-approve (always ask, warning)",
                    requests.Count == 1 && requests[0].AlwaysAsk && (requests[0].Warning ?? "").Length > 0);
                Check("rejected: the hidden INSERT was not executed", await ProbeRows() == 0);
                approve = true;
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = withFunction }, "PDA_W");
                Check("approved: the query runs", requests.Count == 2 && await ProbeRows() == 1);

                approve = false;
                requests.Clear();
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "begin delete from pda_sec_probe; end;" }, "rejected");
                Check("DELETE inside a PL/SQL block asks despite auto-approve, nothing deleted", requests.Count == 1 && requests[0].AlwaysAsk && await ProbeRows() == 1);
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "begin execute immediate 'trun' || 'cate table pda_sec_probe'; end;" }, "rejected");
                Check("dynamic SQL asks despite auto-approve, nothing truncated", requests.Count == 2 && await ProbeRows() == 1);
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "insert into pda_sec_probe values (2)" }, "Executed successfully");
                Check("a normal INSERT runs without asking with auto-approve", requests.Count == 2 && await ProbeRows() == 2);
                await Task.Run(() => ide.Execute("rollback"));

                await Tool(ctx, "compile_plsql", new JObject { ["source"] = "create or replace trigger pda_sec_logon after logon on schema\nbegin\n  null;\nend;" }, "rejected");
                var trigger = await Task.Run(() => ide.Query("select count(*) from user_triggers where trigger_name = 'PDA_SEC_LOGON'", 1));
                Check("a logon trigger asks despite auto-approve and is not created when rejected", requests.Count == 3 && requests[2].AlwaysAsk && trigger.Rows[0][0] == "0");
            }
            finally
            {
                await Tool(plain, "execute_sql", new JObject { ["sql"] = "drop table pda_sec_probe purge" }, null);
            }

            // production guard: the database's own name protects it, whatever the logon text says
            var identity = await Task.Run(() => ide.DatabaseIdentity);
            Line("Database identity: " + identity);
            var c = ide.GetContext();
            var dbName = (identity ?? "").Split('|')[0];
            var gcfg = cfg.Clone();
            gcfg.ProtectedConnections = dbName;
            var protectedState = new ConnectionGuard().Evaluate(gcfg, c, identity);
            Line("Guard: " + protectedState.Describe());
            Check($"guard: the pattern '{dbName}' (database name) protects {c.ConnectionLabel}",
                dbName.Length > 0 && protectedState.Mode == GuardMode.Protected && new ConnectionGuard().Evaluate(gcfg, c).Mode == GuardMode.Normal);
        }

        /// <summary>
        /// The agent's current schema: a switch with execute_sql (ALTER SESSION, or a PL/SQL block) is seen at once
        /// instead of the remembered schema and counted, so the agent session switches back at the end of a request.
        /// </summary>
        /// <summary>
        /// Source read in joined rows (the same text as line by line, also when a very long line forces the fallback),
        /// line ranges and the output limit, Oracle's estimate without touching a transaction, the slow query
        /// confirmation, the row limit in real queries, the runtime note and identical parallel queries.
        /// </summary>
        private static async Task FastReadChecks(IIde ide, AgentConfig cfg)
        {
            Line("\n## 8c. Reads and slow statements");
            var ctx = new ToolContext { Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()) };
            string owner = await Task.Run(() => ctx.CurrentSchema);
            string PerLine(string name, string type) => string.Concat(ide.Query(
                $"select text from all_source where owner = '{owner}' and name = '{name}' and type = '{type}' order by line", 200000).Rows.Select(r => r[0]));
            // a slow query (about 1-2 s) that Oracle estimates as fast
            const string slowQuery = "select count(*) n from (select level l from dual connect by level <= 1000000) a, " +
                                     "(select level l from dual connect by level <= 6) b where mod(a.l + b.l, 7) = 0";
            try
            {
                // a 2,700-line body (with umlauts) and a procedure with a line of 3,991 bytes: its joined row would exceed 4,000 bytes
                var body = new StringBuilder("create or replace package body pda_big_src is\n");
                for (int i = 1; i <= 300; i++)
                    body.Append($"  procedure p{i} is\n    l number;\n  begin\n    l := {i} * 2; -- line of procedure {i}, äöü\n" +
                                $"    if l > 0 then\n      dbms_output.put_line('p{i}: ' || l);\n    end if;\n  end p{i};\n\n");
                body.Append("end pda_big_src;");
                var created = await Task.Run(() => new[]
                {
                    ide.Execute("create or replace package pda_big_src is\n  procedure p1;\nend pda_big_src;"),
                    ide.Execute(body.ToString()),
                    ide.Execute("create or replace procedure pda_long_line is\n  s varchar2(4000) := '" + new string('x', 3965) + "';\nbegin\n  null;\nend;"),
                    ide.Execute("create table pda_est_t as select level id, mod(level, 97) grp from dual connect by level <= 100000"),
                });
                Check("test objects created: " + string.Join(", ", created.Where(c => !c.Ok).Select(c => c.ErrorMessage)), created.All(c => c.Ok || c.ErrorCode == 955));

                // 1. joined rows give exactly the text of the line-by-line read
                foreach (var (name, type) in new[] { ("PDA_BIG_SRC", "PACKAGE BODY"), ("PDA_BIG_SRC", "PACKAGE"), ("PDA_LONG_LINE", "PROCEDURE"), ("EMP_API", "PACKAGE BODY") })
                {
                    var joined = await Task.Run(() => Db.ReadSource(ctx, owner, name, type));
                    var plain = await Task.Run(() => PerLine(name, type));
                    Check($"{type} {name}: joined read = line-by-line read ({joined.Text.Length} chars, lines {joined.FirstLine}-{joined.LastLine} of {joined.TotalLines})",
                        joined.Text == plain && plain.Length > 0 && joined.LastLine == joined.TotalLines);
                }
                Check("Db.CurrentSource (backups, rewind) of the long-line procedure is complete",
                    await Task.Run(() => Db.CurrentSource(ctx, "PROCEDURE", owner, "PDA_LONG_LINE")) == "CREATE OR REPLACE " + (await Task.Run(() => PerLine("PDA_LONG_LINE", "PROCEDURE"))).TrimEnd('\n', '\r', ' '));
                try
                {
                    // network round trips of the two ways (needs V$MYSTAT; only reported)
                    long Trips() => long.Parse(ide.Query("select s.value from v$mystat s join v$statname n on n.statistic# = s.statistic# " +
                                                         "where n.name = 'SQL*Net roundtrips to/from client'", 1).Rows[0][0]);
                    var trips = await Task.Run(() =>
                    {
                        long t0 = Trips(); PerLine("PDA_BIG_SRC", "PACKAGE BODY"); long t1 = Trips();
                        Db.ReadSource(ctx, owner, "PDA_BIG_SRC", "PACKAGE BODY"); long t2 = Trips();
                        return (t1 - t0 - 2, t2 - t1 - 2);
                    });
                    Line($"Round trips for the 2,700-line body: line by line {trips.Item1}, joined rows {trips.Item2}");
                }
                catch (OracleSqlException) { Line("(round trips not measured: no access to V$MYSTAT)"); }

                // 2. line ranges and the output limit
                int total = (await Task.Run(() => Db.ReadSource(ctx, owner, "PDA_BIG_SRC", "PACKAGE BODY", 1, 1))).TotalLines;
                var range = await Tool(ctx, "get_object_source", new JObject { ["name"] = "pda_big_src", ["object_type"] = "PACKAGE BODY", ["start_line"] = 1500, ["end_line"] = 1505, ["with_line_numbers"] = true }, $"lines 1500-1505 of {total}");
                var line1500 = await Task.Run(() => ide.Query($"select text from all_source where owner = '{owner}' and name = 'PDA_BIG_SRC' and type = 'PACKAGE BODY' and line = 1500", 1).Rows[0][0]);
                Line($"Stored line 1500: [{line1500.Replace("\n", "\\n").Replace("\r", "\\r")}]");
                Check("line 1500 is numbered 1500 and has the stored text", range.Contains("1500: " + line1500.TrimEnd('\n', '\r')) && !range.Contains("1506:"));
                var whole = await Tool(ctx, "get_object_source", new JObject { ["name"] = "pda_big_src" }, "output limit reached after line");
                var cont = System.Text.RegularExpressions.Regex.Match(whole, $@"PACKAGE BODY \S+: output limit reached after line (\d+) of {total}\. NOT shown: .*start_line (\d+)");
                Check($"the whole package fits the output limit ({whole.Length} of {cfg.ToolOutputLimit} chars) and says where to continue",
                    whole.Length <= cfg.ToolOutputLimit && cont.Success && int.Parse(cont.Groups[2].Value) == int.Parse(cont.Groups[1].Value) + 1);
                Check("the cut is announced at the top and the procedures not shown are listed with their lines (p300 at line 2693)",
                    whole.Contains("too long for one answer") && whole.Contains("p300 (line 2693)"));
                if (cont.Success)
                {
                    var rest = await Tool(ctx, "get_object_source", new JObject { ["name"] = "pda_big_src", ["object_type"] = "PACKAGE BODY", ["start_line"] = int.Parse(cont.Groups[2].Value) },
                        $"lines {cont.Groups[2].Value}-");
                    Check("reading on continues exactly after the last line shown", rest.Contains($"of {total}") && !rest.Contains("CREATE OR REPLACE"));
                }

                // 3. Oracle's estimate: read without running the query and without touching a transaction
                // 10 billion row pairs: Oracle estimates minutes (never run here); a join on a column with 97 values is
                // estimated (and runs) at about a second
                const string cartesian = "select count(*) from pda_est_t a, pda_est_t b";
                var est = await Task.Run(() => QueryRunner.Estimate(ctx, cartesian));
                var join = await Task.Run(() => QueryRunner.Estimate(ctx, "select count(*) from pda_est_t a, pda_est_t b where a.grp = b.grp"));
                var small = await Task.Run(() => QueryRunner.Estimate(ctx, "select ename from emp where empno = 7839"));
                Line($"Estimates: cartesian product of 100,000 x 100,000 rows {est?.Describe() ?? "none"}; join on GRP {join?.Describe() ?? "none"}; " +
                     $"one row by primary key {small?.Describe() ?? "none"}");
                Check("Oracle estimates the cartesian product as slow and the primary key lookup as fast", est != null && est.Seconds >= 60 && small != null && small.Seconds <= 2);
                Check("the estimate leaves no transaction behind", ide.PendingTransaction == null);
                await Task.Run(() => ide.Execute("update emp set sal = sal where empno = 7839"));
                var txn = ide.PendingTransaction;
                await Task.Run(() => QueryRunner.Estimate(ctx, cartesian));
                Check("an open transaction of the agent is not touched by the estimate", txn != null && ide.PendingTransaction == txn);
                await Task.Run(() => ide.Execute("rollback"));

                // 4. slow by estimate: the user is asked; declined, nothing runs
                int asked = 0;
                var careful = new ToolContext { Ide = ide, Config = cfg, RequestApproval = r => { asked++; return Task.FromResult(new ApprovalResult { Approved = false, Feedback = "too slow" }); } };
                var sw = Stopwatch.StartNew();
                await Tool(careful, "run_query", new JObject { ["sql"] = cartesian }, "Not executed: Oracle estimates");
                Check($"the user was asked once and the declined query did not run ({sw.ElapsedMilliseconds} ms)", asked == 1 && sw.ElapsedMilliseconds < 3000);

                // 5. the row limit inside the query returns the same rows as the query as written
                foreach (var sql in new[]
                {
                    "select ename, sal from emp order by sal desc, ename",
                    "select e.deptno, d.deptno from emp e join dept d on d.deptno = e.deptno order by e.empno",
                    "with x as (select * from emp) select ename from x where sal > 1000 order by ename",
                    "select empno from emp union select deptno from dept order by 1",
                    "select ename from emp order by sal desc, ename fetch first 3 rows only",
                })
                {
                    var limited = await QueryRunner.RunAsync(ctx, sql, 5);
                    var plain = SqlUtil.FormatResult(await Task.Run(() => ide.Query(sql, 5)), cfg.ToolOutputLimit);
                    Check("row limit inside the query, same result: " + sql, limited == plain);
                }

                // 6. the runtime of a slow query is reported; 7. identical parallel queries run once
                var timed = await Tool(ctx, "run_query", new JObject { ["sql"] = slowQuery }, "(The query took ");
                var took = System.Text.RegularExpressions.Regex.Match(timed, @"took ([\d.]+) s");
                var single = took.Success ? double.Parse(took.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                var helperA = ctx.ForHelper(System.Threading.CancellationToken.None, r => Task.FromResult(ApprovalResult.Yes()));
                var helperB = ctx.ForHelper(System.Threading.CancellationToken.None, r => Task.FromResult(ApprovalResult.Yes()));
                sw = Stopwatch.StartNew();
                // helper B asks while helper A's query already runs (its estimate would queue behind that query)
                var first = QueryRunner.RunAsync(helperA, slowQuery, 5);
                await Task.Delay(300);
                var both = await Task.WhenAll(first, QueryRunner.RunAsync(helperB, slowQuery, 5));
                var parallel = sw.Elapsed.TotalSeconds;
                Line($"One run {single:0.0} s; two identical parallel runs {parallel:0.0} s");
                Check("two helpers with the identical query: it ran once, both got the result",
                    both.Count(b => b.Contains("result is shared")) == 1 && both.All(b => b.Contains("N")) && parallel < single * 1.6 + 0.5);
            }
            finally
            {
                await Task.Run(() =>
                {
                    foreach (var drop in new[] { "drop package pda_big_src", "drop procedure pda_long_line", "drop table pda_est_t purge" }) ide.Execute(drop);
                });
            }
        }

        private static async Task SchemaChecks(IIde ide, AgentConfig cfg)
        {
            Line("\n## 8b. Current schema");
            var ctx = new ToolContext { Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()) };
            var home = await Task.Run(() => ctx.CurrentSchema);
            Line("Schema at the start: " + home);
            try
            {
                await Tool(ctx, "get_ide_context", new JObject(), $"(current schema {home})");
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "select count(*) from user_objects" }, "COUNT(*)");
                Check("a plain query does not count as a session change", ctx.SessionChanges == 0);
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "alter session set current_schema = SYSTEM" }, "Executed successfully");
                await Tool(ctx, "get_ide_context", new JObject(), "(current schema SYSTEM)");
                var lookups = await Task.Run(() => ctx.CurrentSchema);
                Check($"ALTER SESSION counts as a session change; unqualified lookups now use {lookups}", ctx.SessionChanges == 1 && lookups == "SYSTEM");
                await Tool(ctx, "execute_sql", new JObject { ["sql"] = $"begin\n  execute immediate 'alter session set current_schema = \"{home}\"';\nend;" }, "Executed successfully");
                await Tool(ctx, "get_ide_context", new JObject(), $"(current schema {home})");
                Check("a PL/SQL block counts as a session change", ctx.SessionChanges == 2);
            }
            finally
            {
                // never leave the session in the other schema
                var now = await Task.Run(() => ctx.QueryCurrentSchema());
                if (now != home) await Task.Run(() => ide.Execute($"alter session set current_schema = \"{home}\""));
                Check("the session is back in schema " + home, await Task.Run(() => ctx.QueryCurrentSchema()) == home);
            }
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
                await TransactionChecks(ide, cfg);
                await SecurityChecks(ide, cfg);
                await AnalysisChecks(ctx, ide);
                await ExploreChecks(ctx);
                await MeasureChecks(ctx, ide);
                await UnitTestChecks(ctx, ide);
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

        /// <summary>
        /// PLSQLDEVAGENT_AUTOTEST_PROMPT = "CACHE-MEASURE": times the dictionary reads a cache could save, through the real
        /// plug-in interface (median of several runs after one warm-up run). Needs the PRB_SRC_* objects of the cache
        /// investigation (packages with 250 to 10,000 body lines and the view PRB_SRC_V).
        /// </summary>
        private static async Task CacheMeasureRunAsync(Plugin plugin)
        {
            var ide = plugin.Ide;
            try
            {
                Line("# PL/Pilot – read costs through the plug-in interface");
                var cfg = AgentConfig.Load();
                var ctx = new ToolContext { Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()) };
                Line($"Separate session: {ide.UsesOwnSession}; tool output limit {cfg.ToolOutputLimit} chars, dossier {cfg.ExploreOutputLimit}\n");
                Line("| What | runs | median ms | min | max | result |");
                Line("|---|---|---|---|---|---|");

                async Task Measure(string label, int runs, Func<string> action)
                {
                    string info = "";
                    try
                    {
                        info = await Task.Run(action); // warm-up (parse, dictionary cache)
                        var ms = new System.Collections.Generic.List<double>();
                        for (int i = 0; i < runs; i++)
                        {
                            var sw = Stopwatch.StartNew();
                            info = await Task.Run(action);
                            ms.Add(sw.Elapsed.TotalMilliseconds);
                        }
                        ms.Sort();
                        Line($"| {label} | {runs} | {ms[ms.Count / 2]:0.0} | {ms[0]:0.0} | {ms[ms.Count - 1]:0.0} | {info} |");
                    }
                    catch (Exception ex) { Line($"| {label} | – | – | – | – | ERROR {ex.GetBaseException().Message.Split('\n')[0]} |"); }
                }
                string Rows(string sql, int max = 200000)
                {
                    var r = ide.Query(sql, max);
                    return $"{r.Rows.Count} rows, {r.Rows.Sum(x => x.Sum(c => (c ?? "").Length)):N0} chars";
                }
                string ToolRun(string name, JObject args)
                {
                    var res = ToolRegistry.All().First(t => t.Name == name).ExecuteAsync(args, ctx).GetAwaiter().GetResult();
                    return $"{res.Length:N0} chars" + (res.Contains("[truncated") ? " (truncated)" : "");
                }
                const string excluded = "('PACKAGE BODY', 'TYPE BODY', 'INDEX PARTITION', 'TABLE PARTITION', 'LOB')";
                string find = "select owner, object_name, object_type, status from all_objects where object_name = 'PRB_SRC_3000' " +
                              $"and object_type not in {excluded} order by decode(owner, 'SCOTT', 0, 'PUBLIC', 2, 1), owner, object_type";
                string findValidator = "select owner, object_name, object_type, status, to_char(object_id), to_char(last_ddl_time, 'YYYYMMDDHH24MISS'), " +
                              "timestamp, to_char(sysdate, 'YYYYMMDDHH24MISS') from all_objects where object_name = 'PRB_SRC_3000' " +
                              $"and object_type not in {excluded} order by decode(owner, 'SCOTT', 0, 'PUBLIC', 2, 1), owner, object_type";

                await Measure("`select 1 from dual` (fixed cost per statement)", 30, () => Rows("select 1 from dual"));
                await Measure("IDE main thread call (GetContext)", 30, () => { ide.GetContext(); return ""; });
                await Measure("current schema query", 20, () => ctx.QueryCurrentSchema());
                await Measure("Find (name lookup as in Db.Find)", 20, () => Rows(find));
                await Measure("Find + validator columns (id, last_ddl_time, timestamp, sysdate)", 20, () => Rows(findValidator));
                await Measure("Validator for 20 objects in one query", 20, () => Rows(
                    "select object_name, object_type, object_id, last_ddl_time, timestamp, status from all_objects where owner = 'SCOTT' " +
                    "and object_name in ('PRB_SRC_250','PRB_SRC_1000','PRB_SRC_3000','PRB_SRC_10000','PRB_SRC_V','EMP','DEPT','EMP_API'," +
                    "'PRB_PKG','PRB_T','PRB_C','PRB_V','PRB_CALLER','PRB_TRG','PRB_SYN','PRB_BIG','PRB_TINY','PRB_SLOW','PRB_CTAS','X')"));
                foreach (var size in new[] { "250", "1000", "3000", "10000" })
                {
                    await Measure($"ALL_SOURCE body PRB_SRC_{size}", 5, () => Rows(
                        $"select text from all_source where owner = 'SCOTT' and name = 'PRB_SRC_{size}' and type = 'PACKAGE BODY' order by line"));
                    await Measure($"get_object_source PRB_SRC_{size} (spec + body)", 5, () => ToolRun("get_object_source", new JObject { ["name"] = "PRB_SRC_" + size }));
                }
                await Measure("get_object_source PRB_SRC_3000 with line numbers", 5, () => ToolRun("get_object_source", new JObject { ["name"] = "PRB_SRC_3000", ["with_line_numbers"] = true }));
                await Measure("ALL_ARGUMENTS PRB_SRC_3000 (334 procedures)", 5, () => Rows(
                    "select object_name, overload, position, argument_name, in_out, data_type, type_name, defaulted from all_arguments " +
                    "where owner = 'SCOTT' and package_name = 'PRB_SRC_3000' and data_level = 0 order by object_name, overload, sequence", 2000));
                await Measure("describe_object PRB_SRC_3000 (signatures)", 5, () => ToolRun("describe_object", new JObject { ["name"] = "PRB_SRC_3000" }));
                await Measure("describe_object PRB_SRC_250 (signatures)", 5, () => ToolRun("describe_object", new JObject { ["name"] = "PRB_SRC_250" }));
                await Measure("View text via IDE_GetObjectSource (main thread)", 5, () => $"{ide.GetObjectSource("VIEW", "SCOTT", "PRB_SRC_V").Length:N0} chars");
                await Measure("View DDL via DBMS_METADATA (CLOB in 1000-char rows)", 5, () => $"{Db.ReadClob(ctx, "dbms_metadata.get_ddl('VIEW', 'PRB_SRC_V', 'SCOTT')").Length:N0} chars");
                await Measure("get_object_source PRB_SRC_V", 5, () => ToolRun("get_object_source", new JObject { ["name"] = "PRB_SRC_V" }));
                await Measure("describe_object EMP (table, not cached in the plan)", 5, () => ToolRun("describe_object", new JObject { ["name"] = "EMP" }));
                await Measure("list_objects (own schema)", 5, () => ToolRun("list_objects", new JObject()));
                await Measure("search_source 'dbms_output' (own schema)", 5, () => ToolRun("search_source", new JObject { ["text"] = "dbms_output" }));
                await Measure("Autocomplete Mentions.Suggest(\"PRB\")", 10, () => $"{Mentions.Suggest("PRB", ctx).Count} suggestions");

                // network round trips per read (V$MYSTAT, needs SELECT_CATALOG_ROLE): at work each one costs the network latency
                (long trips, long bytes) NetStats()
                {
                    var r = ide.Query("select n.name, s.value from v$mystat s join v$statname n on n.statistic# = s.statistic# " +
                                      "where n.name in ('SQL*Net roundtrips to/from client', 'bytes sent via SQL*Net to client')", 5);
                    long Get(string name) => long.Parse(r.Rows.First(x => x[0] == name)[1]);
                    return (Get("SQL*Net roundtrips to/from client"), Get("bytes sent via SQL*Net to client"));
                }
                Line("\n| Read | network round trips | bytes sent to the client |");
                Line("|---|---|---|");
                async Task Trips(string label, Func<string> action)
                {
                    try
                    {
                        await Task.Run(() =>
                        {
                            var b0 = NetStats(); var b1 = NetStats(); // the statistics query itself
                            var a0 = NetStats(); action(); var a1 = NetStats();
                            Line($"| {label} | {(a1.trips - a0.trips) - (b1.trips - b0.trips)} | {(a1.bytes - a0.bytes) - (b1.bytes - b0.bytes):N0} |");
                        });
                    }
                    catch (Exception ex) { Line($"| {label} | ERROR {ex.GetBaseException().Message.Split('\n')[0]} | |"); }
                }
                await Trips("`select 1 from dual`", () => Rows("select 1 from dual"));
                await Trips("Find + validator columns", () => Rows(findValidator));
                foreach (var size in new[] { "250", "1000", "3000", "10000" })
                    await Trips($"ALL_SOURCE body PRB_SRC_{size}", () => Rows(
                        $"select text from all_source where owner = 'SCOTT' and name = 'PRB_SRC_{size}' and type = 'PACKAGE BODY' order by line"));
                // alternative: the lines joined into ~3,000-byte rows on the server (fewer round trips)
                string Chunked(string name) =>
                    "select listagg(text) within group (order by line) from (select line, text, " +
                    "trunc(sum(lengthb(text)) over (order by line) / 3000) bucket from all_source " +
                    $"where owner = 'SCOTT' and name = '{name}' and type = 'PACKAGE BODY') group by bucket order by bucket";
                foreach (var size in new[] { "3000", "10000" })
                {
                    await Trips($"ALL_SOURCE body PRB_SRC_{size}, joined into ~3,000-byte rows", () => Rows(Chunked("PRB_SRC_" + size)));
                    await Measure($"ALL_SOURCE body PRB_SRC_{size}, joined into ~3,000-byte rows", 5, () => Rows(Chunked("PRB_SRC_" + size)));
                }
                await Trips("get_object_source PRB_SRC_3000 (whole tool)", () => ToolRun("get_object_source", new JObject { ["name"] = "PRB_SRC_3000" }));
                await Trips("describe_object PRB_SRC_3000 (whole tool)", () => ToolRun("describe_object", new JObject { ["name"] = "PRB_SRC_3000" }));
                await Trips("View DDL via DBMS_METADATA", () => Db.ReadClob(ctx, "dbms_metadata.get_ddl('VIEW', 'PRB_SRC_V', 'SCOTT')"));
                await Trips("describe_object EMP (whole tool)", () => ToolRun("describe_object", new JObject { ["name"] = "EMP" }));

                // the autocomplete while the agent's statement runs: it queues behind it on the one SQL session
                var slow = Task.Run(() => ide.Query(SlowQuery(3), 1));
                await Task.Delay(300);
                var wait = Stopwatch.StartNew();
                await Task.Run(() => Mentions.Suggest("PRB", ctx));
                Line($"\nAutocomplete while a 3 s statement runs: {wait.ElapsedMilliseconds} ms");
                await slow;
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        /// <summary>
        /// Measures how responsive PL/SQL Developer's main thread is while the agent works in the background (user report
        /// 2026-11-25: the IDE froze while helper agents ran). A probe thread posts a work item to the main thread every
        /// 50 ms and records how long it waited; a second probe makes the IDE read a view through its own session
        /// (IDE_GetObjectSource), like the IDE does when the user opens an object.
        /// </summary>
        private static async Task FreezeMeasureRunAsync(Plugin plugin)
        {
            var ide = plugin.Ide;
            var probe = new MainThreadProbe();
            var timer = new System.Windows.Forms.Timer { Interval = 50 };
            try
            {
                Line("# PL/Pilot – main thread responsiveness while the agent works");
                // PLSQLDEVAGENT_AUTOTEST_SHARED=1: the agent shares PL/SQL Developer's main session (its SQL runs on the main thread)
                bool shared = Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_SHARED") == "1";
                void UseSharedSession()
                {
                    if (!shared) return;
                    if (ide is PlsqlIde pi) { pi.UseSeparateSession = false; pi.ResetSession(); }
                    else Line("(cannot switch the session: " + ide.GetType().Name + ")");
                }
                UseSharedSession();
                // WM_TIMER comes after input and painting: the gaps of a 50 ms timer show whether the IDE could react to the user
                var gaps = new System.Collections.Generic.List<long>();
                var sinceTick = Stopwatch.StartNew();
                timer.Tick += (s2, e2) => { lock (gaps) gaps.Add(sinceTick.ElapsedMilliseconds); sinceTick.Restart(); };
                timer.Start();
                var cfg = AgentConfig.Load();
                var ctx = new ToolContext { Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()) };
                await Task.Run(() => ide.Execute("create or replace view pda_freeze_v as select dummy from dual"));
                Line($"Separate session: {ide.UsesOwnSession}\n");
                Line("| Phase | seconds | main thread: probes | max wait ms | waits > 250 ms | IDE view reads | slowest read ms | timer ticks | longest timer gap ms |");
                Line("|---|---|---|---|---|---|---|---|---|");

                async Task Phase(string label, Func<Task> work, bool ideReads)
                {
                    var reads = new System.Collections.Generic.List<long>();
                    var stop = new System.Threading.CancellationTokenSource();
                    var reader = !ideReads ? Task.CompletedTask : Task.Run(async () =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            var sw = Stopwatch.StartNew();
                            try { ide.GetObjectSource("VIEW", ide.GetContext().User.ToUpperInvariant(), "PDA_FREEZE_V"); } catch { }
                            lock (reads) reads.Add(sw.ElapsedMilliseconds);
                            try { await Task.Delay(700, stop.Token); } catch (OperationCanceledException) { }
                        }
                    });
                    var watch = Stopwatch.StartNew();
                    probe.Reset();
                    lock (gaps) gaps.Clear();
                    try { await work(); }
                    catch (Exception ex) { Line($"(phase {label} failed: {ex.GetBaseException().Message.Split('\n')[0]})"); }
                    stop.Cancel();
                    await reader;
                    var s = probe.Snapshot();
                    long[] g; lock (gaps) g = gaps.ToArray();
                    Line($"| {label} | {watch.Elapsed.TotalSeconds:0.0} | {s.Count} | {s.Max} | {s.Over250} | {reads.Count} | {(reads.Count > 0 ? reads.Max() : 0)} | {g.Length} | {(g.Length > 0 ? g.Max() : 0)} |");
                }

                await Phase("idle", () => Task.Delay(2000), false);
                await Phase("IDE view reads, SQL thread idle", () => Task.Delay(4000), true);
                await Phase("one 6 s statement on the SQL thread", () => Task.Run(() => ide.Query(SlowQuery(6), 1)), false);
                await Phase("one 6 s statement + IDE view reads", () => Task.Run(() => ide.Query(SlowQuery(6), 1)), true);

                // helper-like load: three workers running read-only tools back to back, as parallel run_subtask helpers do
                string[][] jobs =
                {
                    new[] { "explore_objects", "{\"names\":[\"EMP\"]}" },
                    new[] { "get_object_source", "{\"name\":\"EMP_API\"}" },
                    new[] { "describe_object", "{\"name\":\"EMP\"}" },
                    new[] { "search_source", "{\"text\":\"emp\"}" },
                    new[] { "list_objects", "{}" },
                    new[] { "find_dependencies", "{\"name\":\"EMP\"}" },
                };
                int toolRuns = 0;
                async Task Workers(int seconds)
                {
                    var until = DateTime.UtcNow.AddSeconds(seconds);
                    await Task.WhenAll(Enumerable.Range(0, 3).Select(w => Task.Run(() =>
                    {
                        for (int i = w; DateTime.UtcNow < until; i++)
                        {
                            var job = jobs[i % jobs.Length];
                            var tool = ToolRegistry.All().FirstOrDefault(t => t.Name == job[0]);
                            if (tool == null) continue;
                            try { tool.ExecuteAsync(JObject.Parse(job[1]), ctx).GetAwaiter().GetResult(); } catch { }
                            System.Threading.Interlocked.Increment(ref toolRuns);
                        }
                    })));
                }
                await Phase("3 parallel tool loops (helper-like)", () => Workers(12), false);
                await Phase("3 parallel tool loops + IDE view reads", () => Workers(12), true);
                Line($"\nTool runs in the two loop phases: {toolRuns}");

                // the real thing: helper agents of the configured (or PLSQLDEVAGENT_AUTOTEST_MODEL) model
                plugin.OnMenuClick(Bootstrap.MiChat);
                var chat = plugin.ChatWindow;
                for (int i = 0; i < 150 && !chat.IsChatReady; i++) await Task.Delay(200);
                UseTestModel(chat);
                UseSharedSession(); // opening the chat applied the setting again
                await Phase("real run_subtask helpers (chat) + IDE view reads", () => SendAndWait(chat,
                    "Call run_subtask twice in the same response (they run in parallel): helper 1 finds which objects in my schema " +
                    "reference the table EMP, helper 2 summarizes what the package EMP_API does. Then summarize both reports in two sentences."), true);
                var sub = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("[...document.querySelectorAll('.substeps')].map(s => s.innerText).join(' | ')") ?? "\"\"");
                Line("Helper steps: " + sub.Replace("\n", " "));
                Line("\nSlowest main-thread waits overall: " + string.Join(", ", probe.Worst(10)));
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                probe.Dispose();
                timer.Stop();
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        /// <summary>
        /// The editor window of a request while the user activates other windows (IDE_SelectWindow: read and write a window
        /// that is not in front, which stays in the background), and large units (a 4,000-line package body changed with
        /// edit_object_source, compile_plsql from the editor window of the request).
        /// </summary>
        private static async Task WindowBindingRunAsync(Plugin plugin)
        {
            var ide = plugin.Ide;
            try
            {
                Line("# PL/Pilot – editor window of a request, large units");
                var cfg = AgentConfig.Load();
                CloseAllWindows();
                ide.NewWindow(IdeWindowType.Sql, "select 'A' x from dual");
                await Task.Delay(500);
                var a = await Task.Run(() => ide.GetContext());
                var target = EditorTarget.From(a);
                Line($"Window A: {a.WindowName}, editor control {a.EditorHandle}");
                Check("the active window has an editor control handle", a.EditorHandle != 0);
                ide.NewWindow(IdeWindowType.Sql, "select 'B' x from dual");
                await Task.Delay(500);
                var b = await Task.Run(() => ide.GetContext());
                Check($"window B is in front with another editor control ({b.EditorHandle})", b.EditorHandle != 0 && b.EditorHandle != a.EditorHandle);
                var textA = await Task.Run(() => ide.GetEditorText(target));
                var ctxA = await Task.Run(() => ide.GetWindowContext(target));
                Check($"window A is read while B is in front ({textA.Trim()})", textA.Contains("'A'") && ctxA.EditorHandle == a.EditorHandle);
                await Task.Run(() => ide.ReplaceEditorText(textA, "select 'A2' x from dual", target));
                var now = await Task.Run(() => ide.GetContext());
                var aAfter = await Task.Run(() => ide.GetEditorText(target));
                var bText = await Task.Run(() => ide.GetEditorText());
                Check($"writing window A leaves B in front and unchanged (A: {aAfter.Trim()}, active: {bText.Trim()})",
                    aAfter.Contains("'A2'") && now.EditorHandle == b.EditorHandle && bText.Contains("'B'"));

                // the tools with the request's window bound
                var ctx = new ToolContext
                {
                    Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()),
                    EditorBinding = new ToolContext.EditorWindowHolder { Value = target },
                };
                await Tool(ctx, "edit_editor", new JObject { ["old_string"] = "'A2'", ["new_string"] = "'A3'" }, "Editor updated (Sql window");
                Check("edit_editor changed window A, B stays in front",
                    (await Task.Run(() => ide.GetEditorText(target))).Contains("'A3'") && (await Task.Run(() => ide.GetContext())).EditorHandle == b.EditorHandle);
                await Tool(ctx, "get_ide_context", new JObject(), "Window the user has active now");

                // compile_plsql from the request's window while another window is in front
                ide.NewWindow(IdeWindowType.Sql, "create or replace procedure pda_win_p is\nbegin\n  null;\nend;\n");
                await Task.Delay(500);
                ctx.BindEditorWindow(await Task.Run(() => ide.GetContext()));
                ide.NewWindow(IdeWindowType.Sql, "select 'D' x from dual");
                await Task.Delay(500);
                await Tool(ctx, "compile_plsql", new JObject { ["from_editor"] = true }, "PROCEDURE SCOTT.PDA_WIN_P: compiled successfully");

                // a closed window is reported, nothing else is changed
                CloseAllWindows();
                ctx.EditorBinding.Value = target;
                await Tool(ctx, "get_editor_text", new JObject(), "is not open any more");

                // a 4,000-line package: changed with edit_object_source (the model sends only the changed lines)
                var body = new StringBuilder("create or replace package body pda_big is\n");
                for (int i = 1; i <= 800; i++)
                    body.Append($"  procedure p{i} is\n  begin\n    dbms_output.put_line('p{i}');\n  end p{i};\n\n");
                body.Append("end pda_big;");
                var spec = new StringBuilder("create or replace package pda_big is\n");
                for (int i = 1; i <= 800; i++) spec.Append($"  procedure p{i};\n");
                spec.Append("end pda_big;");
                var r1 = await Task.Run(() => ide.Execute(spec.ToString()));
                var r2 = await Task.Run(() => ide.Execute(body.ToString()));
                var lines = (await Task.Run(() => ide.Query("select count(*) from user_source where name = 'PDA_BIG' and type = 'PACKAGE BODY'", 1))).Rows[0][0];
                Line($"PDA_BIG created: spec {r1.Ok}, body {r2.Ok}, {lines} body lines, {body.Length:N0} characters");

                // Program windows (an object opened for editing) changed in the background, for a small and the large unit
                await Task.Run(() => ide.Execute("create or replace procedure pda_win_p2 is\nbegin\n  null;\nend;"));
                foreach (var (type, name) in new[] { ("PROCEDURE", "PDA_WIN_P2"), ("PACKAGE BODY", "PDA_BIG") })
                {
                    CloseAllWindows();
                    ide.ObjectAction("EDIT", type, "SCOTT", name);
                    await Task.Delay(1500);
                    var prog = await Task.Run(() => ide.GetContext());
                    var progTarget = EditorTarget.From(prog);
                    ide.NewWindow(IdeWindowType.Sql, "select 'front' x from dual");
                    await Task.Delay(700);
                    var front = await Task.Run(() => ide.GetContext());
                    Line($"{type} {name}: program window {prog.WindowName} ({prog.EditorHandle}), in front: {front.WindowName} ({front.EditorHandle}); windows: {WindowList()}; MDI front: {MdiFront()}");
                    var ptext = await Task.Run(() => ide.GetEditorText(progTarget));
                    var marker = ptext.Contains("dbms_output.put_line('p3');") ? "dbms_output.put_line('p3');" : "null;";
                    await Task.Run(() => ide.ReplaceEditorText(ptext, ptext.Replace(marker, marker + " -- bg edit"), progTarget));
                    await Task.Delay(700);
                    var after = await Task.Run(() => ide.GetContext());
                    var mdiAfter = MdiFront();
                    Line($"after the edit: selected/top {after.WindowName} ({after.EditorHandle}); windows: {WindowList()}; MDI front: {mdiAfter}");
                    Check($"the {type} window is changed in the background and the SQL window stays in front",
                        (await Task.Run(() => ide.GetEditorText(progTarget))).Contains("-- bg edit") && after.EditorHandle == front.EditorHandle && mdiAfter.Contains("Sql"));
                }
                CloseAllWindows();
                var sw = Stopwatch.StartNew();
                await Tool(ctx, "edit_object_source", new JObject
                {
                    ["name"] = "PDA_BIG",
                    ["edits"] = new JArray(new JObject { ["old_string"] = "    dbms_output.put_line('p777');", ["new_string"] = "    dbms_output.put_line('p777 changed');" }),
                }, "PACKAGE BODY SCOTT.PDA_BIG: compiled successfully");
                Line($"edit_object_source on {lines} lines: {sw.ElapsedMilliseconds} ms");
                var check = await Task.Run(() => ide.Query("select (select status from user_objects where object_name = 'PDA_BIG' and object_type = 'PACKAGE BODY'), " +
                    "(select count(*) from user_source where name = 'PDA_BIG' and type = 'PACKAGE BODY' and text like '%p777 changed%'), " +
                    "(select count(*) from user_source where name = 'PDA_BIG' and type = 'PACKAGE BODY') from dual", 1));
                var row = check.Rows[0];
                Check($"the 4,000-line body is changed in one line and valid ({string.Join(" | ", row)})", row[0] == "VALID" && row[1] == "1" && row[2] == lines);
                await Tool(ctx, "edit_object_source", new JObject
                {
                    ["name"] = "PDA_BIG",
                    ["edits"] = new JArray(new JObject { ["old_string"] = "  procedure p12;", ["new_string"] = "  procedure p12; -- spec edit" }),
                }, "PACKAGE SCOTT.PDA_BIG: compiled successfully");
                await Tool(ctx, "edit_object_source", new JObject
                {
                    ["name"] = "PDA_BIG",
                    ["edits"] = new JArray(new JObject { ["old_string"] = "no such text", ["new_string"] = "x" }),
                }, "old_string was not found");
                await Task.Run(() => ide.Execute("drop package pda_big"));
                await Task.Run(() => ide.Execute("drop procedure pda_win_p"));
                await Task.Run(() => ide.Execute("drop procedure pda_win_p2"));
                Line($"\n**{_pass} passed, {_fail} failed**");
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        /// <summary>
        /// The real model with a 4,000-line package: it must change one procedure without sending the whole package
        /// (edit_object_source, or edit_editor + compile_plsql from_editor), and keep editing its window while another
        /// window is opened in front during the request.
        /// </summary>
        private static async Task BigUnitLlmRunAsync(Plugin plugin)
        {
            var ide = plugin.Ide;
            try
            {
                Line("# PL/Pilot – large package with the real model");
                CloseAllWindows();
                var body = new StringBuilder("create or replace package body pda_big is\n");
                for (int i = 1; i <= 800; i++)
                    body.Append($"  procedure p{i} is\n  begin\n    dbms_output.put_line('p{i}');\n  end p{i};\n\n");
                body.Append("end pda_big;");
                var spec = new StringBuilder("create or replace package pda_big is\n");
                for (int i = 1; i <= 800; i++) spec.Append($"  procedure p{i};\n");
                spec.Append("end pda_big;");
                await Task.Run(() => ide.Execute(spec.ToString()));
                await Task.Run(() => ide.Execute(body.ToString()));

                plugin.OnMenuClick(Bootstrap.MiChat);
                var chat = plugin.ChatWindow;
                for (int i = 0; i < 150 && !chat.IsChatReady; i++) await Task.Delay(200);
                UseTestModel(chat);
                await chat.ExecuteScriptAsync(CarefulApprover());
                async Task<string> Run(string prompt, Func<Task> during = null)
                {
                    Line("\nPrompt: " + prompt);
                    await chat.ExecuteScriptAsync("document.getElementById('input').value = " + JsonConvert.SerializeObject(prompt) + "; submit();");
                    var sw = Stopwatch.StartNew();
                    bool sawBusy = false, done = false;
                    while (sw.Elapsed < TimeSpan.FromMinutes(15))
                    {
                        await Task.Delay(1000);
                        if (chat.IsBusy) sawBusy = true;
                        else if (sawBusy || sw.Elapsed > TimeSpan.FromSeconds(30)) break;
                        if (sawBusy && !done && during != null && sw.Elapsed > TimeSpan.FromSeconds(4)) { done = true; await during(); }
                    }
                    if (chat.IsBusy) { Line("(still working after 15 minutes: stopped)"); chat.StopForTest(); for (int i = 0; i < 60 && chat.IsBusy; i++) await Task.Delay(500); }
                    var transcript = JsonConvert.DeserializeObject<string>(await chat.ExecuteScriptAsync("document.getElementById('messages').innerText") ?? "\"\"") ?? "";
                    Line($"Finished after {sw.Elapsed.TotalSeconds:0} s");
                    return transcript;
                }

                // 1. a stored 4,000-line body
                var t1 = await Run("In the package body PDA_BIG (about 4,000 lines), change procedure p777 so that it prints 'p777 v2' instead of 'p777'. Compile it.");
                File.WriteAllText(Path.Combine(_dir, "transcript-1.txt"), t1, Encoding.UTF8);
                var row = (await Task.Run(() => ide.Query("select (select status from user_objects where object_name = 'PDA_BIG' and object_type = 'PACKAGE BODY'), " +
                    "(select count(*) from user_source where name = 'PDA_BIG' and type = 'PACKAGE BODY' and text like '%p777 v2%'), " +
                    "(select count(*) from user_source where name = 'PDA_BIG' and type = 'PACKAGE BODY') from dual", 1))).Rows[0];
                Line($"Tools used: {ToolNames(t1)}; body {row[0]}, changed lines {row[1]}, {row[2]} lines");
                Check("the model changed one procedure of the 4,000-line body without sending it whole", row[0] == "VALID" && row[1] == "1" && int.Parse(row[2]) >= 4000 &&
                    (t1.Contains("edit_object_source") || t1.Contains(L.T("text of the editor window"))) && !t1.Contains(L.T("cut off at the output limit")));
                // the approval of an existing unit shows a diff, not the whole 4,000 lines
                Check("the compile approval shows a diff against the database, not the whole unit", !t1.Contains("procedure p13 is") || t1.Contains("edit_object_source"));

                // 2. the open program window, while the user opens another window in front during the request
                await NewChatForTest(chat);
                CloseAllWindows();
                await Task.Run(() => ide.ObjectAction("EDIT", "PACKAGE BODY", "SCOTT", "PDA_BIG"));
                await Task.Delay(1500);
                var program = await Task.Run(() => ide.GetContext());
                var target = EditorTarget.From(program);
                Line($"Opened: {program.WindowName} ({program.EditorHandle}); windows: {WindowList()}");
                var t2 = await Run("In this editor window add the comment line '-- checked by PL/Pilot' directly after the 'begin' of procedure p3. Only change the editor text, do not compile.",
                    async () =>
                    {
                        ide.NewWindow(IdeWindowType.Sql, "select 'user window' x from dual");
                        Line($"(the user opened another SQL window in front) windows: {WindowList()}; MDI front: {MdiFront()}");
                        await Task.Delay(100);
                    });
                File.WriteAllText(Path.Combine(_dir, "transcript-2.txt"), t2, Encoding.UTF8);
                var programText = await Task.Run(() => { try { return ide.GetEditorText(target); } catch (Exception ex) { return "ERROR " + ex.Message; } });
                var active = await Task.Run(() => ide.GetContext());
                var activeText = await Task.Run(() => ide.GetEditorText());
                Line($"Tools used: {ToolNames(t2)}; active now: {active.WindowName} ({active.EditorHandle}); windows: {WindowList()}; MDI front: {MdiFront()}");
                Check("the model edited its program window while another window was in front, which stayed unchanged",
                    programText.Contains("-- checked by PL/Pilot") && activeText.Contains("'user window'") && !activeText.Contains("checked by"));
                await Task.Run(() => ide.Execute("drop package pda_big"));
                Line($"\n**{_pass} passed, {_fail} failed**");
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        /// <summary>"0: Sql 68118 | 1: Program PDA_BIG 68208" – the IDE's window list as IDE_SelectWindow sees it.</summary>
        private static string WindowList() => MainThread.Invoke(() =>
        {
            var parts = new System.Collections.Generic.List<string>();
            int count = Callbacks.IDE_GetWindowCount();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (Callbacks.IDE_SelectWindow(i) == 0) continue;
                    var name = Callbacks.IDE_GetWindowObject(out _, out _, out var n, out _) != 0 ? " " + Marshal.PtrToStringAnsi(n) : "";
                    parts.Add($"{i}: {(IdeWindowType)Callbacks.IDE_GetWindowType()}{name} {Callbacks.IDE_GetEditorHandle()}");
                }
            }
            finally { Callbacks.IDE_SelectWindow(0); }
            return string.Join(" | ", parts);
        });

        /// <summary>The MDI child Windows shows in front (WM_MDIGETACTIVE), with the editor control it contains.</summary>
        private static string MdiFront() => MainThread.Invoke(() =>
        {
            var main = new IntPtr(Callbacks.IDE_GetWindowHandle());
            var client = FindWindowEx(main, IntPtr.Zero, "MDIClient", null);
            if (client == IntPtr.Zero) return "(no MDIClient)";
            var child = SendMessage(client, 0x0229 /* WM_MDIGETACTIVE */, IntPtr.Zero, IntPtr.Zero);
            var title = new StringBuilder(256);
            GetWindowText(child, title, 256);
            // which window of the IDE's list owns that child: compare editor controls
            int count = Callbacks.IDE_GetWindowCount();
            string owner = "?";
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (Callbacks.IDE_SelectWindow(i) == 0) continue;
                    var editor = new IntPtr(Callbacks.IDE_GetEditorHandle());
                    if (editor != IntPtr.Zero && IsChild(child, editor)) { owner = $"{i}: {(IdeWindowType)Callbacks.IDE_GetWindowType()} {editor}"; break; }
                }
            }
            finally { Callbacks.IDE_SelectWindow(0); }
            return $"'{title}' = {owner}";
        });

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);

        /// <summary>
        /// Setting "No parallel execution": the agent's own session is switched to serial (v$session pq/pdml/pddl status),
        /// rewritten statements run in Oracle, the executed text carries NO_PARALLEL, switching parallel on is refused,
        /// and turning the setting off restores Oracle's defaults. (Oracle Free has no parallel servers, so actual PX
        /// cannot be observed here; Free sessions also start with parallel query disabled, so it is enabled first.)
        /// </summary>
        private static async Task ParallelRunAsync(Plugin plugin)
        {
            var ide = plugin.Ide;
            try
            {
                Line("# PL/Pilot – no parallel execution");
                if (!(ide is PlsqlIde pi)) { Line("FATAL: not the real IDE"); return; }
                var cfg = AgentConfig.Load();
                cfg.DisableParallel = true;
                var ctx = new ToolContext { Ide = ide, Config = cfg, RequestApproval = r => Task.FromResult(ApprovalResult.Yes()) };
                Task<string> Status() => Task.Run(() => ide.Query("select pq_status || ' ' || pdml_status || ' ' || pddl_status from v$session where sid = sys_context('userenv','sid')", 1).Rows[0][0]);

                pi.DisableParallel = false;
                await Task.Run(() => ide.Execute("alter session enable parallel query"));
                var before = await Status();
                pi.DisableParallel = true;
                var after = await Status();
                Line($"Separate session: {ide.UsesOwnSession}; session status before: {before}, with the setting: {after}");
                bool canParallel = before.StartsWith("ENABLED");
                var log = File.ReadAllText(Log.FilePath);
                log = log.Substring(Math.Max(0, log.Length - 20000));
                if (canParallel)
                    Check($"the setting switches the agent's session to serial ({before} -> {after})", after == "DISABLED DISABLED DISABLED");
                else
                    Check("this database does not allow parallel query in sessions (Oracle Free); the ALTER SESSION statements ran without error",
                        log.Contains("Agent session: parallel execution disabled") && !log.Contains("disable parallel query failed"));

                var run = Guid.NewGuid().ToString("N").Substring(0, 6);
                await Tool(ctx, "run_query", new JObject { ["sql"] = $"select /*+ parallel(4) full(e) */ count(*) pda_px_n{run} from emp e" }, "PDA_PX_N");
                await Tool(ctx, "run_query", new JObject { ["sql"] = $"with d as (select /*+ parallel */ deptno from dept) select count(*) pda_px_w{run} from d" }, "PDA_PX_W");
                await Tool(ctx, "measure_sql", new JObject { ["sql"] = $"select /*+ parallel(2) */ count(*) pda_px_m{run} from emp" }, "PDA_PX_M");
                await Tool(ctx, "explain_plan", new JObject { ["sql"] = $"select /*+ parallel(2) */ count(*) pda_px_e{run} from emp" }, "Plan hash value");
                var texts = await Task.Run(() => ide.Query($"select sql_text from v$sql where lower(sql_text) like '%pda_px__{run}%' " +
                    "and sql_text not like '%v$sql%' order by last_active_time desc", 20));
                var all = texts.Rows.Select(r => r[0]).ToList();
                Line("Executed statements:\n```\n" + string.Join("\n", all) + "\n```");
                Check("Oracle ran the rewritten statements, each with NO_PARALLEL and without its PARALLEL hint",
                    all.Count >= 3 && all.All(t => t.Contains("NO_PARALLEL") && !System.Text.RegularExpressions.Regex.IsMatch(t, @"(?<!NO_)PARALLEL\s*\(|/\*\+\s*PARALLEL\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));

                await Tool(ctx, "execute_sql", new JObject { ["sql"] = "alter session enable parallel query" }, "parallel execution is switched off");
                Check("the session stays serial", (await Status()) == "DISABLED DISABLED DISABLED");

                pi.DisableParallel = false;
                var off = await Status();
                if (canParallel) Check($"turning the setting off restores Oracle's defaults ({off})", off == "ENABLED DISABLED ENABLED");
                else Line($"Setting off: {off} (Oracle Free keeps parallel query disabled)");
                Line($"\n**{_pass} passed, {_fail} failed**");
            }
            catch (Exception ex) { Line("FATAL: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(_dir, "report.md"), Report.ToString(), Encoding.UTF8);
                await CloseIde();
            }
        }

        /// <summary>The tool names that appear in a chat transcript (tool call lines), in order.</summary>
        private static string ToolNames(string transcript) =>
            string.Join(", ", ToolRegistry.All().Select(t => t.Name).Where(n => transcript.Contains(n)));

        private static async Task NewChatForTest(ChatForm chat)
        {
            await chat.ExecuteScriptAsync("document.getElementById('input').value = '/new'; submit();");
            await Task.Delay(1500);
        }

        /// <summary>Posts a work item to the main thread every 50 ms and records how long it waited to run.</summary>
        private sealed class MainThreadProbe : IDisposable
        {
            private readonly System.Collections.Generic.List<(DateTime at, long ms)> _samples = new System.Collections.Generic.List<(DateTime, long)>();
            private readonly System.Collections.Generic.List<(DateTime at, long ms)> _all = new System.Collections.Generic.List<(DateTime, long)>();
            private volatile bool _stop;
            private readonly System.Threading.Thread _thread;

            public MainThreadProbe()
            {
                _thread = new System.Threading.Thread(Loop) { IsBackground = true, Name = "main thread probe" };
                _thread.Start();
            }

            private void Loop()
            {
                while (!_stop)
                {
                    var sw = Stopwatch.StartNew();
                    using (var done = new System.Threading.ManualResetEventSlim(false))
                    {
                        MainThread.Post(() => done.Set());
                        done.Wait(120000);
                    }
                    var sample = (DateTime.Now, sw.ElapsedMilliseconds);
                    lock (_samples) { _samples.Add(sample); _all.Add(sample); }
                    System.Threading.Thread.Sleep(50);
                }
            }

            public void Reset() { lock (_samples) _samples.Clear(); }

            public (int Count, long Max, int Over250) Snapshot()
            {
                lock (_samples)
                    return (_samples.Count, _samples.Count == 0 ? 0 : _samples.Max(s => s.ms), _samples.Count(s => s.ms > 250));
            }

            public System.Collections.Generic.IEnumerable<string> Worst(int n)
            {
                lock (_samples) return _all.OrderByDescending(s => s.ms).Take(n).Select(s => $"{s.ms} ms at {s.at:HH:mm:ss}").ToList();
            }

            public void Dispose() => _stop = true;
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
            UseTestModel(chat);
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
            UseTestModel(chat);
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
            UseTestModel(chat);
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

        /// <summary>
        /// PLSQLDEVAGENT_AUTOTEST_MODEL: the real-model tests use this model instead of the configured one
        /// (for this session only, the settings are not changed), e.g. a smaller, faster model.
        /// </summary>
        private static void UseTestModel(ChatForm chat)
        {
            var model = Environment.GetEnvironmentVariable("PLSQLDEVAGENT_AUTOTEST_MODEL");
            if (string.IsNullOrWhiteSpace(model)) return;
            chat.ApplyConfigForTest(c => c.Model = model.Trim());
            Line($"Model for this test: {model.Trim()} (PLSQLDEVAGENT_AUTOTEST_MODEL; settings unchanged)");
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
