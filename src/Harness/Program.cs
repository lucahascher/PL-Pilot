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
            // --screenshots <dir>: the scripted demo conversation, captured for the README (mock LLM, off-screen)
            int shotIdx = Array.IndexOf(args, "--screenshots");
            HarnessForm.ScreenshotDir = shotIdx >= 0 && shotIdx + 1 < args.Length ? args[shotIdx + 1] : null;
            if (HarnessForm.ScreenshotDir != null) { mock = true; offscreen = true; }

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
                    cfg.Model = HarnessForm.ScreenshotDir != null ? "deepseek-chat" : "mock-deepseek";
                    if (HarnessForm.ScreenshotDir != null) { cfg.WindowWidth = 520; cfg.WindowHeight = 980; }
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
                Plugin.Create(_ide = new MockIde(this));
                if (ScreenshotDir != null) await Screenshots();
                else if (_autoTestDir != null) await AutoTest();
            };
        }

        private static Button Button(string text, Action onClick)
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (s, e) => onClick();
            return b;
        }

        private ChatForm _chat;

        /// <summary>Clicks "Allow" on every approval card (not on rewind cards).</summary>
        private const string AutoApproveJs =
            "window.__autoApprove = setInterval(() => { const b = document.querySelector('.approval:not(.done):not(.rewind-card) button.primary'); if (b) b.click(); }, 700);";
        private MockIde _ide;
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
                await chat.ExecuteScriptAsync(AutoApproveJs);
                await SendAndWait(_prompt);
                log.WriteLine("busy now: " + chat.IsBusy);
                if (_prompt.StartsWith("Please improve"))
                {
                    if (PlsqlDevAgent.Util.L.German) await GermanChecks();
                    else { await FeatureChecks(); await LongTaskChecks(); await ParallelAndScrollChecks(); await RewindUiChecks(); await TransactionUiChecks(); await SchemaChecks(); await SpeedChecks(); await SecurityUiChecks(); await PromptChecks(); await ContextWindowChecks(); await LoopChecks(); }
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

        /// <summary>
        /// Context window detection: the mock server reports max_model_len per model like vLLM (mock-deepseek 65,536,
        /// mock-other 32,768).
        /// </summary>
        private async Task ContextWindowChecks()
        {
            // the notice is gone by now (later checks reopen chats): the saved setting shows it
            Check("after the first answer the context window is taken from the server and saved (65,536)", AgentConfig.Load().ContextWindowTokens == 65536);
            Plugin.Instance.OnMenuClick(Bootstrap.MiSettings);
            await Task.Delay(800);
            await Js("document.getElementById('Model').value = 'mock-other'; document.getElementById('Model').dispatchEvent(new Event('change'))");
            await Task.Delay(2000);
            var field = await Js("document.getElementById('ContextWindowTokens').value + ' | ' + document.getElementById('ctxDetect').innerText");
            Check($"changing the model in the settings fills in its context window ({field})", field.StartsWith("32768") && field.Contains("32,768"));
            await Js("document.getElementById('DetectContextWindow').checked = false; document.getElementById('ContextWindowTokens').value = 100000; " +
                     "document.getElementById('Model').value = 'mock-deepseek'; document.getElementById('Model').dispatchEvent(new Event('change'))");
            await Task.Delay(2000);
            field = await Js("document.getElementById('ContextWindowTokens').value + ' | ' + document.getElementById('ctxDetect').innerText");
            Check($"with detection switched off the value is only shown, not changed ({field})", field.StartsWith("100000") && field.Contains("65,536"));
            await Js("closeSettings()"); // not saved
        }

        /// <summary>A model repeating itself is stopped after a few repetitions; leaked template tokens are not shown.</summary>
        private async Task LoopChecks()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await SendAndWait("LOOPTEST");
            var text = await Js("document.getElementById('messages').innerText");
            int start = text.LastIndexOf("LOOPTEST", StringComparison.Ordinal);
            var segment = start >= 0 ? text.Substring(start) : text;
            int repeats = System.Text.RegularExpressions.Regex.Matches(segment, "I will now recreate the packages").Count;
            Check($"a repeating model is stopped early ({repeats} repetitions shown, {sw.Elapsed.TotalSeconds:0} s)", repeats >= 1 && repeats <= 14);
            Check("the user is told why it stopped", segment.Contains("kept repeating"));
            Check("leaked template tokens are not shown", !segment.Contains("<channel|>") && segment.Contains("Let me check the packages."));
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
            // setting "Suggest Objects After @" off: typing @ causes no database queries
            _chat.ApplyConfigForTest(c => c.MentionSuggestions = false);
            int LikeQueries() { lock (_ide.ExecutedSql) return _ide.ExecutedSql.Count(s => s.Contains("object_name like")); }
            int likeBefore = LikeQueries();
            await Js("(() => { const i = document.getElementById('input'); i.focus(); i.value = 'look at @EM'; i.setSelectionRange(11, 11); i.dispatchEvent(new Event('input')); })()");
            await Task.Delay(1200);
            Check($"mention suggestions off: no database query while typing ({LikeQueries() - likeBefore} queries)",
                LikeQueries() == likeBefore && !(await Js("document.getElementById('popup').innerText")).Contains("EMP_API"));
            await Js("document.getElementById('input').value = ''; closePopup();");
            _chat.ApplyConfigForTest(c => c.MentionSuggestions = true);

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

        /// <summary>Set by --screenshots: folder for the README images.</summary>
        public static string ScreenshotDir;

        /// <summary>Captures the README images: a whole conversation (light and dark) and a pending approval card.</summary>
        private async Task Screenshots()
        {
            try
            {
                Directory.CreateDirectory(ScreenshotDir);
                Plugin.Instance.OnMenuClick(Bootstrap.MiChat);
                _chat = Plugin.Instance.ChatWindow;
                for (int i = 0; i < 150 && !_chat.IsChatReady; i++) await Task.Delay(200);
                await Js(AutoApproveJs);
                await SendAndWait("list_emps is slow, please fix it and compile the package.");
                await Js("document.getElementById('messages').scrollTop = 0");
                await Task.Delay(400);
                await _chat.CapturePageAsync(Path.Combine(ScreenshotDir, "chat.png"));
                await Js("onHost({ type: 'theme', dark: true }); const m = document.getElementById('messages'); m.scrollTop = m.scrollHeight;");
                await Task.Delay(600);
                await _chat.CapturePageAsync(Path.Combine(ScreenshotDir, "chat-dark.png"));
                await Js("onHost({ type: 'theme', dark: false })");

                // a shorter window for the approval: the card, then the result with the uncommitted-changes notice
                _chat.Height = _chat.Height * 560 / 980;
                await Js("clearInterval(window.__autoApprove); document.getElementById('btnNew').click()");
                await Task.Delay(600);
                await Submit("Raise the salaries of department 10 by 10 percent.");
                await WaitForCard();
                await Task.Delay(900);
                await _chat.CapturePageAsync(Path.Combine(ScreenshotDir, "approval.png"));
                await Js("document.querySelector('.approval:not(.done) button[data-a=\"yes\"]').click()");
                await WaitIdle();
                await _chat.CapturePageAsync(Path.Combine(ScreenshotDir, "uncommitted.png"));
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(ScreenshotDir, "error.txt"), ex.ToString());
            }
            Application.Exit();
        }

        private async Task<bool> WaitForCard(int ms = 8000)
        {
            for (int i = 0; i < ms / 50; i++)
            {
                if (await Js("document.querySelector('.approval:not(.done):not(.rewind-card)') ? 'y' : 'n'") == "y") return true;
                await Task.Delay(50);
            }
            return false;
        }

        private async Task WaitIdle()
        {
            for (int i = 0; i < 200 && (_chat.IsBusy || i < 3); i++) await Task.Delay(100);
            await Task.Delay(800);
        }

        private async Task Submit(string text) =>
            await Js("(() => { const i = document.getElementById('input'); i.value = " + JsonConvert.SerializeObject(text) + "; submit(); })()");

        /// <summary>Security fixes: CSP, approval focus/arming, statements that always ask, local data settings.</summary>
        private async Task SecurityUiChecks()
        {
            // 1. Content-Security-Policy: injected HTML or script elements cannot run script
            await Js("document.getElementById('messages').insertAdjacentHTML('beforeend', '<img src=x onerror=\"window.__xss1 = 1\">'); " +
                     "const s = document.createElement('script'); s.textContent = 'window.__xss2 = 1'; document.body.appendChild(s);");
            await Task.Delay(600);
            Check("CSP: injected HTML and script elements cannot run script",
                await Js("window.__xss1 === undefined && window.__xss2 === undefined ? 'blocked' : 'executed'") == "blocked");

            // 2. approval cards: no focus change while typing, no decision in the first moment, Enter in empty feedback never approves
            await Js("clearInterval(window.__autoApprove)");
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            await Js("(() => { const i = document.getElementById('input'); i.value = 'EXECSQL: update emp set sal = sal where deptno = 10'; i.focus(); submit(); i.value = 'I am typing'; i.focus(); })()");
            Check("approval card shown", await WaitForCard());
            Check("the approval card does not take the focus from the input box", await Js("document.activeElement.id") == "input");
            await Js("document.querySelector('.approval:not(.done) button[data-a=\"yes\"]').click()");
            Check("a click in the first moment does not decide the card", await Js("document.querySelectorAll('.approval:not(.done)').length") == "1");
            await Task.Delay(900);
            await Js("document.querySelector('.approval:not(.done) input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))");
            await Task.Delay(300);
            Check("Enter in the empty feedback field does not approve", await Js("document.querySelectorAll('.approval:not(.done)').length") == "1");
            await Js("document.querySelector('.approval:not(.done) button[data-a=\"yes\"]').click()");
            await WaitIdle();
            Check("after the short delay Allow works", _ide.ExecutedSql.Any(s => s.StartsWith("update emp set sal = sal where deptno = 10")));
            await Js("document.getElementById('input').value = ''");

            // 3. with "Auto approve SQL": sensitive statements still ask (without "Always Allow"), normal ones run
            _chat.ApplyConfigForTest(c => c.AutoApproveSql = true);
            foreach (var (sql, expect) in new[]
            {
                ("grant dba to scott", "privileges"),
                ("begin execute immediate 'truncate table emp'; end;", "dynamic SQL"),
                ("with function f return number is pragma autonomous_transaction; begin delete from emp; commit; return 1; end; select f from dual", "WITH FUNCTION"),
            })
            {
                await Submit("EXECSQL: " + sql);
                bool asked = await WaitForCard();
                var card = asked ? await Js("document.querySelector('.approval:not(.done)').innerText") : "";
                Check($"always asks despite auto-approve ({expect}): {(card.Length > 0 ? card.Split('\n')[1] : "no card")}", asked && card.Contains(expect) && !card.Contains("Always Allow"));
                await Task.Delay(800);
                if (asked) await Js("document.querySelector('.approval:not(.done) button[data-a=\"no\"]').click()");
                await WaitIdle();
                Check("rejected, so not executed: " + sql.Substring(0, Math.Min(20, sql.Length)), !_ide.ExecutedSql.Any(s => s.StartsWith(sql.Substring(0, Math.Min(20, sql.Length)))));
            }
            int cards = int.Parse(await Js("document.querySelectorAll('.approval').length"));
            await Submit("EXECSQL: insert into dept values (40, 'OPERATIONS', 'BOSTON')");
            await WaitIdle();
            Check("a normal statement runs without a card when auto-approve is on",
                _ide.ExecutedSql.Any(s => s.StartsWith("insert into dept values (40")) && int.Parse(await Js("document.querySelectorAll('.approval').length")) == cards);
            _chat.ApplyConfigForTest(c => c.AutoApproveSql = false);
            await Js(AutoApproveJs);

            // 4. local data: saved chats without tool results, full content, retention, history off
            // These checks delete chats, prompts, backups and log lines: only ever in the harness's own temp data folder.
            var harnessData = Path.Combine(Path.GetTempPath(), "plsqldevagent-harness-data");
            if (!string.Equals(Path.GetFullPath(PlsqlDevAgent.Util.Log.Directory).TrimEnd('\\'), Path.GetFullPath(harnessData).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                Check("local data checks skipped: the data folder is not the harness temp folder (" + PlsqlDevAgent.Util.Log.Directory + ")", false);
                return;
            }
            string ChatJson(string id) => File.ReadAllText(Path.Combine(ChatStore.Directory, id + ".json"));
            _chat.ApplyConfigForTest(c => { c.SaveChatHistory = true; c.ChatHistoryContent = "noResults"; });
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            await SendAndWait("EXECSQL: select ename from emp");
            var saved = ChatStore.List().First();
            Check("saved chat without tool results: the query result is not on disk",
                saved.Title.Contains("EXECSQL") && !ChatJson(saved.Id).Contains("ALLEN") && ChatJson(saved.Id).Contains("not stored on this PC"));
            _chat.ApplyConfigForTest(c => c.ChatHistoryContent = "full");
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            await SendAndWait("EXECSQL: select ename from emp where 1 = 1");
            Check("saved chat with full content contains the query result", ChatJson(ChatStore.List().First().Id).Contains("ALLEN"));

            // retention: an old chat, prompt, backup and log line are removed, recent ones stay
            var old = DateTime.Now.AddDays(-40);
            ChatStore.Save(new ChatRecord { Id = "20000101-000000-old001", Title = "old chat", Created = old, Updated = old });
            PromptHistory.Delete();
            File.WriteAllText(Path.Combine(PlsqlDevAgent.Util.Log.Directory, "prompts.json"),
                JsonConvert.SerializeObject(new[] { new { Text = "old prompt", Time = old }, new { Text = "new prompt", Time = DateTime.Now } }));
            var backup = BackupStore.Save("TEST@DB", "PROCEDURE", "TEST", "OLD_PROC", "create or replace procedure old_proc is begin null; end;", "test");
            var index = Path.Combine(BackupStore.Directory, "index.jsonl");
            File.WriteAllText(index, File.ReadAllText(index).Replace(JsonConvert.SerializeObject(backup.Time).Trim('"'), JsonConvert.SerializeObject(DateTime.Now.AddDays(-100)).Trim('"')));
            var log = PlsqlDevAgent.Util.Log.FilePath;
            File.WriteAllText(log, $"{DateTime.Now.AddDays(-40):yyyy-MM-dd HH:mm:ss.fff} INFO  very old log line\r\n" + File.ReadAllText(log));
            var retention = new AgentConfig { SaveChatHistory = true, ChatRetentionDays = 30, BackupRetentionDays = 90, LogRetentionDays = 30 };
            LocalData.Cleanup(retention);
            Check("retention: the 40 days old chat is deleted, recent chats stay", ChatStore.Load("20000101-000000-old001") == null && ChatStore.List().Count >= 2);
            var prompts = PromptHistory.Texts(retention);
            Check("retention: the old prompt is deleted, the new one stays", !prompts.Contains("old prompt") && prompts.Contains("new prompt"));
            Check("retention: the 100 days old backup is deleted", BackupStore.Get(backup.Id) == null && !File.Exists(backup.File));
            Check("retention: the old log line is deleted", !File.ReadAllText(log).Contains("very old log line") && File.ReadAllText(log).Length > 0);

            // history off (settings dialog): saved chats and prompts are deleted, nothing is saved any more
            Plugin.Instance.OnMenuClick(Bootstrap.MiSettings);
            await Task.Delay(800);
            await Js("document.getElementById('SaveChatHistory').checked = false; document.getElementById('btnSaveSettings').click();");
            await Task.Delay(1500);
            Check("switching chat history off deletes the saved chats and the prompt history",
                ChatStore.List().Count == 0 && !File.Exists(Path.Combine(PlsqlDevAgent.Util.Log.Directory, "prompts.json")));
            await Js("document.getElementById('btnCloseSettings') && document.getElementById('btnCloseSettings').click()");
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            await SendAndWait("Answer briefly: is anything saved?");
            Check("with chat history off nothing is saved", ChatStore.List().Count == 0 &&
                !File.Exists(Path.Combine(PlsqlDevAgent.Util.Log.Directory, "prompts.json")));
            await Js("document.getElementById('btnHistory').click()");
            await Task.Delay(600);
            Check("the history list says that it is turned off", (await Js("document.getElementById('chatList').innerText")).Contains("turned off"));
            await Js("closeHistory()");
            // back on for the following tests
            Plugin.Instance.OnMenuClick(Bootstrap.MiSettings);
            await Task.Delay(800);
            await Js("document.getElementById('SaveChatHistory').checked = true; document.getElementById('btnSaveSettings').click();");
            await Task.Delay(1000);
            await Js("document.getElementById('btnCloseSettings') && document.getElementById('btnCloseSettings').click()");
        }

        /// <summary>Uncommitted changes of the agent session: notice above the input, Rollback and Commit buttons, note to the model.</summary>
        private async Task TransactionUiChecks()
        {
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            Check("no notice without uncommitted changes", await Js("document.getElementById('txn').className") == "txn");
            await SendAndWait("DMLTEST raise the salaries");
            var bar = await Js("document.getElementById('txn').innerText");
            Check("uncommitted changes notice with the statement count: " + bar.Replace("\n", " "), bar.Contains("Uncommitted data changes") && bar.Contains("1 statement"));
            Check("the notice lists the statement in its tooltip", (await Js("document.getElementById('txn').title")).Contains("update emp set sal"));
            Check("the update step can be rewound", await Js("document.querySelectorAll('[data-rewind-call]').length") == "1");
            var capture = _chat.CapturePageAsync(Path.Combine(_autoTestDir, "uncommitted.png"));
            await Task.WhenAny(capture, Task.Delay(5000));

            // Rollback (the page asks first)
            await Js("window.confirm = () => true; document.querySelector('#txn button[data-txn=\"rollback\"]').click()");
            await Task.Delay(1500);
            Check("Rollback ran in the agent session and the notice is gone",
                _ide.ExecutedSql.Last() == "rollback" && _ide.PendingTransaction == null && await Js("document.getElementById('txn').className") == "txn");
            Check("the chat shows the rollback", (await Js("document.getElementById('messages').innerText")).Contains("Rolled back the changes of the agent."));
            await Js("document.querySelector('[data-rewind-call]').click()");
            await Task.Delay(1000);
            Check("after the rollback the update needs no rewinding", (await Js("document.querySelector('.rewind-card').innerText")).Contains("no changes after this point"));
            await Js("document.querySelector('.rewind-card button[data-a=\"cancel\"]').click()");

            // Commit
            await SendAndWait("DMLTEST once more");
            await Js("document.querySelector('#txn button[data-txn=\"commit\"]').click()");
            await Task.Delay(1500);
            Check("Commit ran in the agent session and the notice is gone",
                _ide.ExecutedSql.Last() == "commit" && await Js("document.getElementById('txn').className") == "txn");
            await SendAndWait("Answer briefly: done?");
            Check("the model was told about the rollback and the commit",
                MockLlmServer.LastMessagesJson.Contains("ROLLED BACK") && MockLlmServer.LastMessagesJson.Contains("COMMITTED"));
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

        /// <summary>
        /// The agent works in the user's schema: its own switch (execute_sql) is seen at once instead of a remembered
        /// schema, and the session is switched back at the end of the request, also after Stop. With a session shared
        /// with the user's windows, the agent starts in the schema they switched to and leaves it as it is.
        /// </summary>
        private async Task SchemaChecks()
        {
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            _ide.CurrentSchema = "SCOTT";
            const string switchBack = "alter session set current_schema = \"SCOTT\"";
            int SwitchBacks() { lock (_ide.ExecutedSql) return _ide.ExecutedSql.Count(s => s == switchBack); }

            // the agent switches the schema in one request, the next request starts in the user's schema again
            await SendAndWait("EXECSQL: alter session set current_schema = HR");
            Check("execute_sql switched the session to HR", _ide.ExecutedSql.Contains("alter session set current_schema = HR"));
            Check("at the end of the request the session is switched back to the user's schema SCOTT", _ide.CurrentSchema == "SCOTT" && SwitchBacks() == 1);
            Check("the chat says that the session was switched back",
                (await Js("document.getElementById('messages').innerText")).Contains("Switched the session back from schema HR to SCOTT"));
            await SendAndWait("TOOL: get_ide_context {}");
            Check("get_ide_context reports the user's schema SCOTT again", MockLlmServer.LastMessagesJson.Contains("(current schema SCOTT)"));
            Check("the model is told that the session was switched back", MockLlmServer.LastUserContent.Contains("switched the session back from schema HR to the user's schema SCOTT"));

            // within one request the switch is seen at once (the remembered schema is read again)
            await SendAndWait("TOOL: execute_sql {\"sql\":\"alter session set current_schema = HR\"}\nTOOL: get_ide_context {}");
            Check("get_ide_context right after the switch reports HR, not a remembered SCOTT", MockLlmServer.LastMessagesJson.Contains("(current schema HR)"));
            Check("switched back to SCOTT at the end of that request", _ide.CurrentSchema == "SCOTT" && SwitchBacks() == 2);

            // Stop after the switch: the session is switched back as well
            await Submit("SLOWAFTERTOOL\nTOOL: execute_sql {\"sql\":\"alter session set current_schema = HR\"}");
            for (int i = 0; i < 100 && _ide.CurrentSchema != "HR"; i++) await Task.Delay(100);
            await Task.Delay(500);
            Check("still working after the switch (slow model answer)", _ide.CurrentSchema == "HR" && _chat.IsBusy);
            _chat.StopForTest();
            for (int i = 0; i < 50 && (_chat.IsBusy || _ide.CurrentSchema != "SCOTT"); i++) await Task.Delay(100);
            Check("after Stop the session is switched back to SCOTT as well", _ide.CurrentSchema == "SCOTT" && SwitchBacks() == 3);
            await WaitIdle();

            // a session shared with the user's windows: the agent starts in the schema the user switched to there
            _ide.UsesOwnSession = false;
            try
            {
                await SendAndWait("TOOL: get_ide_context {}"); // the agent now remembers SCOTT
                Check("shared session: SCOTT before the user switches", MockLlmServer.LastMessagesJson.Contains("(current schema SCOTT)"));
                _ide.CurrentSchema = "HR"; // the user ran ALTER SESSION SET CURRENT_SCHEMA = HR in a SQL window
                await SendAndWait("TOOL: get_ide_context {}");
                Check("shared session: the request starts in the schema of the user's windows (HR)",
                    MockLlmServer.LastUserContent.Contains("current schema HR") && MockLlmServer.LastMessagesJson.Contains("(current schema HR)"));
                Check("shared session: the user's schema is left as the user set it", _ide.CurrentSchema == "HR" && SwitchBacks() == 3);
            }
            finally
            {
                _ide.UsesOwnSession = true;
                _ide.CurrentSchema = "SCOTT";
            }
        }

        /// <summary>Content of the last tool result the model received.</summary>
        private static string LastToolResult()
        {
            var messages = Newtonsoft.Json.Linq.JArray.Parse(MockLlmServer.LastMessagesJson);
            return (string)messages.LastOrDefault(m => (string)m["role"] == "tool")?["content"] ?? "";
        }

        private System.Collections.Generic.List<string> ExecutedSince(int index)
        {
            lock (_ide.ExecutedSql) return _ide.ExecutedSql.Skip(index).ToList();
        }

        private int ExecutedCount() { lock (_ide.ExecutedSql) return _ide.ExecutedSql.Count; }

        /// <summary>
        /// Speed: the prompt cache entry in the log, shortening old tool outputs in one bigger step, source reads in
        /// joined rows and by line range, the slow query estimate, the row limit in the query, the runtime note and
        /// parallel helpers sharing one identical query.
        /// </summary>
        private async Task SpeedChecks()
        {
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            var editorBefore = Editor.Text;
            Editor.Text = SampleCode;
            try
            {
                // 1. the log says how much of the prompt the server took from its prompt cache
                string log;
                using (var f = new FileStream(PlsqlDevAgent.Util.Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(f)) log = reader.ReadToEnd();
                Check("the log shows the server's prompt cache hits", System.Text.RegularExpressions.Regex.IsMatch(log, @"prompt cache 8000/\d+ tokens \(\d+ %\)"));

                // 2. over the budget old tool outputs are shortened in one bigger step, so the beginning of the
                //    conversation (what the server's prompt cache can reuse) stays unchanged for several steps
                Editor.Text = SampleCode + string.Concat(Enumerable.Range(1, 40).Select(i => $"-- comment line {i} that makes the editor text longer\r\n"));
                // (the budget must be large against the last 8 messages, which are never shortened: 300,000 by default)
                _chat.ApplyConfigForTest(c => c.ContextBudgetChars = 30000);
                int start;
                lock (MockLlmServer.MainRequests) start = MockLlmServer.MainRequests.Count;
                await SendAndWait(string.Join("\n", Enumerable.Repeat("TOOL: get_editor_text {}", 20)));
                System.Collections.Generic.List<Newtonsoft.Json.Linq.JArray> requests;
                lock (MockLlmServer.MainRequests) requests = MockLlmServer.MainRequests.Skip(start).ToList();
                int changed = 0;
                for (int i = 1; i < requests.Count; i++)
                    if (!requests[i - 1].Zip(requests[i], (a, b) => Newtonsoft.Json.Linq.JToken.DeepEquals(a, b)).All(x => x)) changed++;
                bool shortened = requests.Last().ToString().Contains("older tool output removed");
                // shortening just below the budget changed one on every step from the 12th on (9 of 20)
                Check($"over the budget earlier messages change only every few steps ({changed} of {requests.Count - 1} steps changed one)",
                    shortened && changed > 0 && changed <= 4);
                _chat.ApplyConfigForTest(c => c.ContextBudgetChars = new AgentConfig().ContextBudgetChars);
                Editor.Text = SampleCode;
                await Js("document.getElementById('btnNew').click()");
                await Task.Delay(600);

                // 3. get_object_source: lines joined into rows, line ranges, cut at the output limit with a note where to continue
                int q = ExecutedCount();
                await SendAndWait("TOOL: get_object_source {\"name\":\"EMP_API\",\"object_type\":\"PACKAGE BODY\",\"start_line\":2,\"end_line\":4,\"with_line_numbers\":true}");
                var src = LastToolResult();
                Check("get_object_source with start_line 2 / end_line 4 returns those lines, numbered from 2",
                    src.Contains("lines 2-4 of 8") && src.Contains("2:   procedure list_emps") && src.Contains("4:     for r in") && !src.Contains("5:"));
                Check("the source is read as joined rows (LISTAGG)", ExecutedSince(q).Any(s => s.Contains("listagg(text)")));
                _ide.FailJoinedSource = true;
                q = ExecutedCount();
                await SendAndWait("TOOL: get_object_source {\"name\":\"EMP_API\",\"object_type\":\"PACKAGE BODY\",\"start_line\":2,\"end_line\":4,\"with_line_numbers\":true}");
                Check("after ORA-01489 (very long line) the source is read line by line, same result",
                    LastToolResult() == src && ExecutedSince(q).Any(s => s.StartsWith("select line, line last_line, total, text")));
                _ide.FailJoinedSource = false;
                // a long body with a procedure at its end, longer than the output limit
                Editor.Text = SampleCode.Replace("end emp_api;\r\n", "") +
                              string.Concat(Enumerable.Range(1, 40).Select(i => $"  -- comment line {i} of the long package body\r\n")) +
                              "  procedure close_quarter is\r\n  begin\r\n    null;\r\n  end close_quarter;\r\nend emp_api;\r\n";
                _chat.ApplyConfigForTest(c => c.MaxToolOutputChars = 2000);
                await SendAndWait("TOOL: get_object_source {\"name\":\"EMP_API\",\"object_type\":\"PACKAGE BODY\"}");
                src = LastToolResult();
                var next = System.Text.RegularExpressions.Regex.Match(src, @"output limit reached after line (\d+) of 52\. NOT shown: .*close_quarter \(line (\d+)\).*start_line (\d+)");
                Check("a cut unit says so at the top, lists the subprograms it does not show and where to continue",
                    src.Contains("too long for one answer") && next.Success && int.Parse(next.Groups[3].Value) == int.Parse(next.Groups[1].Value) + 1 && next.Groups[2].Value == "48");
                if (next.Success)
                {
                    await SendAndWait("TOOL: get_object_source {\"name\":\"EMP_API\",\"object_type\":\"PACKAGE BODY\",\"start_line\":" + next.Groups[2].Value + "}");
                    Check("reading from the listed line returns that procedure", LastToolResult().Contains("lines 48-52 of 52") &&
                        LastToolResult().Contains("procedure close_quarter") && LastToolResult().Contains("end emp_api;"));
                }
                _chat.ApplyConfigForTest(c => c.MaxToolOutputChars = new AgentConfig().MaxToolOutputChars);
                Editor.Text = SampleCode;

                // 4. a query Oracle estimates as slow asks first; declined it does not run, approved it runs with the row limit inside
                _ide.EstimateSeconds = 120;
                await Js("clearInterval(window.__autoApprove)");
                const string slowSql = "TOOL: run_query {\"sql\":\"select * from emp order by sal desc\"}";
                bool Ran(int from) => ExecutedSince(from).Any(s => s.StartsWith("select * from (") && s.Contains("order by sal desc") && s.Contains("rownum <= "));
                q = ExecutedCount();
                await Submit(slowSql);
                bool asked = await WaitForCard();
                var card = asked ? await Js("document.querySelector('.approval:not(.done)').innerText") : "";
                Check("a query Oracle estimates at 2 minutes asks first", asked && card.Contains("Oracle estimates about 2 min"));
                await Task.Delay(800);
                if (asked) await Js("document.querySelector('.approval:not(.done) button[data-a=\"no\"]').click()");
                await WaitIdle();
                Check("declined: the query did not run and the model got the estimate",
                    !Ran(q) && LastToolResult().StartsWith("Not executed: Oracle estimates about 2 min"));
                q = ExecutedCount();
                await Submit(slowSql);
                asked = await WaitForCard();
                await Task.Delay(800);
                if (asked) await Js("document.querySelector('.approval:not(.done) button[data-a=\"yes\"]').click()");
                await WaitIdle();
                Check("approved: the query ran with the row limit inside (rownum)", asked && Ran(q));
                await Js("clearInterval(window.__autoApprove)");
                await Js(AutoApproveJs);
                _ide.EstimateSeconds = 1;
                int cards = int.Parse(await Js("document.querySelectorAll('.approval').length"));
                q = ExecutedCount();
                await SendAndWait(slowSql);
                Check("a query estimated below the limit runs without asking",
                    Ran(q) && int.Parse(await Js("document.querySelectorAll('.approval').length")) == cards);
                _chat.ApplyConfigForTest(c => c.ConfirmSlowQuerySeconds = 0);
                q = ExecutedCount();
                await SendAndWait(slowSql);
                Check("setting 0: no estimate is read", Ran(q) && !ExecutedSince(q).Any(s => s.Contains("PDA_ESTIMATE")));
                _chat.ApplyConfigForTest(c => c.ConfirmSlowQuerySeconds = new AgentConfig().ConfirmSlowQuerySeconds);

                // 5. the model is told how long a query took (a second or more)
                await SendAndWait("TOOL: run_query {\"sql\":\"select * from pda_slow_table\"}");
                Check("the result says how long the query took: " + LastToolResult().Split('\n').Last(), System.Text.RegularExpressions.Regex.IsMatch(LastToolResult(), @"\(The query took 1\.\d s\.\)"));

                // 6. three parallel helpers run exactly the same slow query: it runs once, all get the result
                int Slow() { lock (_ide.ExecutedSql) return _ide.ExecutedSql.Count(s => s.StartsWith("select * from (") && s.Contains("pda_slow_objects")); }
                int before = Slow();
                await SendAndWait("SAMEQUERY research");
                Check($"3 parallel helpers with the same query: it ran once ({Slow() - before} times)", Slow() - before == 1);
                Check("the helpers that waited were told that the result is shared",
                    System.Text.RegularExpressions.Regex.Matches(MockLlmServer.LastMessagesJson, "result is shared").Count == 2);
            }
            finally
            {
                Editor.Text = editorBefore;
                _ide.FailJoinedSource = false;
                _ide.EstimateSeconds = 1;
                await Js("clearInterval(window.__autoApprove)"); // exactly one auto-approver (later checks stop it)
                await Js(AutoApproveJs);
            }
        }

        /// <summary>Several run_subtask calls run in parallel; scrolling up stops following the output, scrolling down resumes it.</summary>
        private async Task ParallelAndScrollChecks()
        {
            await FollowChecks();
            await Js("document.getElementById('btnNew').click()");
            await Task.Delay(600);
            MockLlmServer.ExpectContinueRequests = 0;
            int SchemaQueries() { lock (_ide.ExecutedSql) return _ide.ExecutedSql.Count(s => s.Contains("CURRENT_SCHEMA")); }
            int schemaQueries = SchemaQueries();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Js("document.getElementById('input').value = 'PARALLEL research three things'; submit();");
            for (int i = 0; i < 300 && !(await Js("document.getElementById('messages').innerText")).Contains("Parallel reports"); i++) await Task.Delay(100);
            var helpers = sw.Elapsed.TotalSeconds;
            Check($"3 helper agents ran in parallel ({helpers:0.0} s; one after the other takes more than 10 s)", helpers < 7);
            schemaQueries = SchemaQueries() - schemaQueries;
            Check($"the parallel helpers share the agent's schema, it is read once for the request ({schemaQueries} queries)", schemaQueries == 1);
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
