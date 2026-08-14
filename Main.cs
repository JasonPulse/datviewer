using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Vellichor.Dat;

namespace DatViewer;

/// <summary>
/// FFXI DAT browser/viewer. Two jobs:
///   1. Browse a retail install's ROM tree so you can see WHERE a DAT lives — its ROM-relative
///      path (e.g. ROM/9/2.DAT) is the path you mirror under your XIPivot overlay folder.
///   2. Open ANY .DAT (retail or user-made) and preview its contents — decoded textures, a 3D
///      model view, the raw chunk list, and a hex dump — so you can identify what a file holds.
/// All decoding is reused from Vellichor.Dat (no duplication).
/// </summary>
public partial class Main : Control
{
    private DatArchive? _archive;
    private Vellichor.Render.ModelResolver? _resolver;
    private string _root = "";
    private readonly Dictionary<string, int> _pathToId = new(StringComparer.OrdinalIgnoreCase);

    // persisted settings (user://settings.cfg): the FFXI install root + UI scale + last browse dir
    private string _savedRoot = "";
    private string _lastOpenDir = "";
    private float _uiScale = 1.5f;
    private Control? _settingsOverlay;
    private Label? _settingsStatus;
    private LineEdit? _settingsRootEdit;
    private const string SettingsPath = "user://settings.cfg";

    // last-loaded DAT (so the model view can rebuild when the wear controls change)
    private byte[]? _lastData;
    private List<DatChunk> _lastChunks = new();

    // "wear equipment on a base body" controls (Model tab)
    private CheckBox _wearChk = null!;
    private OptionButton _raceOpt = null!;
    private OptionButton _slotOpt = null!;

    // left / browser
    private Tree _tree = null!;
    private Label _rootLabel = null!;
    private LineEdit _idBox = null!;

    // named "Library" browser (AltanaViewer CSV catalog: PC→race→slot→item, NPC→family→creature…)
    private AltanaCatalog? _catalog;
    private OptionButton _libCat = null!, _libGroup = null!, _libSlot = null!;
    private Label _libGroupLabel = null!, _libSlotLabel = null!;
    private LineEdit _libSearch = null!;
    private Tree _libTree = null!;
    private List<LibEntry> _libEntries = new();      // current list, unfiltered
    // Resolved ABSOLUTE paths of the last Library selection. A composite (creature / worn set) is
    // several DATs; the primary is previewed here and the full list is exposed for the render pass.
    private List<string> _selectedParts = new();
    public IReadOnlyList<string> SelectedParts => _selectedParts;
    private string _libLabel = "";   // current Library item name — so a wear-race change reloads THAT race's version

    // right / info + preview
    private RichTextLabel _info = null!;
    private TabContainer _tabs = null!;
    private GridContainer _texGrid = null!;
    private RichTextLabel _chunkText = null!;
    private RichTextLabel _hexText = null!;
    private Label _modelInfo = null!;

    // music player (bottom bar) — FFXI .bgw playback
    private AudioStreamPlayer _audio = null!;
    private PanelContainer _musicBar = null!;
    private Label _musicNow = null!, _musicTime = null!;
    private Button _musicPlayBtn = null!;
    private HSlider _musicSeek = null!;
    private double _musicLenSec;
    private bool _musicSeekProgrammatic;

    // animation playback (Model tab) — drives Vellichor's AnimationDriver from our own clock so the
    // clip/play/pause/speed/scrub controls all read consistently (the driver just poses at a time T).
    private Vellichor.Render.CharacterModel? _animCm;
    private Vellichor.Render.AnimationDriver? _animDriver;
    private Skeleton3D? _animSkel;
    private Control _animRow = null!;
    private OptionButton _clipOpt = null!;
    private Button _animPlayBtn = null!;
    private CheckBox _animLoopChk = null!;
    private HSlider _animSpeedSld = null!, _animSeek = null!;
    private Label _animTime = null!;
    private double _animClock, _animDuration;
    private int _animFrames;
    private float _animFps = 30f, _animSpeed = 1f;
    private bool _animPlaying, _animScrubbing, _animSeekProg;

    // 3d preview
    private SubViewport _viewport = null!;
    private Camera3D _cam = null!;
    private Node3D _modelRoot = null!;
    private float _yaw = 0.6f, _pitch = 0.5f, _dist = 6f;
    private Vector3 _focus = Vector3.Zero;
    private bool _dragging;

    public override void _Ready()
    {
        LoadSettings();

        // Scale the whole UI up (hi-DPI / Retina makes native-pixel fonts read tiny). One knob that
        // uniformly scales every font + control. Priority: env override > saved setting > 1.5 default.
        float uiScale = float.TryParse(System.Environment.GetEnvironmentVariable("DATVIEWER_UISCALE"), out var us)
            ? us : _uiScale;
        _uiScale = uiScale;
        GetWindow().ContentScaleFactor = uiScale;

        BuildUi();

        // Choose the install root: env override > saved setting > the bundled Vellichor corpus
        // (dev only; absent from a released build). If none is valid, onboard via the Settings page.
        string? def = System.Environment.GetEnvironmentVariable("DATVIEWER_ROOT");
        if (string.IsNullOrEmpty(def) || !Directory.Exists(def)) def = _savedRoot;
        if (string.IsNullOrEmpty(def) || !Directory.Exists(def))
        {
            string corpus = ProjectSettings.GlobalizePath("res://../Vellichor/corpus");
            if (Directory.Exists(corpus)) def = corpus;
        }
        if (!string.IsNullOrEmpty(def) && Directory.Exists(def)) SetRoot(def);
        else Callable.From(() => OpenSettings(firstRun: true)).CallDeferred(); // no DATs yet → onboard

        // Optional: open a file straight away (DATVIEWER_OPEN=/path or a headless screenshot run).
        // Pre-select wear race/slot (scripting / screenshots).
        if (int.TryParse(System.Environment.GetEnvironmentVariable("DATVIEWER_RACE"), out var rid))
            for (int i = 0; i < _raceOpt.ItemCount; i++) if (_raceOpt.GetItemId(i) == rid) _raceOpt.Selected = i;
        string? slotEnv = System.Environment.GetEnvironmentVariable("DATVIEWER_SLOT");
        if (!string.IsNullOrEmpty(slotEnv))
            for (int i = 0; i < _slotOpt.ItemCount; i++) if (_slotOpt.GetItemText(i) == slotEnv) _slotOpt.Selected = i;

        string? open = System.Environment.GetEnvironmentVariable("DATVIEWER_OPEN");
        if (!string.IsNullOrEmpty(open) && File.Exists(open)) LoadDat(open);
        if (int.TryParse(System.Environment.GetEnvironmentVariable("DATVIEWER_TAB"), out var tb)) _tabs.CurrentTab = tb;

        if (System.Environment.GetEnvironmentVariable("DATVIEWER_TREETEST") is not null)
            Callable.From(TreeTest).CallDeferred();

        if (System.Environment.GetEnvironmentVariable("DATVIEWER_SETTINGS") is not null)
            Callable.From(() => OpenSettings(firstRun: false)).CallDeferred();

        if (System.Environment.GetEnvironmentVariable("DATVIEWER_BGWSELFTEST") is not null)
            Callable.From(BgwSelfTest).CallDeferred();

        if (System.Environment.GetEnvironmentVariable("DATVIEWER_ANIMTEST") is { } at)
            Callable.From(() => AnimTest(at)).CallDeferred();

        if (int.TryParse(System.Environment.GetEnvironmentVariable("DATVIEWER_NAV"), out var navi))
            _navTabs.CurrentTab = navi; // 0 Library, 1 Animation, 2 Music, 3 ROM Tree

        // Headless check of the Library select→resolve→load path (DATVIEWER_LIBTEST=cat/group/slot).
        if (System.Environment.GetEnvironmentVariable("DATVIEWER_LIBTEST") is { } lt)
            Callable.From(() => LibTest(lt)).CallDeferred();

        // Headless check of the wear-race change path (DATVIEWER_WEARRACE=<id>): after loading, simulate the
        // "Wear on body → Race" dropdown changing, which should reload that race's OWN version of the item.
        if (int.TryParse(System.Environment.GetEnvironmentVariable("DATVIEWER_WEARRACE"), out var wrid))
            Callable.From(() =>
            {
                for (int i = 0; i < _raceOpt.ItemCount; i++) if (_raceOpt.GetItemId(i) == wrid) _raceOpt.Selected = i;
                OnWearRaceChanged();
            }).CallDeferred();

        string? shot = System.Environment.GetEnvironmentVariable("DATVIEWER_SHOT");
        if (!string.IsNullOrEmpty(shot))
        {
            int frame = int.TryParse(System.Environment.GetEnvironmentVariable("DATVIEWER_SHOT_FRAME"), out var fr) ? fr : 30;
            ShotAfter(shot, frame);
        }
    }

    // ---- UI construction -------------------------------------------------------------------

    private void BuildUi()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = UiTheme.Build();
        AddChild(new ColorRect { Color = UiTheme.Bg0, MouseFilter = MouseFilterEnum.Ignore, AnchorRight = 1, AnchorBottom = 1 });

        // outer padding so panels don't touch the window edge
        var pad = new MarginContainer();
        pad.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var m in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(m, 10);
        AddChild(pad);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 8);
        pad.AddChild(outer);

        var split = new HSplitContainer { SplitOffset = 360, SizeFlagsVertical = SizeFlags.ExpandFill };
        outer.AddChild(split);

        BuildMusicBar(outer); // bottom "now playing" bar for FFXI music

        // ---- left: browsers ----
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
        left.AddThemeConstantOverride("separation", 8);
        split.AddChild(left);

        // title
        var title = new Label { Text = "FFXI  DAT  VIEWER" };
        title.AddThemeFontSizeOverride("font_size", 15);
        title.AddThemeColorOverride("font_color", UiTheme.Gold);
        left.AddChild(title);

        // global toolbar (open a file / set the install root)
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 6);
        left.AddChild(btnRow);
        var openBtn = new Button { Text = "Open .DAT…" };
        openBtn.Pressed += OpenFileDialog;
        btnRow.AddChild(openBtn);
        var settingsBtn = new Button { Text = "⚙ Settings" };
        settingsBtn.Pressed += () => OpenSettings(firstRun: false);
        btnRow.AddChild(settingsBtn);

        _rootLabel = new Label { Text = "(no install root)", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _rootLabel.AddThemeFontSizeOverride("font_size", 11);
        _rootLabel.AddThemeColorOverride("font_color", UiTheme.Muted);
        left.AddChild(_rootLabel);

        // two navigation modes: the AltanaViewer-style named Library, and the raw ROM tree
        var navTabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        _navTabs = navTabs;
        left.AddChild(navTabs);

        // -- Library tab (named, categorised) --
        var libTab = new VBoxContainer { Name = "Library" };
        libTab.AddThemeConstantOverride("separation", 6);
        navTabs.AddChild(libTab);
        BuildLibraryPanel(libTab);

        // -- Animation tab (named actions per race) --
        var animTab = new VBoxContainer { Name = "Animation" };
        animTab.AddThemeConstantOverride("separation", 6);
        navTabs.AddChild(animTab);
        BuildAnimationTab(animTab);

        // -- Music tab (FFXI .bgw tracks by expansion) --
        var musicTab = new VBoxContainer { Name = "Music" };
        musicTab.AddThemeConstantOverride("separation", 6);
        navTabs.AddChild(musicTab);
        BuildMusicTab(musicTab);

        // -- ROM Tree tab (raw folders) --
        var treeTab = new VBoxContainer { Name = "ROM Tree" };
        treeTab.AddThemeConstantOverride("separation", 6);
        navTabs.AddChild(treeTab);

        var idRow = new HBoxContainer();
        treeTab.AddChild(idRow);
        idRow.AddChild(new Label { Text = "file id:" });
        _idBox = new LineEdit { PlaceholderText = "e.g. 52795", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _idBox.TextSubmitted += _ => GoToId();
        idRow.AddChild(_idBox);
        var goBtn = new Button { Text = "Go" };
        goBtn.Pressed += GoToId;
        idRow.AddChild(goBtn);

        _tree = new Tree { SizeFlagsVertical = SizeFlags.ExpandFill, HideRoot = true };
        _tree.ItemSelected += OnTreeItemSelected;
        _tree.ItemCollapsed += OnTreeItemCollapsed;
        treeTab.AddChild(_tree);

        // ---- right: info + preview ----
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 8);
        split.AddChild(right);

        _info = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, CustomMinimumSize = new Vector2(0, 96),
            SelectionEnabled = true,
        };
        _info.Text = "[i]Select a DAT in the tree, open a file, or enter a file id.[/i]";
        right.AddChild(_info);

        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddChild(_tabs);

        // Textures tab
        var texScroll = new ScrollContainer { Name = "Textures", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _texGrid = new GridContainer { Columns = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        texScroll.AddChild(_texGrid);
        _tabs.AddChild(texScroll);

        // Model tab (3D)
        var modelBox = new VBoxContainer { Name = "Model", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };

        // "wear on a base body" controls — an equipment part alone is just the garment; this assembles
        // it onto a chosen race's naked body (skeleton + head/hands/legs/feet) so you see it worn.
        var wearRow = new HBoxContainer();
        _wearChk = new CheckBox { Text = "Wear on body", ButtonPressed = true };
        _wearChk.Toggled += _ => BuildModelView();
        wearRow.AddChild(_wearChk);
        wearRow.AddChild(new Label { Text = "  Race:" });
        _raceOpt = new OptionButton();
        foreach (var (name, id) in new[] { ("Hume ♂", 1), ("Hume ♀", 2), ("Elvaan ♂", 3), ("Elvaan ♀", 4),
                                           ("Tarutaru ♂", 5), ("Tarutaru ♀", 6), ("Mithra", 7), ("Galka", 8) })
            _raceOpt.AddItem(name, id);
        _raceOpt.ItemSelected += _ => OnWearRaceChanged();
        wearRow.AddChild(_raceOpt);
        wearRow.AddChild(new Label { Text = "  Slot:" });
        _slotOpt = new OptionButton();
        foreach (var s in new[] { "body", "head", "hands", "legs", "feet" }) _slotOpt.AddItem(s);
        _slotOpt.ItemSelected += _ => BuildModelView();
        wearRow.AddChild(_slotOpt);
        modelBox.AddChild(wearRow);

        // animation controls — clip picker + transport (shown only when the model has 0x2b clips)
        var animRow = new HBoxContainer { Visible = false };
        animRow.AddThemeConstantOverride("separation", 6);
        _animRow = animRow;
        animRow.AddChild(new Label { Text = "Anim:" });
        _clipOpt = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
        _clipOpt.ItemSelected += _ => PlaySelectedClip(resetClock: true);
        animRow.AddChild(_clipOpt);
        _animPlayBtn = new Button { Text = "❚❚", CustomMinimumSize = new Vector2(40, 0) };
        _animPlayBtn.Pressed += ToggleAnim;
        animRow.AddChild(_animPlayBtn);
        _animLoopChk = new CheckBox { Text = "loop", ButtonPressed = true };
        animRow.AddChild(_animLoopChk);
        animRow.AddChild(new Label { Text = "speed" });
        _animSpeedSld = new HSlider { MinValue = 0.1, MaxValue = 2, Step = 0.05, Value = 1, CustomMinimumSize = new Vector2(80, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _animSpeedSld.ValueChanged += v => _animSpeed = (float)v;
        animRow.AddChild(_animSpeedSld);
        _animSeek = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.001, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _animSeek.DragStarted += () => _animScrubbing = true;
        _animSeek.DragEnded += _ => _animScrubbing = false;
        _animSeek.ValueChanged += v =>
        {
            if (_animSeekProg) return;
            _animClock = v * _animDuration;
            _animDriver?.Seek((float)_animClock);
        };
        animRow.AddChild(_animSeek);
        _animTime = new Label { Text = "", CustomMinimumSize = new Vector2(72, 0) };
        _animTime.AddThemeColorOverride("font_color", UiTheme.Muted);
        animRow.AddChild(_animTime);
        modelBox.AddChild(animRow);

        _modelInfo = new Label { Text = "" };
        modelBox.AddChild(_modelInfo);
        var vpc = new SubViewportContainer { Stretch = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        vpc.GuiInput += OnViewportInput;
        modelBox.AddChild(vpc);
        _viewport = new SubViewport { RenderTargetUpdateMode = SubViewport.UpdateMode.Always, Size = new Vector2I(800, 600) };
        vpc.AddChild(_viewport);
        BuildViewportScene();
        _tabs.AddChild(modelBox);

        // Chunks tab
        var chunkScroll = new ScrollContainer { Name = "Chunks", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _chunkText = new RichTextLabel { BbcodeEnabled = true, FitContent = true, SelectionEnabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        chunkScroll.AddChild(_chunkText);
        _tabs.AddChild(chunkScroll);

        // Hex tab
        var hexScroll = new ScrollContainer { Name = "Hex", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _hexText = new RichTextLabel { BbcodeEnabled = false, FitContent = true, SelectionEnabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _hexText.AddThemeFontSizeOverride("normal_font_size", 12);
        hexScroll.AddChild(_hexText);
        _tabs.AddChild(hexScroll);
    }

    private void BuildViewportScene()
    {
        var world = new Node3D();
        _viewport.AddChild(world);
        _cam = new Camera3D { Position = new Vector3(0, 2, 6) };
        world.AddChild(_cam);
        world.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, -40, 0) });
        var fill = new DirectionalLight3D { RotationDegrees = new Vector3(-20, 140, 0), LightEnergy = 0.4f };
        world.AddChild(fill);
        _modelRoot = new Node3D();
        world.AddChild(_modelRoot);
    }

    // ---- music player (FFXI .bgw) ----------------------------------------------------------

    private void BuildMusicBar(Control host)
    {
        _audio = new AudioStreamPlayer();
        AddChild(_audio);
        _audio.Finished += () => _musicPlayBtn.Text = "▶";

        _musicBar = new PanelContainer { Visible = false };
        host.AddChild(_musicBar);
        var pad = new MarginContainer();
        foreach (var m in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(m, 8);
        _musicBar.AddChild(pad);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        pad.AddChild(row);

        _musicPlayBtn = new Button { Text = "▶", CustomMinimumSize = new Vector2(44, 0) };
        _musicPlayBtn.Pressed += ToggleMusic;
        row.AddChild(_musicPlayBtn);
        var stopBtn = new Button { Text = "■" };
        stopBtn.Pressed += () => { _audio.Stop(); _musicPlayBtn.Text = "▶"; };
        row.AddChild(stopBtn);

        _musicNow = new Label { Text = "", CustomMinimumSize = new Vector2(220, 0), ClipText = true };
        _musicNow.AddThemeColorOverride("font_color", UiTheme.Gold);
        row.AddChild(_musicNow);

        _musicSeek = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.0005, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _musicSeek.ValueChanged += v =>
        {
            if (_musicSeekProgrammatic || _audio.Stream is null) return;
            _audio.Seek((float)(v * _musicLenSec)); // user dragged the scrubber
        };
        row.AddChild(_musicSeek);

        _musicTime = new Label { Text = "0:00 / 0:00", CustomMinimumSize = new Vector2(92, 0) };
        _musicTime.AddThemeColorOverride("font_color", UiTheme.Muted);
        row.AddChild(_musicTime);

        row.AddChild(new Label { Text = "🔊" });
        var vol = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.01, Value = 0.8, CustomMinimumSize = new Vector2(90, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        vol.ValueChanged += v => _audio.VolumeDb = v <= 0.001 ? -80f : Mathf.LinearToDb((float)v);
        _audio.VolumeDb = Mathf.LinearToDb(0.8f);
        row.AddChild(vol);
    }

    private void ToggleMusic()
    {
        if (_audio.Stream is null) return;
        if (!_audio.Playing) _audio.Play();
        else _audio.StreamPaused = !_audio.StreamPaused;
        _musicPlayBtn.Text = _audio.Playing && !_audio.StreamPaused ? "❚❚" : "▶";
    }

    private void PlayMusic(string name, BgwAudio a)
    {
        var wav = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = a.SampleRate,
            Stereo = a.Channels == 2,
            Data = a.Pcm,
        };
        if (a.LoopStartSample >= 0)
        {
            wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            wav.LoopBegin = a.LoopStartSample;
            wav.LoopEnd = a.SampleCount;
        }
        _audio.Stream = wav;
        _audio.StreamPaused = false;
        _musicLenSec = a.Seconds;
        _audio.Play();
        _musicBar.Visible = true;
        _musicNow.Text = "♪ " + name;
        _musicPlayBtn.Text = "❚❚";
        _musicTime.Text = $"0:00 / {FmtTime(_musicLenSec)}";
    }

    /// Play a Music-tab entry. Its ref is a bare track number (e.g. "034"); the file lives at
    /// <c>&lt;install&gt;/&lt;soundN&gt;/win/music/data/music&lt;id&gt;.bgw</c>.
    private void PlayMusicEntry(LibEntry e, string folder)
    {
        string id = e.RefToken.Trim();
        string path = Path.Combine(_root, folder, "win", "music", "data", $"music{id}.bgw");
        _info.Clear();
        if (string.IsNullOrEmpty(_root) || !File.Exists(path))
        {
            Flash($"'{e.Label}': no music file at {folder}/win/music/data/music{id}.bgw under your FFXI install");
            return;
        }
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex) { Flash("read failed: " + ex.Message); return; }

        var r = BgwDecoder.TryDecode(data, out var audio);
        _info.AppendText($"[b][color=#8fd0ff]{Path.GetFileName(path)}[/color][/b]   [color=#aaa]({FormatBytes(data.Length)})[/color]\n");
        _info.AppendText($"[color=#e8c877]music:[/color] [b]{e.Label}[/b]   ·   {folder}\n");
        switch (r)
        {
            case BgwDecoder.Result.Ok:
                PlayMusic(e.Label, audio!);
                _info.AppendText($"[color=#7c7]playing · {audio!.SampleRate} Hz · {(audio.Channels == 2 ? "stereo" : "mono")} · {FmtTime(audio.Seconds)}[/color]");
                break;
            case BgwDecoder.Result.Atrac3Unsupported:
                _info.AppendText("[color=#f88]ATRAC3 audio (Aht Urhgan and later) — not playable, just like the original Altana Viewer. Only sound/sound2/sound3 (ADPCM) tracks play.[/color]");
                break;
            default:
                _info.AppendText($"[color=#f88]could not decode this .bgw ({r}).[/color]");
                break;
        }
    }

    private static string FmtTime(double sec)
    {
        if (sec < 0 || double.IsNaN(sec)) sec = 0;
        int t = (int)sec;
        return $"{t / 60}:{t % 60:00}";
    }

    // Structural check of the BGW decoder + player bar (DATVIEWER_BGWSELFTEST): decode a synthetic
    // codec-0 stream (no real .bgw ships here) and show it in the player. Not an audio-fidelity test.
    private void BgwSelfTest()
    {
        const int blockAlign = 8, blockSize = 400, sampleRate = 22050;
        int frameSize = blockAlign / 2 + 1;
        var buf = new byte[0x30 + blockSize * frameSize];
        foreach (var (i, ch) in "BGMStream".Select((c, i) => (i, c))) buf[i] = (byte)ch;
        void W32(int o, uint v) { buf[o] = (byte)v; buf[o + 1] = (byte)(v >> 8); buf[o + 2] = (byte)(v >> 16); buf[o + 3] = (byte)(v >> 24); }
        W32(0x0c, 0);                    // codec 0 (PSX ADPCM)
        W32(0x10, (uint)buf.Length);     // file size
        W32(0x18, blockSize);
        W32(0x20, sampleRate); W32(0x24, 0);
        W32(0x28, 0x30);                 // start offset
        buf[0x2e] = 1;                   // mono
        buf[0x2f] = blockAlign;
        for (int f = 0; f < blockSize; f++)
        {
            int o = 0x30 + f * frameSize;
            buf[o] = 0x08;               // coef 0, shift 8 → gentle
            for (int b = 1; b < frameSize; b++) buf[o + b] = (byte)((f * 7 + b * 3) & 0xff); // arbitrary waveform
        }
        var r = BgwDecoder.TryDecode(buf, out var a);
        GD.Print($"[bgwtest] result={r} samples={a?.SampleCount} ch={a?.Channels} sr={a?.SampleRate} sec={a?.Seconds:0.00}");
        if (r == BgwDecoder.Result.Ok && a is not null) PlayMusic("Self-test tone (synthetic)", a);
    }

    // ---- Library (named AltanaViewer catalog) ----------------------------------------------

    private Control _slotField = null!;

    private void BuildLibraryPanel(VBoxContainer host)
    {
        _catalog = new AltanaCatalog(AssetDir("List"));

        // Music lives in its own top-level tab, not the Library category list.
        _libCat = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var c in _catalog.Categories.Where(c => c.Name != "Music")) _libCat.AddItem(c.Name);
        _libCat.ItemSelected += _ => OnLibCategory();
        host.AddChild(WrapField("Category", _libCat, out _));

        _libGroup = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _libGroup.ItemSelected += _ => OnLibGroup();
        host.AddChild(WrapField("Race", _libGroup, out _libGroupLabel));

        _libSlot = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _libSlot.ItemSelected += _ => OnLibSlot();
        _slotField = WrapField("Slot", _libSlot, out _libSlotLabel);
        host.AddChild(_slotField);

        _libSearch = new LineEdit { PlaceholderText = "search this list…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _libSearch.TextChanged += _ => PopulateLibraryTree();
        host.AddChild(_libSearch);

        _libTree = new Tree { SizeFlagsVertical = SizeFlags.ExpandFill, HideRoot = true };
        _libTree.ItemSelected += OnLibItemSelected;
        host.AddChild(_libTree);

        if (_libCat.ItemCount > 0) { _libCat.Selected = 0; OnLibCategory(); }
    }

    private static HBoxContainer WrapField(string label, Control ctl, out Label lbl)
    {
        var row = new HBoxContainer();
        lbl = new Label { Text = label, CustomMinimumSize = new Vector2(62, 0) };
        lbl.AddThemeColorOverride("font_color", UiTheme.Muted);
        row.AddChild(lbl);
        row.AddChild(ctl);
        return row;
    }

    // Headless: navigate the Library to "cat/group/slot" and load its first on-disk entry.
    private void LibTest(string spec)
    {
        var p = spec.Split('/');
        void Pick(OptionButton ob, string name, Action after)
        { for (int i = 0; i < ob.ItemCount; i++) if (ob.GetItemText(i) == name) { ob.Selected = i; after(); return; } }
        if (p.Length > 0) Pick(_libCat, p[0], OnLibCategory);
        if (p.Length > 1) Pick(_libGroup, p[1], OnLibGroup);
        if (p.Length > 2) Pick(_libSlot, p[2], OnLibSlot);
        string? want = p.Length > 3 ? p[3] : null; // optional exact item name to select
        foreach (var e in _libEntries)
        {
            if (e.IsHeader || e.Label == "None" || !EntryExists(e)) continue;
            if (want is not null && e.Label != want) continue;
            GD.Print($"[libtest] {spec} -> '{e.Label}' ({e.RefToken}) -> {string.Join(", ", e.RomPaths)}");
            LoadLibraryEntry(e);
            return;
        }
        GD.Print($"[libtest] {spec} -> no loadable entry");
    }

    private LibCategory? CurCat =>
        _catalog is null || _libCat.Selected < 0 ? null
            : _catalog.Categories.FirstOrDefault(c => c.Name == _libCat.GetItemText(_libCat.Selected));

    private void OnLibCategory()
    {
        var cat = CurCat;
        if (cat is null) return;
        _libGroupLabel.Text = cat.GroupLabel;
        _libGroup.Clear();
        foreach (var g in cat.Groups) _libGroup.AddItem(g.Name);
        _slotField.Visible = cat.HasSlots;
        if (cat.Groups.Count > 0) { _libGroup.Selected = 0; OnLibGroup(); }
        else { _libEntries = new(); PopulateLibraryTree(); }
    }

    private void OnLibGroup()
    {
        var cat = CurCat;
        if (cat is null || _libGroup.Selected < 0 || _libGroup.Selected >= cat.Groups.Count) return;
        var g = cat.Groups[_libGroup.Selected];
        if (cat.HasSlots && g.IsPc)
        {
            _libSlot.Clear();
            foreach (var s in _catalog!.SlotsFor(g)) _libSlot.AddItem(s);
            if (_libSlot.ItemCount > 0)
            {
                int def = 0;
                for (int i = 0; i < _libSlot.ItemCount; i++) if (_libSlot.GetItemText(i) == "Body") def = i;
                _libSlot.Selected = def; OnLibSlot(); return;
            }
        }
        _libEntries = _catalog!.EntriesForGroup(g);
        PopulateLibraryTree();
    }

    private void OnLibSlot()
    {
        var cat = CurCat;
        if (cat is null || _libGroup.Selected < 0 || _libSlot.Selected < 0) return;
        var g = cat.Groups[_libGroup.Selected];
        _libEntries = _catalog!.EntriesForPcSlot(g, _libSlot.GetItemText(_libSlot.Selected));
        PopulateLibraryTree();
    }

    private void PopulateLibraryTree() => FillEntryTree(_libTree, _libEntries, _libSearch?.Text, greyMissing: true);

    /// Fill a browser Tree from a list of catalog entries (shared by the Library and Music tabs).
    /// Headers render as gold non-selectable rows; each real row stores its index as metadata.
    /// When greyMissing is set, entries whose DAT isn't on disk under the current root are dimmed.
    private void FillEntryTree(Tree tree, List<LibEntry> entries, string? search, bool greyMissing)
    {
        if (tree is null) return;
        tree.Clear();
        var root = tree.CreateItem();
        string q = search?.Trim() ?? "";
        int shown = 0;
        for (int i = 0; i < entries.Count && shown < 4000; i++)
        {
            var e = entries[i];
            if (e.IsHeader)
            {
                if (q.Length > 0) continue; // hide section headers while filtering
                var h = tree.CreateItem(root);
                h.SetText(0, e.Label);
                h.SetSelectable(0, false);
                h.SetCustomColor(0, UiTheme.Gold);
                continue;
            }
            if (q.Length > 0 && e.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var it = tree.CreateItem(root);
            it.SetText(0, e.Label);
            it.SetMetadata(0, i);
            if (greyMissing && !EntryExists(e)) it.SetCustomColor(0, UiTheme.Muted);
            shown++;
        }
        if (shown == 0)
        {
            var it = tree.CreateItem(root);
            it.SetText(0, _catalog is null ? "  (no List/ catalog found)" : "  (nothing here)");
            it.SetSelectable(0, false);
        }
    }

    // ---- Music tab (FFXI .bgw tracks by expansion / sound folder) ---------------------------

    private TabContainer _navTabs = null!;
    private OptionButton _sndGroup = null!;
    private LineEdit _sndSearch = null!;
    private Tree _sndTree = null!;
    private List<LibEntry> _sndEntries = new();

    // Animation tab (named actions per race, from AltanaViewer Action.csv)
    private OptionButton _actRace = null!;
    private LineEdit _actSearch = null!;
    private Tree _actTree = null!;
    private List<LibEntry> _actEntries = new();
    private List<byte[]>? _extraClipDats; // clip DATs merged when assembling (set by the Animation tab)
    private string? _prefClipName;        // clip to auto-select after assembling (the opened action)
    private List<string>? _restrictClips; // if set, the Anim dropdown lists ONLY these clips (the action's own)

    private LibCategory? MusicCat => _catalog?.Categories.FirstOrDefault(c => c.Name == "Music");
    private LibCategory? PcCat => _catalog?.Categories.FirstOrDefault(c => c.Name == "PC");

    private void BuildMusicTab(VBoxContainer host)
    {
        var mus = MusicCat;
        if (mus is null || mus.Groups.Count == 0) { host.AddChild(new Label { Text = "  (no music catalog)" }); return; }

        _sndGroup = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var g in mus.Groups) _sndGroup.AddItem(g.Name);
        _sndGroup.ItemSelected += _ => OnSndGroup();
        host.AddChild(WrapField("Set", _sndGroup, out _));

        _sndSearch = new LineEdit { PlaceholderText = "search tracks…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _sndSearch.TextChanged += _ => FillEntryTree(_sndTree, _sndEntries, _sndSearch.Text, greyMissing: false);
        host.AddChild(_sndSearch);

        var note = new Label
        {
            Text = "Pick a track to play. sound/sound2/sound3 (ADPCM) play; later sets are ATRAC3 and can't be decoded.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeColorOverride("font_color", UiTheme.Muted);
        note.AddThemeFontSizeOverride("font_size", 11);
        host.AddChild(note);

        _sndTree = new Tree { SizeFlagsVertical = SizeFlags.ExpandFill, HideRoot = true };
        _sndTree.ItemSelected += OnSndItemSelected;
        host.AddChild(_sndTree);

        _sndGroup.Selected = 0;
        OnSndGroup();
    }

    private void OnSndGroup()
    {
        var mus = MusicCat;
        if (mus is null || _sndGroup.Selected < 0 || _sndGroup.Selected >= mus.Groups.Count) return;
        _sndEntries = _catalog!.EntriesForGroup(mus.Groups[_sndGroup.Selected]);
        FillEntryTree(_sndTree, _sndEntries, _sndSearch.Text, greyMissing: false);
    }

    private void OnSndItemSelected()
    {
        var it = _sndTree.GetSelected();
        if (it is null) return;
        var meta = it.GetMetadata(0);
        if (meta.VariantType == Variant.Type.Nil) return;
        int idx = meta.AsInt32();
        if (idx < 0 || idx >= _sndEntries.Count) return;
        PlayMusicEntry(_sndEntries[idx], SndFolder());
    }

    // Sound folder for the selected Music set ("sound", "sound2", …) — first token of its label.
    private string SndFolder()
    {
        var mus = MusicCat;
        if (mus is null || _sndGroup.Selected < 0 || _sndGroup.Selected >= mus.Groups.Count) return "sound";
        string name = mus.Groups[_sndGroup.Selected].Name.Trim();
        int sp = name.IndexOf(' ');
        return sp > 0 ? name[..sp] : name;
    }

    // ---- Animation tab (named actions per race, from Action.csv) -----------------------------

    // PC races that have a non-empty Action.csv and a playable base body (child races / chocobo skip).
    private List<LibGroup> AnimRaces()
    {
        var pc = PcCat;
        if (pc is null) return new();
        return pc.Groups.Where(g => PcRaceId(g.Name) > 0
                                    && new FileInfo(Path.Combine(g.FolderPath, "Action.csv")) is { Exists: true, Length: > 0 })
                        .ToList();
    }

    private void BuildAnimationTab(VBoxContainer host)
    {
        var races = AnimRaces();
        if (races.Count == 0) { host.AddChild(new Label { Text = "  (no animation catalog)" }); return; }

        _actRace = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var g in races) _actRace.AddItem(g.Name);
        _actRace.ItemSelected += _ => OnActRace();
        host.AddChild(WrapField("Race", _actRace, out _));

        _actSearch = new LineEdit { PlaceholderText = "search actions…", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _actSearch.TextChanged += _ => FillEntryTree(_actTree, _actEntries, _actSearch.Text, greyMissing: true);
        host.AddChild(_actSearch);

        var note = new Label
        {
            Text = "Pick an action to play it on that race's body (needs your FFXI install).",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeColorOverride("font_color", UiTheme.Muted);
        note.AddThemeFontSizeOverride("font_size", 11);
        host.AddChild(note);

        _actTree = new Tree { SizeFlagsVertical = SizeFlags.ExpandFill, HideRoot = true };
        _actTree.ItemSelected += OnActItemSelected;
        host.AddChild(_actTree);

        _actRace.Selected = 0;
        OnActRace();
    }

    // Headless: pick a race, then play its first action whose DATs exist under the root.
    private void AnimTest(string race)
    {
        for (int i = 0; i < _actRace.ItemCount; i++)
            if (_actRace.GetItemText(i) == race) { _actRace.Selected = i; OnActRace(); break; }
        var g = CurActRace();
        if (g is null) { GD.Print("[animtest] no race"); return; }
        foreach (var e in _actEntries)
        {
            if (e.IsHeader || !EntryExists(e)) continue;
            GD.Print($"[animtest] {race} -> '{e.Label}' ({e.RefToken}) -> {string.Join(", ", e.RomPaths)}");
            PlayAnimationEntry(e, PcRaceId(g.Name), g.Name);
            AnimDiag(e.Label);
            return;
        }
        GD.Print("[animtest] no loadable action");
    }

    // Diagnostic: which bones does the selected clip actually animate vs the skeleton (ownership of the
    // "legs static / torso-only" issue). Coverage lives entirely in Vellichor's decode + AnimationDriver.
    private void AnimDiag(string action)
    {
        if (_animCm is null || _animSkel is null || _clipOpt.Selected < 0) { GD.Print("[animdiag] no model built"); return; }
        string name = _clipOpt.GetItemText(_clipOpt.Selected);
        if (_animCm.Clip(name) is not { } cc) { GD.Print($"[animdiag] clip '{name}' undecodable"); return; }
        int bones = _animSkel.GetBoneCount();
        var covered = new HashSet<int>();
        foreach (var t in cc.tracks) if (t.Keys.Length > 0) covered.Add(t.Bone);
        var uncovered = new List<string>();
        for (int i = 0; i < bones; i++) if (!covered.Contains(i)) uncovered.Add($"{i}:{_animSkel.GetBoneName(i)}");
        GD.Print($"[animdiag] dropdown lists {_clipOpt.ItemCount} clip(s) for this action");
        GD.Print($"[animdiag] action '{action}' clip '{name}': {cc.tracks.Length} tracks, {covered.Count}/{bones} bones animated, {cc.frames} frames");
        GD.Print($"[animdiag] NOT animated ({uncovered.Count}): {string.Join(", ", uncovered.Take(60))}");
    }

    private LibGroup? CurActRace()
    {
        var races = AnimRaces();
        return _actRace.Selected >= 0 && _actRace.Selected < races.Count ? races[_actRace.Selected] : null;
    }

    private void OnActRace()
    {
        var g = CurActRace();
        if (g is null) return;
        _actEntries = AltanaCatalog.ParseList(Path.Combine(g.FolderPath, "Action.csv"));
        FillEntryTree(_actTree, _actEntries, _actSearch.Text, greyMissing: true);
    }

    private void OnActItemSelected()
    {
        var it = _actTree.GetSelected();
        if (it is null) return;
        var meta = it.GetMetadata(0);
        if (meta.VariantType == Variant.Type.Nil) return;
        int idx = meta.AsInt32();
        var g = CurActRace();
        if (g is null || idx < 0 || idx >= _actEntries.Count) return;
        PlayAnimationEntry(_actEntries[idx], PcRaceId(g.Name), g.Name);
    }

    /// Apply a named action's clip DAT(s) to the race's base body and play it in the Model tab.
    private void PlayAnimationEntry(LibEntry e, int raceId, string raceLabel)
    {
        if (_resolver?.Ready != true) { Flash("set your FFXI install root first (⚙ Settings)"); return; }

        var abs = new List<string>();
        foreach (var rel in e.RomPaths)
        {
            string p = Path.Combine(_root, rel);
            if (File.Exists(p)) abs.Add(p);
        }
        if (abs.Count == 0) { Flash($"'{e.Label}': animation DAT(s) not found under your install (ref {e.RefToken})"); return; }

        var animDats = new List<byte[]>();
        foreach (var p in abs) { try { animDats.Add(File.ReadAllBytes(p)); } catch { } }
        if (animDats.Count == 0) { Flash("read failed"); return; }

        // Route through the normal model path so wear-race changes etc. keep working: the opened "DAT"
        // is the first clip DAT; _extraClipDats carries the full set to merge onto the body.
        _lastData = animDats[0];
        _lastChunks = ChunkReader.Walk(animDats[0]);
        _extraClipDats = animDats;
        // This action's OWN clips (0x2b names across its DATs) — what the Anim dropdown should list.
        _restrictClips = animDats
            .SelectMany(d => ChunkReader.Walk(d).Where(c => c.Type == 0x2b).Select(c => c.Name))
            .Distinct().ToList();
        _prefClipName = _restrictClips.FirstOrDefault();
        for (int i = 0; i < _raceOpt.ItemCount; i++) if (_raceOpt.GetItemId(i) == raceId) _raceOpt.Selected = i;
        _wearChk.ButtonPressed = true;

        PopulateTextures(_lastData, _lastChunks);
        PopulateChunks(_lastChunks);
        PopulateHex(_lastData);
        BuildModelView();
        _tabs.CurrentTab = 1; // Model tab

        _info.Clear();
        _info.AppendText($"[color=#e8c877]animation:[/color] [b]{e.Label}[/b]   ·   {raceLabel}   ·   {abs.Count} DAT(s)   ·   ref {e.RefToken}");
    }

    private bool EntryExists(LibEntry e)
    {
        if (e.RomPaths.Count == 0) return false;
        if (string.IsNullOrEmpty(_root)) return true;
        return e.RomPaths.Any(rel => File.Exists(Path.Combine(_root, rel)));
    }

    private void OnLibItemSelected()
    {
        var it = _libTree.GetSelected();
        if (it is null) return;
        var meta = it.GetMetadata(0);
        if (meta.VariantType == Variant.Type.Nil) return;
        int idx = meta.AsInt32();
        if (idx < 0 || idx >= _libEntries.Count) return;
        LoadLibraryEntry(_libEntries[idx]);
    }

    /// Resolve a Library entry to on-disk DATs, preview the primary, and expose the full part list.
    private void LoadLibraryEntry(LibEntry e)
    {
        var abs = new List<string>();
        foreach (var rel in e.RomPaths)
        {
            string p = string.IsNullOrEmpty(_root) ? rel : Path.Combine(_root, rel);
            if (File.Exists(p)) abs.Add(Path.GetFullPath(p));
        }
        _selectedParts = abs;
        if (abs.Count == 0) { Flash($"'{e.Label}' — no DAT on disk under this root (ref {e.RefToken})"); return; }

        _libLabel = e.IsHeader ? "" : e.Label; // remember the item so a wear-race change reloads ITS per-race DAT
        TrySetWearFromLibrary(); // PC equipment → wear on the matching race body (existing flow)
        LoadDat(abs[0]);         // preview the primary; LoadDat rebuilds the info panel

        _info.AppendText(abs.Count > 1
            ? $"\n[color=#e8c877]library:[/color] [b]{e.Label}[/b] — [b]{abs.Count}[/b] parts (ref {e.RefToken}); previewing part 1. Full list exposed as SelectedParts for the model build."
            : $"\n[color=#e8c877]library:[/color] [b]{e.Label}[/b]   (ref {e.RefToken})");
    }

    // PC equipment part: point the Model-tab wear controls at the right race/slot so it shows worn.
    /// The "Wear on body → Race" dropdown changed. Equipment is PER-RACE, so load THAT race's version of the
    /// currently-selected Library item (each race's mesh matches its own skeleton) instead of re-skinning the
    /// same DAT onto a different skeleton (which explodes it). Falls back to a plain re-skin for a raw-opened DAT.
    private void OnWearRaceChanged()
    {
        if (!string.IsNullOrEmpty(_libLabel))
        {
            string? abs = LibDatForRace(_raceOpt.GetSelectedId());
            if (abs is not null) { LoadDat(abs); return; } // that race's own mesh, on its own skeleton
            // Race-exclusive item (no version for this race): don't slam it onto a mismatched skeleton.
            Flash($"'{_libLabel}' has no {_raceOpt.GetItemText(_raceOpt.Selected)} version — keeping current.");
            return;
        }
        BuildModelView(); // raw-opened DAT (no Library item): honor the explicit race choice
    }

    /// Absolute path of the current Library item's DAT for the given race (via the per-race catalog list), or
    /// null if there's no active Library item or that race has no version on disk.
    private string? LibDatForRace(int raceId)
    {
        if (string.IsNullOrEmpty(_libLabel) || _catalog is null) return null;
        var pc = _catalog.Categories.FirstOrDefault(c => c.HasSlots);
        var grp = pc?.Groups.FirstOrDefault(g => PcRaceId(g.Name) == raceId);
        if (grp is null) return null;
        string slot = _slotOpt.Selected >= 0 ? _slotOpt.GetItemText(_slotOpt.Selected) : "body";
        string slotCap = slot.Length > 0 ? char.ToUpperInvariant(slot[0]) + slot[1..] : "Body";
        var match = _catalog.EntriesForPcSlot(grp, slotCap).FirstOrDefault(e => !e.IsHeader && e.Label == _libLabel);
        if (match is null || match.RomPaths.Count == 0) return null;
        string abs = string.IsNullOrEmpty(_root) ? match.RomPaths[0] : Path.Combine(_root, match.RomPaths[0]);
        return File.Exists(abs) ? Path.GetFullPath(abs) : null;
    }

    private void TrySetWearFromLibrary()
    {
        var cat = CurCat;
        if (cat is null || !cat.HasSlots || _libGroup.Selected < 0) return;
        var g = cat.Groups[_libGroup.Selected];
        int raceId = PcRaceId(g.Name);
        string slot = _libSlot.ItemCount > 0 && _libSlot.Selected >= 0
            ? _libSlot.GetItemText(_libSlot.Selected).ToLowerInvariant() : "";
        if (raceId <= 0 || slot is not ("body" or "head" or "hands" or "legs" or "feet")) return;
        for (int i = 0; i < _raceOpt.ItemCount; i++) if (_raceOpt.GetItemId(i) == raceId) _raceOpt.Selected = i;
        for (int i = 0; i < _slotOpt.ItemCount; i++) if (_slotOpt.GetItemText(i) == slot) _slotOpt.Selected = i;
        _wearChk.ButtonPressed = true;
    }

    private static int PcRaceId(string group) => group switch
    {
        "Hume Male" => 1, "Hume Female" => 2, "Elvaan Male" => 3, "Elvaan Female" => 4,
        "Mithra" => 7, "Galka" => 8,
        _ when group.Equals("Tarutaru Female", StringComparison.OrdinalIgnoreCase) => 6,
        _ when group.StartsWith("Tarutaru", StringComparison.OrdinalIgnoreCase) => 5,
        _ => 0,
    };

    // ---- install root + tree ---------------------------------------------------------------

    private void SetRoot(string root)
    {
        _root = Path.GetFullPath(root); // collapse ".." so map keys match tree/open lookups
        _rootLabel.Text = "root: " + _root;
        try { _archive = new DatArchive(_root); }
        catch (Exception e) { _archive = null; GD.Print("archive load failed: " + e.Message); }

        // Race/equipment path resolver (for "wear on a base body") — model tables vendored under
        // res://data/models (so it works in an exported build, not just against a sibling checkout).
        try { _resolver = new Vellichor.Render.ModelResolver(_root, ModelsDataDir()); }
        catch (Exception e) { _resolver = null; GD.Print("resolver load failed: " + e.Message); }

        // Reverse map (path → file id) so a browsed file can show its numeric id.
        _pathToId.Clear();
        if (_archive is not null)
            foreach (var (id, path) in _archive.EnumerateAll()) _pathToId[path] = id;
        GD.Print($"[root] {_root} · {_pathToId.Count} mapped file ids · resolver={( _resolver?.Ready == true ? "ok" : "no")}");

        BuildTree();
        PopulateLibraryTree(); // re-evaluate which Library entries have a DAT on disk under this root
        SaveSettings();        // remember this install root for next launch
        RefreshSettingsStatus();
    }

    /// Directory holding the vendored FFXI model tables (race/slot equipment paths).
    private static string ModelsDataDir() => AssetDir("data/models");

    /// Resolve a bundled data folder (List, data/models) to a REAL on-disk path — needed because the
    /// catalog + model tables are read with System.IO, which can't read from inside an exported .pck.
    /// So these folders ship as loose files (they carry a `.gdignore`, so Godot never packs/imports
    /// them). Order: the project tree (editor / `--path`) → next to the binary (Windows/Linux) →
    /// inside the macOS .app bundle (Contents/Resources).
    private static string AssetDir(string rel)
    {
        string res = ProjectSettings.GlobalizePath("res://" + rel);
        if (Directory.Exists(res)) return res;                       // dev: files are on disk
        string exeDir = Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
        string beside = Path.Combine(exeDir, rel);
        if (Directory.Exists(beside)) return beside;                 // Windows/Linux: beside the exe
        string macRes = Path.GetFullPath(Path.Combine(exeDir, "..", "Resources", rel));
        if (Directory.Exists(macRes)) return macRes;                 // macOS: inside the .app bundle
        return res;                                                  // give up gracefully
    }

    // ---- settings (persisted to user://settings.cfg) ---------------------------------------

    private void LoadSettings()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) != Error.Ok) return;
        _savedRoot = cfg.GetValue("paths", "rom_root", "").AsString();
        _lastOpenDir = cfg.GetValue("paths", "last_open_dir", "").AsString();
        _uiScale = cfg.GetValue("ui", "scale", 1.5f).AsSingle();
        if (_uiScale is < 0.5f or > 4f) _uiScale = 1.5f;
    }

    private void SaveSettings()
    {
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath); // preserve any keys we don't manage
        cfg.SetValue("paths", "rom_root", _root);
        cfg.SetValue("paths", "last_open_dir", _lastOpenDir);
        cfg.SetValue("ui", "scale", _uiScale);
        cfg.Save(SettingsPath);
    }

    /// The Settings page — where you point the viewer at your FFXI install (needed on first run,
    /// since a released build ships no DATs). Also exposes the UI scale. Built as an in-scene overlay
    /// so it inherits the theme + content scaling.
    private void OpenSettings(bool firstRun)
    {
        if (_settingsOverlay is not null && GodotObject.IsInstanceValid(_settingsOverlay))
        {
            if (_settingsRootEdit is not null) _settingsRootEdit.Text = _root;
            _settingsOverlay.Visible = true;
            RefreshSettingsStatus();
            return;
        }

        var overlay = new Control { MouseFilter = MouseFilterEnum.Stop };
        overlay.SetAnchorsPreset(LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f) };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        overlay.AddChild(dim);
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        overlay.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(580, 0) };
        center.AddChild(panel);
        var pad = new MarginContainer();
        foreach (var m in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            pad.AddThemeConstantOverride(m, 18);
        panel.AddChild(pad);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        pad.AddChild(box);

        var head = new Label { Text = firstRun ? "Welcome — point the viewer at your FFXI install" : "Settings" };
        head.AddThemeFontSizeOverride("font_size", 18);
        head.AddThemeColorOverride("font_color", UiTheme.Gold);
        box.AddChild(head);

        box.AddChild(new Label
        {
            Text = "FFXI install folder — the folder that contains ROM/, ROM2/ …, FTABLE.DAT.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        var row = new HBoxContainer();
        _settingsRootEdit = new LineEdit
        {
            Text = _root,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            PlaceholderText = @"e.g. C:\Program Files (x86)\PlayOnline\SquareEnix\FINAL FANTASY XI",
        };
        _settingsRootEdit.TextSubmitted += _ => ApplySettingsRoot();
        row.AddChild(_settingsRootEdit);
        var browse = new Button { Text = "Browse…" };
        browse.Pressed += OpenRootDialog;
        row.AddChild(browse);
        var useBtn = new Button { Text = "Use" };
        useBtn.Pressed += ApplySettingsRoot;
        row.AddChild(useBtn);
        box.AddChild(row);

        _settingsStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_settingsStatus);

        box.AddChild(new HSeparator());

        var scaleRow = new HBoxContainer();
        scaleRow.AddChild(new Label { Text = "UI scale", CustomMinimumSize = new Vector2(70, 0) });
        var scale = new HSlider { MinValue = 0.75, MaxValue = 3.0, Step = 0.05, Value = _uiScale, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var scaleVal = new Label { Text = $"{_uiScale:0.00}×", CustomMinimumSize = new Vector2(56, 0) };
        scale.ValueChanged += v =>
        {
            _uiScale = (float)v;
            GetWindow().ContentScaleFactor = _uiScale;
            scaleVal.Text = $"{_uiScale:0.00}×";
            SaveSettings();
        };
        scaleRow.AddChild(scale);
        scaleRow.AddChild(scaleVal);
        box.AddChild(scaleRow);

        var note = new Label
        {
            Text = "No game data is bundled or uploaded — only your local install is read.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        note.AddThemeColorOverride("font_color", UiTheme.Muted);
        box.AddChild(note);

        var footer = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var close = new Button { Text = "Close" };
        close.Pressed += () => { if (_settingsOverlay is not null) _settingsOverlay.Visible = false; };
        footer.AddChild(close);
        box.AddChild(footer);

        AddChild(overlay);
        _settingsOverlay = overlay;
        RefreshSettingsStatus();
    }

    private void ApplySettingsRoot()
    {
        string p = _settingsRootEdit?.Text.Trim() ?? "";
        if (Directory.Exists(p)) SetRoot(p);
        else RefreshSettingsStatus("✗ not a folder: " + p);
    }

    private void RefreshSettingsStatus(string? msg = null)
    {
        if (_settingsRootEdit is not null && GodotObject.IsInstanceValid(_settingsRootEdit) && !string.IsNullOrEmpty(_root))
            _settingsRootEdit.Text = _root;
        if (_settingsStatus is null || !GodotObject.IsInstanceValid(_settingsStatus)) return;
        if (msg is not null) { _settingsStatus.Text = msg; _settingsStatus.AddThemeColorOverride("font_color", UiTheme.Gold); return; }
        if (string.IsNullOrEmpty(_root) || !Directory.Exists(_root))
        {
            _settingsStatus.Text = "No install set yet.";
            _settingsStatus.AddThemeColorOverride("font_color", UiTheme.Muted);
            return;
        }
        int roms = Directory.EnumerateDirectories(_root)
            .Count(d => Path.GetFileName(d).StartsWith("ROM", StringComparison.OrdinalIgnoreCase));
        _settingsStatus.Text = $"✓ {_pathToId.Count} files mapped · {roms} ROM folders · resolver {(_resolver?.Ready == true ? "ok" : "—")}";
        _settingsStatus.AddThemeColorOverride("font_color", new Color(0.5f, 0.85f, 0.5f));
    }

    private void BuildTree()
    {
        _tree.Clear();
        var rootItem = _tree.CreateItem();
        if (!Directory.Exists(_root)) return;
        // Top level: ROM, ROM2 … folders. Lazily fill children when expanded.
        foreach (var dir in Directory.EnumerateDirectories(_root)
                     .Where(d => Path.GetFileName(d).StartsWith("ROM", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(NaturalKey))
            AddFolderItem(rootItem, dir);
    }

    private void AddFolderItem(TreeItem parent, string dir)
    {
        var it = _tree.CreateItem(parent);
        if (it is null) return;
        it.SetText(0, Path.GetFileName(dir));
        it.SetMetadata(0, dir);
        it.Collapsed = true;
        // placeholder so the fold arrow shows; replaced on first expand
        var ph = _tree.CreateItem(it);
        ph?.SetText(0, "…");
    }

    private void OnTreeItemCollapsed(TreeItem item)
    {
        if (item.Collapsed) return; // only fill on expand
        // has it only the placeholder? then populate real children — but Tree forbids CreateItem DURING
        // the collapse/selection event, so defer the fill to the next idle frame.
        var first = item.GetFirstChild();
        if (first is null || first.GetText(0) != "…") return;
        Callable.From(() => FillFolder(item)).CallDeferred();
    }

    private void FillFolder(TreeItem item)
    {
        if (!GodotObject.IsInstanceValid(item)) return;
        var first = item.GetFirstChild();
        if (first is null || first.GetText(0) != "…") return; // already filled
        first.Free();
        string dir = item.GetMetadata(0).AsString();
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(NaturalKey))
            AddFolderItem(item, sub);
        foreach (var f in Directory.EnumerateFiles(dir).Where(IsDat).OrderBy(NaturalKey))
        {
            var fi = _tree.CreateItem(item);
            if (fi is null) continue;
            fi.SetText(0, Path.GetFileName(f));
            fi.SetMetadata(0, f);
        }
    }

    private static bool IsDat(string f) => f.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase);

    // Headless check of the lazy folder-fill (DATVIEWER_TREETEST): expand ROM, then a subfolder.
    private void TreeTest()
    {
        var rom = _tree.GetRoot()?.GetFirstChild();
        if (rom is null) { GD.Print("[treetest] no ROM items"); GetTree().Quit(); return; }
        FillFolder(rom);
        var names = new List<string>();
        int n = 0; for (var c = rom.GetFirstChild(); c is not null; c = c.GetNext()) { if (names.Count < 6) names.Add(c.GetText(0)); n++; }
        GD.Print($"[treetest] {rom.GetText(0)} -> {n} children: {string.Join(", ", names)}");
        var sub = rom.GetFirstChild();
        if (sub is not null) { FillFolder(sub); int m = 0; for (var c = sub.GetFirstChild(); c is not null; c = c.GetNext()) m++; GD.Print($"[treetest] {rom.GetText(0)}/{sub.GetText(0)} -> {m} children"); }
        GetTree().Quit();
    }

    private void OnTreeItemSelected()
    {
        var it = _tree.GetSelected();
        if (it is null) return;
        string path = it.GetMetadata(0).AsString();
        if (File.Exists(path) && path.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase)) LoadDat(path);
    }

    private void GoToId()
    {
        if (_archive is null) { Flash("set an install root first"); return; }
        if (!int.TryParse(_idBox.Text.Trim(), out var id)) { Flash("enter a numeric file id"); return; }
        string? p = _archive.ResolveFileId(id);
        if (p is null || !File.Exists(p)) { Flash($"file id {id} is not mapped / missing"); return; }
        LoadDat(p);
    }

    // ---- load + preview a DAT --------------------------------------------------------------

    private void LoadDat(string path)
    {
        path = Path.GetFullPath(path); // normalize so the reverse-map (path→id) lookup matches
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception e) { Flash("read failed: " + e.Message); return; }

        // Where does it live? ROM-relative path = what you mirror under the XIPivot overlay.
        string rel = RomRelative(path);
        int fileId = _pathToId.TryGetValue(path, out var fid) ? fid : -1;

        _lastData = data;
        _extraClipDats = null; _prefClipName = null; _restrictClips = null; // opening a real DAT clears Animation-tab state
        var chunks = ChunkReader.Walk(data);
        _lastChunks = chunks;
        int nTex = chunks.Count(c => c.Type == 0x20);
        int nMesh = chunks.Count(c => c.Type == 0x2a);
        int nBone = chunks.Count(c => c.Type == 0x29);
        int nSkel = chunks.Count(c => c.Type == 0x2b);
        string kind = nMesh > 0 || nSkel > 0 ? "model" : nTex > 0 ? "texture set"
            : chunks.Count == 0 ? "data / non-chunked" : "mixed / other";

        _info.Clear();
        _info.AppendText($"[b][color=#8fd0ff]{rel}[/color][/b]   [color=#aaa]({FormatBytes(data.Length)})[/color]\n");
        if (rel != path) _info.AppendText($"[color=#888]full: {path}[/color]\n");
        _info.AppendText(fileId >= 0
            ? $"file id: [b]{fileId}[/b]   ·   "
            : "file id: [color=#888]—[/color]   ·   ");
        _info.AppendText($"type: [b]{kind}[/b]   ·   chunks: {chunks.Count}   ·   textures: {nTex}   ·   meshes: {nMesh}\n");
        if (!string.IsNullOrEmpty(rel) && rel != path)
            _info.AppendText($"[color=#7c7]XIPivot: drop your replacement at [b]<overlay>/{rel.Replace('\\','/')}[/b][/color]");

        PopulateTextures(data, chunks);
        BuildModelView();
        PopulateChunks(chunks);
        PopulateHex(data);
    }

    private void PopulateTextures(byte[] data, List<DatChunk> chunks)
    {
        foreach (var c in _texGrid.GetChildren()) c.QueueFree();
        int shown = 0;
        foreach (var c in chunks.Where(c => c.Type == 0x20))
        {
            if (shown >= 400) break;
            ImgTexture? t;
            try { t = ImgDecoder.Decode(data.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray()); }
            catch { continue; }
            if (t is null) continue;
            var img = Image.CreateFromData(t.Width, t.Height, false, Image.Format.Rgba8, t.Rgba);
            var box = new VBoxContainer { CustomMinimumSize = new Vector2(150, 176) };
            box.AddChild(new TextureRect
            {
                Texture = ImageTexture.CreateFromImage(img),
                CustomMinimumSize = new Vector2(140, 140),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            });
            box.AddChild(new Label
            {
                Text = $"'{t.Id}'\n{t.Width}x{t.Height}",
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(140, 0),
            });
            _texGrid.AddChild(box);
            shown++;
        }
        if (shown == 0)
            _texGrid.AddChild(new Label { Text = "  (no IMG textures in this DAT)" });
    }

    /// Rebuild the 3D preview from the last-loaded DAT + the current "wear" controls.
    ///  - self-contained model (creature / full NPC with its own skeleton) → show posed;
    ///  - equipment part (skinned mesh, no own skeleton) + "Wear on body" → assemble on a base race body;
    ///  - otherwise → a raw mesh dump (bare garment / MMB object).
    private void BuildModelView()
    {
        if (_modelRoot is null) return;
        foreach (var c in _modelRoot.GetChildren()) c.QueueFree(); // frees the old AnimationDriver too
        _animDriver = null; _animCm = null; _animSkel = null;
        if (_animRow is not null) _animRow.Visible = false;
        if (_lastData is null) { _modelInfo.Text = ""; return; }

        Vellichor.Render.CharacterModel? self = null;
        try { self = Vellichor.Render.CharacterModel.Decode(_lastData); } catch { }
        bool isPart = self is null || self.BoneCount == 0;

        // wear controls only matter for equipment parts
        _wearChk.Disabled = _raceOpt.Disabled = _slotOpt.Disabled = !isPart;

        // Explicit clip DATs from the Animation tab → always assemble-on-body with them.
        if (_extraClipDats is not null && _wearChk.ButtonPressed && _resolver?.Ready == true && TryAnimOnBody()) return;

        if (!isPart)
        {
            ShowCharacter(self!, $"character model · {self!.BoneCount} bones · clips: {string.Join(", ", self.ClipNames.Take(8))}");
            return;
        }

        // An animation-only DAT (0x2b clips, no skeleton/mesh) — play it on the chosen race's body.
        int n29 = _lastChunks.Count(c => c.Type == 0x29);
        int n2a = _lastChunks.Count(c => c.Type == 0x2a);
        int n2b = _lastChunks.Count(c => c.Type == 0x2b);
        bool isAnimOnly = n2b > 0 && n29 == 0 && n2a == 0;
        if (isAnimOnly && _wearChk.ButtonPressed && _resolver?.Ready == true && TryAnimOnBody()) return;

        if (_wearChk.ButtonPressed && _resolver?.Ready == true && TryWearOnBody()) return;

        ShowRawMeshes();
    }

    /// A standalone animation DAT (0x2b clips only) → assemble the chosen race's naked body and merge
    /// this DAT's clips onto it, so the motion can be previewed. Uses Vellichor's clipDats path.
    private bool TryAnimOnBody()
    {
        int race = _raceOpt.GetSelectedId();
        var recipe = _resolver!.PcBaseParts(race, 0);
        if (recipe is not { } rec) { _modelInfo.Text = "  (no base body for this race in the model tables)"; return false; }

        byte[] skel;
        try { skel = File.ReadAllBytes(rec.skeleton); } catch { return false; }
        var parts = new List<byte[]>();
        foreach (var (_, path) in rec.parts) { try { parts.Add(File.ReadAllBytes(path)); } catch { } }

        var clipDats = _extraClipDats ?? new List<byte[]> { _lastData! };
        Vellichor.Render.CharacterModel? cm = null;
        try { cm = Vellichor.Render.CharacterModel.DecodeAssembled(skel, parts, clipDats: clipDats); }
        catch (Exception e) { GD.Print("anim assemble failed: " + e.Message); }
        if (cm is null || cm.BoneCount == 0) { _modelInfo.Text = "  (could not apply this animation to a body)"; return false; }

        string? pref = _prefClipName ?? _lastChunks.Where(c => c.Type == 0x2b).Select(c => c.Name).FirstOrDefault();
        int shownClips = _restrictClips is { Count: > 0 } ? cm.ClipNames.Count(_restrictClips.Contains) : cm.ClipNames.Count;
        ShowCharacter(cm, $"animation on {_raceOpt.GetItemText(_raceOpt.Selected)} · {shownClips} clip(s)", pref);
        return true;
    }

    /// Equipment part → assemble it onto the chosen race's naked base body, swapping the chosen slot.
    private bool TryWearOnBody()
    {
        int race = _raceOpt.GetSelectedId();
        string slot = _slotOpt.GetItemText(_slotOpt.Selected);
        var recipe = _resolver!.PcBaseParts(race, 0);
        if (recipe is not { } rec) { _modelInfo.Text = "  (no base body for this race in the model tables)"; return false; }

        byte[] skel;
        try { skel = File.ReadAllBytes(rec.skeleton); } catch { return false; }

        var parts = new List<byte[]>();
        bool replaced = false;
        foreach (var (s, path) in rec.parts)
        {
            if (s == slot) { parts.Add(_lastData!); replaced = true; }
            else { try { parts.Add(File.ReadAllBytes(path)); } catch { } }
        }
        if (!replaced) parts.Add(_lastData!); // slot has no naked default — just add the piece

        Vellichor.Render.CharacterModel? cm = null;
        try { cm = Vellichor.Render.CharacterModel.DecodeAssembled(skel, parts); }
        catch (Exception e) { GD.Print("assemble failed: " + e.Message); }
        if (cm is null || cm.BoneCount == 0) { _modelInfo.Text = "  (could not assemble on a body)"; return false; }

        ShowCharacter(cm, $"worn on {_raceOpt.GetItemText(_raceOpt.Selected)} · '{slot}' slot · {cm.BoneCount} bones");
        return true;
    }

    private void ShowCharacter(Vellichor.Render.CharacterModel cm, string label, string? preferredClip = null)
    {
        var (root, skel, bounds) = cm.BuildInstance();
        _modelRoot.AddChild(root);
        _modelInfo.Text = $"  {label}   (drag to orbit · wheel to zoom)";

        // Animation: FFXI models are stored in a splayed reference pose that must be driven by a 0x2b
        // clip to look natural. Wire the driver + clip picker so any clip can be played/scrubbed.
        _animCm = cm;
        _animSkel = skel;
        // When shown from the Animation tab, list ONLY the selected action's own clips (not the ~195
        // clips the assembled base body also carries). Otherwise (creature / worn) list them all.
        var clips = _restrictClips is { Count: > 0 }
            ? cm.ClipNames.Where(_restrictClips.Contains).ToList()
            : cm.ClipNames.ToList();
        _clipOpt.Clear();
        foreach (var n in clips) _clipOpt.AddItem(n);
        _animRow.Visible = clips.Count > 0;
        if (clips.Count > 0)
        {
            _animDriver = new Vellichor.Render.AnimationDriver();
            root.AddChild(_animDriver);
            // default to the opened animation's clip, else an idle/standing/walk clip, else the first
            string pick = preferredClip is not null && clips.Contains(preferredClip)
                ? preferredClip
                : cm.FindClip("idl", "std", "wlk", "") ?? clips[0];
            int idx = clips.IndexOf(pick);
            _clipOpt.Selected = idx < 0 ? 0 : idx;
            PlaySelectedClip(resetClock: true);
        }

        var measured = WorldAabbOf(root);
        FrameCamera(measured.Size.Length() > 0.01f ? measured : bounds);
    }

    /// Load the currently-selected clip into the driver (we drive it via Seek from our own clock).
    private void PlaySelectedClip(bool resetClock)
    {
        if (_animCm is null || _animDriver is null || _animSkel is null || _clipOpt.Selected < 0) return;
        string name = _clipOpt.GetItemText(_clipOpt.Selected);
        if (_animCm.Clip(name) is not { } cc) { _animTime.Text = "(undecodable)"; return; }
        _animDriver.Setup(_animSkel, cc.tracks, cc.frames, cc.fps);
        _animDriver.Playing = false;                 // our _Process drives it via Seek
        _animFrames = cc.frames;
        _animFps = cc.fps > 0.01f ? cc.fps : 30f;
        _animDuration = _animFrames > 0 ? _animFrames / _animFps : 0;
        if (resetClock) _animClock = 0;
        _animPlaying = _animDuration > 0;
        _animPlayBtn.Text = _animPlaying ? "❚❚" : "▶";
        _animDriver.Seek((float)_animClock);
    }

    private void ToggleAnim()
    {
        if (_animDriver is null || _animDuration <= 0) return;
        _animPlaying = !_animPlaying;
        _animPlayBtn.Text = _animPlaying ? "❚❚" : "▶";
    }

    /// Union of every child MeshInstance3D's AABB (in root space) — a reliable extent for framing.
    private static Aabb WorldAabbOf(Node3D root)
    {
        Aabb total = default; bool any = false;
        void Walk(Node n, Transform3D xf)
        {
            if (n is Node3D n3) xf *= n3.Transform;
            if (n is MeshInstance3D mi && mi.Mesh is not null)
            {
                var a = mi.Mesh.GetAabb();
                // expand `total` by the 8 transformed corners
                for (int i = 0; i < 8; i++)
                {
                    var corner = a.Position + a.Size * new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
                    var w = xf * corner;
                    if (!any) { total = new Aabb(w, Vector3.Zero); any = true; }
                    else total = total.Expand(w);
                }
            }
            foreach (var c in n.GetChildren()) Walk(c, xf);
        }
        Walk(root, Transform3D.Identity);
        return total;
    }

    /// Fallback: the bare meshes (MMB objects, or an equipment garment with wear off), no skeleton.
    private void ShowRawMeshes()
    {
        var meshes = new List<MeshData>();
        foreach (var c in _lastChunks.Where(c => c.Type == 0x2a))
        {
            try
            {
                var mmb = MmbDecoder.Decode(_lastData!.AsSpan(c.PayloadOffset, c.PayloadLength).ToArray());
                if (mmb.Meshes.Count > 0) meshes.AddRange(mmb.Meshes);
            }
            catch { }
        }
        if (meshes.Count == 0) { try { meshes.AddRange(ModelDecoder.DecodeCharacterMeshes(_lastData!)); } catch { } }
        if (meshes.Count == 0) { _modelInfo.Text = "  (no decodable model in this DAT)"; return; }

        int tris = 0;
        var aabb = new Aabb();
        bool first = true;
        foreach (var m in meshes)
        {
            if (m.VertexCount == 0) continue;
            var mi = new MeshInstance3D { Mesh = BuildMesh(m, out var mAabb) };
            mi.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.8f, 0.82f), CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            _modelRoot.AddChild(mi);
            tris += m.TriangleCount;
            aabb = first ? mAabb : aabb.Merge(mAabb);
            first = false;
        }
        _modelInfo.Text = $"  {meshes.Count} mesh(es) · {tris} triangles (bare — no skeleton)   (drag to orbit · wheel to zoom)";
        FrameCamera(aabb);
    }

    private void FrameCamera(Aabb bounds)
    {
        _focus = bounds.GetCenter();
        _dist = MathF.Max(0.5f, bounds.Size.Length() * 0.5f) * 2.6f;
        // Default to a FRONT view (character facing the user). +90° showed the BACK, so the front camera is
        // at −90°. Slight downward pitch for a natural character-viewer shot.
        _yaw = -Mathf.Pi / 2; _pitch = 0.12f;
        // Diagnosis overrides (headless close-ups): DATVIEWER_DIST scales distance, DATVIEWER_YAW sets angle.
        if (float.TryParse(System.Environment.GetEnvironmentVariable("DATVIEWER_DIST"), out var dm)) _dist *= dm;
        if (float.TryParse(System.Environment.GetEnvironmentVariable("DATVIEWER_YAW"), out var ym)) _yaw = ym;
        if (float.TryParse(System.Environment.GetEnvironmentVariable("DATVIEWER_PITCH"), out var pm)) _pitch = pm;
    }

    private static ArrayMesh BuildMesh(MeshData m, out Aabb aabb)
    {
        var verts = new Vector3[m.VertexCount];
        aabb = new Aabb();
        for (int i = 0; i < m.VertexCount; i++)
        {
            var v = new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);
            verts[i] = v;
            aabb = i == 0 ? new Aabb(v, Vector3.Zero) : aabb.Expand(v);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        if (m.Normals is { Length: > 0 })
        {
            var n = new Vector3[m.VertexCount];
            for (int i = 0; i < m.VertexCount && i * 3 + 2 < m.Normals.Length; i++)
                n[i] = new Vector3(m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2]);
            arrays[(int)Mesh.ArrayType.Normal] = n;
        }
        if (m.Uvs is { Length: > 0 })
        {
            var uv = new Vector2[m.VertexCount];
            for (int i = 0; i < m.VertexCount && i * 2 + 1 < m.Uvs.Length; i++)
                uv[i] = new Vector2(m.Uvs[i * 2], m.Uvs[i * 2 + 1]);
            arrays[(int)Mesh.ArrayType.TexUV] = uv;
        }
        arrays[(int)Mesh.ArrayType.Index] = m.Indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private void PopulateChunks(List<DatChunk> chunks)
    {
        _chunkText.Clear();
        if (chunks.Count == 0)
        {
            _chunkText.AppendText("Not a chunk container (fixed-record data or unknown format). See the Hex tab.");
            return;
        }
        _chunkText.AppendText($"[b]{chunks.Count} chunks[/b]\n\n");
        _chunkText.AppendText("[table=4][cell][b]#[/b][/cell][cell][b]name[/b][/cell][cell][b]type[/b][/cell][cell][b]size[/b][/cell]");
        int i = 0;
        foreach (var c in chunks)
        {
            _chunkText.AppendText($"[cell]{i}[/cell][cell]'{c.Name}'[/cell][cell]0x{c.Type:X2} ({ChunkTypeName(c.Type)})[/cell][cell]{FormatBytes(c.LengthBytes)}[/cell]");
            i++;
            if (i >= 4000) break;
        }
        _chunkText.AppendText("[/table]");
    }

    private void PopulateHex(byte[] data)
    {
        int n = Math.Min(data.Length, 4096);
        var sb = new System.Text.StringBuilder(n * 4);
        for (int off = 0; off < n; off += 16)
        {
            sb.Append(off.ToString("X6")).Append("  ");
            int lineLen = Math.Min(16, n - off);
            for (int j = 0; j < 16; j++)
            {
                sb.Append(j < lineLen ? data[off + j].ToString("X2") : "  ").Append(' ');
                if (j == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int j = 0; j < lineLen; j++)
            {
                byte b = data[off + j];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            sb.Append('\n');
        }
        if (data.Length > n) sb.Append($"\n… {FormatBytes(data.Length - n)} more");
        _hexText.Text = sb.ToString();
    }

    // ---- 3D orbit --------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_cam is null) return;
        float cp = Mathf.Cos(_pitch);
        var offset = new Vector3(Mathf.Sin(_yaw) * cp, Mathf.Sin(_pitch), Mathf.Cos(_yaw) * cp) * _dist;
        _cam.Position = _focus + offset;
        _cam.LookAt(_focus, Vector3.Up);

        // advance the animation (we own the clock; the driver just poses at the time we Seek to)
        if (_animDriver is not null && _animDuration > 0)
        {
            if (_animPlaying && !_animScrubbing)
            {
                _animClock += delta * _animSpeed;
                if (_animClock >= _animDuration)
                {
                    if (_animLoopChk.ButtonPressed) _animClock = Mathf.PosMod((float)_animClock, (float)_animDuration);
                    else { _animClock = _animDuration; _animPlaying = false; _animPlayBtn.Text = "▶"; }
                }
                _animDriver.Seek((float)_animClock);
            }
            if (!_animScrubbing)
            {
                _animSeekProg = true;
                _animSeek.Value = _animClock / _animDuration;
                _animSeekProg = false;
            }
            _animTime.Text = $"{(int)(_animClock * _animFps)}/{_animFrames}";
        }

        // advance the music scrubber (skip while the user is dragging it)
        if (_audio is not null && _audio.Playing && !_audio.StreamPaused && _musicLenSec > 0)
        {
            double pos = _audio.GetPlaybackPosition();
            _musicSeekProgrammatic = true;
            _musicSeek.Value = Math.Clamp(pos / _musicLenSec, 0, 1);
            _musicSeekProgrammatic = false;
            _musicTime.Text = $"{FmtTime(pos)} / {FmtTime(_musicLenSec)}";
        }
    }

    private void OnViewportInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left) _dragging = mb.Pressed;
            else if (mb.ButtonIndex == MouseButton.WheelUp) _dist = Mathf.Max(0.3f, _dist * 0.9f);
            else if (mb.ButtonIndex == MouseButton.WheelDown) _dist *= 1.1f;
        }
        else if (e is InputEventMouseMotion mm && _dragging)
        {
            _yaw -= mm.Relative.X * 0.01f;
            _pitch = Mathf.Clamp(_pitch + mm.Relative.Y * 0.01f, -1.4f, 1.4f);
        }
    }

    // ---- dialogs ---------------------------------------------------------------------------

    private void OpenFileDialog()
    {
        var fd = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.DAT ; FFXI DAT files", "* ; All files" },
            Size = new Vector2I(900, 640),
            Title = "Open a .DAT file",
        };
        // Start in the last directory you browsed (remembered across launches), else the install root.
        string startDir = Directory.Exists(_lastOpenDir) ? _lastOpenDir : (Directory.Exists(_root) ? _root : "");
        if (startDir.Length > 0) fd.CurrentDir = startDir;
        fd.FileSelected += p =>
        {
            _lastOpenDir = Path.GetDirectoryName(p) ?? _lastOpenDir;
            SaveSettings();
            LoadDat(p);
            fd.QueueFree();
        };
        fd.Canceled += fd.QueueFree;
        AddChild(fd);
        fd.PopupCentered();
    }

    private void OpenRootDialog()
    {
        var fd = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Size = new Vector2I(900, 640),
            Title = "Select your FFXI install root (the folder with ROM/, FTABLE.DAT)",
        };
        fd.DirSelected += d => { SetRoot(d); fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        AddChild(fd);
        fd.PopupCentered();
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// ROM-relative path (e.g. ROM/9/2.DAT) — the path to mirror in an XIPivot overlay.
    private string RomRelative(string path)
    {
        if (!string.IsNullOrEmpty(_root) && path.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            return path.Substring(_root.Length).TrimStart('/', '\\');
        // Not under the current root: fall back to the last ROM*/dir/file segment if present.
        var parts = path.Replace('\\', '/').Split('/');
        int idx = Array.FindLastIndex(parts, s => s.StartsWith("ROM", StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? string.Join('/', parts.Skip(idx)) : path;
    }

    private static string ChunkTypeName(int t) => t switch
    {
        0x20 => "IMG texture",
        0x29 => "skeleton/bone",
        0x2a => "MMB mesh",
        0x2b => "skeleton ref",
        0x2e => "scheduler",
        0x35 => "generator",
        0x39 => "collision",
        _ => "?",
    };

    private static string FormatBytes(long n) =>
        n < 1024 ? $"{n} B" : n < 1024 * 1024 ? $"{n / 1024.0:0.0} KB" : $"{n / (1024.0 * 1024):0.0} MB";

    private static string NaturalKey(string path)
    {
        // sort ROM2 after ROM, 10 after 2, etc. — pad trailing digits.
        string name = Path.GetFileName(path);
        int i = 0; while (i < name.Length && !char.IsDigit(name[i])) i++;
        string prefix = name.Substring(0, i);
        return int.TryParse(name.Substring(i), out var num) ? $"{prefix}{num:D6}" : name;
    }

    private void Flash(string msg)
    {
        _info.Clear();
        _info.AppendText($"[color=#f88]{msg}[/color]");
        GD.Print("[dv] " + msg);
    }

    // Headless screenshot helper (DATVIEWER_SHOT). A true --headless *exported* build uses the dummy
    // renderer with no framebuffer, so GetImage() is null — guard it so the helper quits cleanly
    // instead of throwing/hanging (screenshots still work from the editor build, which has a display).
    private async void ShotAfter(string file, int frames)
    {
        for (int i = 0; i < frames; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var img = GetViewport().GetTexture()?.GetImage();
        if (img is not null) { img.SavePng(file); GD.Print("saved -> " + file); }
        else GD.Print("no framebuffer (headless) — skipped screenshot " + file);
        GetTree().Quit();
    }
}
