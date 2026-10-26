using EchoType.Native;

namespace EchoType.Ui;

/// <summary>
/// Small popup for adding one word replacement without opening the full list:
/// opened by the Add word replacement shortcut (pre-filled with the selected word)
/// or the tray menu. Enter saves, Esc cancels.
/// </summary>
internal sealed class QuickWordReplacementForm : Form {

    private readonly TextBox _from;
    private readonly TextBox _to;
    private readonly Label _status;
    private readonly IReadOnlyList<WordReplacement> _rules;

    public string From => _from.Text.Trim();
    public string To => _to.Text.Trim();

    /// <summary>True when the user clicked "All replacements…" instead of saving.</summary>
    public bool OpenFullList { get; private set; }

    public QuickWordReplacementForm(IReadOnlyList<WordReplacement> rules, string? from) {
        _rules = rules;

        Text = Loc.T("Add word replacement");
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
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));

        _from = new TextBox { Dock = DockStyle.Fill, Text = from ?? "" };
        _to = new TextBox { Dock = DockStyle.Fill };
        _from.TextChanged += (_, _) => UpdateStatus();
        _to.TextChanged += (_, _) => UpdateStatus();
        _status = new Label {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 6, 3, 0),
            MaximumSize = new Size(420, 0),
        };

        layout.Controls.Add(FieldLabel(Loc.T("Model writes")), 0, 0);
        layout.Controls.Add(_from, 1, 0);
        layout.Controls.Add(FieldLabel(Loc.T("Replace with")), 0, 1);
        layout.Controls.Add(_to, 1, 1);
        layout.Controls.Add(_status, 0, 2);
        layout.SetColumnSpan(_status, 2);

        var add = new Button { Text = Loc.T("Add"), AutoSize = true };
        var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        var all = new LinkLabel { Text = Loc.T("All replacements…"), AutoSize = true, Margin = new Padding(3, 8, 24, 3) };
        add.Click += (_, _) => Save();
        all.LinkClicked += (_, _) => {
            OpenFullList = true;
            DialogResult = DialogResult.Cancel;
            Close();
        };
        AcceptButton = add;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(add);
        buttons.Controls.Add(all);

        Controls.Add(layout);
        Controls.Add(buttons);
        Loc.ApplyTo(this);
        UpdateStatus();
    }

    private static Label FieldLabel(string text) => new() {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(3, 6, 10, 3),
    };

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        BringToForeground();
        // A pre-filled word only needs its replacement typed.
        var box = _from.Text.Length > 0 ? _to : _from;
        box.Select();
        box.SelectAll();
    }

    /// <summary>
    /// Opened from a global shortcut while another app is in front, so Windows would
    /// otherwise leave the keyboard with that app. Joining its input queue lets us take focus.
    /// </summary>
    private void BringToForeground() {
        uint ourThread = NativeMethods.GetCurrentThreadId();
        uint fgThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        bool attached = fgThread != 0 && fgThread != ourThread
            && NativeMethods.AttachThreadInput(ourThread, fgThread, true);
        try {
            Activate();
            NativeMethods.SetForegroundWindow(Handle);
        } finally {
            if (attached) {
                NativeMethods.AttachThreadInput(ourThread, fgThread, false);
            }
        }
    }

    private void UpdateStatus() {
        var existing = WordReplacement.Find(_rules, From);
        _status.Text = existing != null
            ? Loc.F("Already replaced with \"{0}\" — saving updates it.", existing.To)
            : Loc.T("Whole words only. Applied to every transcript before it is pasted.");
    }

    private void Save() {
        if (From.Length == 0) {
            _from.Select();
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
