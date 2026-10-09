namespace EchoType.Ui;

/// <summary>Edit the prompt Auto Clean sends ahead of every dictation.</summary>
internal sealed class CleanPromptForm : Form {

    private readonly TextBox _promptBox;

    public string Result { get; private set; }

    public CleanPromptForm(string prompt, string modelName) {
        Result = prompt;

        Text = "Auto Clean prompt";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(560, 420);
        MinimumSize = new Size(440, 320);
        Padding = new Padding(14);

        var title = new Label {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "Prompt sent to " + modelName,
            Margin = new Padding(0, 0, 0, 4),
        };
        var hint = new Label {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "The transcription is appended after this prompt, and the model's reply is pasted "
                + "instead of the raw transcript. Any language works.",
            Padding = new Padding(0, 6, 0, 0),
        };
        _promptBox = new TextBox {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true,
            Text = prompt.Replace("\r\n", "\n").Replace("\n", "\r\n"),
        };

        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var reset = new Button { Text = "Reset to default", AutoSize = true };
        save.Click += (_, _) => TrySave();
        reset.Click += (_, _) => {
            _promptBox.Text = Settings.DefaultAutoCleanPrompt;
            _promptBox.Select();
        };
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        buttons.Controls.Add(reset);

        Controls.Add(_promptBox);
        Controls.Add(title);
        Controls.Add(hint);
        Controls.Add(buttons);
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        _promptBox.Select();
        _promptBox.SelectionStart = _promptBox.TextLength;
    }

    private void TrySave() {
        string prompt = _promptBox.Text.Trim();
        if (prompt.Length == 0) {
            MessageBox.Show(this, "The prompt can't be empty. Use Reset to default to restore it.", "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _promptBox.Select();
            return;
        }
        Result = prompt;
        DialogResult = DialogResult.OK;
        Close();
    }
}
