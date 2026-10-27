using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using BarracudaTray.Audio;
using BarracudaTray.Headset;
using BarracudaTray.Interop;
using BarracudaTray.Lighting;
using BarracudaTray.Ui;

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

    static readonly (string Name, EqPreset Preset)[] EqPresets =
    [
        ("Oyun", EqPreset.Game),
        ("Müzik", EqPreset.Music),
        ("Film", EqPreset.Movie),
        ("Özel", EqPreset.Custom),
    ];

    static readonly int[] SidetoneSteps = [0, 20, 40, 60, 80, 100];

    // Only the timeouts seen in Synapse captures; others are probably plain minutes too, but unverified.
    static readonly int[] PowerSavingMinutes = [0, 15, 30];

    static readonly int[] LowBatteryLevels = [5, 10, 20];

    readonly AppSettings _settings = AppSettings.Load();
    readonly LightingEngine _engine;
    readonly HeadsetMonitor _headset = new();
    readonly SurroundController _surround;
    readonly GlobalHotkeys _hotkeys = new();
    readonly NotifyIcon _tray;
    readonly SynchronizationContext _ui;

    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _hint = new() { Enabled = false };
    readonly ToolStripMenuItem _enabled;
    readonly ToolStripMenuItem _colors = new("Renk");
    readonly ToolStripMenuItem _brightness = new("Parlaklık");
    readonly ToolStripMenuItem _effects = new("Efekt");
    readonly ToolStripMenuItem _autonomous;

    readonly ToolStripMenuItem _surroundMenu = new("7.1 Surround");
    readonly ToolStripMenuItem _surroundEnabled;
    readonly ToolStripMenuItem _surroundStatus = new() { Enabled = false };
    readonly ToolStripMenuItem _surroundExclusive;
    readonly List<ToolStripMenuItem> _hrirItems = [];

    readonly ToolStripMenuItem _soundMenu = new("Kulaklık sesi");
    readonly ToolStripMenuItem _eq = new("EQ");
    readonly ToolStripMenuItem _sidetone = new("Sidetone (kendi sesin)");
    readonly ToolStripMenuItem _powerSaving = new("Güç tasarrufu");
    readonly ToolStripMenuItem _micMute;

    readonly ToolStripMenuItem _profiles = new("Profiller");
    readonly ToolStripMenuItem _startup;
    readonly ToolStripMenuItem _notifications;

    Icon? _icon;
    bool _wasLinked;
    bool _wasCharging;
    int? _lastBattery;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        // Creating the first control installs the WinForms synchronization context.
        _ui = SynchronizationContext.Current!;

        _enabled = new ToolStripMenuItem("Işıklar açık", null, (_, _) => ToggleLights());
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
        _surroundEnabled = new ToolStripMenuItem("Açık", null, (_, _) => ToggleSurround());
        _surroundExclusive = new ToolStripMenuItem("Özel mod (en düşük gecikme)", null,
            (_, _) => ChangeSurround(s => s.SurroundExclusive = !s.SurroundExclusive));
        BuildSurroundMenu();

        _micMute = new ToolStripMenuItem("Mikrofonu sustur", null, (_, _) => ToggleMicMute());
        BuildSoundMenu();

        _startup = new ToolStripMenuItem("Windows ile başlat", null, (_, _) => ToggleStartup());
        _notifications = new ToolStripMenuItem("Bildirimler", null, (_, _) =>
        {
            _settings.Notifications = !_settings.Notifications;
            _settings.Save();
            SyncMenu();
        });

        menu.Items.AddRange([
            _status,
            _hint,
            new ToolStripSeparator(),
            _enabled,
            _colors,
            _brightness,
            _effects,
            _autonomous,
            new ToolStripSeparator(),
            _surroundMenu,
            _soundMenu,
            _micMute,
            new ToolStripSeparator(),
            _profiles,
            _startup,
            _notifications,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Çıkış", null, (_, _) => ExitThread()),
        ]);

        // The latency estimate and the saved profiles change on their own; refresh them on open.
        menu.Opening += (_, _) =>
        {
            _surroundStatus.Text = $"Durum: {_surround.Status}";
            BuildProfilesMenu();
        };

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ToggleLights();

        RegisterHotkey(Keys.Control | Keys.Alt | Keys.L, _enabled, ToggleLights);
        RegisterHotkey(Keys.Control | Keys.Alt | Keys.S, _surroundEnabled, ToggleSurround);
        RegisterHotkey(Keys.Control | Keys.Alt | Keys.M, _micMute, ToggleMicMute);

        _engine = new LightingEngine(_settings.ToState());
        _headset.StatusChanged += _ => _ui.Post(_ => OnHeadsetChanged(), null);
        _surround.Apply(_settings.Surround, _settings.SurroundProfile, _settings.SurroundExclusive);
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
            _hotkeys.Dispose();
            _surround.Dispose();
            _headset.Dispose();
            _engine.Dispose();
            _tray.Dispose();
            _icon?.Dispose();
        }
        base.Dispose(disposing);
    }

    void RegisterHotkey(Keys keys, ToolStripMenuItem item, Action action)
    {
        if (_hotkeys.Register(keys, action))
            item.ShortcutKeyDisplayString = new KeysConverter().ConvertToString(keys);
    }

    // ---- Lighting ----

    /// <summary>Any lighting choice takes the lamps back from the headset's own effect.</summary>
    void Change(Action<AppSettings> change)
    {
        change(_settings);
        _settings.Autonomous = false;
        CommitLighting();
    }

    void CommitLighting()
    {
        _settings.Save();
        _engine.Apply(_settings.ToState());
        SyncMenu();
    }

    void ToggleLights() => Change(s => s.Enabled = !s.Enabled);

    void ToggleAutonomous()
    {
        _settings.Autonomous = !_settings.Autonomous;
        CommitLighting();
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

    // ---- 7.1 ----

    void BuildSurroundMenu()
    {
        var profiles = HrirLibrary.List();
        ToolStripMenuItem Item((string Name, string Description) p) =>
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
                    var item = Item(favorite);
                    _hrirItems.Add(item);
                    _surroundMenu.DropDownItems.Add(item);
                }
            }
            var all = new ToolStripMenuItem("Tüm profiller");
            foreach (var profile in profiles)
            {
                var item = Item(profile);
                _hrirItems.Add(item);
                all.DropDownItems.Add(item);
            }
            _surroundMenu.DropDownItems.Add(all);
        }
        _surroundMenu.DropDownItems.AddRange([new ToolStripSeparator(), _surroundExclusive]);
    }

    void ToggleSurround() => ChangeSurround(s => s.Surround = !s.Surround);

    void ChangeSurround(Action<AppSettings> change)
    {
        change(_settings);
        _settings.Save();
        _surround.Apply(_settings.Surround, _settings.SurroundProfile, _settings.SurroundExclusive);
        SyncMenu();
    }

    // ---- Headset sound ----

    void BuildSoundMenu()
    {
        foreach (var (name, preset) in EqPresets)
            _eq.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => _headset.Set(HeadsetProperty.EqPreset, (byte)preset)) { Tag = preset });
        _eq.DropDownItems.Add(new ToolStripSeparator());
        _eq.DropDownItems.Add(new ToolStripMenuItem("Özel EQ'yu düzenle…", null, (_, _) => EditEq()));

        foreach (var percent in SidetoneSteps)
            _sidetone.DropDownItems.Add(new ToolStripMenuItem(percent == 0 ? "Kapalı" : $"%{percent}", null, (_, _) => SetSidetone(percent)) { Tag = percent });

        foreach (var minutes in PowerSavingMinutes)
            _powerSaving.DropDownItems.Add(new ToolStripMenuItem(minutes == 0 ? "Kapalı" : $"{minutes} dakika", null,
                (_, _) => _headset.Set(HeadsetProperty.PowerSaving, (byte)minutes)) { Tag = minutes });

        _soundMenu.DropDownItems.AddRange([_eq, _sidetone, _powerSaving]);
    }

    void EditEq()
    {
        var curve = _headset.Status.EqCurve ?? new sbyte[HeadsetMonitor.EqBands];
        using var form = new EqualizerForm(curve);
        if (form.ShowDialog() != DialogResult.OK)
            return;
        // The curve only plays while the Custom preset is selected.
        _headset.Set([
            (HeadsetProperty.EqPreset, [(byte)EqPreset.Custom]),
            (HeadsetProperty.EqCurve, form.Curve.Select(v => (byte)v).ToArray()),
        ]);
    }

    /// <summary>Synapse maps its percentage slider to the headset's 0–15 scale the same way.</summary>
    void SetSidetone(int percent)
    {
        if (percent == 0)
        {
            _headset.Set(HeadsetProperty.SidetoneEnabled, 0);
            return;
        }
        var level = (byte)Math.Round(percent * HeadsetMonitor.SidetoneMaxLevel / 100.0);
        _headset.Set([(HeadsetProperty.SidetoneLevel, [level]), (HeadsetProperty.SidetoneEnabled, [1])]);
    }

    static int SidetonePercent(int level) => (int)Math.Round(level * 100.0 / HeadsetMonitor.SidetoneMaxLevel);

    void ToggleMicMute()
    {
        try
        {
            if (CoreAudio.FindEndpoint(Audio.DataFlow.Capture, SurroundController.HeadsetName) is { } microphone)
            {
                var muted = CoreAudio.ToggleMute(microphone);
                if (_settings.Notifications)
                    Notify("Mikrofon", muted ? "Susturuldu" : "Açıldı");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            Notify("Mikrofon", $"Susturulamadı: {ex.Message}");
        }
    }

    // ---- Profiles, startup, notifications ----

    void BuildProfilesMenu()
    {
        _profiles.DropDownItems.Clear();
        foreach (var name in _settings.Profiles.Keys.Order(StringComparer.CurrentCultureIgnoreCase))
            _profiles.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => ApplyProfile(_settings.Profiles[name])));
        if (_settings.Profiles.Count > 0)
            _profiles.DropDownItems.Add(new ToolStripSeparator());
        _profiles.DropDownItems.Add(new ToolStripMenuItem("Şu anki ayarları kaydet…", null, (_, _) => SaveProfile()));
        if (_settings.Profiles.Count > 0)
        {
            var delete = new ToolStripMenuItem("Sil");
            foreach (var name in _settings.Profiles.Keys.Order(StringComparer.CurrentCultureIgnoreCase))
                delete.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
                {
                    _settings.Profiles.Remove(name);
                    _settings.Save();
                }));
            _profiles.DropDownItems.Add(delete);
        }
    }

    void SaveProfile()
    {
        if (TextPrompt.Show("Profil kaydet", "Profil adı:") is not { } name)
            return;
        var headset = _headset.Status;
        _settings.Profiles[name] = new HeadsetProfile
        {
            Enabled = _settings.Enabled,
            Color = _settings.Color,
            Brightness = _settings.Brightness,
            Effect = _settings.Effect,
            Autonomous = _settings.Autonomous,
            Surround = _settings.Surround,
            SurroundProfile = _settings.SurroundProfile,
            Eq = headset.Eq,
            EqCurve = headset.EqCurve,
            SidetoneOn = headset.SidetoneOn,
            SidetoneLevel = headset.SidetoneLevel,
        };
        _settings.Save();
        Notify("Profil kaydedildi", name);
    }

    void ApplyProfile(HeadsetProfile profile)
    {
        _settings.Enabled = profile.Enabled;
        _settings.Color = profile.Color;
        _settings.Brightness = profile.Brightness;
        _settings.Effect = profile.Effect;
        _settings.Autonomous = profile.Autonomous;
        CommitLighting();
        ChangeSurround(s =>
        {
            s.Surround = profile.Surround;
            s.SurroundProfile = profile.SurroundProfile;
        });

        var writes = new List<(HeadsetProperty, byte[])>();
        if (profile.Eq is { } eq)
            writes.Add((HeadsetProperty.EqPreset, [(byte)eq]));
        if (profile.Eq == EqPreset.Custom && profile.EqCurve is { Length: HeadsetMonitor.EqBands } curve)
            writes.Add((HeadsetProperty.EqCurve, curve.Select(v => (byte)v).ToArray()));
        if (profile.SidetoneLevel is { } level)
            writes.Add((HeadsetProperty.SidetoneLevel, [(byte)level]));
        if (profile.SidetoneOn is { } on)
            writes.Add((HeadsetProperty.SidetoneEnabled, [on ? (byte)1 : (byte)0]));
        if (writes.Count > 0)
            _headset.Set(writes);
    }

    void ToggleStartup()
    {
        try
        {
            StartupRegistration.Set(!StartupRegistration.IsEnabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Notify("Windows ile başlat", ex.Message);
        }
        SyncMenu();
    }

    void Notify(string title, string text) => _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    void OnHeadsetChanged()
    {
        var status = _headset.Status;
        var linked = status.Linked == true;
        // The headset drops host colors while away, so repaint the moment it links up again.
        if (linked && !_wasLinked)
            _engine.Refresh();
        _wasLinked = linked;

        if (linked && status.Battery is { } battery)
        {
            if (_settings.Notifications && !status.Charging
                && LowBatteryLevels.FirstOrDefault(l => battery <= l && (_lastBattery is null || _lastBattery > l)) is > 0)
                Notify("Kulaklığın pili azaldı", $"%{battery} pil kaldı.");
            // "Charging" drops to false the moment the battery is full, with the cable still in.
            if (_settings.Notifications && _wasCharging && !status.Charging && battery >= 100)
                Notify("Şarj tamamlandı", "Kulaklığın pili dolu.");
            _lastBattery = battery;
            _wasCharging = status.Charging;
        }
        SyncMenu();
    }

    // ---- Menu state ----

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
                + (headset.MicMuted == true ? " · mikrofon kapalı" : "")
                + (headset.DoNotDisturb == true ? " · rahatsız etme" : "");
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
        foreach (var item in _hrirItems)
            item.Checked = item.Tag as string == _settings.SurroundProfile;
        _surroundMenu.Text = _settings.Surround ? "7.1 Surround · açık" : "7.1 Surround";

        // Headset settings only exist while the headset is reachable.
        _soundMenu.Enabled = connected;
        _micMute.Enabled = connected;
        _micMute.Checked = headset.MicMuted == true;
        foreach (var item in _eq.DropDownItems.OfType<ToolStripMenuItem>())
            item.Checked = item.Tag is EqPreset preset && preset == headset.Eq;
        var sidetone = headset.SidetoneOn == false ? 0 : headset.SidetoneLevel is { } st ? SidetonePercent(st) : -1;
        foreach (var item in _sidetone.DropDownItems.OfType<ToolStripMenuItem>())
            item.Checked = item.Tag is int percent && Math.Abs(percent - sidetone) < 10;
        foreach (var item in _powerSaving.DropDownItems.OfType<ToolStripMenuItem>())
            item.Checked = item.Tag is int minutes && minutes == headset.PowerSavingMinutes;

        _startup.Checked = StartupRegistration.IsEnabled;
        _notifications.Checked = _settings.Notifications;

        var state = !connected ? _status.Text
            : _settings.Autonomous ? "kulaklığın efekti"
            : !_settings.Enabled ? "ışıklar kapalı"
            : $"{Effects.First(e => e.Effect == _settings.Effect).Name}, %{_settings.Brightness}";
        if (connected && headset.Battery is { } charge)
            state = $"pil %{charge} · {state}";
        if (_settings.Surround)
            state += " · 7.1";
        _tray.Text = Truncate($"Barracuda Tray: {state}", 127);

        var old = _icon;
        _icon = DrawIcon(connected);
        _tray.Icon = _icon;
        old?.Dispose();
    }

    static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

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
