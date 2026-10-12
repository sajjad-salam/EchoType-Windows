using EchoType.Translation;

namespace EchoType.Ui;

/// <summary>Enter, test or clear the OpenAI API key the Translate shortcut uses.</summary>
internal sealed class OpenAiKeyForm : Form {

    private readonly TextBox _keyBox;
    private readonly Label _status;
    private readonly Button _test;
    private readonly string? _model;

    public string Result { get; private set; }

    public OpenAiKeyForm(string apiKey, string? model) {
        Result = apiKey;
        _model = model;

        Text = "OpenAI API key (translation)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var layout = new TableLayoutPanel {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
        };
        layout.Controls.Add(new Label {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Text = "With a key, the Translate shortcut uses OpenAI (" + (string.IsNullOrWhiteSpace(model) ? OpenAiTranslation.DefaultModel : model)
                + "), which also translates the English terms inside Arabic speech (and the other way round). "
                + "If the OpenAI call fails, Google Translate is used instead. Leave empty to use Google Translate only.",
            Margin = new Padding(0, 0, 0, 8),
        });
        _keyBox = new TextBox {
            Width = 520,
            UseSystemPasswordChar = true,
            Text = apiKey,
            PlaceholderText = "sk-…",
        };
        layout.Controls.Add(_keyBox);
        var show = new CheckBox { Text = "Show key", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        show.CheckedChanged += (_, _) => _keyBox.UseSystemPasswordChar = !show.Checked;
        layout.Controls.Add(show);
        _status = new Label {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 6, 0, 0),
        };
        layout.Controls.Add(_status);

        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        _test = new Button { Text = "Test", AutoSize = true };
        var clear = new Button { Text = "Clear", AutoSize = true };
        save.Click += (_, _) => {
            Result = _keyBox.Text.Trim();
            DialogResult = DialogResult.OK;
            Close();
        };
        clear.Click += (_, _) => {
            _keyBox.Text = "";
            _status.Text = "";
            _keyBox.Select();
        };
        _test.Click += async (_, _) => await TestAsync();
        AcceptButton = save;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        buttons.Controls.Add(_test);
        buttons.Controls.Add(clear);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
    }

    private async Task TestAsync() {
        string key = _keyBox.Text.Trim();
        if (key.Length == 0) {
            _status.ForeColor = SystemColors.GrayText;
            _status.Text = "Enter a key first.";
            return;
        }
        _test.Enabled = false;
        _status.ForeColor = SystemColors.GrayText;
        _status.Text = "Testing…";
        var started = DateTime.UtcNow;
        string? error = await OpenAiTranslation.TestKeyAsync(key, _model);
        if (IsDisposed) {
            return;
        }
        _test.Enabled = true;
        if (error == null) {
            _status.ForeColor = Color.ForestGreen;
            _status.Text = "The key works (" + (int)(DateTime.UtcNow - started).TotalMilliseconds + " ms).";
        } else {
            _status.ForeColor = Color.Firebrick;
            _status.Text = "The key did not work: " + error;
        }
    }
}
