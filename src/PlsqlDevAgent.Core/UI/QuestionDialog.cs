using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using PlsqlDevAgent.Agent;
using PlsqlDevAgent.Ide;

namespace PlsqlDevAgent.UI
{
    /// <summary>
    /// A question with buttons named after what they do ("Commit", "Roll back" …), used instead of MessageBox.
    /// <para>
    /// PL/SQL Developer's dark theme restyles the standard Windows dialogs (#32770) of the whole process: in a
    /// MessageBox the text was painted light on the dialog's white background and could not be read, so "Yes" and
    /// "No" were the only thing left to see. This WinForms dialog paints itself in PL/Pilot's light or dark colors.
    /// </para>
    /// </summary>
    internal sealed class QuestionDialog : Form
    {
        public sealed class Choice
        {
            public string Id, Label;
            public Choice(string id, string label) { Id = id; Label = label; }
        }

        /// <summary>Test hook: called when a dialog is shown (the self test answers it with <see cref="Choose"/>).</summary>
        internal static Action<QuestionDialog> ShownForTest;

        private readonly Label _text;
        private string _result;

        public string Question => _text.Text;

        /// <summary>
        /// Shows the question modally and returns the id of the chosen button; <paramref name="cancel"/> when the
        /// dialog is closed with Esc or the close box. No button is the default, so Enter answers nothing by accident.
        /// </summary>
        public static string Ask(IWin32Window owner, bool dark, string title, string text, Icon icon, string cancel, params Choice[] choices)
        {
            using (var d = new QuestionDialog(dark, title, text, icon, cancel, choices))
            {
                d.StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
                d.ShowDialog(owner);
                return d._result ?? cancel;
            }
        }

        /// <summary>The theme of the chat window: the Theme setting, otherwise PL/SQL Developer's.</summary>
        public static bool Dark(IIde ide)
        {
            try
            {
                switch ((AgentConfig.Load().Theme ?? "auto").Trim().ToLowerInvariant())
                {
                    case "dark": return true;
                    case "light": return false;
                }
                return ide.DarkMode;
            }
            catch { return false; }
        }

        private QuestionDialog(bool dark, string title, string text, Icon icon, string cancel, Choice[] choices)
        {
            Color back = dark ? Color.FromArgb(0x25, 0x26, 0x29) : SystemColors.Window;
            Color fore = dark ? Color.FromArgb(0xE6, 0xE6, 0xE6) : SystemColors.WindowText;
            Color bar = dark ? Color.FromArgb(0x1E, 0x1F, 0x22) : SystemColors.Control;
            Color buttonBack = dark ? Color.FromArgb(0x3A, 0x3B, 0x40) : SystemColors.ControlLightLight;
            Color border = dark ? Color.FromArgb(0x5A, 0x5B, 0x60) : SystemColors.ControlDark;

            Text = title;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = back;
            ForeColor = fore;

            var layout = new TableLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 2,
                BackColor = back, Padding = new Padding(16, 16, 20, 8), Dock = DockStyle.Fill,
            };
            var picture = new PictureBox
            {
                Image = (icon ?? SystemIcons.Question).ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize,
                Margin = new Padding(0, 0, 14, 0), BackColor = back,
            };
            _text = new Label
            {
                Text = text, AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = fore, BackColor = back,
                Margin = new Padding(0, 4, 0, 12), UseMnemonic = false,
            };
            layout.Controls.Add(picture, 0, 0);
            layout.Controls.Add(_text, 1, 0);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, BackColor = bar, Padding = new Padding(12, 10, 12, 10), Margin = new Padding(0),
            };
            // right to left: the last choice is the rightmost button
            foreach (var c in choices.Reverse())
            {
                var b = new Button
                {
                    Text = c.Label, AutoSize = true, MinimumSize = new Size(96, 0), Padding = new Padding(8, 2, 8, 2),
                    FlatStyle = FlatStyle.Flat, BackColor = buttonBack, ForeColor = fore, UseMnemonic = false,
                    Margin = new Padding(8, 0, 0, 0), Tag = c.Id,
                };
                b.FlatAppearance.BorderColor = border;
                b.Click += (s, e) => Choose((string)((Button)s).Tag);
                buttons.Controls.Add(b);
            }

            var outer = new TableLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, BackColor = back, Margin = new Padding(0),
            };
            outer.Controls.Add(layout, 0, 0);
            outer.Controls.Add(buttons, 0, 1);
            Controls.Add(outer);

            KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Escape) return;
                e.Handled = true;
                Choose(cancel);
            };
            Shown += (s, e) => ShownForTest?.Invoke(this);
        }

        /// <summary>Answers the dialog (buttons, Esc, the self test).</summary>
        public void Choose(string id)
        {
            _result = id;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult != DialogResult.OK) _result = null; // close box: the caller's cancel answer
            base.OnFormClosing(e);
        }
    }
}
