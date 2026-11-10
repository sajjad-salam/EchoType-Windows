namespace EchoType.Ui;

/// <summary>
/// Shared visual and interaction treatment for settings and editor dialogs.
/// Keeping this in one place prevents each feature from drifting back to the
/// default WinForms look and makes new settings screens consistent by default.
/// </summary>
internal static class DialogTheme {

    private static readonly Color Selection = Color.FromArgb(225, 235, 255);
    private static readonly Color SelectionText = Color.FromArgb(17, 45, 95);
    private static readonly Color Alternating = Color.FromArgb(252, 252, 251);

    public static void Apply(Form form) {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.BackColor = HudTheme.Paper;
        form.ForeColor = HudTheme.Ink;
        form.Font = HudTheme.BodyText;
        form.Padding = ScalePadding(form, form.Padding.All <= 14 ? 24 : form.Padding.All);
        form.KeyPreview = true;

        StyleChildren(form, form);
        form.HandleCreated += (_, _) => HudTheme.ApplyChrome(form, paper: true);
        form.Shown += (_, _) => {
            HudTheme.ApplyChrome(form, paper: true);
            form.Invalidate(true);
        };
    }

    /// <summary>Styles controls added after <see cref="Apply"/> (e.g. a wizard's next step).</summary>
    public static void Restyle(Form form) => StyleChildren(form, form);

    public static void Primary(Button button) {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = HudTheme.Ink;
        button.ForeColor = HudTheme.OnInk;
        button.FlatAppearance.BorderColor = HudTheme.Ink;
        button.FlatAppearance.MouseOverBackColor = HudTheme.InkHover;
        button.FlatAppearance.MouseDownBackColor = Color.Black;
        button.Font = HudTheme.BodyBold;
    }

    public static void Danger(Button button) {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = HudTheme.NegativeWash;
        button.ForeColor = HudTheme.Negative;
        button.FlatAppearance.BorderColor = Color.FromArgb(248, 190, 190);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 226, 226);
    }

    public static Label Heading(string text) => new() {
        Text = text,
        AutoSize = true,
        Font = HudTheme.Display,
        ForeColor = HudTheme.Ink,
        Margin = new Padding(0, 0, 0, 6),
    };

    public static Label SupportingText(string text) => new() {
        Text = text,
        AutoSize = true,
        Font = HudTheme.Small,
        ForeColor = HudTheme.InkSoft,
        MaximumSize = new Size(760, 0),
        Margin = new Padding(0),
    };

    private static void StyleChildren(Form form, Control parent) {
        foreach (Control control in parent.Controls) {
            switch (control) {
                case DataGridView grid:
                    StyleGrid(grid);
                    break;
                case Button button:
                    StyleButton(form, button);
                    break;
                case TextBoxBase text:
                    text.BackColor = HudTheme.Surface;
                    text.ForeColor = HudTheme.Ink;
                    text.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox combo:
                    combo.BackColor = HudTheme.Surface;
                    combo.ForeColor = HudTheme.Ink;
                    combo.FlatStyle = FlatStyle.Flat;
                    break;
                case ListBox list:
                    list.BackColor = HudTheme.Surface;
                    list.ForeColor = HudTheme.Ink;
                    list.BorderStyle = BorderStyle.FixedSingle;
                    list.IntegralHeight = false;
                    break;
                case LinkLabel link:
                    link.LinkColor = HudTheme.Info;
                    link.ActiveLinkColor = Color.FromArgb(29, 78, 216);
                    link.VisitedLinkColor = HudTheme.Info;
                    break;
                case Label label when label.ForeColor == SystemColors.GrayText:
                    label.ForeColor = HudTheme.InkSoft;
                    label.Font = HudTheme.Small;
                    break;
                case FlowLayoutPanel flow:
                    flow.BackColor = HudTheme.Paper;
                    break;
                case TableLayoutPanel table:
                    table.BackColor = HudTheme.Paper;
                    break;
                case Panel panel:
                    panel.BackColor = HudTheme.Paper;
                    break;
            }

            if (control.HasChildren) {
                StyleChildren(form, control);
            }
        }
    }

    private static void StyleButton(Form form, Button button) {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = HudTheme.Surface;
        button.ForeColor = HudTheme.Ink;
        button.FlatAppearance.BorderColor = HudTheme.HairlineStrong;
        button.FlatAppearance.MouseOverBackColor = HudTheme.Well;
        button.FlatAppearance.MouseDownBackColor = HudTheme.WellHover;
        button.Padding = new Padding(12, 5, 12, 5);
        button.MinimumSize = new Size(0, Scale(form, 36));
        button.Cursor = Cursors.Hand;

        if (ReferenceEquals(form.AcceptButton, button)) {
            Primary(button);
        }
    }

    private static void StyleGrid(DataGridView grid) {
        grid.BackgroundColor = HudTheme.Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = HudTheme.Hairline;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = 42;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.RowHeadersVisible = false;
        grid.RowTemplate.Height = 40;
        grid.DefaultCellStyle = new DataGridViewCellStyle(grid.DefaultCellStyle) {
            BackColor = HudTheme.Surface,
            ForeColor = HudTheme.Ink,
            SelectionBackColor = Selection,
            SelectionForeColor = SelectionText,
            Padding = new Padding(10, 5, 10, 5),
            Font = HudTheme.BodyText,
        };
        // Only the background: alternating-row styles outrank column styles, so copying the
        // grid default here would also force WrapMode=False onto every other row and turn
        // a column's multi-line editor single-line (pasted text then stops at the first line).
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle {
            BackColor = Alternating,
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle(grid.ColumnHeadersDefaultCellStyle) {
            BackColor = HudTheme.Well,
            ForeColor = HudTheme.InkSoft,
            SelectionBackColor = HudTheme.Well,
            SelectionForeColor = HudTheme.InkSoft,
            Padding = new Padding(10, 7, 10, 7),
            Font = HudTheme.SmallBold,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
        };
    }

    private static Padding ScalePadding(Control control, int value) {
        int scaled = HudTheme.Dip(control, value);
        return new Padding(scaled);
    }

    private static int Scale(Control control, int value) => HudTheme.Dip(control, value);
}
