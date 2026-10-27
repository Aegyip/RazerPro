namespace BarracudaTray.Ui;

/// <summary>Asks for one line of text, e.g. a profile name.</summary>
internal static class TextPrompt
{
    public static string? Show(string title, string label, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(320, 110),
        };
        var caption = new Label { Text = label, Left = 12, Top = 12, Width = 296 };
        var input = new TextBox { Text = initial, Left = 12, Top = 36, Width = 296 };
        var ok = new Button { Text = "Tamam", DialogResult = DialogResult.OK, Left = 140, Top = 72, Width = 80 };
        var cancel = new Button { Text = "İptal", DialogResult = DialogResult.Cancel, Left = 228, Top = 72, Width = 80 };
        form.Controls.AddRange([caption, input, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK && input.Text.Trim() is { Length: > 0 } text ? text : null;
    }
}
