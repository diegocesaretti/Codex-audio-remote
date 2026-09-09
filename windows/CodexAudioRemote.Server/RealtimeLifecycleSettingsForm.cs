using System.Drawing;
using System.Windows.Forms;

internal sealed class RealtimeLifecycleSettingsForm : Form
{
    readonly NumericUpDown listenTimeout = new();
    readonly NumericUpDown conversationTimeout = new();

    public static void ShowSettings()
    {
        using var form = new RealtimeLifecycleSettingsForm();
        form.ShowDialog();
    }

    RealtimeLifecycleSettingsForm()
    {
        Text = "Realtime · Escucha y conversación";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(640, 330);

        var title = new Label
        {
            Text = "Dos timeouts independientes",
            Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
            AutoSize = true
        };
        title.SetBounds(20, 18, 500, 24); Controls.Add(title);

        var info = new Label
        {
            Text = "Al vencer el primero se cierra sólo el micrófono: la sesión Realtime y el contexto siguen vivos. Decir la wake word reabre el micrófono en la misma conversación. El segundo timeout recién cierra por completo el chat de voz.",
            ForeColor = SystemColors.GrayText,
            AutoSize = false
        };
        info.SetBounds(22, 52, 590, 64); Controls.Add(info);

        AddRow("Fin de escucha tras silencio", listenTimeout, 132, 0, 600);
        var listenSuffix = new Label { Text = "segundos (0 = nunca)", AutoSize = true };
        listenSuffix.SetBounds(430, 137, 170, 24); Controls.Add(listenSuffix);

        AddRow("Cerrar conversación estando en espera", conversationTimeout, 182, 0, 3600);
        var convSuffix = new Label { Text = "segundos (0 = nunca)", AutoSize = true };
        convSuffix.SetBounds(430, 187, 170, 24); Controls.Add(convSuffix);

        var states = new Label
        {
            Text = "Flujo: LISTENING → (silencio) → PAUSED → (wake) → LISTENING\n                                      ↘ (timeout conversación) → IDLE",
            AutoSize = false,
            ForeColor = SystemColors.GrayText
        };
        states.SetBounds(22, 225, 590, 48); Controls.Add(states);

        var save = new Button { Text = "Guardar", Width = 100, Height = 30, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Width = 100, Height = 30, DialogResult = DialogResult.Cancel };
        save.SetBounds(414, 286, 100, 30); cancel.SetBounds(524, 286, 100, 30);
        save.Click += (_, _) =>
        {
            RealtimeMirrorSettings.ListenSilenceTimeoutSeconds = (int)listenTimeout.Value;
            RealtimeMirrorSettings.ConversationIdleTimeoutSeconds = (int)conversationTimeout.Value;
        };
        Controls.Add(save); Controls.Add(cancel); AcceptButton = save; CancelButton = cancel;

        listenTimeout.Value = RealtimeMirrorSettings.ListenSilenceTimeoutSeconds;
        conversationTimeout.Value = RealtimeMirrorSettings.ConversationIdleTimeoutSeconds;
    }

    void AddRow(string text, NumericUpDown numeric, int y, int min, int max)
    {
        var label = new Label { Text = text, AutoSize = true };
        label.SetBounds(22, y + 5, 285, 24); Controls.Add(label);
        numeric.Minimum = min; numeric.Maximum = max; numeric.Increment = 1;
        numeric.SetBounds(310, y, 105, 28); Controls.Add(numeric);
    }
}
