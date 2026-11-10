using EchoType.Health;
using EchoType.History;
using EchoType.Hotkey;

namespace EchoType.Ui;

/// <summary>
/// First-run setup in four short steps: Windows prerequisites (microphone, WebView2), signing
/// in to the models, choosing how to record, and a live test dictation. Opens by itself on the
/// first launch and again from tray → More → Run setup again, or Settings → Setup.
/// </summary>
internal sealed class SetupWizardForm : Form {

    private readonly IAppWindowHost _host;
    private readonly Label _stepLabel;
    private readonly Label _title;
    private readonly Label _intro;
    private readonly Panel _body;
    private readonly Button _back;
    private readonly Button _next;
    private readonly System.Windows.Forms.Timer _refresh;
    private readonly List<(string Id, Label Status, Button Fix)> _rows = [];
    private int _step;
    private TextBox? _testBox;
    private Label? _testStatus;

    private const int StepCount = 4;

    public SetupWizardForm(IAppWindowHost host) {
        _host = host;
        Text = Loc.T("Set up EchoType");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        ClientSize = new Size(680, 540);
        Padding = new Padding(24);

        _stepLabel = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = SystemColors.GrayText };
        _title = DialogTheme.Heading("");
        _title.Dock = DockStyle.Top;
        _intro = DialogTheme.SupportingText("");
        _intro.Dock = DockStyle.Top;
        _intro.MaximumSize = new Size(620, 0);
        _intro.Padding = new Padding(0, 0, 0, 14);

        _body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        _back = new Button { Text = Loc.T("Back"), AutoSize = true };
        _back.Click += (_, _) => Go(_step - 1);
        _next = new Button { Text = Loc.T("Next"), AutoSize = true };
        _next.Click += (_, _) => {
            if (_step == StepCount - 1) {
                DialogResult = DialogResult.OK;
                Close();
            } else {
                Go(_step + 1);
            }
        };
        var skip = new LinkLabel { Text = Loc.T("Skip setup"), AutoSize = true, Margin = new Padding(3, 10, 24, 3) };
        skip.LinkClicked += (_, _) => Close();
        AcceptButton = _next;

        var footer = new FlowLayoutPanel {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 14, 0, 0),
        };
        footer.Controls.Add(_next);
        footer.Controls.Add(_back);
        footer.Controls.Add(skip);

        Controls.Add(_body);
        Controls.Add(_intro);
        Controls.Add(_title);
        Controls.Add(_stepLabel);
        Controls.Add(footer);

        _refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        _refresh.Tick += (_, _) => RefreshRows();

        Loc.ApplyTo(this);
        DialogTheme.Apply(this);
        DialogTheme.Primary(_next);
        TranscriptHistory.Added += OnTranscript;
        Go(0);
        _refresh.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        TranscriptHistory.Added -= OnTranscript;
        _refresh.Stop();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _refresh.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Go(int step) {
        _step = Math.Clamp(step, 0, StepCount - 1);
        _rows.Clear();
        _testBox = null;
        _testStatus = null;
        _body.SuspendLayout();
        foreach (Control c in _body.Controls.Cast<Control>().ToList()) {
            c.Dispose();
        }
        _body.Controls.Clear();
        _stepLabel.Text = Loc.F("Step {0} of {1}", _step + 1, StepCount);
        switch (_step) {
            case 0:
                _title.Text = Loc.T("Welcome to EchoType");
                _intro.Text = Loc.T("EchoType types what you say into any app, using the dictation built into ChatGPT, Gemini or Claude — no API keys. First, two things Windows needs.");
                AddChecks(["mic", "webview"]);
                break;
            case 1:
                _title.Text = Loc.T("Sign in to a model");
                _intro.Text = Loc.T("Sign in to at least one. Each model opens in its own window and remembers your login. EchoType uses the first one that's ready; you can switch any time.");
                AddChecks(["chatgpt", "gemini", "claude"]);
                break;
            case 2:
                _title.Text = Loc.T("How do you want to record?");
                _intro.Text = Loc.F("Your dictation key is {0}. You can change it later on the Home page.",
                    HotkeyNames.For(_host.Settings.DictationChord));
                AddRecordingChoices();
                break;
            default:
                _title.Text = Loc.T("Try it");
                AddTest();
                break;
        }
        _body.ResumeLayout();
        _back.Enabled = _step > 0;
        _next.Text = _step == StepCount - 1 ? Loc.T("Finish") : Loc.T("Next");
        RefreshRows();
        DialogTheme.Restyle(this);
        DialogTheme.Primary(_next);
    }

    private void AddChecks(string[] ids) {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;
        foreach (string id in ids) {
            var title = new Label { AutoSize = true, Font = HudTheme.BodyBold, Margin = new Padding(0, 14, 8, 14) };
            var status = new Label { AutoSize = true, MaximumSize = new Size(330, 0), Margin = new Padding(0, 14, 8, 14) };
            var fix = new Button { AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
            string captured = id;
            fix.Click += (_, _) => {
                _host.FixHealth(captured);
                RefreshRows();
            };
            title.Tag = id;
            table.Controls.Add(title, 0, row);
            table.Controls.Add(status, 1, row);
            table.Controls.Add(fix, 2, row);
            _rows.Add((id, status, fix));
            row++;
        }
        _body.Controls.Add(table);
    }

    private void RefreshRows() {
        if (_rows.Count == 0 || IsDisposed) {
            return;
        }
        var health = _host.Health;
        foreach (var (id, status, fix) in _rows) {
            var item = health.FirstOrDefault(h => h.Id == id);
            if (item == null) {
                continue;
            }
            if (status.Parent is TableLayoutPanel table
                && table.GetControlFromPosition(0, table.GetRow(status)) is Label title) {
                title.Text = item.Title;
            }
            string mark = item.Level switch {
                HealthLevel.Ok => "✓ ",
                HealthLevel.Error => "✕ ",
                HealthLevel.Warning => "! ",
                _ => "… ",
            };
            status.Text = mark + item.Status;
            status.ForeColor = item.Level switch {
                HealthLevel.Ok => HudTheme.Positive,
                HealthLevel.Error => HudTheme.Negative,
                HealthLevel.Warning => HudTheme.Caution,
                _ => HudTheme.InkSoft,
            };
            fix.Text = item.FixLabel ?? Loc.T("Open");
            fix.Visible = item.FixLabel != null;
        }
    }

    private void AddRecordingChoices() {
        var s = _host.Settings;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var hold = new RadioButton {
            AutoSize = true,
            Text = Loc.F("Hold to talk — hold {0}, speak, release", HotkeyNames.For(s.DictationChord)),
            Checked = !s.ToggleRecording,
            Margin = new Padding(0, 4, 0, 4),
        };
        var press = new RadioButton {
            AutoSize = true,
            Text = Loc.F("Press to start/stop — press {0}, speak, press it again", HotkeyNames.For(s.DictationChord)),
            Checked = s.ToggleRecording,
            Margin = new Padding(0, 4, 0, 16),
        };
        hold.CheckedChanged += (_, _) => {
            if (hold.Checked) _host.SetToggleRecording(false);
        };
        press.CheckedChanged += (_, _) => {
            if (press.Checked) _host.SetToggleRecording(true);
        };
        flow.Controls.Add(hold);
        flow.Controls.Add(press);
        flow.Controls.Add(Check(Loc.T("Auto Clean — remove ums, repeats and false starts before pasting"),
            s.AutoClean, v => _host.SetAutoClean(v)));
        flow.Controls.Add(Check(Loc.T("Spoken shortcuts — start with \"ask …\" or \"translate …\" (اسأل / ترجم) to route what you say"),
            s.VoicePrefixes, v => _host.SetVoicePrefixes(v)));
        flow.Controls.Add(Check(Loc.T("Keep a History of what I dictate (stored only on this PC)"),
            s.HistoryEnabled, v => _host.SetHistoryEnabled(v)));
        flow.Controls.Add(Check(Loc.T("Start EchoType with Windows"),
            s.StartWithWindows, v => _host.SetStartWithWindows(v)));
        _body.Controls.Add(flow);
    }

    private static CheckBox Check(string text, bool value, Action<bool> changed) {
        var box = new CheckBox { AutoSize = true, Text = text, Checked = value, Margin = new Padding(0, 6, 0, 6), MaximumSize = new Size(620, 0) };
        box.CheckedChanged += (_, _) => changed(box.Checked);
        return box;
    }

    private void AddTest() {
        var s = _host.Settings;
        string key = HotkeyNames.For(s.DictationChord);
        _intro.Text = (s.ToggleRecording
            ? Loc.F("Press {0}, say a sentence, then press {0} again.", key)
            : Loc.F("Hold {0}, say a sentence, then release.", key))
            + " " + Loc.T("What EchoType hears shows up below (and on the clipboard). Afterwards it pastes into whatever app you're typing in.");
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _testBox = new TextBox {
            Multiline = true,
            ReadOnly = true,
            Width = 620,
            Height = 120,
            ScrollBars = ScrollBars.Vertical,
            Margin = new Padding(0, 0, 0, 8),
        };
        _testStatus = new Label {
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = SystemColors.GrayText,
            Text = Loc.T("Waiting for your first dictation…"),
            Margin = new Padding(0, 0, 0, 18),
        };
        var tips = DialogTheme.SupportingText(
            Loc.T("Tips: double-tap the dictation key to cancel. Select text first, then speak an instruction (\"make this shorter\") to rewrite it. Everything you dictate is in History (tray → History…), and Shortcuts → Action palette gives you every action from one key."));
        tips.MaximumSize = new Size(620, 0);
        flow.Controls.Add(_testBox);
        flow.Controls.Add(_testStatus);
        flow.Controls.Add(tips);
        _body.Controls.Add(flow);
    }

    private void OnTranscript(HistoryEntry entry) {
        if (IsDisposed || _testStatus == null) {
            return;
        }
        if (InvokeRequired) {
            BeginInvoke(() => OnTranscript(entry));
            return;
        }
        if (_testBox != null && !entry.Failed) {
            _testBox.Text = entry.Text;
            _testBox.RightToLeft = ArabicFont.StartsRightToLeft(entry.Text) ? RightToLeft.Yes : RightToLeft.No;
        }
        _testStatus.ForeColor = entry.Failed ? HudTheme.Negative : HudTheme.Positive;
        _testStatus.Text = entry.Failed
            ? Loc.T("That one didn't make it — check the step 2 sign-in and try again.")
            : Loc.T("✓ It works! Click Finish, or try another one.");
    }
}
