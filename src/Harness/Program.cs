using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using PlsqlDevAgent.Agent;
using PlsqlDevAgent.UI;

namespace PlsqlDevAgent.Harness
{
    /// <summary>
    /// Runs the agent UI outside PL/SQL Developer.
    ///   --mock-llm        use a scripted fake LLM server (no real model needed)
    ///   --autotest <dir>  drive one conversation automatically, auto-approve, write results + screenshot to dir, exit
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool mock = args.Contains("--mock-llm");
            int autoIdx = Array.IndexOf(args, "--autotest");
            string outDir = autoIdx >= 0 && autoIdx + 1 < args.Length ? args[autoIdx + 1] : null;

            int llmIdx = Array.IndexOf(args, "--llm");     // --llm <baseUrl> <model>: use a real server with a temp config
            int promptIdx = Array.IndexOf(args, "--prompt");
            string prompt = promptIdx >= 0 && promptIdx + 1 < args.Length ? args[promptIdx + 1] : "Please improve the query in the editor";
            bool offscreen = args.Contains("--offscreen");

            if (mock || llmIdx >= 0)
            {
                // keep test chats, prompts and logs out of the user's real data folder
                Environment.SetEnvironmentVariable("PLSQLDEVAGENT_DATA", Path.Combine(Path.GetTempPath(), "plsqldevagent-harness-data"));
                var cfgPath = Path.Combine(Path.GetTempPath(), "plsqldevagent-harness-config.json");
                Environment.SetEnvironmentVariable("PLSQLDEVAGENT_CONFIG", cfgPath);
                int langIdx = Array.IndexOf(args, "--lang");
                var cfg = new AgentConfig
                {
                    UseSystemProxy = false, WindowX = offscreen ? -4000 : -1, WindowY = offscreen ? 100 : -1, Theme = args.Contains("--dark") ? "dark" : "light",
                    Language = langIdx >= 0 && langIdx + 1 < args.Length ? args[langIdx + 1] : "en",
                };
                if (mock)
                {
                    var server = new MockLlmServer(18765);
                    server.Start();
                    cfg.BaseUrl = $"http://127.0.0.1:{server.Port}/v1";
                    cfg.Model = "mock-deepseek";
                }
                else
                {
                    cfg.BaseUrl = args[llmIdx + 1];
                    cfg.Model = args[llmIdx + 2];
                }
                cfg.Save();
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new HarnessForm(outDir, prompt, offscreen));
        }
    }

    internal sealed class HarnessForm : Form
    {
        public readonly TextBox Editor;
        private readonly string _autoTestDir;

        private const string SampleCode =
            "create or replace package body emp_api is\r\n" +
            "  procedure list_emps(p_deptno in number) is\r\n" +
            "  begin\r\n" +
            "    for r in (select * from emp e where e.deptno = p_deptno) loop\r\n" +
            "      dbms_output.put_line(r.ename);\r\n" +
            "    end loop;\r\n" +
            "  end list_emps;\r\n" +
            "end emp_api;\r\n";

        private readonly string _prompt;
        private readonly bool _offscreen;

        public HarnessForm(string autoTestDir, string prompt, bool offscreen)
        {
            _autoTestDir = autoTestDir;
            _prompt = prompt;
            _offscreen = offscreen;
            Text = "PL/Pilot – test harness (simulated IDE)";
            Size = new Size(900, 700);
            StartPosition = FormStartPosition.Manual;
            Location = offscreen ? new Point(-4000, 40) : new Point(40, 40);
            if (offscreen) ShowInTaskbar = false;

            Editor = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 10), ScrollBars = ScrollBars.Both, WordWrap = false, Text = SampleCode, HideSelection = false };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34 };
            bar.Controls.Add(Button("PL/Pilot Chat", () => Plugin.Instance.OnMenuClick(Bootstrap.MiChat)));
            bar.Controls.Add(Button("Ask about Selection", () => Plugin.Instance.OnMenuClick(Bootstrap.MiAskSelection)));
            bar.Controls.Add(Button("Ask about object EMP", () => Plugin.Instance.OnMenuClick(Bootstrap.MiPopupObject)));
            bar.Controls.Add(Button("Settings", () => Plugin.Instance.OnMenuClick(Bootstrap.MiSettings)));
            Controls.Add(Editor);
            Controls.Add(bar);

            Shown += async (s, e) =>
            {
                Plugin.Create(new MockIde(this));
                if (_autoTestDir != null) await AutoTest();
            };
        }

        private static Button Button(string text, Action onClick)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (s, e) => onClick();
            return b;
        }

        private ChatForm _chat;
        private StringWriter _log;
        private int _pass, _fail;

        private async Task<string> Js(string script)
        {
            var r = await _chat.ExecuteScriptAsync(script);
            try { return JsonConvert.DeserializeObject<object>(r ?? "null")?.ToString() ?? ""; } catch { return r ?? ""; }
        }

        private void Check(string name, bool ok)
        {
            if (ok) _pass++; else _fail++;
            _log.WriteLine((ok ? "PASS " : "FAIL ") + name);
        }

        private async Task SendAndWait(string text)
        {
            await Js("document.getElementById('input').value = " + JsonConvert.SerializeObject(text) + "; submit();");
            bool sawBusy = false;
            for (int i = 0; i < 3000; i++) // up to 10 minutes for real models
            {
                await Task.Delay(200);
                if (_chat.IsBusy) sawBusy = true;
                else if (sawBusy || i > 25) break;
            }
            await Task.Delay(1200);
        }

        /// <summary>Runs one conversation (and, with the mock LLM, checks all chat features).</summary>
        private async Task AutoTest()
        {
            Directory.CreateDirectory(_autoTestDir);
            _log = new StringWriter();
            var log = _log;
            try
            {
                Plugin.Instance.OnMenuClick(Bootstrap.MiChat);
                var chat = _chat = Plugin.Instance.ChatWindow;
                for (int i = 0; i < 150 && !chat.IsChatReady; i++) await Task.Delay(200);
                log.WriteLine("chat ready: " + chat.IsChatReady);

                // auto approve every approval card
                await chat.ExecuteScriptAsync("setInterval(() => { const b = document.querySelector('.approval:not(.done):not(.rewind-card) button.primary'); if (b) b.click(); }, 700);");
                await SendAndWait(_prompt);
                log.WriteLine("busy now: " + chat.IsBusy);
                if (_prompt.StartsWith("Please improve"))
                {
                    if (PlsqlDevAgent.Util.L.German) await GermanChecks();
                    else { await FeatureChecks(); await LongTaskChecks(); await ParallelAndScrollChecks(); await RewindUiChecks(); await PromptChecks(); }
                }

                var transcript = await chat.ExecuteScriptAsync("document.getElementById('messages').innerText");
                File.WriteAllText(Path.Combine(_autoTestDir, "transcript.txt"), JsonConvert.DeserializeObject<string>(transcript ?? "\"\""));
                File.WriteAllText(Path.Combine(_autoTestDir, "editor.txt"), Editor.Text);
                if (Plugin.Instance != null && _offscreen) { log.WriteLine("offscreen: no screenshots"); throw new OperationCanceledException("done"); }

                // expand tool details for the screenshot
                await chat.ExecuteScriptAsync("document.querySelectorAll('details.tool')[1] && (document.querySelectorAll('details.tool')[1].open = true); document.getElementById('messages').scrollTop = 0;");
                await Task.Delay(500);
                await ScreenshotAsync(chat, Path.Combine(_autoTestDir, "chat-top.png"));
                await chat.ExecuteScriptAsync("const m = document.getElementById('messages'); m.scrollTop = m.scrollHeight;");
                await Task.Delay(500);
                await ScreenshotAsync(chat, Path.Combine(_autoTestDir, "chat-bottom.png"));

                // settings dialog + connection test against the mock server
                Plugin.Instance.OnMenuClick(Bootstrap.MiSettings);
                await Task.Delay(800);
                await chat.ExecuteScriptAsync("document.getElementById('btnTest').click()");
                await Task.Delay(2000);
                await ScreenshotAsync(chat, Path.Combine(_autoTestDir, "settings.png"));
                var test = await chat.ExecuteScriptAsync("document.getElementById('testResult').innerText");
                log.WriteLine("connection test: " + test);
            }
            catch (Exception ex)
            {
                log.WriteLine("EXCEPTION: " + ex);
            }
            log.WriteLine($"TOTAL: {_pass} passed, {_fail} failed");
            File.WriteAllText(Path.Combine(_autoTestDir, "autotest.log"), log.ToString());
            Application.Exit();
        }

        private async Task FeatureChecks()
        {
            var original = SampleCode;
            Check("agent edited the editor", Editor.Text.Contains("select e.empno, e.ename, e.sal from emp"));
            Check("token meter shows server usage", (await Js("document.getElementById('meterText').textContent")).Contains("/") &&
                                                    !(await Js("document.getElementById('meterText').textContent")).StartsWith("~"));

            // revert the agent's edit
            await Js("document.querySelector('.actions [data-undo]').click()");
            await Task.Delay(800);
            Check("revert button restored the editor", Editor.Text == original);

            // insert at cursor (mock cursor = line 1, col 1) and revert
            await Js("document.querySelectorAll('[data-insert]')[0].click()");
            await Task.Delay(800);
            Check("insert at cursor", Editor.Text.StartsWith("select e.empno, e.ename, e.sal\r\n  from emp e"));
            await Js("document.querySelector('.notice.applied [data-undo]').click()");
            await Task.Delay(800);
            Check("revert of the insert", Editor.Text == original);

            // replace selection
            var sel = "dbms_output.put_line(r.ename);";
            Editor.Select(Editor.Text.IndexOf(sel, StringComparison.Ordinal), sel.Length);
            await Js("document.querySelectorAll('[data-replace]')[0].click()");
            await Task.Delay(800);
            Check("replace selection", !Editor.Text.Contains(sel) && Editor.Text.Contains("where e.deptno = p_deptno; -- comment"));
            await Js("[...document.querySelectorAll('.notice.applied [data-undo]')].pop().click()");
            await Task.Delay(800);
            Check("revert of the replacement", Editor.Text == original);

            // retry
            await Js("document.querySelector('.endbar button').click()");
            await Task.Delay(300);
            for (int i = 0; i < 100 && _chat.IsBusy; i++) await Task.Delay(200);
            await Task.Delay(1200);
            Check("retry keeps one user message and answers again",
                await Js("document.querySelectorAll('.msg.user').length") == "1" && (await Js("document.getElementById('messages').innerText")).Contains("Done"));
            Editor.Text = original;

            // commands
            await Js("document.getElementById('input').value = '/help'; submit();");
            await Task.Delay(800);
            Check("/help shows the command list", (await Js("document.querySelector('.msg.info') ? document.querySelector('.msg.info').innerText : ''")).Contains("/review"));
            await Js("(() => { const i = document.getElementById('input'); i.focus(); i.value = '/re'; i.setSelectionRange(3, 3); i.dispatchEvent(new Event('input')); })()");
            await Task.Delay(300);
            Check("command popup filters (/re -> /review)", (await Js("document.getElementById('popup').innerText")).Contains("/review"));
            await Js("(() => { const i = document.getElementById('input'); i.focus(); i.value = 'look at @EM'; i.setSelectionRange(11, 11); i.dispatchEvent(new Event('input')); })()");
            await Task.Delay(1200);
            Check("mention popup suggests objects", (await Js("document.getElementById('popup').innerText")).Contains("EMP_API"));
            await Js("(() => { const p = document.getElementById('popup'); p.querySelectorAll('.item')[1].dispatchEvent(new MouseEvent('mousedown')); })()");
            Check("choosing a mention inserts it", await Js("document.getElementById('input').value") == "look at @EMP_API ");
            await Js("document.getElementById('input').value = ''");

            // @-mention attachment
            await SendAndWait("@EMP_API Answer briefly: what does it do?");
            Check("mention chip shown", (await Js("document.querySelector('.chip') ? document.querySelector('.chip').innerText : ''")).Contains("EMP_API"));

            // production guard badge
            var before = await Js("document.getElementById('guard').className");
            await Js("document.getElementById('guard').click()");
            await Task.Delay(600);
            var locked = await Js("document.getElementById('guard').className");
            await Js("document.getElementById('guard').click()");
            await Task.Delay(600);
            Check($"guard badge: {before} -> {locked} -> {await Js("document.getElementById('guard').className")}",
                before.Contains("Normal") && locked.Contains("Locked") && (await Js("document.getElementById('guard').className")).Contains("Normal"));

            // compaction
            await SendAndWait("/compact");
            Check("/compact summarizes", (await Js("document.getElementById('messages').innerText")).Contains("Conversation compacted"));

            await Js("document.querySelectorAll('details.tool')[1].open = true; document.querySelectorAll('.code-bar').forEach(b => b.style.opacity = 1); document.getElementById('messages').scrollTop = 0;");
            await Task.Delay(600);
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "features-top.png"));
            await Js("const m = document.getElementById('messages'); m.scrollTop = m.scrollHeight;");
            await Task.Delay(600);
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "features.png"));

            // saved chats: new chat, history list, reopen
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(800);
            await Js("document.getElementById('btnHistory').click()");
            await Task.Delay(800);
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "history.png"));
            Check("history lists the chat", (await Js("document.getElementById('chatList').innerText")).Contains("Please improve the query"));
            await Js("document.querySelector('.chatitem').click()");
            await Task.Delay(1200);
            var replayed = await Js("document.getElementById('messages').innerText");
            Check("reopened chat is replayed", replayed.Contains("Please improve the query") && replayed.Contains("Conversation compacted"));
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "replayed.png"));
        }

        /// <summary>German UI: texts in the chat, the settings and the agent's notices; answer-language hint for the model.</summary>
        private async Task GermanChecks()
        {
            var text = await Js("document.getElementById('messages').innerText");
            Check("approval verdict in German (Zugelassen)", text.Contains("Zugelassen"));
            Check("revert button in German", (await Js("document.querySelector('.actions [data-undo]').innerText")).Contains("Rückgängig"));
            Check("user header in German (Sie)", text.Contains("Sie"));
            Check("tool pane labels in German", (await Js("document.querySelector('details.tool .tbody .label').textContent")).ToLowerInvariant().Contains("eingabe"));
            Check("input placeholder in German", (await Js("document.getElementById('input').placeholder")).Contains("PL/Pilot fragen"));
            Check("model gets the German answer hint", MockLlmServer.LastUserContent.Contains("User interface language: German"));
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "de-chat.png"));

            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(500);
            Check("welcome screen in German", (await Js("document.getElementById('messages').innerText")).Contains("Jede Änderung braucht Ihre Zustimmung"));
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "de-welcome.png"));

            await SendAndWait("LONGTASK zwei Dinge erledigen");
            text = await Js("document.getElementById('messages').innerText");
            Check("auto-continue notice in German", text.Contains("automatisch fortgesetzt"));
            Check("task list in German", (await Js("document.getElementById('tasks').innerText")).Contains("Alles erledigt"));
            Check("retry button in German", (await Js("document.querySelector('.endbar').innerText")).Contains("Wiederholen"));
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "de-longtask.png"));

            await Js("document.getElementById('input').value = '/help'; submit();");
            await Task.Delay(800);
            Check("/help in German", (await Js("document.querySelector('.msg.info').innerText")).Contains("Code-Review"));

            await Js("document.getElementById('btnSettings').click()");
            await Task.Delay(800);
            var settings = await Js("document.getElementById('settings').innerText");
            Check("settings in German", settings.Contains("Anweisungen & Regeln") && settings.Contains("Firmenregeln") && settings.Contains("Geschützte Verbindungen") && settings.Contains("Speichern"));
            Check("settings language selector shows Deutsch", await Js("document.getElementById('Language').value") == "de");
            Check("prompt state in German", await Js("document.getElementById('promptState').textContent") == "Standard");
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "de-settings.png"));

            // rules editor with the German template
            var rulesFile = Path.Combine(Path.GetTempPath(), "plsqldevagent-harness-data", "firmenregeln-test.md");
            if (File.Exists(rulesFile)) File.Delete(rulesFile);
            await Js("document.getElementById('CompanyRulesPath').value = " + JsonConvert.SerializeObject(rulesFile) + "; document.getElementById('btnEditRules').click();");
            await Task.Delay(1000);
            Check("German rules template", (await Js("document.getElementById('edText').value")).Contains("## Namenskonventionen"));
            Check("editor in German", (await Js("document.getElementById('edTitle').textContent")) == "Firmenregeln"
                && (await Js("document.getElementById('edTemplate').textContent")) == "Vorlage einfügen" && (await Js("document.getElementById('edSave').textContent")) == "Speichern");
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "de-rules.png"));
            await Js("document.getElementById('edCancel').click(); document.getElementById('CompanyRulesPath').value = '';");
            await Js("document.getElementById('btnEditPrompt').click()");
            await Task.Delay(300);
            Check("prompt editor opens in German", (await Js("document.getElementById('edTitle').textContent")) == "System-Prompt"
                && (await Js("document.getElementById('edSave').textContent")) == "Übernehmen");
            await Js("document.getElementById('edCancel').click()");
            await Js("document.getElementById('lnkAbout').click()");
            await Task.Delay(300);
            Check("about card in German", (await Js("document.querySelector('#about .about-card').innerText")).Contains("Erstellt von Luca Hascher"));
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "de-about.png"));
            await Js("document.getElementById('btnCloseAbout').click()");

            // switch to English without reopening the window
            await Js("document.getElementById('Language').value = 'en'; document.getElementById('btnSaveSettings').click();");
            await Task.Delay(1000);
            Check("switching to English applies immediately", (await Js("document.getElementById('input').placeholder")).Contains("Ask PL/Pilot")
                && (await Js("document.getElementById('btnSettings').title")) == "Settings");
        }

        private async Task PromptChecks()
        {
            var rulesFile = Path.Combine(Path.GetTempPath(), "plsqldevagent-harness-data", "company-rules.md");
            if (File.Exists(rulesFile)) File.Delete(rulesFile);
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(500);

            await Js("document.getElementById('btnSettings').click()");
            await Task.Delay(800);
            Check("system prompt shows as Default", await Js("document.getElementById('promptState').textContent") == "Default");

            // edit the base prompt
            await Js("document.getElementById('btnEditPrompt').click()");
            await Task.Delay(400);
            Check("prompt editor opens with the built-in prompt", (await Js("document.getElementById('edText').value")).StartsWith("You are PL/Pilot"));
            await Js("(() => { const t = document.getElementById('edText'); t.value = 'You are TESTBOT, a strict PL/SQL reviewer.\\n\\n' + t.value; document.getElementById('edSave').click(); })()");
            await Task.Delay(300);
            Check("prompt marked as customized", (await Js("document.getElementById('promptState').textContent")).StartsWith("Customized"));

            // company rules file from the template
            await Js("document.getElementById('CompanyRulesPath').value = " + JsonConvert.SerializeObject(rulesFile) + "; document.getElementById('btnEditRules').click();");
            await Task.Delay(1000);
            Check("rules editor opens with the template for a new file", (await Js("document.getElementById('edText').value")).Contains("## Naming conventions"));
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "rules-editor.png"));
            await Js("(() => { const t = document.getElementById('edText'); t.value = '# Rules\\n- RULE-XYZ: every package name starts with PKG_.'; document.getElementById('edSave').click(); })()");
            await Task.Delay(1200);
            Check("rules file saved", File.Exists(rulesFile) && File.ReadAllText(rulesFile).Contains("RULE-XYZ"));
            Check("rules info lists the file", (await Js("document.getElementById('rulesInfo').textContent")).Contains("company-rules.md"));

            // preview
            await Js("document.getElementById('btnPreviewPrompt').click()");
            await Task.Delay(1000);
            var preview = await Js("document.getElementById('edText').value");
            Check("preview contains custom prompt, tool rules and company rules",
                preview.StartsWith("You are TESTBOT") && preview.Contains("# Modes and context") && preview.Contains("# Company rules (mandatory)") && preview.Contains("RULE-XYZ"));
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "prompt-preview.png"));
            await Js("document.getElementById('edCancel').click()");
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "settings-rules.png"));

            // save settings and check what the model receives
            await Js("document.getElementById('btnSaveSettings').click()");
            await Task.Delay(800);
            await SendAndWait("Answer briefly: hello");
            var sys = MockLlmServer.LastSystemPrompt;
            Check("model receives custom prompt + company rules + tool rules",
                sys.StartsWith("You are TESTBOT") && sys.Contains("RULE-XYZ") && sys.Contains("# Longer tasks"));

            // reset to default
            await Js("document.getElementById('btnSettings').click()");
            await Task.Delay(800);
            await Js("document.getElementById('btnResetPrompt').click(); document.getElementById('CompanyRulesPath').value = ''; document.getElementById('btnSaveSettings').click();");
            await Task.Delay(800);
            await SendAndWait("Answer briefly: hello again");
            sys = MockLlmServer.LastSystemPrompt;
            Check("after reset the model gets the built-in prompt again", sys.StartsWith("You are PL/Pilot") && !sys.Contains("TESTBOT") && !sys.Contains("RULE-XYZ"));

            // about card: settings link and the logo on the welcome screen
            await Js("document.getElementById('btnSettings').click()");
            await Task.Delay(600);
            await Js("document.getElementById('lnkAbout').click()");
            await Task.Delay(400);
            var about = await Js("document.querySelector('#about .about-card').innerText");
            Check("about card via settings shows the credit and version", about.Contains("Created by Luca Hascher") && about.Contains("Version 0.1.0"));
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "about.png"));
            await Js("document.getElementById('btnCloseAbout').click(); document.getElementById('btnCancelSettings').click(); document.getElementById('btnNew').click();");
            await Task.Delay(500);
            await Js("document.querySelector('.empty .logo').click()");
            await Task.Delay(300);
            Check("about card via the welcome logo", await Js("document.getElementById('about').className") == "overlay open");
            await Js("document.getElementById('btnCloseAbout').click()");
        }

        private async Task LongTaskChecks()
        {
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            Editor.Text = SampleCode;

            // task list + auto-continue: the mock stops halfway and asks whether to continue
            await SendAndWait("LONGTASK do two things");
            var tasks = await Js("document.getElementById('tasks').innerText");
            var text = await Js("document.getElementById('messages').innerText");
            Check("task list panel shows 2/2 done", tasks.Contains("2/2") && tasks.Contains("All done"));
            Check("auto-continue kicked in after the agent stopped with open tasks", text.Contains("continuing automatically"));
            Check("agent finished after auto-continue", text.Contains("All done: Task A and Task B are finished."));
            // the task list above the input box grew during the run: the chat must still show the end
            var dist = await Js(DistanceJs);
            Check($"chat followed the output to the end although the task list grew (distance {dist} px)", int.Parse(dist) <= 8);
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "longtask.png"));

            // sub-task: helper agent with its own context
            await SendAndWait("SUBTASK where is EMP used?");
            var sub = await Js("[...document.querySelectorAll('.substeps')].map(s => s.innerText).join('|')");
            Check("helper agent steps shown under run_subtask: " + sub.Replace("\n", " "), sub.Contains("search_source"));
            Check("main agent used the helper's report", (await Js("document.getElementById('messages').innerText")).Contains("the helper found EMP"));
            dist = await Js(DistanceJs);
            Check($"chat at the end after the helper's steps (distance {dist} px)", int.Parse(dist) <= 8);

            // plan mode: read-only tools, then "Execute plan"
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            await Js("document.getElementById('btnMode').click()");
            Check("mode switch shows Plan", (await Js("document.getElementById('btnMode').innerText")).Contains("Plan"));
            await SendAndWait("PLANTEST improve the query");
            Check("plan mode offers no editing tools: " + string.Join(",", MockLlmServer.LastTools.Where(t => t.Contains("edit") || t.Contains("compile") || t.Contains("execute"))),
                !MockLlmServer.LastTools.Contains("edit_editor") && !MockLlmServer.LastTools.Contains("compile_plsql") && MockLlmServer.LastTools.Contains("get_editor_text"));
            Check("editor unchanged after planning", Editor.Text == SampleCode);
            Check("Execute plan button shown", (await Js("document.querySelector('.endbar') ? document.querySelector('.endbar').innerText : ''")).Contains("Execute plan"));
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "plan.png"));
            await Js("document.querySelector('.endbar button.primary').click()");
            await Task.Delay(300);
            for (int i = 0; i < 150 && (_chat.IsBusy || i < 3); i++) await Task.Delay(200);
            await Task.Delay(1000);
            Check("executing the plan offers editing tools again", MockLlmServer.LastTools.Contains("edit_editor"));
            Check("plan was executed in the editor", Editor.Text.Contains("select e.empno, e.ename from emp"));
            Check("mode switched back to Agent", (await Js("document.getElementById('btnMode').innerText")).Contains("Agent"));
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "plan-executed.png"));
        }

        /// <summary>Pixels between the visible part of the chat and its end (0 = scrolled to the end).</summary>
        private const string DistanceJs = "(m => Math.round(m.scrollHeight - m.scrollTop - m.clientHeight))(document.getElementById('messages'))";

        /// <summary>
        /// Follow mode, driven directly through the page's event handler: things that grow without scrolling themselves
        /// (task list above the input, helper steps, a growing input box) must not make the chat lose the end.
        /// </summary>
        private async Task FollowChecks()
        {
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            await Js("addUser('follow test', 'agent'); for (let i = 0; i < 40; i++) onHost({ type: 'notice', text: 'filler line ' + i });");
            await Task.Delay(300);
            Check($"follow test starts at the end ({await Js(DistanceJs)} px)", int.Parse(await Js(DistanceJs)) <= 8);

            await Js("onHost({ type: 'tasks', items: [1, 2, 3, 4, 5, 6].map(i => ({ content: 'Task ' + i, status: i === 1 ? 'in_progress' : 'pending' })) })");
            await Task.Delay(300);
            await Js("onHost({ type: 'notice', text: 'output after the task list appeared' })");
            await Task.Delay(300);
            var d = await Js(DistanceJs);
            Check($"a task list with 6 tasks does not make the chat lose the end ({d} px)", int.Parse(d) <= 8);

            await Js("onHost({ type: 'tool_start', callId: 'f1', name: 'run_subtask', summary: 'research', args: '{}' }); " +
                     "for (let i = 0; i < 8; i++) onHost({ type: 'subtask_step', callId: 'f1', text: 'search_source step ' + i });");
            await Task.Delay(300);
            await Js("onHost({ type: 'tool_end', callId: 'f1', ok: true, result: 'report' }); onHost({ type: 'notice', text: 'output after the helper' });");
            await Task.Delay(300);
            d = await Js(DistanceJs);
            Check($"8 helper steps do not make the chat lose the end ({d} px)", int.Parse(d) <= 8);

            await Js("(() => { const i = document.getElementById('input'); i.value = 'typing while the agent works\\n2\\n3\\n4\\n5\\n6'; i.dispatchEvent(new Event('input')); })()");
            await Task.Delay(300);
            await Js("onHost({ type: 'notice', text: 'output while the user types' })");
            await Task.Delay(300);
            d = await Js(DistanceJs);
            Check($"a growing input box does not make the chat lose the end ({d} px)", int.Parse(d) <= 8);
            await Js("(() => { const i = document.getElementById('input'); i.value = ''; i.dispatchEvent(new Event('input')); })(); onHost({ type: 'tasks', items: [] });");

            // a small scroll up (one wheel step of smooth scrolling) while output arrives must stop following
            await Js("(() => { const m = document.getElementById('messages'); m.scrollTop = m.scrollHeight; })()");
            await Task.Delay(200);
            await Js("document.getElementById('messages').scrollTop -= 30");
            await Task.Delay(200);
            await Js("onHost({ type: 'notice', text: 'output after a small scroll up' })");
            await Task.Delay(300);
            d = await Js(DistanceJs);
            Check($"a small scroll up is respected, the next output does not pull the view down ({d} px)", int.Parse(d) >= 30);
        }

        /// <summary>Rewind through the chat UI: "Undo from here" on a step, then back to before the first message with "edit".</summary>
        private async Task RewindUiChecks()
        {
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            Editor.Text = SampleCode;
            await SendAndWait("REWINDTEST one");
            var afterOne = Editor.Text;
            await SendAndWait("REWINDTEST two");
            Check("two editor changes applied", Editor.Text.Contains("select e.empno") && Editor.Text.Contains("e.sal > 0"));
            Check("both user messages have a rewind button", await Js("document.querySelectorAll('.msg.user [data-rewind]').length") == "2");
            Check("both editing steps offer 'Undo from here'", await Js("document.querySelectorAll('[data-rewind-call]').length") == "2");

            // 1. undo the second step only (conversation stays)
            await Js("[...document.querySelectorAll('[data-rewind-call]')].pop().click()");
            await Task.Delay(1000);
            var card = await Js("document.querySelector('.rewind-card') ? document.querySelector('.rewind-card').innerText : ''");
            Check("rewind card lists one editor change: " + card.Replace("\n", " | "), card.Contains("previous text") && card.Contains("Undo 1 change"));
            Check("'Undo from here' does not offer to edit a message", !card.Contains("Edit the message"));
            await Js("document.querySelector('.rewind-card button[data-a=\"undo\"]').click()");
            await Task.Delay(1500);
            Check("undoing the second step restores the text after the first message", Editor.Text == afterOne);
            Check("the rewind result is shown and the step's Revert button is gone",
                (await Js("document.querySelector('.notice.rewound') ? document.querySelector('.notice.rewound').innerText : ''")).Contains("✓") &&
                await Js("document.querySelectorAll('[data-undo]').length") == "1");
            Check("the undone step no longer offers 'Undo from here'", await Js("document.querySelectorAll('[data-rewind-call]').length") == "1");

            // 2. back to before the first message, removing the conversation from there
            await Js("document.querySelector('.msg.user [data-rewind]').click()");
            await Task.Delay(1000);
            card = await Js("document.querySelector('.rewind-card') ? document.querySelector('.rewind-card').innerText : ''");
            Check("rewind to the first message offers 'Undo and edit the message': " + card.Replace("\n", " | "), card.Contains("Undo and edit the message"));
            await Js("document.querySelector('.msg.user').classList.add('hover-demo'); document.head.insertAdjacentHTML('beforeend', '<style>.hover-demo .msg-head .rewind{visibility:visible}</style>')");
            var capture = _chat.CapturePageAsync(Path.Combine(_autoTestDir, "rewind-card.png"));
            await Task.WhenAny(capture, Task.Delay(5000));
            await Js("document.querySelector('.rewind-card button[data-a=\"edit\"]').click()");
            await Task.Delay(1500);
            Check("rewind to the first message restores the original editor text", Editor.Text == SampleCode);
            Check("the conversation from that message on is removed",
                await Js("document.querySelectorAll('.msg.user').length") == "0" && await Js("document.getElementById('input').value") == "REWINDTEST one");
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "rewind.png"));
            await Js("document.getElementById('input').value = ''");
            await SendAndWait("Answer briefly: are you there?");
            Check("the model no longer sees the removed messages", !MockLlmServer.LastMessagesJson.Contains("REWINDTEST"));
        }

        /// <summary>Several run_subtask calls run in parallel; scrolling up stops following the output, scrolling down resumes it.</summary>
        private async Task ParallelAndScrollChecks()
        {
            await FollowChecks();
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            MockLlmServer.ExpectContinueRequests = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Js("document.getElementById('input').value = 'PARALLEL research three things'; submit();");
            for (int i = 0; i < 300 && !(await Js("document.getElementById('messages').innerText")).Contains("Parallel reports"); i++) await Task.Delay(100);
            var helpers = sw.Elapsed.TotalSeconds;
            Check($"3 helper agents ran in parallel ({helpers:0.0} s; one after the other takes more than 10 s)", helpers < 7);
            Check("each helper shows its steps under its own call", await Js("document.querySelectorAll('.substeps').length") == "3");
            Check("LLM requests do not wait for '100 Continue'", MockLlmServer.ExpectContinueRequests == 0);

            // the answer is still streaming: the user scrolls up -> the view stays there
            await Js("document.getElementById('messages').scrollTop = 0");
            await Task.Delay(1000);
            var top = int.Parse(await Js("Math.round(document.getElementById('messages').scrollTop)"));
            Check("still streaming during the scroll test", _chat.IsBusy);
            Check($"view stays where the user scrolled to while output streams (scrollTop {top})", top < 20);
            // back to the end -> follows again
            await Js("const m = document.getElementById('messages'); m.scrollTop = m.scrollHeight;");
            for (int i = 0; i < 200 && _chat.IsBusy; i++) await Task.Delay(100);
            await Task.Delay(1000);
            var dist = await Js(DistanceJs);
            Check($"after scrolling back down the chat follows to the end again (distance {dist} px)", int.Parse(dist) <= 8);
            Check("helper reports are returned in call order", (await Js("document.getElementById('messages').innerText")).Contains("Parallel reports in order: A,B,C"));
            await ScreenshotAsync(_chat, Path.Combine(_autoTestDir, "parallel.png"));
        }

        private static async Task ScreenshotAsync(Form form, string path)
        {
            form.TopMost = true;
            form.Activate();
            await Task.Delay(700); // let the window come to the front and repaint (without blocking the UI thread)
            var b = form.Bounds;
            using (var bmp = new Bitmap(b.Width, b.Height))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            form.TopMost = false;
        }
    }
}
