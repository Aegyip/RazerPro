namespace BarracudaTray.Ui;

/// <summary>Ten-band editor for the headset's custom EQ curve.</summary>
internal sealed class EqualizerForm : Form
{
    // The headset stores one signed byte per band. Razer's own presets stay within ±5 and the
    // supported range is not documented, so the editor does not go past ±6.
    public const int Limit = 6;

    static readonly string[] Bands = ["31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    readonly TrackBar[] _sliders = new TrackBar[Bands.Length];

    public EqualizerForm(IReadOnlyList<sbyte> curve)
    {
        Text = "Özel EQ";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(Bands.Length * 44 + 24, 270);

        for (var i = 0; i < Bands.Length; i++)
        {
            var x = 12 + i * 44;
            var value = new Label { Left = x, Top = 8, Width = 44, TextAlign = ContentAlignment.MiddleCenter };
            var slider = new TrackBar
            {
                Orientation = Orientation.Vertical,
                Minimum = -Limit,
                Maximum = Limit,
                TickFrequency = 1,
                TickStyle = TickStyle.Both,
                Left = x,
                Top = 30,
                Height = 170,
                Value = Math.Clamp(i < curve.Count ? curve[i] : 0, -Limit, Limit),
            };
            void Show() => value.Text = slider.Value > 0 ? $"+{slider.Value}" : slider.Value.ToString();
            slider.ValueChanged += (_, _) => Show();
            Show();
            var band = new Label { Text = Bands[i], Left = x, Top = 202, Width = 44, TextAlign = ContentAlignment.MiddleCenter };
            Controls.AddRange([value, slider, band]);
            _sliders[i] = slider;
        }

        var flat = new Button { Text = "Düz", Left = 12, Top = 232, Width = 80 };
        flat.Click += (_, _) =>
        {
            foreach (var slider in _sliders)
                slider.Value = 0;
        };
        var ok = new Button { Text = "Uygula", DialogResult = DialogResult.OK, Left = ClientSize.Width - 184, Top = 232, Width = 80 };
        var cancel = new Button { Text = "İptal", DialogResult = DialogResult.Cancel, Left = ClientSize.Width - 96, Top = 232, Width = 80 };
        Controls.AddRange([flat, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public sbyte[] Curve => _sliders.Select(s => (sbyte)s.Value).ToArray();
}
