namespace EchoType.Ui;

/// <summary>
/// Small always-on-top chooser shown after dictation when a custom command
/// has more than one action button. Completes <see cref="Completion"/> with
/// the chosen button, or null if the user cancels.
/// </summary>
internal sealed class CommandButtonPickerForm : Form {

    private readonly TaskCompletionSource<CommandButton?> _tcs;
    private readonly IReadOnlyList<CommandButton> _buttons;
    private bool _completed;

    public Task<CommandButton?> Completion => _tcs.Task;

    public CommandButtonPickerForm(
        string commandName,
        IReadOnlyList<CommandButton> buttons,
        TaskCompletionSource<CommandButton?> tcs) {
        _buttons = buttons;
        _tcs = tcs;

        Text = "EchoType";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        KeyPreview = true;
        Padding = new Padding(14);
        ClientSize = new Size(340, 110 + Math.Min(buttons.Count, CustomCommand.MaxButtons) * 48);

        var title = new Label {
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = string.IsNullOrWhiteSpace(commandName) ? "Choose an action" : commandName.Trim(),
            Padding = new Padding(0, 0, 0, 2),
        };
        var hint = new Label {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "Choose a button. Esc cancels. Number keys 1–" + Math.Min(9, buttons.Count) + " also work.",
            Padding = new Padding(0, 0, 0, 10),
        };

        var list = new FlowLayoutPanel {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };
        for (int i = 0; i < buttons.Count; i++) {
            var button = buttons[i];
            int index = i;
            var pick = new Button {
                Text = (i + 1) + "   " + button.DisplayName,
                Width = 308,
                Height = 40,
                Margin = new Padding(0, 0, 0, 8),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 12, 0),
                UseVisualStyleBackColor = true,
            };
            pick.Click += (_, _) => Complete(button);
            list.Controls.Add(pick);
            if (i == 0) {
                AcceptButton = pick;
            }
        }

        Controls.Add(list);
        Controls.Add(hint);
        Controls.Add(title);

        KeyDown += OnKeyDown;
        FormClosed += (_, _) => {
            if (_completed) {
                return;
            }
            _completed = true;
            _tcs.TrySetResult(null);
        };
    }

    public void PlaceNearCursor() {
        var screen = Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        int x = Cursor.Position.X + 16;
        int y = Cursor.Position.Y + 16;
        if (x + Width > area.Right) {
            x = area.Right - Width - 8;
        }
        if (y + Height > area.Bottom) {
            y = area.Bottom - Height - 8;
        }
        Location = new Point(Math.Max(area.Left + 8, x), Math.Max(area.Top + 8, y));
    }

    public void Dismiss() => Complete(null);

    private void OnKeyDown(object? sender, KeyEventArgs e) {
        if (e.KeyCode == Keys.Escape) {
            Complete(null);
            e.Handled = true;
            return;
        }
        int digit = e.KeyCode switch {
            Keys.D1 or Keys.NumPad1 => 1,
            Keys.D2 or Keys.NumPad2 => 2,
            Keys.D3 or Keys.NumPad3 => 3,
            Keys.D4 or Keys.NumPad4 => 4,
            Keys.D5 or Keys.NumPad5 => 5,
            Keys.D6 or Keys.NumPad6 => 6,
            Keys.D7 or Keys.NumPad7 => 7,
            Keys.D8 or Keys.NumPad8 => 8,
            Keys.D9 or Keys.NumPad9 => 9,
            _ => 0,
        };
        if (digit > 0 && digit <= _buttons.Count) {
            Complete(_buttons[digit - 1]);
            e.Handled = true;
        }
    }

    private void Complete(CommandButton? button) {
        if (_completed) {
            return;
        }
        _completed = true;
        _tcs.TrySetResult(button);
        if (!IsDisposed) {
            Close();
        }
    }
}
