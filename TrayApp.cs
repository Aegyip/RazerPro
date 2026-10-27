using System.Drawing.Drawing2D;
using BarracudaTray.Audio;
using BarracudaTray.Headset;
using BarracudaTray.Interop;
using BarracudaTray.Lighting;

namespace BarracudaTray;

internal sealed class TrayApp : ApplicationContext
{
    static readonly (string Name, LampColor Color)[] Presets =
    [
        ("Razer yeşili", new(0x44, 0xD6, 0x2C)),
        ("Kırmızı", new(255, 0, 0)),
        ("Turuncu", new(255, 96, 0)),
        ("Sarı", new(255, 200, 0)),
        ("Camgöbeği", new(0, 255, 255)),
        ("Mavi", new(0, 64, 255)),
        ("Mor", new(140, 0, 255)),
        ("Pembe", new(255, 0, 140)),
        ("Beyaz", new(255, 255, 255)),
    ];

    static readonly int[] BrightnessLevels = [10, 25, 50, 75, 100];

    // Shown at the top of the 7.1 menu; every other HeSuVi profile sits under "Tüm profiller".
    static readonly string[] FavoriteProfiles = ["razer", "razer_fix", "sonic", "atmos", "dtshx"];

    static readonly (string Name, LightingEffect Effect)[] Effects =
    [
        ("Sabit", LightingEffect.Static),
        ("Nefes", LightingEffect.Breathing),
        ("Gökkuşağı", LightingEffect.Spectrum),
    ];

    readonly AppSettings _settings = AppSettings.Load();
    readonly LightingEngine _engine;
    readonly HeadsetMonitor _headset = new();
    readonly NotifyIcon _tray;
    readonly SynchronizationContext _ui;
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _hint = new() { Enabled = false };
    bool _wasLinked;
    readonly ToolStripMenuItem _enabled;
    readonly ToolStripMenuItem _colors = new("Renk");
    readonly ToolStripMenuItem _brightness = new("Parlaklık");
    readonly ToolStripMenuItem _effects = new("Efekt");
    readonly ToolStripMenuItem _autonomous;
    readonly SurroundController _surround;
    readonly ToolStripMenuItem _surroundMenu = new("7.1 Surround");
    readonly ToolStripMenuItem _surroundEnabled;
    readonly ToolStripMenuItem _surroundStatus = new() { Enabled = false };
    readonly ToolStripMenuItem _surroundExclusive;
    readonly List<ToolStripMenuItem> _profileItems = [];
    Icon? _icon;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        // Creating the first control installs the WinForms synchronization context.
        _ui = SynchronizationContext.Current!;

        _enabled = new ToolStripMenuItem("Işıklar açık", null, (_, _) => Change(s => s.Enabled = !s.Enabled));

        foreach (var (name, color) in Presets)
            _colors.DropDownItems.Add(new ToolStripMenuItem(name, Swatch(color), (_, _) => PickColor(color)) { Tag = color });
        _colors.DropDownItems.Add(new ToolStripSeparator());
        _colors.DropDownItems.Add(new ToolStripMenuItem("Özel renk…", null, (_, _) => PickCustomColor()));

        foreach (var level in BrightnessLevels)
            _brightness.DropDownItems.Add(new ToolStripMenuItem($"%{level}", null, (_, _) => Change(s => s.Brightness = level)) { Tag = level });

        foreach (var (name, effect) in Effects)
            _effects.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => Change(s => s.Effect = effect)) { Tag = effect });

        _autonomous = new ToolStripMenuItem("Kulaklığın kendi efekti", null, (_, _) => ToggleAutonomous());

        _surround = new SurroundController(_ui);
        _surround.Changed += SyncMenu;
        _surroundEnabled = new ToolStripMenuItem("Açık", null, (_, _) => ChangeSurround(s => s.Surround = !s.Surround));
        _surroundExclusive = new ToolStripMenuItem("Özel mod (en düşük gecikme)", null,
            (_, _) => ChangeSurround(s => s.SurroundExclusive = !s.SurroundExclusive));
        BuildSurroundMenu();

        menu.Items.AddRange([
            _status,
            _hint,
            new ToolStripSeparator(),
            _enabled,
            _colors,
            _brightness,
            _effects,
            new ToolStripSeparator(),
            _autonomous,
            new ToolStripSeparator(),
            _surroundMenu,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Çıkış", null, (_, _) => ExitThread()),
        ]);

        // The latency estimate moves on its own, so refresh it whenever the menu is about to show.
        menu.Opening += (_, _) => _surroundStatus.Text = $"Durum: {_surround.Status}";

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Change(s => s.Enabled = !s.Enabled);

        _engine = new LightingEngine(_settings.ToState());
        _headset.StatusChanged += _ => _ui.Post(_ => OnHeadsetChanged(), null);
        _surround.Apply(_settings.Surround, _settings.SurroundProfile, _settings.SurroundExclusive);
        SyncMenu();
    }

    void BuildSurroundMenu()
    {
        var profiles = HrirLibrary.List();
        ToolStripMenuItem ProfileItem((string Name, string Description) p) =>
            new(p.Description, null, (_, _) => ChangeSurround(s => s.SurroundProfile = p.Name)) { Tag = p.Name };

        _surroundMenu.DropDownItems.AddRange([_surroundEnabled, _surroundStatus, new ToolStripSeparator()]);
        if (profiles.Count == 0)
        {
            _surroundMenu.DropDownItems.Add(new ToolStripMenuItem($"Profil yok: {HrirLibrary.Folder}") { Enabled = false });
        }
        else
        {
            foreach (var name in FavoriteProfiles)
            {
                if (profiles.FirstOrDefault(p => p.Name == name) is { Name: not null } favorite)
                {
                    var item = ProfileItem(favorite);
                    _profileItems.Add(item);
                    _surroundMenu.DropDownItems.Add(item);
                }
            }
            var all = new ToolStripMenuItem("Tüm profiller");
            foreach (var profile in profiles)
            {
                var item = ProfileItem(profile);
                _profileItems.Add(item);
                all.DropDownItems.Add(item);
            }
            _surroundMenu.DropDownItems.Add(all);
        }
        _surroundMenu.DropDownItems.AddRange([new ToolStripSeparator(), _surroundExclusive]);
    }

    void ChangeSurround(Action<AppSettings> change)
    {
        change(_settings);
        _settings.Save();
        _surround.Apply(_settings.Surround, _settings.SurroundProfile, _settings.SurroundExclusive);
        SyncMenu();
    }

    void OnHeadsetChanged()
    {
        // The headset drops host colors while away, so repaint the moment it links up again.
        var linked = _headset.Status.Linked == true;
        if (linked && !_wasLinked)
            _engine.Refresh();
        _wasLinked = linked;
        SyncMenu();
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _surround.Dispose();
            _headset.Dispose();
            _engine.Dispose();
            _tray.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Any lighting choice takes the lamps back from the headset's own effect.</summary>
    void Change(Action<AppSettings> change)
    {
        change(_settings);
        _settings.Autonomous = false;
        Commit();
    }

    void Commit()
    {
        _settings.Save();
        _engine.Apply(_settings.ToState());
        SyncMenu();
    }

    void ToggleAutonomous()
    {
        _settings.Autonomous = !_settings.Autonomous;
        Commit();
    }

    void PickColor(LampColor color) => Change(s =>
    {
        s.Color = color.ToString();
        s.Enabled = true;
        if (s.Effect == LightingEffect.Spectrum)
            s.Effect = LightingEffect.Static;
    });

    void PickCustomColor()
    {
        var current = _settings.LampColor;
        using var dialog = new ColorDialog { FullOpen = true, Color = Color.FromArgb(current.R, current.G, current.B) };
        if (dialog.ShowDialog() == DialogResult.OK)
            PickColor(new LampColor(dialog.Color.R, dialog.Color.G, dialog.Color.B));
    }

    void SyncMenu()
    {
        var headset = _headset.Status;
        var connected = headset.Linked == true;
        _status.Text = !headset.DonglePresent ? "Dongle takılı değil"
            : headset.Linked is null ? "Kulaklık aranıyor…"
            : !connected ? "Kulaklık Bluetooth'ta ya da kapalı"
            : "Barracuda X Chroma · bağlı"
                + (headset.Battery is { } battery ? $" · pil %{battery}" : "")
                + (headset.Charging ? " (şarj oluyor)" : "")
                + (headset.MicMuted == true ? " · mikrofon kapalı" : "");
        _hint.Text = headset.DonglePresent && headset.Linked == false ? "Dongle'a geçmek için SmartSwitch'e 2 kez bas"
            : connected && headset.LightingOn == false ? "Chroma kulaklıkta kapalı: SmartSwitch'e 3 kez bas"
            : "";
        _hint.Visible = _hint.Text.Length > 0;
        _enabled.Checked = _settings.Enabled && !_settings.Autonomous;
        _autonomous.Checked = _settings.Autonomous;

        var color = _settings.LampColor;
        foreach (var item in _colors.DropDownItems.OfType<ToolStripMenuItem>())
            item.Checked = item.Tag is LampColor c && c == color && _settings.Effect != LightingEffect.Spectrum;
        foreach (var item in _brightness.DropDownItems.OfType<ToolStripMenuItem>())
            item.Checked = item.Tag is int level && level == _settings.Brightness;
        foreach (var item in _effects.DropDownItems.OfType<ToolStripMenuItem>())
            item.Checked = item.Tag is LightingEffect effect && effect == _settings.Effect;

        _surroundEnabled.Checked = _settings.Surround;
        _surroundExclusive.Checked = _settings.SurroundExclusive;
        _surroundStatus.Text = $"Durum: {_surround.Status}";
        foreach (var item in _profileItems)
            item.Checked = item.Tag as string == _settings.SurroundProfile;
        _surroundMenu.Text = _settings.Surround ? "7.1 Surround · açık" : "7.1 Surround";

        var state = !connected ? _status.Text
            : _settings.Autonomous ? "kulaklığın efekti"
            : !_settings.Enabled ? "ışıklar kapalı"
            : $"{Effects.First(e => e.Effect == _settings.Effect).Name}, %{_settings.Brightness}";
        if (connected && headset.Battery is { } charge)
            state = $"pil %{charge} · {state}";
        _tray.Text = $"Barracuda Tray: {state}";

        var old = _icon;
        _icon = DrawIcon(connected);
        _tray.Icon = _icon;
        old?.Dispose();
    }

    Icon DrawIcon(bool connected)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(3, 3, 26, 26);
            if (_settings.Autonomous || (_settings.Enabled && _settings.Effect == LightingEffect.Spectrum))
            {
                for (var i = 0; i < 6; i++)
                {
                    var c = LampColor.FromHue(i * 60);
                    using var slice = new SolidBrush(Color.FromArgb(c.R, c.G, c.B));
                    g.FillPie(slice, bounds, i * 60 - 90, 60);
                }
            }
            else
            {
                var c = _settings.Enabled ? _settings.LampColor : new LampColor(40, 40, 40);
                using var fill = new SolidBrush(Color.FromArgb(c.R, c.G, c.B));
                g.FillEllipse(fill, bounds);
            }
            using var ring = new Pen(connected ? Color.White : Color.DimGray, 3);
            g.DrawEllipse(ring, bounds);
            if (!connected)
                g.DrawLine(ring, 7, 25, 25, 7);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    static Bitmap Swatch(LampColor color)
    {
        var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        using var fill = new SolidBrush(Color.FromArgb(color.R, color.G, color.B));
        g.FillRectangle(fill, 1, 1, 14, 14);
        g.DrawRectangle(Pens.Gray, 1, 1, 13, 13);
        return bitmap;
    }
}
