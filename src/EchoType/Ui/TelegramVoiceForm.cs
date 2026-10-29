using EchoType.Telegram;

namespace EchoType.Ui;

/// <summary>
/// Telegram bot token, chat id and the Gemini API key used to turn voice notes sent to
/// the bot into text replies.
/// </summary>
internal sealed class TelegramVoiceForm : Form {

    private const int FieldWidth = 460;

    private readonly CheckBox _enabled;
    private readonly TextBox _geminiKey;
    private readonly TextBox _geminiModel;
    private readonly TextBox _token;
    private readonly TextBox _chatId;
    private readonly Label _geminiStatus;
    private readonly Label _telegramStatus;
    private readonly Button _testGemini;
    private readonly Button _testTelegram;

    public bool TelegramEnabled => _enabled.Checked;
    public string GeminiKey => _geminiKey.Text.Trim();
    public string GeminiModel => _geminiModel.Text.Trim();
    public string TelegramToken => _token.Text.Trim();
    public string TelegramChatId => _chatId.Text.Trim();

    public TelegramVoiceForm(Settings settings) {
        Text = Loc.T("Telegram voice notes");
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

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };

        layout.Controls.Add(Note(Loc.T("While EchoType is running, send a voice note to your bot: EchoType downloads it, Gemini turns it into text, and the bot replies with the text in the same chat.")));
        _enabled = new CheckBox {
            Text = Loc.T("Transcribe Telegram voice notes"),
            AutoSize = true,
            Checked = settings.TelegramEnabled,
            Margin = new Padding(0, 6, 0, 0),
        };
        layout.Controls.Add(_enabled);

        layout.Controls.Add(Heading("Telegram"));
        layout.Controls.Add(Note(Loc.T("1. In Telegram, open @BotFather → /newbot and copy the token here.  2. Save, then send any message to your bot: it replies with your chat id.  3. Paste the chat id here. Only that chat's voice notes are transcribed.")));
        _token = new TextBox { Width = FieldWidth, UseSystemPasswordChar = true, Text = settings.TelegramBotToken, PlaceholderText = "123456789:AA…" };
        layout.Controls.Add(Note(Loc.T("Bot token:")));
        layout.Controls.Add(_token);
        _chatId = new TextBox { Width = FieldWidth, Text = settings.TelegramChatId, PlaceholderText = "7812…" };
        layout.Controls.Add(Note(Loc.T("Chat id:")));
        layout.Controls.Add(_chatId);
        _testTelegram = new Button { Text = Loc.T("Test Telegram"), AutoSize = true };
        _testTelegram.Click += async (_, _) => await TestTelegramAsync();
        _telegramStatus = Status();
        layout.Controls.Add(Row(_testTelegram, _telegramStatus));

        layout.Controls.Add(Heading("Gemini"));
        layout.Controls.Add(Note(Loc.T("Turns the voice notes into text. Get a free key at aistudio.google.com → Get API key.")));
        _geminiKey = new TextBox { Width = FieldWidth, UseSystemPasswordChar = true, Text = settings.GeminiApiKey, PlaceholderText = "AIza…" };
        layout.Controls.Add(_geminiKey);
        _geminiModel = new TextBox { Width = FieldWidth, Text = settings.GeminiModel, PlaceholderText = GeminiTranscriber.DefaultModel };
        layout.Controls.Add(Note(Loc.T("Model (empty = latest Flash):")));
        layout.Controls.Add(_geminiModel);
        _testGemini = new Button { Text = Loc.T("Test Gemini"), AutoSize = true };
        _testGemini.Click += async (_, _) => await TestGeminiAsync();
        _geminiStatus = Status();
        layout.Controls.Add(Row(_testGemini, _geminiStatus));

        var show = new CheckBox { Text = Loc.T("Show keys"), AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        show.CheckedChanged += (_, _) => {
            _geminiKey.UseSystemPasswordChar = !show.Checked;
            _token.UseSystemPasswordChar = !show.Checked;
        };
        layout.Controls.Add(show);
        layout.Controls.Add(Note(Loc.T("Keys are saved only in %APPDATA%\\EchoType\\config.json on this PC.")));

        var save = new Button { Text = Loc.T("Save"), AutoSize = true };
        var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) => {
            DialogResult = DialogResult.OK;
            Close();
        };
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
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        Loc.ApplyTo(this);
    }

    private static Label Heading(string text) => new() {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 12, 0, 4),
    };

    private static Label Note(string text) => new() {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(FieldWidth, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 4, 0, 2),
    };

    private static Label Status() => new() {
        AutoSize = true,
        MaximumSize = new Size(FieldWidth - 120, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(8, 8, 0, 0),
    };

    private static FlowLayoutPanel Row(params Control[] controls) {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        row.Controls.AddRange(controls);
        return row;
    }

    private async Task TestGeminiAsync() {
        if (GeminiKey.Length == 0) {
            SetStatus(_geminiStatus, Loc.T("Enter a key first."), null);
            return;
        }
        _testGemini.Enabled = false;
        SetStatus(_geminiStatus, Loc.T("Testing…"), null);
        string? error = await new GeminiTranscriber(GeminiKey, GeminiModel).TestAsync(CancellationToken.None);
        if (IsDisposed) {
            return;
        }
        _testGemini.Enabled = true;
        SetStatus(_geminiStatus, error == null ? Loc.T("Gemini works.") : Loc.T("Didn't work: ") + error, error == null);
    }

    private async Task TestTelegramAsync() {
        if (TelegramToken.Length == 0) {
            SetStatus(_telegramStatus, Loc.T("Enter the bot token first."), null);
            return;
        }
        _testTelegram.Enabled = false;
        SetStatus(_telegramStatus, Loc.T("Testing…"), null);
        try {
            string username = await TelegramBot.GetUsernameAsync(TelegramToken);
            if (TelegramBot.TryParseChatId(TelegramChatId, out long chatId)) {
                using var bot = new TelegramBot(TelegramToken);
                await bot.SendMessageAsync(chatId, "✅ EchoType: " + Loc.T("connected. Send me a voice note and I'll reply with its text."));
                SetStatus(_telegramStatus, Loc.F("@{0} works — a test message was sent.", username), true);
            } else {
                SetStatus(_telegramStatus, Loc.F("@{0} works. Save, message it, then paste your chat id.", username), true);
            }
        } catch (Exception ex) {
            SetStatus(_telegramStatus, Loc.T("Didn't work: ") + ex.Message, false);
        }
        if (!IsDisposed) {
            _testTelegram.Enabled = true;
        }
    }

    private void SetStatus(Label label, string text, bool? ok) {
        if (IsDisposed) {
            return;
        }
        label.ForeColor = ok == true ? Color.ForestGreen : ok == false ? Color.Firebrick : SystemColors.GrayText;
        label.Text = text;
    }
}
