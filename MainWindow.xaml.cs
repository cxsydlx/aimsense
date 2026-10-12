using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AimSense;

public partial class MainWindow : Window
{
    private readonly DisplayModeService _displays = new();
    private readonly FpsCamera _camera = new();
    private readonly NativeArenaRenderer _scene;
    private readonly DispatcherTimer _safetyTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _scoringTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
    private readonly Dictionary<TrialMode, ModeSearch> _searches = [];
    private readonly Dictionary<TrialMode, PracticeDifficulty> _difficulties = [];
    // 每块的逐帧运动学（按 BlockId 暂存）：必须在 _samples 清空前算，供快照与上传包使用。
    private readonly Dictionary<string, Dictionary<string, double>> _blockKinematics = [];
    private readonly List<BlockResult> _pilots = [];
    private readonly List<string> _notes = [];
    private readonly List<object> _events = [], _samples = [];
    private readonly Queue<double> _frameTimes = new();
    private readonly List<double> _calibrationSamples = [];
    private IReadOnlyList<DisplayMode> _displayModes = [];
    private RawMouseInput? _input;
    private DisplayModeLease? _displayLease;
    private TraceRecorder? _trace;
    private CompositeValidation? _validation;
    private ResumeSessionState? _resumeState;
    private bool _resumeEntry;
    private string _motionVersion = MotionScript.CurrentVersion;
    private string _evidenceVersion = CompositeValidation.CurrentEvidenceVersion;
    private readonly CompositeReviewConsent _reviewConsent = new();
    private SessionSearchSchedule? _searchSchedule;
    private BlockRequest? _request;
    private TrialBlockRunner? _runner;
    private MotionScript? _script;
    private TrialMode[] _modes = [];
    private readonly TestSessionStateMachine _state = new();
    private SessionPhase _phase => _state.Phase;
    private GameProfile _game = GameProfiles.Get("CS2");
    private bool _session, _stopping, _closing, _allowClose, _displayTransition, _inputChecked, _calibrating;
    private Task _stopTask = Task.CompletedTask;
    private CancellationTokenSource? _sessionCancellation;
    private int _modeIndex, _pilotCount, _seed, _badBlocks;
    private int _slot => _runner?.Slot ?? -1;
    private bool[] _resolved => _runner?.Resolved ?? new bool[6];
    private double _dpi, _fov, _low, _high, _cm, _blockYaw, _deadline, _usedSeconds, _trainingDistance = 10;
    // 标签：主观手感 / 真实对局成绩 / 纵向留存。_centerCm 是会话开始时的当前档位，用作"更好/更差"的基准。
    private double _centerCm;
    private DisplayMode? _lastSessionDisplay;
    private string _sessionId = "";
    private string? _tracePath;
    private readonly List<FeelRow> _feelRows = [];
    private IReadOnlyList<SessionCandidateLabel> _summaryCandidates = [];
    private sealed record FeelRow(double Cm360, Slider Feel, ComboBox Compare, TextBox Note, TextBlock Value);
    private long _blockStart, _stageStart, _previousFrame, _lastStatus, _positiveInput, _negativeInput, _calibrationX, _calibrationY;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private nint _lockedDevice;
    private double _crossLength = 5, _crossGap = 3, _crossThickness = 2;
    private Brush _crossBrush = Brushes.Cyan;
    private bool _fullScreen, _savedCursor;
    private Rect _oldBounds;
    private WindowStyle _oldStyle;
    private ResizeMode _oldResize;
    private WindowState _oldState;
    private bool _oldTopmost;
    private NativeRect _oldClip;
    private NativePoint _oldCursor;
    private double _oldMinWidth, _oldMinHeight;
    private PhysicalDisplayBounds _fullScreenBounds;
    private long _layoutSettlingUntil, _lastHud;
    private string? _displayMismatch;
    private static readonly double[] TargetOffsets = [-12, 10, -7, 14, -15, 6];
    private static string DataDirectory => AppPaths.DataDirectory;
    private sealed record PracticeDifficulty(double DistanceScale, double SpeedScale, double Deadline);
    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
    private static double Since(long timestamp) => Seconds(Stopwatch.GetTimestamp() - timestamp);
    private TrialMode ActiveMode => _modes[Math.Min(_modeIndex, _modes.Length - 1)];
    private bool InBlock => _request != null && _phase is SessionPhase.Preview or SessionPhase.Adaptation or SessionPhase.Scoring or SessionPhase.Transition;
    private static string ModeName(TrialMode mode) => mode switch { TrialMode.Click => "点击", TrialMode.Track => "跟随", _ => "移动点击" };
    private double Aspect => Math.Max(.1, Arena.ActualWidth / Math.Max(1, Arena.ActualHeight));

    public MainWindow()
    {
        InitializeComponent(); _scene = new() { Visibility = Visibility.Hidden }; Arena.Children.Insert(0, _scene); CharacterViewport.Visibility = Visibility.Collapsed; _scene.HideActor();
        BuildNavigation();
        SourceInitialized += (_, _) =>
        {
            try
            {
                WindowBackdrop.ApplyFrame(this);
                _input = new((HwndSource)PresentationSource.FromVisual(this));
                _input.Moved += RawMoved; _input.PrimaryPressed += RawClicked; _input.DevicesChanged += DevicesChanged;
                ((HwndSource)PresentationSource.FromVisual(this)).AddHook(WindowMessages);
            }
            catch (Exception ex) { ResultText.Text = "RawInput 初始化失败：" + ex.Message; TestButton.IsEnabled = false; }
        };
        Loaded += (_, _) => { RefreshGameList(); RefreshHardware(); UpdateCrosshair(); RefreshLabelPanels(); RefreshSurvey(prefill: true); UpdateStartPreview(); };
        PreviewKeyDown += KeyPressed;
        Deactivated += (_, _) => { if (_session && !_displayTransition && !_stopping) _ = StopSessionAsync("窗口失焦，未完成块已排除。"); };
        StateChanged += (_, _) => { if (_session && !_displayTransition && WindowState == WindowState.Minimized) _ = StopSessionAsync("窗口最小化，停止测试。"); };
        Closing += async (_, e) =>
        {
            if (_allowClose) return; e.Cancel = true; if (_closing) return; _closing = true;
            await StopSessionAsync("关闭软件。"); _input?.Dispose(); CompositionTarget.Rendering -= Render; _scene.Dispose(); _allowClose = true;
            // StopSessionAsync can complete synchronously. Defer Close until this Closing
            // event has returned to avoid recursively closing an already-closing window.
            _ = Dispatcher.BeginInvoke(Close);
        };
        _safetyTimer.Tick += SafetyTick; CompositionTarget.Rendering += Render;
        _scoringTimer.Tick += (_, _) =>
        {
            if (!_session || _stopping || !InBlock) return;
            long now = Stopwatch.GetTimestamp(); AdvanceBlock(now); _scene.SetResolved(_resolved);
            if (_scene.LastStalledBlock == _blockStart) Invalidate("计分期间原生3D帧间隔超过100毫秒");
            if (Seconds(now - _blockStart) >= 16) CompleteBlock();
        };
    }
    private void RefreshClicked(object sender, RoutedEventArgs e) => RefreshHardware();
    private void RefreshHardware()
    {
        if (_session) return;
        nint prior = (DeviceBox.SelectedItem as MouseDevice)?.Handle ?? 0;
        DeviceBox.ItemsSource = RawMouseInput.Enumerate();
        DeviceBox.SelectedItem = DeviceBox.Items.Cast<MouseDevice>().FirstOrDefault(x => x.Handle == prior) ?? DeviceBox.Items[0];
        try
        {
            string? priorDisplay = (DisplayBox.SelectedItem as DisplayDevice)?.DeviceName;
            var displays = _displays.EnumerateDisplays(); DisplayBox.ItemsSource = displays;
            DisplayBox.SelectedItem = displays.FirstOrDefault(x => x.DeviceName == priorDisplay) ?? displays.FirstOrDefault(x => x.IsPrimary) ?? displays.FirstOrDefault();
        }
        catch (Exception ex) { DisplayInfoText.Text = ex.Message; }
    }
    private void DevicesChanged()
    {
        if (_session)
        {
            if (_lockedDevice != 0 && !RawMouseInput.Enumerate().Any(x => x.Handle == _lockedDevice)) _ = StopSessionAsync("选定鼠标已断开，停止测试。");
        }
        else RefreshHardware();
    }
    private void DisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplayBox.SelectedItem is not DisplayDevice display || _session) return;
        try
        {
            var current = _displays.ReadCurrent(display); _displayModes = _displays.EnumerateModes(display);
            ResolutionBox.ItemsSource = _displayModes.Select(x => $"{x.Width} × {x.Height}").Distinct().ToArray();
            ResolutionBox.SelectedItem = $"{current.Width} × {current.Height}";
            DisplayModeBox.SelectedItem = _displayModes.FirstOrDefault(x => x.Width == current.Width && x.Height == current.Height && x.Native.DisplayFrequency == current.Native.DisplayFrequency);
            DisplayInfoText.Text = $"当前：{current}。可选组合来自显示器支持的模式。";
        }
        catch (Exception ex) { _displayModes = []; DisplayInfoText.Text = ex.Message; }
    }
    private void ResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResolutionBox.SelectedItem is not string resolution) return;
        var matching = _displayModes.Where(x => $"{x.Width} × {x.Height}" == resolution).ToArray();
        DisplayModeBox.ItemsSource = matching; DisplayModeBox.SelectedItem = matching.LastOrDefault();
    }
    private static string Choice(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    // ===================== 搜索起点：连续模型（不再只有 3 个档） =====================

    private double WristShare => Math.Clamp(WristSlider.Value / 100.0, 0, 1);

    private (double Cm360, string Source) ComputeStartCenter(GameProfile game, double dpi)
    {
        if (TryDouble(SensitivityBox.Text) is { } typed && typed is > 0 and < 1000)
            return (game.Cm360(dpi, typed), "以你填写的当前灵敏度为起点");
        double pad = TryDouble(PadBox.Text) is { } p && p is >= 10 and <= 150 ? p : 40;
        double cm = StartPointModel.CenterCm(WristShare, pad, game.TrackingFamily);
        return (cm, $"由 {StartPointModel.DescribeWrist(WristShare)} + 垫宽 {pad:0}cm"
            + (game.TrackingFamily ? " + 跟随类游戏偏低灵敏度" : "") + " 连续推算");
    }

    private double SearchRangeShare => Math.Clamp(TryDouble(SearchRangeBox.Text) ?? 25, 10, 60) / 100.0;

    private void WristChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateStartPreview();

    private void StartInputChanged(object sender, TextChangedEventArgs e) => UpdateStartPreview();

    private void StartInputChanged(object sender, SelectionChangedEventArgs e) => UpdateStartPreview();

    private void UpdateStartPreview()
    {
        if (StartPreviewText == null || DpiBox == null || WristSlider == null || PadBox == null
            || SearchRangeBox == null || GameBox == null || SensitivityBox == null) return;   // 初始化期间控件可能还没建好
        try
        {
            WristReadoutText.Text = StartPointModel.DescribeWrist(WristShare);
            if (GameProfileStore.Resolve(SelectedGameId()) is not { } game)
            { StartPreviewText.Text = "这个游戏还没有换算系数：请在下面「自定义 / 修正游戏换算系数」里填好腰射 yaw。"; return; }
            double dpi = TryDouble(DpiBox.Text) ?? 0;
            if (dpi <= 0) { StartPreviewText.Text = "DPI 须大于 0。"; return; }
            (double cm, string source) = ComputeStartCenter(game, dpi);
            double sensitivity = game.Sensitivity(dpi, cm);
            (double low, double high) = StartPointModel.Range(cm, SearchRangeShare);
            StartPreviewText.Text =
                $"起点 {cm:0.00} cm/360 · 灵敏度 {sensitivity:0.0000} · eDPI {dpi * sensitivity:0.0}\n"
                + $"（{source}）\n"
                + $"搜索区间 {low:0.0}–{high:0.0} cm/360（±{SearchRangeShare * 100:0}%）· {(game.TrackingFamily ? "跟随系" : "点击系")}权重 "
                + $"点击 {game.Weight(TrialMode.Click):0.00} / 移动点击 {game.Weight(TrialMode.MovingClick):0.00} / 跟随 {game.Weight(TrialMode.Track):0.00}";
        }
        catch (Exception ex) { StartPreviewText.Text = "起点预览失败：" + ex.Message; }
    }

    // ===================== 游戏换算系数：内置 4 个 + 用户自定义 =====================

    private void RefreshGameList(string? keepId = null)
    {
        string wanted = keepId ?? (GameBox.SelectedItem as GameProfile)?.Id ?? _game.Id;
        var profiles = GameProfileStore.All();
        GameBox.ItemsSource = profiles;
        GameBox.SelectedItem = profiles.FirstOrDefault(x => string.Equals(x.Id, wanted, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
    }

    private string SelectedGameId() => (GameBox.SelectedItem as GameProfile)?.Id ?? "CS2";

    private static bool TryYaw(string text, out double yaw) =>
        double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out yaw) && yaw is > 1e-6 and < 1;

    private static double? OptionalWeight(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value is < 0 or > 1)
            throw new ArgumentException($"{label}权重须留空或填 0–1 之间的数。");
        return value;
    }

    private void SaveCustomGameClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            string id = CustomGameNameBox.Text.Trim();
            if (id.Length == 0) throw new ArgumentException("请填写游戏名称。");
            if (!TryYaw(CustomYawBox.Text, out double yaw))
                throw new ArgumentException("腰射 yaw 须在 0.000001–1 之间（例如 CS2 是 0.022）。不知道就用下面的反解工具算。");
            double? click = OptionalWeight(CustomClickWeightBox.Text, "点击");
            double? moving = OptionalWeight(CustomMovingWeightBox.Text, "移动点击");
            double? track = OptionalWeight(CustomTrackWeightBox.Text, "跟随");
            if ((click.HasValue || moving.HasValue || track.HasValue) && !((click ?? 0) + (moving ?? 0) + (track ?? 0) > 0))
                throw new ArgumentException("三项权重不能全为 0。");
            bool overridesBuiltIn = GameProfiles.BuiltInIds.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            GameProfileStore.Save(new CustomGame(id, yaw, CustomFamilyBox.SelectedIndex == 1, click, moving, track));
            RefreshGameList(id);
            GameProfile resolved = GameProfileStore.Resolve(id)!;
            CustomGameStatusText.Text = $"已保存「{id}」：yaw {GameProfileStore.FormatYaw(resolved.YawDegreesPerCount)} · "
                + $"{(resolved.TrackingFamily ? "跟随系" : "点击系")} · 权重 点击 {resolved.Weight(TrialMode.Click):0.00} / 移动点击 {resolved.Weight(TrialMode.MovingClick):0.00} / 跟随 {resolved.Weight(TrialMode.Track):0.00}"
                + (overridesBuiltIn ? "（同名，已覆盖内置系数）" : "");
        }
        catch (Exception ex) { CustomGameStatusText.Text = "保存失败：" + ex.Message; }
    }

    private void DeleteCustomGameClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            string id = SelectedGameId();
            bool isBuiltIn = GameProfiles.BuiltInIds.Contains(id, StringComparer.OrdinalIgnoreCase);
            if (!GameProfileStore.Customs().Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
            { CustomGameStatusText.Text = $"「{id}」不是自定义条目（内置游戏本身删不掉）。"; return; }
            GameProfileStore.Delete(id);
            RefreshGameList(isBuiltIn ? id : "CS2");
            CustomGameStatusText.Text = $"已删除自定义条目「{id}」" + (isBuiltIn ? "，该游戏已恢复为内置系数。" : "。");
        }
        catch (Exception ex) { CustomGameStatusText.Text = "删除失败：" + ex.Message; }
    }

    private void SolveYawClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!double.TryParse(DpiBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dpi) || dpi <= 0)
                throw new ArgumentException("请先在上面填好 DPI。");
            if (TryDouble(YawSolveSensitivityBox.Text) is not { } sensitivity) throw new ArgumentException("请填写游戏内灵敏度。");
            if (TryDouble(YawSolveCmBox.Text) is not { } cm) throw new ArgumentException("请填写它对应的 cm/360。");
            if (GameProfileStore.YawFromKnownPair(dpi, sensitivity, cm) is not { } value)
                throw new ArgumentException("这组数据解不出合理的 yaw，请检查三个数值。");
            CustomYawBox.Text = GameProfileStore.FormatYaw(value);
            YawSolveStatusText.Text = $"解出 yaw = {GameProfileStore.FormatYaw(value)}（{dpi:0} DPI · 灵敏度 {sensitivity:0.0000} ↔ {cm:0.00} cm/360）；确认游戏名称后保存即可。";
        }
        catch (Exception ex) { YawSolveStatusText.Text = "反解失败：" + ex.Message; }
    }
    private void CharacterDistanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrainingDistanceBox == null) return;
        TrainingDistanceBox.IsEnabled = CharacterDistanceBox.SelectedIndex == 3;
        if (CharacterDistanceBox.SelectedIndex is >= 0 and < 3)
            TrainingDistanceBox.Text = new[] { 6, 10, 16 }[CharacterDistanceBox.SelectedIndex].ToString(CultureInfo.InvariantCulture);
    }
    private static double Number(TextBox box, double minimum, double maximum, string label)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentException($"{label}须在 {minimum}–{maximum} 之间。");
        return value;
    }
    private async void TestClicked(object sender, RoutedEventArgs e)
        => await StartTestAsync();
    private async Task StartTestAsync()
    {
        if (_session || _stopping) return;
        CancellationToken cancellation = default;
        try
        {
            if (_input == null) throw new InvalidOperationException("RawInput 尚未就绪。");
            if (_calibrating) throw new InvalidOperationException("请先结束 DPI 测量。");
            _dpi = Number(DpiBox, 100, 32000, "DPI"); _fov = Number(FovBox, 60, 140, "水平视场角");
            _trainingDistance = CharacterDistanceBox.SelectedIndex == 3 ? Number(TrainingDistanceBox, 2, 60, "人物距离") : new[] { 6.0, 10.0, 16.0 }[Math.Clamp(CharacterDistanceBox.SelectedIndex, 0, 2)];
            _crossLength = Number(CrosshairLengthBox, 1, 40, "准心线长"); _crossGap = Number(CrosshairGapBox, 0, 30, "准心间隙"); _crossThickness = Number(CrosshairThicknessBox, 1, 8, "准心线宽");
            _crossBrush = CrosshairColorBox.SelectedIndex switch { 1 => Brushes.Lime, 2 => Brushes.White, 3 => Brushes.OrangeRed, _ => Brushes.Cyan };
            _game = GameProfileStore.Resolve(SelectedGameId())
                ?? throw new ArgumentException($"未知游戏「{SelectedGameId()}」：请先在「自定义 / 修正游戏换算系数」里填好它的腰射 yaw，本软件不猜系数。");
            (double startCm, string startSource) = ComputeStartCenter(_game, _dpi);
            if (startCm < 5 || startCm > 150) throw new ArgumentException("起点换算后超出 5–150 cm/360，请检查 DPI、灵敏度与垫宽。");
            double rangeShare = SearchRangeShare;
            _low = StartPointModel.Range(startCm, rangeShare).Low;
            _high = StartPointModel.Range(startCm, rangeShare).High;
            _cm = startCm; _centerCm = startCm;
            _notes.Add($"起点 {startCm:0.00} cm/360（{startSource}），搜索区间 {_low:0.0}–{_high:0.0} cm/360。");
            _modes = TestModeBox.SelectedIndex switch { 1 => [TrialMode.Click], 2 => [TrialMode.Track], 3 => [TrialMode.MovingClick], _ => [TrialMode.Click, TrialMode.Track, TrialMode.MovingClick] };
            if (DisplayModeBox.SelectedItem is not DisplayMode mode) throw new InvalidOperationException("请选择显示器支持的模式。");
            _lockedDevice = (DeviceBox.SelectedItem as MouseDevice)?.Handle ?? 0;
            _modeIndex = _pilotCount = _badBlocks = 0; _usedSeconds = 0; _inputChecked = false; _positiveInput = _negativeInput = 0; _seed = Random.Shared.Next();
            _searches.Clear(); _difficulties.Clear(); _pilots.Clear(); _notes.Clear(); _validation = null; _searchSchedule = null; _request = null; _frameTimes.Clear(); _previousFrame = 0;
            _summaryCandidates = [];
            _blockKinematics.Clear();
            _reviewConsent.Reset(); _displayMismatch = null;
            _motionVersion = _resumeState?.MotionVersion ?? MotionScript.CurrentVersion;
            _evidenceVersion = _resumeState?.EvidenceVersion ?? CompositeValidation.CurrentEvidenceVersion;
            if (_resumeState is {} restoreState)
            {
                _game=restoreState.Game; _dpi=restoreState.Dpi; _fov=restoreState.Fov; _trainingDistance=restoreState.Distance;
                _low=restoreState.Low;_high=restoreState.High;_seed=restoreState.Seed;_modes=restoreState.Modes;
                foreach(var search in restoreState.Searches) _searches[search.Mode]=search;
                foreach(var item in restoreState.Difficulties) _difficulties[item.Key]=new(item.Value.DistanceScale,item.Value.SpeedScale,item.Value.Deadline);
                _pilots.AddRange(restoreState.Blocks.Select(b=>b.Result).Where(b=>b.Request.Purpose==TrialPurpose.Pilot));
                _validation=restoreState.Validation;
                _searchSchedule=new(_modes,restoreState.ScheduleCursor);
                if(restoreState.SearchEvidenceIsPrior) _notes.Add("显示条件变化：历史分项成绩仅用于生成候选；当前统一设置只比较新显示条件下的18块完整复核。旧复核成绩不混入当前比较。");
            }
            _camera.Yaw=0; _camera.Pitch=0;
            _session = true; TestButton.IsEnabled = false; SettingsPanel.IsEnabled = false; NavList.IsEnabled = false;
            _sessionCancellation?.Dispose(); _sessionCancellation = new(); cancellation = _sessionCancellation.Token;
            _scene.Visibility = Visibility.Visible; UpdateLayout();
            await _scene.Ready.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            if (_scene.Error != null) throw _scene.Error;
            _displayTransition = true;
            try
            {
                SaveDesktopState();
                _displayLease = _displays.ApplyTemporary(mode); EnterFullScreen(_displayLease.AppliedMode.Bounds);
                _lastSessionDisplay = _displayLease.AppliedMode;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                ApplyFullScreenBounds(); UpdateLayout();
                Activate();
                _scene.SetRefresh(_displayLease.AppliedMode.RefreshHz);
                await VerifyFullScreenAsync(cancellation);
            }
            finally { _displayTransition = false; }
            _trace = new(Path.Combine(DataDirectory, "traces"));
            _sessionId = Path.GetFileNameWithoutExtension(_trace.Path); _tracePath = _trace.Path;
            _trace.Add(new { Kind = "session", SchemaVersion = 2, ScoreVersion = CalibrationScoring.Version, MotionVersion = _motionVersion, EvidenceVersion = _evidenceVersion, Dpi = _dpi, Fov = _fov, CharacterDistanceMeters = _trainingDistance, Game = _game, Seed = _seed, Low = _low, High = _high, Modes = _modes, Display = _displayLease.AppliedMode, Started = DateTimeOffset.Now, ResumedFrom = _resumeState?.SourcePath, SearchEvidenceIsPrior = _resumeState?.SearchEvidenceIsPrior??false, PriorDisplay=_resumeState?.PriorDisplay });
            if(_resumeState is {} carried)
            {
                foreach(var block in carried.Blocks)
                {
                    _trace.Add(new {Kind="sampleBatch",BlockId=block.Result.Request.Id,Events=block.Events,Samples=Array.Empty<object>(),Carried=true});
                    _trace.Add(new {Kind="block",Result=block.Result});
                }
                foreach(var difficulty in carried.Difficulties)
                    _trace.Add(new {Kind="resumeDifficulty", Mode=difficulty.Key, Difficulty=difficulty.Value});
                foreach(var archived in carried.ArchivedReview)
                    _trace.Add(new {Kind="priorValidation", Result=archived.Result, Events=archived.Events, Display=carried.PriorDisplay, ExcludedFromCurrentReview=true});
                _trace.Add(new {Kind="resumeConsent",Accepted=true,Source=carried.SourcePath});
            }
            TryClientBounds(out var verifiedClient);
            _trace.Add(new { Kind = "displayVerified", Selected = mode, Actual = _displayLease.AppliedMode, Client = new { verifiedClient.Left, verifiedClient.Top, verifiedClient.Right, verifiedClient.Bottom }, SurfaceWidth = _scene.SurfaceWidth, SurfaceHeight = _scene.SurfaceHeight, WindowDpi = GetDpiForWindow(new WindowInteropHelper(this).Handle) });
            _safetyTimer.Start(); _scoringTimer.Start();
            if (_displayLease.IsChanged)
            {
                SetPhase(SessionPhase.DisplayConfirm); _stageStart = Stopwatch.GetTimestamp();
                ShowOverlay("确认显示模式", $"实际回读：{_displayLease.AppliedMode}\n15 秒内按 Enter 保留；Esc 或超时恢复。", "保留模式 · Enter", false);
            }
            else ShowSessionEntry();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (cancellation.IsCancellationRequested) return; if (_session) await StopSessionAsync(ex.Message); else ResultText.Text = ex.Message; }
    }
    private void ShowOverlay(string title, string text, string ready = "准备完成 · Enter", bool demo = true)
    {
        ReleaseMouse(); _scene.SetPlayback(null); _scene.HideActor(); _scene.Visibility = Visibility.Hidden; InstructionTitle.Text = title; InstructionText.Text = text; ReadyButton.Content = ready;
        DemoButton.Visibility = demo ? Visibility.Visible : Visibility.Collapsed; InstructionOverlay.Visibility = Visibility.Visible; ReadyButton.Focus();
        FinishSearchButton.Visibility = _phase == SessionPhase.ReviewConsent ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowInstructions()
    {
        SetPhase(SessionPhase.Instructions); _pilotCount = 0; _difficulties[ActiveMode] = new(1, 1, 1.2);
        ShowOverlay($"项目 {_modeIndex + 1}/{_modes.Length} · {ModeName(ActiveMode)} · 操作说明", Operation(ActiveMode) + "\n鼠标控制第一人称视角，准心固定中心。\n每块预告2秒、适应2秒、计分12秒；P暂停，Esc或右键结束。\n三项先分别预检，随后交错比较；仅在块间调整灵敏度。\n先看演示，再按准备完成开始。");
        StartDemo();
    }
    private void ShowSessionEntry()
    {
        if(!_resumeEntry || _resumeState==null) { ShowInstructions(); return; }
        var nextMode=_resumeState.PendingPilot ?? _validation?.NextRequest()?.Mode ?? _searches.Values.FirstOrDefault(s=>!s.IsComplete)?.Mode ?? _modes[0];
        _modeIndex=Array.IndexOf(_modes,nextMode); SetPhase(SessionPhase.Instructions);
        ShowOverlay("从JSONL继续 · 准备开始",_resumeState.Description+
            $"\n\n沿用 {_game.Id} · {_dpi:0} DPI · FOV {_fov:0}° · 人物距离 {_trainingDistance:0.0}m。\n请保持鼠标实际DPI不变；更换DPI或设备请开始新会话。\n"+
            Operation(nextMode)+"\n先检查左右RawInput，再从预告阶段开始；已有完整块不会重做。\n续测沿用原记录的动作和比较版本，避免混算。", "准备完成 · 检查输入",false);
    }
    private void ContinueImportedSession()
    {
        _resumeEntry=false;
        if(_resumeState!.PendingPilot is {} mode)
        {
            _modeIndex=Array.IndexOf(_modes,mode);
            _pilotCount=_pilots.Count(p=>p.Request.Mode==mode); StartPilot();
        }
        else ScheduleNext();
    }
    private static string Operation(TrialMode mode) => mode switch
    {
        TrialMode.Track => "持续贴住人物头部，无需点击。同一人物连续移动12秒。",
        TrialMode.Click => "瞄准人物头部后单击。每时段只接受第一次点击；错点或超时算失败。",
        _ => "等待合适时机，单击移动人物的头部。每时段只接受第一次点击；错点或超时算失败。"
    };
    private void DemoClicked(object sender, RoutedEventArgs e) { if (_phase == SessionPhase.Instructions) StartDemo(); }
    private void StartDemo()
    {
        SetPhase(SessionPhase.Demo); _stageStart = Stopwatch.GetTimestamp(); InstructionOverlay.Visibility = Visibility.Collapsed;
        _script = MotionScript.Create(_game.TrackingFamily ? MotionProfile.Mobile : MotionProfile.Tactical, 0, _trainingDistance, .6, _fov, Aspect, _motionVersion);
        _scene.Visibility = Visibility.Visible; _scene.SetPlayback(new(_script, ActiveMode, 0, _stageStart, 1.2, true));
        PhaseHint.Text = "操作演示（不计分）：" + Operation(ActiveMode);
    }
    private void EndDemo()
    {
        _scene.SetCamera(_camera.Yaw, _camera.Pitch, _fov); SetPhase(SessionPhase.Instructions);
        ShowOverlay(ModeName(ActiveMode) + " · 准备开始", Operation(ActiveMode) + "\n鼠标控制视角，准心固定在中心。人物出现在视野外时，按箭头找回。\n预告、适应与切换不计分；不自动归正相机。需要时可重看演示。");
    }
    private void ReadyClicked(object sender, RoutedEventArgs e) => Ready();
    private void Ready()
    {
        if (!_session) return;
        switch (_phase)
        {
            case SessionPhase.DisplayConfirm: ShowSessionEntry(); break;
            case SessionPhase.Instructions:
                InstructionOverlay.Visibility = Visibility.Collapsed;
                if (!_inputChecked) { SetPhase(SessionPhase.InputCheck); _stageStart = Stopwatch.GetTimestamp(); _scene.Visibility = Visibility.Visible; _scene.SetPlayback(null); CaptureTestMouse(); PhaseHint.Text = "输入检查：请向左、向右移动鼠标"; }
                else if(_resumeEntry) ContinueImportedSession(); else StartPilot(); break;
            case SessionPhase.Rest: StartNextMode(); break;
            case SessionPhase.Paused: Resume(); break;
            case SessionPhase.ReviewConsent:
                _reviewConsent.Accept(); _reviewConsent.RequireAccepted();
                _trace?.Add(new { Kind = "reviewConsent", Accepted = true, Tick = Stopwatch.GetTimestamp(), Blocks = 18 });
                _validation = new(_game, _searches.ToDictionary(x => x.Key, x => x.Value.BestCm360), _seed, _evidenceVersion);
                FinishSearchButton.Visibility = Visibility.Collapsed; ScheduleNext(); break;
        }
    }
    private void StartPilot()
    {
        _pilotCount++; StartBlock(new($"pilot-{ActiveMode}-{_pilotCount}", ActiveMode, TrialPurpose.Pilot, Math.Sqrt(_low * _high), (_pilotCount - 1) % 2));
    }
    private void StartBlock(BlockRequest request)
    {
        _request = request; _cm = request.Cm360; _blockYaw = _camera.Yaw; _events.Clear(); _samples.Clear();
        var difficulty = _difficulties[request.Mode]; _deadline = difficulty.Deadline;
        double distance = _trainingDistance * difficulty.DistanceScale;
        double pathFov = request.Mode == TrialMode.Track ? _fov : Math.Max(20, _fov - 32);
        _script = MotionScript.Create(_game.TrackingFamily ? MotionProfile.Mobile : MotionProfile.Tactical, request.ScriptId, Math.Max(2, distance), difficulty.SpeedScale, pathFov, Aspect, _motionVersion);
        _runner = new(request, _deadline, _camera, PoseAt, _events);
        _blockStart = Stopwatch.GetTimestamp(); _previousFrame = 0; InstructionOverlay.Visibility = Visibility.Collapsed; SetPhase(SessionPhase.Preview); CaptureTestMouse();
        _scene.Visibility = Visibility.Visible; _scene.SetPlayback(new(_script, request.Mode, _blockYaw, _blockStart, _deadline));
        _trace?.Add(new { Kind = "blockStart", Request = request, Difficulty = difficulty, Script = _script.ScriptId, _script.Distance, _script.PathScale, BlockYaw = _blockYaw, CameraYaw = _camera.Yaw, CameraPitch = _camera.Pitch, Fov = _fov, Aspect, Sensitivity = _game.Sensitivity(_dpi, _cm), EDpi = _game.Edpi(_dpi, _cm) });
    }
    private ActorPose PoseAt(double time, int slot)
    {
        double offset = _request!.Mode == TrialMode.Track ? 0 : TargetLayout.Offset(_request.ScriptId, slot, _motionVersion);
        return _script!.Sample(_request.Mode == TrialMode.Click ? 0 : time).RotateYaw(_blockYaw + offset);
    }
    private void Invalidate(string reason) => _runner?.Invalidate(reason);
    private BlockClock AdvanceBlock(long now)
    {
        var clock = _runner!.Advance(Seconds(now - _blockStart)); SetPhase(clock.Phase); return clock;
    }
    private void CompleteBlock()
    {
        if (_scene.LastStalledBlock == _blockStart) Invalidate("计分期间原生3D帧间隔超过100毫秒");
        var request = _request!; var result = _runner!.Complete();
        _trace?.Add(new { Kind = "sampleBatch", BlockId = request.Id, Events = _events.ToArray(), Samples = _samples.ToArray() });
        _trace?.Add(new { Kind = "block", Result = result });
        _blockKinematics[request.Id] = BlockKinematics();   // 必须在下一行 _samples.Clear() 之前算
        _usedSeconds += 16; _request = null; _runner = null; _events.Clear(); _samples.Clear();
        if (request.Purpose == TrialPurpose.Pilot) _pilots.Add(result);
        else if (request.Purpose == TrialPurpose.Search) _searches[request.Mode].Submit(result);
        else _validation!.Submit(result);
        _badBlocks = result.Valid ? 0 : _badBlocks + 1;
        ResultText.Text = $"{ModeName(request.Mode)} · {request.Cm360:0.0} cm/360 · {result.Score:0.0} 表现分\n" +
            (request.Mode == TrialMode.Track ? $"贴靶 {result.TrackingShare:P0}，误差 {result.MeanAngularErrorRadii:0.00} 个头部角半径。" : $"命中 {result.Hits}/6，错点 {result.Misses}，超时 {result.Timeouts}。") +
            (result.Valid ? "" : "\n排除本块：" + string.Join("；", result.InvalidReasons));
        if (_badBlocks >= 3) { _ = StopSessionAsync("连续三个块出现采样或帧时间故障，请检查性能或输入设备。"); return; }
        if (_usedSeconds >= 1200) { _ = StopSessionAsync("达到20分钟测试预算，未完成的比较不作推荐。"); return; }
        if (request.Purpose == TrialPurpose.Pilot)
        {
            bool passed = result.Valid && (request.Mode == TrialMode.Track ? result.TrackingShare >= .30 : result.Hits >= 4);
            if (passed && _pilotCount >= 2) { _searches[request.Mode] = new(request.Mode, _low, _high, _seed + _modeIndex); NextModeOrValidation(); }
            else if (_pilotCount < 3)
            {
                if (!passed && result.Valid)
                {
                    var prior = _difficulties[request.Mode];
                    _difficulties[request.Mode] = prior with { DistanceScale = Math.Max(.50, prior.DistanceScale * .75), SpeedScale = Math.Max(.25, prior.SpeedScale * .65), Deadline = 1.5 };
                }
                else if (_pilotCount == 1 && request.Mode != TrialMode.Track && result.Hits == 6 && result.MeanShotSeconds < .5)
                    _difficulties[request.Mode] = _difficulties[request.Mode] with { Deadline = .9 };
                StartPilot();
            }
            else
            {
                _notes.Add($"{ModeName(request.Mode)}：难度预检未通过（{(result.Valid ? "未达到命中4/6或贴靶30%的门槛" : string.Join("；", result.InvalidReasons))}），停止该项，不生成该项建议。");
                NextModeOrValidation();
            }
        }
        else
        {
            ScheduleNext();
        }
    }
    private void ScheduleNext()
    {
        BlockRequest? next = _validation != null ? _validation.NextRequest() : _searchSchedule!.Next(_searches);
        if (next != null) { _modeIndex = Array.IndexOf(_modes, next.Mode); StartBlock(next); return; }
        if (_validation != null) { _ = StopSessionAsync("完整复核已结束。"); return; }
        if (_modes.Length == 3 && _searches.Count == 3 && _searches.Values.All(x => x.HasEvidence && x.IsComplete))
        {
            _reviewConsent.Request(); SetPhase(SessionPhase.ReviewConsent);
            var preview = new CompositeValidation(_game, _searches.ToDictionary(x => x.Key, x => x.Value.BestCm360), _seed, _evidenceVersion);
            ShowOverlay("第1阶段已完成 · 等待你的选择", CompositeReviewPresentation.Consent(preview, _dpi), "开始第2阶段 · 18块复核", false);
            _trace?.Add(new { Kind = "reviewConsent", Accepted = false, State = "awaiting", Tick = Stopwatch.GetTimestamp() });
        }
        else _ = StopSessionAsync("分项测试已结束。");
    }
    private void NextModeOrValidation()
    {
        _modeIndex++;
        if (_resumeState != null)
            while (_modeIndex < _modes.Length && (_searches.ContainsKey(_modes[_modeIndex]) || _pilots.Count(p=>p.Request.Mode==_modes[_modeIndex])>=3)) _modeIndex++;
        if (_modeIndex < _modes.Length)
        {
            SetPhase(SessionPhase.Rest); _stageStart = Stopwatch.GetTimestamp();
            ShowOverlay("休息15秒", "放松手腕和手臂。下一项：" + ModeName(ActiveMode), "跳过休息 · Enter", false); return;
        }
        _searchSchedule = new(_modes); ScheduleNext();
    }
    private void StartNextMode() { _usedSeconds += Math.Min(15, Since(_stageStart)); ShowInstructions(); }
    private void FinishSearchClicked(object sender, RoutedEventArgs e) { if (_phase == SessionPhase.ReviewConsent) _ = StopSessionAsync("分项搜索已完成，用户选择结束；未进行额外综合复核。"); }
    private void RawMoved(nint device, int dx, int dy, long timestamp)
    {
        if (_calibrating)
        {
            if (_lockedDevice == 0) _lockedDevice = device;
            if (device == _lockedDevice) { _calibrationX += dx; _calibrationY += dy; }
        }
        if (!_session || _stopping || device == 0 || (_lockedDevice != 0 && device != _lockedDevice)) return;
        if (_lockedDevice == 0) _lockedDevice = device;
        if (_phase == SessionPhase.InputCheck) { if (dx > 0) _positiveInput += dx; else _negativeInput -= dx; }
        if (_phase is not (SessionPhase.InputCheck or SessionPhase.Preview or SessionPhase.Adaptation or SessionPhase.Scoring or SessionPhase.Transition)) return;
        if (InBlock) AdvanceBlock(timestamp);
        _camera.ApplyCounts(dx, dy, _dpi, _cm);
        _scene.SetCamera(_camera.Yaw, _camera.Pitch, _fov);
        if (InBlock)
        {
            var clock = AdvanceBlock(timestamp); var pose = PoseAt(clock.ScoredTime, clock.Slot);
            _samples.Add(new { Tick = timestamp, Time = Seconds(timestamp - _blockStart), Phase = _phase.ToString(), DX = dx, DY = dy,
                CameraYaw = _camera.Yaw, CameraPitch = _camera.Pitch, HeadX = pose.HeadCenter.X, HeadY = pose.HeadCenter.Y, HeadZ = pose.HeadCenter.Z,
                RootX = pose.RootPosition.X, RootY = pose.RootPosition.Y, RootZ = pose.RootPosition.Z, pose.Crouch, pose.FacingYaw, pose.GaitPhase });
        }
    }
    private void RawClicked(nint device, long timestamp)
    {
        if (!_session || _stopping || !InBlock || device != _lockedDevice || _request!.Mode == TrialMode.Track) return;
        var clock = AdvanceBlock(timestamp);
        if (clock.Phase != SessionPhase.Scoring || _resolved[clock.Slot]) return;
        _runner!.Click(Seconds(timestamp - _blockStart));
        _scene.SetResolved(_resolved);
    }
    private void Render(object? sender, EventArgs e)
    {
        if (!_session || _stopping) return;
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = rendering.RenderingTime;
        }
        long now = Stopwatch.GetTimestamp(); double frameMs = _previousFrame == 0 ? 0 : Seconds(now - _previousFrame) * 1000; _previousFrame = now;
        bool updateHud = Seconds(now - _lastHud) >= .1;
        if (updateHud) _lastHud = now;
        if (frameMs > 0) { _frameTimes.Enqueue(frameMs); while (_frameTimes.Count > 480) _frameTimes.Dequeue(); }
        if (InBlock && _scene.LastStalledBlock == _blockStart) Invalidate("计分期间原生3D帧间隔超过100毫秒");
        if (Seconds(now - _lastStatus) >= .25)
        {
            _lastStatus = now; var sorted = _frameTimes.Order().ToArray();
            double fps = sorted.Length == 0 ? 0 : 1000 / sorted.Average(), p95 = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * .95)];
            var gpu = _scene.Metrics;
            FrameStatus.Text = $"模式 {_displayLease?.AppliedMode.Width}×{_displayLease?.AppliedMode.Height} · {_displayLease?.AppliedMode.RefreshHz:0.##} Hz · 原生3D {gpu.Fps:0} FPS · P95 {gpu.P95Ms:0.0} ms{(_scene.Multisample ? " · 4× MSAA" : "")}\n渲染区 {_scene.SurfaceWidth}×{_scene.SurfaceHeight} · {gpu.Device} · 会话回调 {fps:0} Hz";
            _trace?.Add(new { Kind = "frame", Tick = now, Phase = _phase.ToString(), FrameMs = frameMs, CallbackFps = fps, P95Ms = p95, NativeFps = gpu.Fps, NativeP95Ms = gpu.P95Ms, gpu.Presents, gpu.Device, gpu.VSync });
        }
        if (_phase == SessionPhase.Demo)
        {
            double elapsed = Since(_stageStart);
            if (elapsed >= 12) { _usedSeconds += 12; EndDemo(); return; }
            int slot = Math.Min(5, (int)(elapsed / 2)); double slotTime = elapsed - slot * 2;
            double offset = ActiveMode == TrialMode.Track ? 0 : TargetOffsets[slot];
            var pose = _script!.Sample(ActiveMode == TrialMode.Click ? 0 : elapsed).RotateYaw(offset);
            var angles = _camera.TargetAngles(pose);
            double amount = Math.Clamp(slotTime / .6, 0, 1);
            _scene.SetCamera(ActiveMode == TrialMode.Track ? angles.Yaw : angles.Yaw * amount,
                ActiveMode == TrialMode.Track ? angles.Pitch : angles.Pitch * amount, _fov);
            if (ActiveMode != TrialMode.Track && slotTime >= 1.1) _scene.HideActor(); else _scene.SetActor(pose, false);
            if (updateHud)
            {
                PhaseHint.Text = ActiveMode == TrialMode.Track ? "演示：持续贴住移动的头部，无需点击" : slotTime < .6 ? "演示：移动鼠标，把准心对准头部" : slotTime < 1.1 ? "演示：瞄准后单击" : "演示：命中后等待下一时段，不用归正鼠标";
                ArenaStatus.Text = $"项目 {_modeIndex + 1}/{_modes.Length} · {ModeName(ActiveMode)} · 操作演示（不计分）";
                UpdateNativeHud();
            }
            return;
        }
        if (_phase == SessionPhase.InputCheck)
        {
            _scene.SetCamera(_camera.Yaw, _camera.Pitch, _fov);
            if (updateHud) { InputStatus.Text = $"左右RawInput计数：{_negativeInput} / {_positiveInput} · {_dpi:0} DPI × {_game.Sensitivity(_dpi, _cm):0.0000} = {_game.Edpi(_dpi, _cm):0.##} eDPI"; UpdateNativeHud(); }
            if (_positiveInput >= 20 && _negativeInput >= 20) { _inputChecked = true; if(_resumeEntry) ContinueImportedSession(); else StartPilot(); }
            else if (Since(_stageStart) >= 12) _ = StopSessionAsync("RawInput输入检查未通过：未收到同一实体鼠标左右移动计数。请检查设备选择。");
            return;
        }
        if (!InBlock) return;
        var clock = TestSessionTimeline.At(Seconds(now - _blockStart), _request!.Mode, _deadline);
        if (_request.Mode != TrialMode.Track && _resolved[clock.Slot] && clock.Phase == SessionPhase.Scoring) clock = clock with { Phase = SessionPhase.Transition };
        if (Seconds(now - _blockStart) >= 16) return; // The scoring timer seals the block independently of WPF redraws.
        var current = PoseAt(clock.ScoredTime, clock.Slot); _scene.SetCamera(_camera.Yaw, _camera.Pitch, _fov);
        bool hidden = _request!.Mode != TrialMode.Track && (clock.Phase == SessionPhase.Transition || _resolved[clock.Slot] && clock.Phase == SessionPhase.Scoring);
        if (hidden) _scene.HideActor(); else _scene.SetActor(current, clock.Phase == SessionPhase.Preview);
        if (!updateHud) return;
        var projection = _camera.Project(current.HeadCenter, _fov, Aspect);
        if (!hidden && !projection.OnScreen)
        {
            var target = _camera.TargetAngles(current); double bearing = ((target.Yaw - _camera.Yaw + 540) % 360 + 360) % 360 - 180;
            DirectionHint.Text = Math.Abs(bearing) > 5 ? (bearing > 0 ? "→ 人物在右侧" : "← 人物在左侧") : (target.Pitch > _camera.Pitch ? "↑ 人物在上方" : "↓ 人物在下方");
        }
        else DirectionHint.Text = "";
        string purpose = _request.Purpose switch { TrialPurpose.Pilot => $"难度预检 {_pilotCount}/3", TrialPurpose.Search => $"正式比较 第{_searches[_request.Mode].Results.Count + 1}/14轮", _ => $"额外综合复核 {_validation!.Results.Count + 1}/18块" };
        string progress = string.Join(" / ", _modes.Select(mode => $"{ModeName(mode)} {_searches.GetValueOrDefault(mode)?.Results.Count ?? 0}/14"));
        ArenaStatus.Text = $"{ModeName(_request.Mode)} · {purpose} · {_cm:0.0} cm/360\n{_game.Id} · {_dpi:0} DPI × {_game.Sensitivity(_dpi, _cm):0.0000} 灵敏度 = {_game.Edpi(_dpi, _cm):0.##} eDPI";
        InputStatus.Text = $"{progress}\n相机 {_camera.Yaw:0.0}° / {_camera.Pitch:0.0}° · {_script!.ScriptId} · 出生距离 {_script.Distance:0.0}m · FOV {_fov:0}°";
        if (_request.Purpose == TrialPurpose.Validation)
        {
            ArenaStatus.Text = CompositeReviewPresentation.Current(_validation!, _request) + $"\n{_game.Id} · {_dpi:0} DPI × {_game.Sensitivity(_dpi, _cm):0.0000} = {_game.Edpi(_dpi, _cm):0.0} eDPI · {_cm:0.00} cm/360";
            int valid = _validation!.Results.Count(r => r.Valid);
            InputStatus.Text = CompositeReviewPresentation.Progress(_validation) + $"\n总计 {valid}/18有效 · 还缺{18-valid}块 · 每项3档×2遍，共6块";
        }
        PhaseHint.Text = clock.Phase switch
        {
            SessionPhase.Preview => $"{BlockOperation(_request.Mode)} · 出生预告{Math.Ceiling(2 - Seconds(now - _blockStart))}秒 · 不计分",
            SessionPhase.Adaptation => BlockOperation(_request.Mode)+" · 适应当前档 · 不计分",
            SessionPhase.Transition => $"目标 {clock.Slot + 1}/6 · 切换等待 · 不计分",
            _ when _request.Mode == TrialMode.Track => $"持续贴住头部，无需点击 · 剩余 {Math.Ceiling(12 - clock.ScoredTime)}秒",
            _ when _resolved[clock.Slot] => $"目标 {clock.Slot + 1}/6 已结束 · 等待下一时段",
            _ => $"目标 {clock.Slot + 1}/6 · 瞄准头部后单击 · 剩余 {Math.Max(0, _deadline - clock.SlotTime):0.0}秒"
        };
        UpdateNativeHud();
    }
    private static string BlockOperation(TrialMode mode) => mode switch
    {
        TrialMode.Track=>"跟随：持续贴头，不用点击",
        TrialMode.Click=>"点击：瞄准头部后单击",
        _=>"移动点击：对准移动头部再单击"
    };
    private void UpdateNativeHud() => _scene.SetHud(ArenaStatus.Text + "\n" + InputStatus.Text + "\n" + FrameStatus.Text, PhaseHint.Text, DirectionHint.Text);
    private void SetPhase(SessionPhase phase)
    {
        if (_phase == phase) return; _state.Change(phase);
        _trace?.Add(new { Kind = "phase", Phase = phase.ToString(), Tick = Stopwatch.GetTimestamp(), BlockId = _request?.Id });
    }
    private void SafetyTick(object? sender, EventArgs e)
    {
        if (!_session || _stopping) return;
        try
        {
            _displayLease?.Heartbeat();
            if (_fullScreen && !_displayTransition && Stopwatch.GetTimestamp() > _layoutSettlingUntil)
            {
                string? mismatch = FullScreenMismatch();
                if (mismatch != null)
                {
                    if (_displayMismatch != null) throw new InvalidOperationException(mismatch + "。已停止测试并恢复设置。");
                    _displayMismatch = mismatch;
                    if (InBlock) Invalidate("显示边界变化，计分块无效");
                    ApplyFullScreenBounds();
                }
                else _displayMismatch = null;
            }
            if (_scene.Error != null) throw _scene.Error;
            if (_trace?.Error != null) throw new IOException("轨迹保存失败：" + _trace.Error.Message);
            if (_displayLease != null && !_displays.EnumerateDisplays().Any(x => x.DeviceName == _displayLease.AppliedMode.DeviceName && x.DeviceId == _displayLease.AppliedMode.DeviceId)) throw new InvalidOperationException("测试显示器已断开。");
            if (_phase == SessionPhase.DisplayConfirm)
            {
                double remaining = Math.Max(0, 15 - Since(_stageStart));
                InstructionText.Text = $"实际回读：{_displayLease!.AppliedMode}\n{Math.Ceiling(remaining)}秒内按Enter保留，Esc或超时恢复。";
                if (remaining <= 0) _ = StopSessionAsync("显示模式确认超时，停止测试并恢复原设置。");
            }
            else if (_phase == SessionPhase.Rest && Since(_stageStart) >= 15) StartNextMode();
        }
        catch (Exception ex) { _ = StopSessionAsync(ex.Message); }
    }
    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (!_session) return;
        if (e.Key == Key.Escape) { e.Handled = true; _ = StopSessionAsync("用户结束测试。"); }
        else if (e.Key == Key.Enter) { e.Handled = true; if (!e.IsRepeat) Ready(); }
        else if (e.Key == Key.P && !e.IsRepeat)
        {
            e.Handled = true;
            if (_phase == SessionPhase.Paused) Resume();
            else if (InBlock) { FlushInterrupted("暂停，恢复后重做此块"); SetPhase(SessionPhase.Paused); ShowOverlay("测试已暂停", "当前块不参与评分。继续后重做预告、适应和完整计分。", "继续 · P / Enter", false); }
        }
    }
    private void Resume()
    {
        if (_usedSeconds >= 1200) { _ = StopSessionAsync("达到20分钟测试预算，未完成的比较不作推荐。"); return; }
        if (_request != null) StartBlock(_request);
    }
    private void FlushInterrupted(string reason)
    {
        if (_request == null || _events.Count == 0 && _samples.Count == 0) return;
        _usedSeconds += Math.Min(16, Since(_blockStart));
        _trace?.Add(new { Kind = "interrupted", BlockId = _request.Id, Reason = reason, Events = _events.ToArray(), Samples = _samples.ToArray() }); _events.Clear(); _samples.Clear();
    }
    private void ArenaRightClicked(object sender, MouseButtonEventArgs e) { if (_session) { e.Handled = true; _ = StopSessionAsync("用户右键结束测试。"); } }
    private Task StopSessionAsync(string reason)
    {
        if (_stopping) return _stopTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopTask = completion.Task;
        _ = FinishStopAsync(reason, completion);
        return _stopTask;
    }
    private async Task FinishStopAsync(string reason, TaskCompletionSource completion)
    {
        try { await StopSessionCoreAsync(reason); completion.SetResult(); }
        catch (Exception ex)
        {
            ReleaseMouse(); _session = false; _stopping = false;
            ResultText.Text = reason + "\n结束测试时出现异常：" + ex.Message;
            completion.SetResult();
        }
    }
    private async Task StopSessionCoreAsync(string reason)
    {
        if (_stopping) return; _stopping = true;
        try
        {
            _sessionCancellation?.Cancel(); _safetyTimer.Stop(); _scoringTimer.Stop(); FlushInterrupted(reason); _request = null; _runner = null; ReleaseMouse(); InstructionOverlay.Visibility = Visibility.Collapsed; _scene.SetPlayback(null); _scene.HideActor(); _scene.Visibility = Visibility.Hidden;
            _displayTransition = true; DisplayRestoreResult restore = new(true);
            try { if (_displayLease != null) restore = _displayLease.Restore(); ExitFullScreen(); }
            finally { _displayTransition = false; _displayLease = null; }
            string report = reason + "\n" + string.Join("\n", _notes) + "\n\n";
            if (_validation != null) report += CompositeReviewPresentation.Details(_validation, _dpi) + "\n\n";
            else if (_modes.Length == 3) report += "第2阶段综合复核尚未开始（0/18块）。分项搜索完成不等于综合复核完成，以下只展示有证据的分项结果。\n\n";
            report += _searches.Count > 0 ? RecommendationReport.Build(_game, _dpi, _searches.Values, _validation, _trace?.Path) : "尚无完整、有效的灵敏度比较，未生成转换建议。";
            if (!restore.Success) report += "\n显示恢复需要处理：" + restore.Error;
            _trace?.Add(new { Kind = "report", ReportFormatVersion=2, Reason = reason, Notes = _notes.ToArray(), Report = report,
                Game = _game, Dpi = _dpi, TracePath = _trace?.Path,
                Text = RecommendationReport.Build(_game, _dpi, _searches.Values, _validation, _trace?.Path),
                Searches = _searches.Values.ToArray(), Validation = _validation });
            if (_trace != null)
            {
                await _trace.CompleteAsync();
                if (_trace.Error != null) report += "\n记录保存失败：" + _trace.Error.Message;
                else
                {
                    try
                    {
                        string history = Path.Combine(DataDirectory, "history-v2"); Directory.CreateDirectory(history);
                        await File.WriteAllTextAsync(Path.Combine(history, Path.GetFileNameWithoutExtension(_trace.Path) + ".json"), JsonSerializer.Serialize(new { SchemaVersion = 2, ScoreVersion = CalibrationScoring.Version, Date = DateTimeOffset.Now, Game = _game.Id, Dpi = _dpi, Report = report, TracePath = _trace.Path }));
                    }
                    catch (Exception ex) { report += "\n历史记录写入失败：" + ex.Message; }
                }
            }
            report += AppendRetestLabel();
            report += AppendShadowPrediction();
            if (_session) ResultText.Text = report;
            WriteSessionSummary();
            RefreshLabelPanels();
            // 标签是唯一的真瓶颈：刚测完是记忆最新鲜的时刻，若有档位还没打分就直接落在「手感评分」页。
            int target = HasUnratedCandidates() ? 1 : 0;
            if (NavList.SelectedIndex != target) NavList.SelectedIndex = target;
            _trace = null; _session = false; SetPhase(SessionPhase.Finished); PhaseHint.Text = "测试结束 · 查看推荐依据"; DirectionHint.Text = "";
        }
        finally { SettingsPanel.IsEnabled = true; NavList.IsEnabled = true; TestButton.IsEnabled = _input != null; _stopping = false; _resumeState=null;_resumeEntry=false; }
    }
    private void CalibrationClicked(object sender, RoutedEventArgs e)
    {
        if (_session) return;
        try
        {
            double distance = Number(DistanceBox, 2, 100, "测量距离");
            if (!_calibrating)
            {
                _lockedDevice = (DeviceBox.SelectedItem as MouseDevice)?.Handle ?? 0; _calibrationX = _calibrationY = 0; _calibrating = true;
                CalibrationButton.Content = "结束测量 · 空格";
                CalibrationButton.Focus();
                CalibrationText.Text = $"沿尺子水平移动 {distance:0.0}cm，按空格结束；不要把鼠标移回按钮。";
            }
            else
            {
                _calibrating = false; CalibrationButton.Content = "开始测量";
                if (Math.Abs(_calibrationX) < 100 || Math.Abs(_calibrationY) > Math.Abs(_calibrationX) * .3) throw new InvalidOperationException("水平位移不足或偏离方向，请重新测量。");
                double measured = Math.Abs(_calibrationX) * 2.54 / distance; _calibrationSamples.Add(measured);
                if (_calibrationSamples.Count >= 3)
                {
                    var values = _calibrationSamples.TakeLast(3).Order().ToArray();
                    if ((values[2] - values[0]) / values[1] > .10) CalibrationText.Text = "三次测量差距超过10%，请检查距离后重新测量。";
                    else { DpiBox.Text = values[1].ToString("0", CultureInfo.InvariantCulture); CalibrationText.Text = $"三次中位数 {values[1]:0} CPI（需准确测量实际距离）。"; }
                }
                else CalibrationText.Text = $"本次 {measured:0} CPI，请再测 {3 - _calibrationSamples.Count}次。";
            }
        }
        catch (Exception ex) { CalibrationText.Text = ex.Message; }
    }
    private void HistoryClicked(object sender, RoutedEventArgs e)
    {
        if (_session) return;
        var text = new System.Text.StringBuilder(); string folder = Path.Combine(DataDirectory, "history-v2");
        if (Directory.Exists(folder)) foreach (string path in Directory.EnumerateFiles(folder, "*.json").OrderDescending().Take(20))
        {
            try { using var record = JsonDocument.Parse(File.ReadAllText(path)); text.AppendLine(record.RootElement.GetProperty("Date").ToString() + " · " + record.RootElement.GetProperty("ScoreVersion").GetString()); text.AppendLine(record.RootElement.GetProperty("Report").GetString()); text.AppendLine(); }
            catch (Exception ex) { text.AppendLine("无法读取记录：" + ex.Message); }
        }
        string legacy = Path.Combine(DataDirectory, "history.json");
        if (File.Exists(legacy)) { text.AppendLine("旧版记录（评分版本不同，不与新版混算）"); text.AppendLine(File.ReadAllText(legacy)); }
        if (text.Length == 0) text.Append("暂无历史记录。");
        new Window { Title = "AimSense · 历史记录", Owner = this, Width = 900, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = text.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20) } }.ShowDialog();
    }
    private void ImportTraceClicked(object sender, RoutedEventArgs e) { if (!_session && !_stopping) OpenTraceImport(); }

    // ===================== 标签：主观手感 / 真实对局成绩 / 纵向留存 =====================
    // 三类标签都不参与计分与推荐，只写入 %LOCALAPPDATA%\AimSense\labels\labels-v1.jsonl，
    // 供离线模型使用。任何标签读写失败都不允许中断或影响测试流程。

    private sealed record FeelCandidate(double Cm360, double Sensitivity, double EDPi, double? MeanScore);

    private IEnumerable<BlockResult> AllResults() => _searches.Values.SelectMany(x => x.Results)
        .Concat(_validation?.Results ?? Enumerable.Empty<BlockResult>());

    // 影子模式：模型只在这里被调用，输出仅写进报告文本，绝不改变计分、搜索与推荐。
    private string AppendShadowPrediction()
    {
        try
        {
            OfflineModel? model = OfflineModel.Load();
            if (model == null) return "";
            if (!model.CanPredict(CalibrationScoring.Version))
                return $"\n影子预测：模型训练于评分版本 {model.ScoreVersion}，当前为 {CalibrationScoring.Version}，不做跨版本预测。";
            if (!model.CanPredictGame(_game.Id))
                return $"\n影子预测：模型训练于 {model.Game}，本次为 {_game.Id}，不做跨游戏预测。";
            BlockResult[] results = AllResults().ToArray();
            IReadOnlyList<FeelCandidate> candidates = LabelCandidates();
            if (results.Length == 0 || candidates.Count == 0) return "";
            var text = new StringBuilder("\n影子预测（离线模型，不参与推荐，仅供对比与积累）：");
            foreach (FeelCandidate candidate in candidates)
            {
                double probability = model.Predict(CandidateFeatures(results, candidate.Cm360, _centerCm), out int missing);
                text.Append($"\n  {LabelAnalysis.F(candidate.Cm360)} cm/360 → 「{model.Target}」概率 {probability:0.00}");
                if (missing > 0) text.Append($"（{missing} 项特征缺失，已按训练均值填充）");
            }
            text.Append($"\n  模型：{model.Describe()}");
            if (model.Limits() is { Length: > 0 } limits) text.Append($"\n  模型自述局限：{limits}");
            return text.ToString();
        }
        catch (Exception ex) { return "\n影子预测失败：" + ex.Message; }
    }

    // 候选级特征：名字与 ml/train.py 的 candidate-features-1 严格对应；无数据的项直接不填，
    // 由模型按训练均值填充并回报缺失数量，避免"悄悄填 0"造成的伪特征。
    private Dictionary<string, double> CandidateFeatures(IReadOnlyList<BlockResult> results, double cm, double centerCm)
    {
        var features = new Dictionary<string, double>();
        BlockResult[] atCm = results.Where(x => LabelAnalysis.SameCm(x.Request.Cm360, cm)).ToArray();
        BlockResult[] blocks = atCm.Where(x => x.Valid).ToArray();
        features["blocks"] = atCm.Length;
        features["validBlocks"] = blocks.Length;
        double[] scores = blocks.Select(x => x.Score).ToArray();
        if (scores.Length > 0)
        {
            double mean = scores.Average();
            features["meanScore"] = mean; features["maxScore"] = scores.Max(); features["minScore"] = scores.Min();
            features["scoreStd"] = scores.Length > 1 ? Math.Sqrt(scores.Sum(x => (x - mean) * (x - mean)) / (scores.Length - 1)) : 0;
        }
        foreach (TrialMode mode in Enum.GetValues<TrialMode>())
        {
            BlockResult[] modeBlocks = blocks.Where(x => x.Request.Mode == mode).ToArray();
            if (modeBlocks.Length == 0) continue;
            features[mode switch { TrialMode.Click => "clickScore", TrialMode.Track => "trackScore", _ => "movingClickScore" }] = modeBlocks.Average(x => x.Score);
        }
        BlockResult[] clickBlocks = blocks.Where(x => x.Request.Mode != TrialMode.Track).ToArray();
        if (clickBlocks.Length > 0)
        {
            int hits = clickBlocks.Sum(x => x.Hits), misses = clickBlocks.Sum(x => x.Misses), timeouts = clickBlocks.Sum(x => x.Timeouts);
            if (hits + misses + timeouts > 0)
            {
                double total = hits + misses + timeouts;
                features["hitRate"] = hits / total; features["missRate"] = misses / total; features["timeoutRate"] = timeouts / total;
            }
            double[] shots = clickBlocks.Where(x => x.Hits > 0).Select(x => x.MeanShotSeconds).ToArray();
            if (shots.Length > 0) features["meanShotSeconds"] = shots.Average();
        }
        BlockResult[] trackBlocks = blocks.Where(x => x.Request.Mode == TrialMode.Track).ToArray();
        if (trackBlocks.Length > 0)
        {
            features["trackingShare"] = trackBlocks.Average(x => x.TrackingShare);
            features["meanAngularError"] = trackBlocks.Average(x => x.MeanAngularErrorRadii);
        }
        if (blocks.Length > 0)
        {
            features["overshootsPerBlock"] = blocks.Average(x => (double)x.Diagnostics.Overshoots);
            features["correctionsPerBlock"] = blocks.Average(x => (double)x.Diagnostics.Corrections);
            features["meanOvershootRadii"] = blocks.Average(x => x.Diagnostics.MeanOvershootRadii);
            double[] turns = blocks.Where(x => x.Diagnostics.MeanTurnResponseSeconds.HasValue).Select(x => x.Diagnostics.MeanTurnResponseSeconds!.Value).ToArray();
            if (turns.Length > 0) features["meanTurnResponse"] = turns.Average();
            double[] recoveries = blocks.Where(x => x.Diagnostics.MeanRecoverySeconds.HasValue).Select(x => x.Diagnostics.MeanRecoverySeconds!.Value).ToArray();
            if (recoveries.Length > 0) features["meanRecovery"] = recoveries.Average();
        }
        features["cm360"] = cm;
        features["logCm360"] = Math.Log(cm);
        if (centerCm > 0) features["centerRatio"] = cm / centerCm;
        if (_dpi > 0)
        {
            features["dpi"] = _dpi;
            features["sensitivity"] = _game.Sensitivity(_dpi, cm);
            features["edpi"] = _game.Edpi(_dpi, cm);
        }
        return features;
    }

    private string AppendRetestLabel()
    {
        try
        {
            var (label, reason) = LabelAnalysis.EvaluateRetest(AllResults(), _game.Id, CalibrationScoring.Version, _sessionId, _tracePath);
            if (label != null)
            {
                LabelStore.Append(LabelStore.RetestKind, label);
                return $"\n纵向复测：{_game.Id} · {LabelAnalysis.F(label.Cm360)} cm/360 复测 {LabelAnalysis.F(label.RetestScore)} 分，基线 {LabelAnalysis.F(label.BaselineScore)} 分（{LabelAnalysis.Signed(label.Delta)} 分，{label.Basis}，{label.RetestBlocks} 块）。";
            }
            return reason == null ? "" : "\n纵向复测：" + reason;
        }
        catch (Exception ex) { return "\n纵向标签写入失败：" + ex.Message; }
    }

    private IReadOnlyList<FeelCandidate> LabelCandidates()
    {
        var results = AllResults().Where(x => x.Valid).ToArray();
        IEnumerable<double> values = results.Select(x => x.Request.Cm360);
        if (_centerCm > 0) values = values.Concat([_centerCm]);
        var merged = new List<double>();
        foreach (double value in values.OrderBy(x => x))
            if (merged.Count == 0 || !LabelAnalysis.SameCm(merged[^1], value)) merged.Add(value);
        return merged.Take(6).Select(cm =>
        {
            double[] scores = results.Where(x => LabelAnalysis.SameCm(x.Request.Cm360, cm)).Select(x => x.Score).ToArray();
            return new FeelCandidate(cm, _game.Sensitivity(_dpi, cm), _game.Edpi(_dpi, cm), scores.Length == 0 ? null : scores.Average());
        }).ToArray();
    }

    private void WriteSessionSummary()
    {
        try
        {
            SessionCandidateLabel[] candidates = LabelCandidates().Select(c => new SessionCandidateLabel(c.Cm360, c.Sensitivity,
                c.EDPi, c.MeanScore, AllResults().Count(x => x.Valid && LabelAnalysis.SameCm(x.Request.Cm360, c.Cm360)),
                LabelAnalysis.ModeScores(AllResults(), c.Cm360),
                CandidateFeatures(AllResults().ToArray(), c.Cm360, _centerCm), BlockAggregates(c.Cm360))).ToArray();
            if (candidates.Length == 0) return;
            LabelStore.Append(LabelStore.SessionKind, new SessionSummaryLabel(_sessionId, _tracePath, _game.Id, _dpi,
                CalibrationScoring.Version, _centerCm, candidates));
        }
        catch (Exception ex) { _notes.Add("会话候选快照写入失败：" + ex.Message); }
    }

    // 按块计算逐帧运动学。用反射读 `_samples` 里的匿名字段：块结束时只做一次，
    // **不动每帧输入的热路径**（RawMoved 是每个鼠标事件都会跑的）。PropertyInfo 每块只取一次。
    private Dictionary<string, double> BlockKinematics()
    {
        if (_samples.Count == 0) return [];
        System.Reflection.PropertyInfo[]? properties = null;
        var points = new List<KinSample>(_samples.Count);
        foreach (object? sample in _samples)
        {
            if (sample == null) continue;
            properties ??= sample.GetType().GetProperties();
            double Number(string name)
            {
                foreach (System.Reflection.PropertyInfo property in properties)
                    if (property.Name == name)
                    {
                        object? value = property.GetValue(sample);
                        return value switch { double d => d, float f => f, int i => i, long l => l, _ => double.NaN };
                    }
                return double.NaN;
            }
            string phase = "";
            foreach (System.Reflection.PropertyInfo property in properties)
                if (property.Name == "Phase") phase = property.GetValue(sample) as string ?? "";
            points.Add(new KinSample(Number("Time"), Number("DX"), Number("DY"), phase, Number("CameraYaw"), Number("CameraPitch")));
        }
        return KinematicsAnalysis.FromSamples(points);
    }

    // 块级聚合：训练要吃的"jsonl 派生层"里**不需要逐帧样本**就能算的部分（诊断量、过冲、回拉、换向、恢复）。
    // 逐帧运动学（Kin*：路径长度、方向反转、速度峰度…）目前仍只在 Python 导出脚本里算，这是已知缺口。
    private Dictionary<string, double> BlockAggregates(double cm)
    {
        var result = new Dictionary<string, double>();
        BlockResult[] blocks = AllResults().Where(x => x.Valid && LabelAnalysis.SameCm(x.Request.Cm360, cm)).ToArray();
        if (blocks.Length == 0) return result;
        result["blockCount"] = blocks.Length;
        result["overshootsPerBlock"] = blocks.Average(x => (double)x.Diagnostics.Overshoots);
        result["correctionsPerBlock"] = blocks.Average(x => (double)x.Diagnostics.Corrections);
        result["meanOvershootRadii"] = blocks.Average(x => x.Diagnostics.MeanOvershootRadii);
        double[] turns = blocks.Where(x => x.Diagnostics.MeanTurnResponseSeconds.HasValue).Select(x => x.Diagnostics.MeanTurnResponseSeconds!.Value).ToArray();
        if (turns.Length > 0) result["meanTurnResponseSeconds"] = turns.Average();
        double[] recoveries = blocks.Where(x => x.Diagnostics.MeanRecoverySeconds.HasValue).Select(x => x.Diagnostics.MeanRecoverySeconds!.Value).ToArray();
        if (recoveries.Length > 0) result["meanRecoverySeconds"] = recoveries.Average();
        // 逐帧运动学：按块取平均合并进来（口径与 ml/export_dataset.py 逐字一致，已用 45 块 / 675 值对拍验证）
        var kinematics = blocks.Select(x => _blockKinematics.GetValueOrDefault(x.Request.Id)).Where(x => x != null).ToArray();
        if (kinematics.Length > 0)
            foreach (string key in KinematicsAnalysis.Keys)
            {
                double[] values = kinematics.Select(x => x!.GetValueOrDefault(key, double.NaN)).Where(double.IsFinite).ToArray();
                if (values.Length > 0) result[key] = values.Average();
            }
        return result;
    }

    // 本次会话还有档位没打分吗（决定测试结束后落到哪一页）。
    private bool HasUnratedCandidates()
    {
        if (_feelRows.Count == 0) return false;
        int rated = LabelStore.Read(LabelStore.FeelKind).Count(x => LabelStore.Str(x, "SessionId") == _sessionId && _sessionId.Length > 0);
        return rated < _feelRows.Count;
    }

    // 训练数据进度：把"还差多少"摆在眼前，比任何劝说都有效。
    private string TrainingProgressText()
    {
        int feel = LabelStore.Count(LabelStore.FeelKind), match = LabelStore.Count(LabelStore.MatchKind);
        int adoption = LabelStore.Count(LabelStore.AdoptionKind), retest = LabelStore.Count(LabelStore.RetestKind);
        int preference = LabelStore.Count(LabelStore.PreferenceKind);
        const int candidateGoal = 200, sessionGoal = 10;
        int sessions = LabelStore.Read(LabelStore.SessionKind).Count;
        return $"训练数据进度：手感 {feel} 条 · 成对偏好 {preference} 条 · 对局成绩 {match} 条 · 采用 {adoption} · 复测 {retest}（会话快照 {sessions} 场）\n"
            + $"模型门槛：≥{candidateGoal} 条有标签候选、≥{sessionGoal} 场；还差手感 {Math.Max(0, candidateGoal - feel)} 条、会话 {Math.Max(0, sessionGoal - sessions)} 场。"
            + "每场测试结束后为本次比较过的档位打一次分，就是全部的日常投入。";
    }

    private void RefreshLabelPanels()
    {
        try
        {
            IReadOnlyList<FeelCandidate> candidates = LabelCandidates();
            bool fromSummary = false;
            if (candidates.Count == 0 && LabelAnalysis.LastSession() is { } summary)
            {
                // 重启后补评：内存里的会话结果已经随进程结束丢失，改用最近一次会话的快照。
                _sessionId = summary.SessionId; _tracePath = summary.TracePath;
                if (summary.Dpi > 0) _dpi = summary.Dpi;
                _centerCm = summary.CenterCm;
                try { _game = GameProfiles.Get(summary.Game); } catch { /* 未知游戏保留当前档案 */ }
                candidates = summary.Candidates.Select(x => new FeelCandidate(x.Cm360, x.Sensitivity, x.EDPi, x.MeanScore)).ToArray();
                _summaryCandidates = summary.Candidates; fromSummary = true;
            }
            BuildFeelRows(candidates, LabelStore.Read(LabelStore.FeelKind)
                .Where(x => LabelStore.Str(x, "SessionId") == _sessionId && _sessionId.Length > 0).ToArray(), fromSummary);
            FillCandidateBox(MatchCmBox, candidates, "不关联具体档位");
            FillCandidateBox(AdoptCmBox, candidates, null);
            if (string.IsNullOrWhiteSpace(MatchDateBox.Text)) MatchDateBox.Text = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (MatchGameBox.SelectedIndex < 0)
                MatchGameBox.SelectedIndex = Math.Max(0, MatchGameBox.Items.Cast<ComboBoxItem>().ToList()
                    .FindIndex(x => string.Equals(x.Content?.ToString(), _game.Id, StringComparison.OrdinalIgnoreCase)));
            RefreshMatchList();
            RefreshAdoptionList();
            RefreshSurvey(prefill: false);
            LocalDataText.Text = LabelAnalysis.LocalScaleSummary(Path.Combine(DataDirectory, "traces"), Path.Combine(DataDirectory, "history-v2"));
            FeelHistoryText.Text = TrainingProgressText();
            LabelPathText.Text = $"标签文件：{LabelStore.FilePath}\n离线模型：{OfflineModel.Load()?.Describe() ?? "未安装（把 ml/train.py 训练出的 preference-v1.json 放到 " + OfflineModel.FilePath + "，即启用影子预测）"}\n手感 {LabelStore.Count(LabelStore.FeelKind)} 条 · 成对偏好 {LabelStore.Count(LabelStore.PreferenceKind)} 条 · 对局成绩 {LabelStore.Count(LabelStore.MatchKind)} 条 · 采用 {LabelStore.Count(LabelStore.AdoptionKind)} 条 · 复测 {LabelStore.Count(LabelStore.RetestKind)} 条";
        }
        catch (Exception ex) { FeelStatusText.Text = "标签面板刷新失败：" + ex.Message; }
    }

    private void BuildFeelRows(IReadOnlyList<FeelCandidate> candidates, IReadOnlyList<JsonElement> saved, bool fromSummary = false)
    {
        FeelPanel.Children.Clear(); _feelRows.Clear();
        if (candidates.Count == 0)
        {
            FeelPanel.Children.Add(new TextBlock { Text = "还没有可评分的档位：完成一次测试后，这里会列出本次比较过的档位（含开始测试时的当前设置）。", Style = (Style)FindResource("Caption") });
            return;
        }
        foreach (FeelCandidate candidate in candidates)
        {
            JsonElement? prior = saved.FirstOrDefault(x => LabelStore.Num(x, "Cm360") is { } cm && LabelAnalysis.SameCm(cm, candidate.Cm360));
            bool isBaseline = _centerCm > 0 && LabelAnalysis.SameCm(candidate.Cm360, _centerCm);
            var row = new Grid { Margin = new Thickness(0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            info.Children.Add(new TextBlock { Text = $"{LabelAnalysis.F(candidate.Cm360)} cm/360 · 灵敏度 {candidate.Sensitivity:0.0000} · eDPI {candidate.EDPi:0.0}{(isBaseline ? " · 当前设置" : "")}", Style = (Style)FindResource("BodyText") });
            info.Children.Add(new TextBlock { Text = candidate.MeanScore.HasValue ? $"本次该档有效块平均 {candidate.MeanScore.Value:0.0} 分" : "本次该档没有有效块", Style = (Style)FindResource("Caption") });
            row.Children.Add(info);

            var feel = new Slider { Minimum = 0, Maximum = 10, TickFrequency = 1, IsSnapToTickEnabled = true, Value = LabelStore.Int(prior ?? default, "Feel") ?? 5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(feel, 1); row.Children.Add(feel);
            var value = new TextBlock { Text = feel.Value.ToString("0"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Style = (Style)FindResource("BodyStrong") };
            Grid.SetColumn(value, 2); row.Children.Add(value);
            feel.ValueChanged += (_, e) => value.Text = e.NewValue.ToString("0");

            var compare = new ComboBox { Margin = new Thickness(8, 0, 0, 0), IsEnabled = !isBaseline, VerticalAlignment = VerticalAlignment.Center };
            compare.Items.Add(new ComboBoxItem { Content = "更好" }); compare.Items.Add(new ComboBoxItem { Content = "一样" }); compare.Items.Add(new ComboBoxItem { Content = "更差" });
            compare.SelectedIndex = LabelStore.Str(prior ?? default, "ComparedToCurrent") switch { "更好" => 0, "更差" => 2, _ => 1 };
            Grid.SetColumn(compare, 3); row.Children.Add(compare);

            var note = new TextBox { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Text = LabelStore.Str(prior ?? default, "Note") ?? "" };
            Grid.SetColumn(note, 4); row.Children.Add(note);

            FeelPanel.Children.Add(new Border { BorderBrush = (Brush)FindResource("Divider"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8), Child = row });
            _feelRows.Add(new FeelRow(candidate.Cm360, feel, compare, note, value));
        }
        FeelStatusText.Text = fromSummary
            ? $"正在补评上次会话（{LabelAnalysis.ShortDate(LabelStore.Str(LabelStore.Read(LabelStore.SessionKind).LastOrDefault(), "Created") ?? "")}）：只保存改动过的档位（滑杆不是 5、比较不是“一样”，或写了备注）。"
            : "只保存改动过的档位（滑杆不是 5、比较不是“一样”，或写了备注）。";
    }

    private void FillCandidateBox(ComboBox box, IReadOnlyList<FeelCandidate> candidates, string? noneLabel)
    {
        object? prior = (box.SelectedItem as ComboBoxItem)?.Tag;
        box.Items.Clear();
        if (noneLabel != null) box.Items.Add(new ComboBoxItem { Content = noneLabel, Tag = null });
        foreach (FeelCandidate candidate in candidates)
            box.Items.Add(new ComboBoxItem
            {
                Content = $"{LabelAnalysis.F(candidate.Cm360)} cm/360 · 灵敏度 {candidate.Sensitivity:0.0000} · eDPI {candidate.EDPi:0.0}",
                Tag = candidate.Cm360
            });
        int index = prior is double value ? box.Items.Cast<ComboBoxItem>().ToList().FindIndex(x => x.Tag is double cm && LabelAnalysis.SameCm(cm, value)) : -1;
        box.SelectedIndex = index >= 0 ? index : (box.Items.Count > 0 ? 0 : -1);
    }

    private static double? SelectedCm(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as double?;

    private void RefreshLabelsClicked(object sender, RoutedEventArgs e) { if (!_session) RefreshLabelPanels(); }

    private void ClearFeelClicked(object sender, RoutedEventArgs e)
    {
        foreach (FeelRow row in _feelRows) { row.Feel.Value = 5; row.Compare.SelectedIndex = 1; row.Note.Text = ""; }
        FeelStatusText.Text = "已清空本次填写（未保存任何内容）。";
    }

    private void SaveFeelClicked(object sender, RoutedEventArgs e)
    {
        if (_session || _feelRows.Count == 0) return;
        try
        {
            int saved = 0, pairs = 0;
            foreach (FeelRow row in _feelRows)
            {
                string compare = (row.Compare.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "一样";
                int feel = (int)Math.Round(row.Feel.Value);
                string note = row.Note.Text.Trim();
                if (feel == 5 && compare == "一样" && note.Length == 0) continue;
                LabelStore.Append(LabelStore.FeelKind, new FeelLabel(_sessionId, _tracePath, _game.Id, _dpi, row.Cm360,
                    _game.Sensitivity(_dpi, row.Cm360), _game.Edpi(_dpi, row.Cm360), feel, compare, _centerCm,
                    CalibrationScoring.Version, note.Length == 0 ? null : note));
                saved++;
                bool sameAsBaseline = _centerCm <= 0 || LabelAnalysis.SameCm(row.Cm360, _centerCm);
                if (!sameAsBaseline && compare == "更好")
                { LabelStore.Append(LabelStore.PreferenceKind, new PreferenceLabel(_sessionId, _tracePath, _game.Id, row.Cm360, _centerCm, "Overall", null)); pairs++; }
                else if (!sameAsBaseline && compare == "更差")
                { LabelStore.Append(LabelStore.PreferenceKind, new PreferenceLabel(_sessionId, _tracePath, _game.Id, _centerCm, row.Cm360, "Overall", null)); pairs++; }
            }
            FeelStatusText.Text = saved == 0
                ? "没有任何改动，未写入标签。"
                : $"已保存 {saved} 条手感评分、{pairs} 条成对偏好（基准 {LabelAnalysis.F(_centerCm)} cm/360）。";
            RefreshLabelPanels();
        }
        catch (Exception ex) { FeelStatusText.Text = "保存失败：" + ex.Message; }
    }

    private void SaveMatchClicked(object sender, RoutedEventArgs e)
    {
        if (_session) return;
        try
        {
            string game = (MatchGameBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? _game.Id;
            string date = MatchDateBox.Text.Trim();
            if (date.Length == 0) date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            double? cm = SelectedCm(MatchCmBox);
            string rank = MatchRankBox.Text.Trim();
            LabelStore.Append(LabelStore.MatchKind, new MatchLabel(game, date, cm,
                cm.HasValue && _dpi > 0 ? _game.Sensitivity(_dpi, cm.Value) : null,
                rank.Length == 0 ? null : rank, TryPercent(MatchTrackingBox.Text), TryPercent(MatchHeadshotBox.Text),
                TryInt(MatchMatchesBox.Text), TryDouble(rank), NullText(MatchNoteBox.Text), _sessionId.Length == 0 ? null : _sessionId));
            MatchStatusText.Text = "已保存对局成绩。";
            MatchNoteBox.Text = "";
            RefreshMatchList();
        }
        catch (Exception ex) { MatchStatusText.Text = "保存失败：" + ex.Message; }
    }

    private void RefreshMatchList()
    {
        var text = new StringBuilder();
        foreach (JsonElement record in LabelStore.Read(LabelStore.MatchKind).Reverse().Take(20))
        {
            text.Append(LabelStore.Str(record, "Date")).Append(" · ").Append(LabelStore.Str(record, "Game"));
            if (LabelStore.Num(record, "Cm360") is { } cm) text.Append(" · ").Append(LabelAnalysis.F(cm)).Append(" cm/360");
            if (LabelStore.Str(record, "Rank") is { Length: > 0 } rank) text.Append(" · 段位 ").Append(rank);
            if (LabelStore.Num(record, "TrackingHitRate") is { } tracking) text.Append(" · 跟枪 ").Append(tracking.ToString("0.#")).Append('%');
            if (LabelStore.Num(record, "HeadshotRate") is { } headshot) text.Append(" · 爆头 ").Append(headshot.ToString("0.#")).Append('%');
            if (LabelStore.Int(record, "Matches") is { } matches) text.Append(" · ").Append(matches).Append(" 场");
            if (LabelStore.Str(record, "Note") is { Length: > 0 } note) text.Append(" · ").Append(note);
            text.AppendLine();
        }
        MatchListText.Text = text.Length == 0 ? "暂无对局成绩记录。" : text.ToString();
    }

    private void AdoptClicked(object sender, RoutedEventArgs e)
    {
        if (_session) return;
        try
        {
            if (SelectedCm(AdoptCmBox) is not { } cm)
            { AdoptStatusText.Text = "请先选择要采用的档位：需要先完成一次测试。"; return; }
            IReadOnlyDictionary<string, double> modes = LabelAnalysis.ModeScores(AllResults(), cm);
            int blocks = AllResults().Count(x => x.Valid && LabelAnalysis.SameCm(x.Request.Cm360, cm));
            if (modes.Count == 0 && _summaryCandidates.FirstOrDefault(x => LabelAnalysis.SameCm(x.Cm360, cm)) is { } summary)
            { modes = summary.ModeScores; blocks = summary.ValidBlocks; }
            if (modes.Count == 0) { AdoptStatusText.Text = "本次会话没有该档位的有效块，无法建立基线，未登记。"; return; }
            double baseline = modes.Values.Average();
            LabelStore.Append(LabelStore.AdoptionKind, new AdoptionLabel(_sessionId, _tracePath, _game.Id, _dpi, cm,
                _game.Sensitivity(_dpi, cm), _game.Edpi(_dpi, cm), CalibrationScoring.Version, baseline, blocks, modes, null));
            AdoptStatusText.Text = $"已登记采用 {LabelAnalysis.F(cm)} cm/360：基线 {LabelAnalysis.F(baseline)} 分（{LabelAnalysis.DescribeModes(modes)}）。下次测试打到同一档位会自动计算复测差值。";
            RefreshAdoptionList();
        }
        catch (Exception ex) { AdoptStatusText.Text = "登记失败：" + ex.Message; }
    }

    private void RefreshAdoptionList()
    {
        IReadOnlyList<LabelAnalysis.AdoptionView> adoptions = LabelAnalysis.Adoptions();
        var text = new StringBuilder();
        foreach (LabelAnalysis.AdoptionView adoption in adoptions.Reverse())
        {
            text.Append(LabelAnalysis.ShortDate(adoption.Date)).Append(" 采用 ").Append(adoption.Game).Append(" · ")
                .Append(LabelAnalysis.F(adoption.Cm360)).Append(" cm/360（灵敏度 ").Append(adoption.Sensitivity.ToString("0.0000"))
                .Append("）· 基线 ").Append(LabelAnalysis.F(adoption.BaselineScore)).Append(" 分（").Append(LabelAnalysis.DescribeModes(adoption.ModeScores)).Append('）').AppendLine();
            text.AppendLine(adoption.Retest == null
                ? "    等待复测：重做同一档位后自动比较（要求同游戏、同评分版本、有共同测试项目）。"
                : $"    复测 {LabelAnalysis.F(adoption.Retest.RetestScore)} 分 · Δ {LabelAnalysis.Signed(adoption.Retest.Delta)} 分 · {adoption.Retest.Basis} · {adoption.Retest.Blocks} 块 · {LabelAnalysis.ShortDate(adoption.Retest.Date)}");
        }
        AdoptListText.Text = text.Length == 0 ? "暂无采用记录。完成测试后可在本页登记采用的档位。" : text.ToString();
        LabelAnalysis.AdoptionView[] pending = adoptions.Where(x => x.Pending).ToArray();
        RetestStatusText.Text = pending.Length == 0
            ? "当前没有等待复测的档位。"
            : $"等待复测 {pending.Length} 个：{string.Join("、", pending.Select(x => LabelAnalysis.F(x.Cm360) + " cm/360"))}。";
    }

    // ===================== 本地问卷（profile）：只写本机，无任何上传路径 =====================

    private const int SurveyVersion = 1;
    private bool _surveyPrefilled;

    private void RefreshSurvey(bool prefill)
    {
        SurveyLabel? saved = LabelAnalysis.LastSurvey();
        if (prefill && !_surveyPrefilled)
        {
            _surveyPrefilled = true;
            if (saved != null)
            {
                SelectCombo(SurveyGameBox, saved.Game);
                SurveyMouseBox.Text = saved.MouseModel ?? "";
                SurveyWeightBox.Text = saved.MouseWeightGrams?.ToString("0.#", CultureInfo.InvariantCulture) ?? "";
                SelectCombo(SurveyPadTypeBox, saved.PadType);
                SelectCombo(SurveyGripBox, saved.Grip);
                SelectCombo(SurveyAgeBox, saved.AgeBracket);
                SurveyFpsYearsBox.Text = saved.FpsYears?.ToString("0.#", CultureInfo.InvariantCulture) ?? "";
                SurveyTrainingBox.Text = saved.AimTrainingMonths?.ToString("0.#", CultureInfo.InvariantCulture) ?? "";
                SurveyWeeklyHoursBox.Text = saved.WeeklyHours?.ToString("0.#", CultureInfo.InvariantCulture) ?? "";
                SurveyNoteBox.Text = saved.Note ?? "";
            }
            else if (SurveyGameBox.SelectedIndex < 0) SurveyGameBox.SelectedIndex = 0;
        }
        SurveyHistoryText.Text = saved == null
            ? "还没有填写过问卷。"
            : $"最近一次（{LabelStore.Count(LabelStore.ProfileKind)} 条 profile 记录中的最后一条）：{saved.Game} · " +
              $"鼠标 {Value(saved.MouseModel)} · 垫子 {Value(saved.PadType)} · 握法 {Value(saved.Grip)} · " +
              $"FPS {Number(saved.FpsYears)} 年 · 训练 {Number(saved.AimTrainingMonths)} 月 · 每周 {Number(saved.WeeklyHours)} 小时 · 年龄区间 {Value(saved.AgeBracket)}";
    }

    private static void SelectCombo(ComboBox box, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        int index = box.Items.Cast<ComboBoxItem>().ToList().FindIndex(x => string.Equals(x.Content?.ToString(), value, StringComparison.Ordinal));
        if (index >= 0) box.SelectedIndex = index;
    }

    private static string Value(string? text) => string.IsNullOrWhiteSpace(text) ? "未填" : text;
    private static string Number(double? value) => value?.ToString("0.#", CultureInfo.InvariantCulture) ?? "未填";

    private void SaveSurveyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var survey = new SurveyLabel(SurveyVersion,
                (SurveyGameBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? _game.Id,
                TryDouble(DpiBox.Text), StartPointModel.DescribeWrist(WristShare), TryDouble(PadBox.Text),
                (DisplayModeBox.SelectedItem as DisplayMode) is { } mode ? (int)Math.Round(mode.RefreshHz) : null,
                NullText(SurveyMouseBox.Text), TryDouble(SurveyWeightBox.Text),
                (SurveyPadTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                (SurveyGripBox.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                TryDouble(SurveyFpsYearsBox.Text), TryDouble(SurveyTrainingBox.Text), TryDouble(SurveyWeeklyHoursBox.Text),
                SurveyAgeBox.SelectedIndex <= 0 ? null : (SurveyAgeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                NullText(SurveyNoteBox.Text));
            LabelStore.Append(LabelStore.ProfileKind, survey);
            SurveyStatusText.Text = "已保存到本机标签文件（未上传，也目前没有任何自动上传）。";
            RefreshLabelPanels();
        }
        catch (Exception ex) { SurveyStatusText.Text = "保存失败：" + ex.Message; }
    }

    private void OpenDataFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Process.Start(new ProcessStartInfo(DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { SurveyStatusText.Text = "打开文件夹失败：" + ex.Message; }
    }

    // ===================== 数据与隐私：上传包预览（本机，无网络） =====================

    internal UploadRequest BuildUploadRequest()
    {
        BlockResult[] results = AllResults().ToArray();
        IReadOnlyList<FeelCandidate> candidates = LabelCandidates();
        SessionSummaryLabel? snapshot = LabelAnalysis.LastSession();
        bool live = results.Length > 0 && _sessionId.Length > 0;

        UploadSession? session = null;
        if (live)
            session = new UploadSession(_game.Id, _dpi, _fov, _trainingDistance, _modes.Select(ModeName).ToArray(),
                _lastSessionDisplay?.Width ?? 0, _lastSessionDisplay?.Height ?? 0,
                (int)Math.Round(_lastSessionDisplay?.RefreshHz ?? 0), CalibrationScoring.Version, _motionVersion,
                _evidenceVersion, _low, _high, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var uploadCandidates = new List<UploadCandidate>();
        foreach (FeelCandidate candidate in candidates)
        {
            if (live)
            {
                int valid = results.Count(x => x.Valid && LabelAnalysis.SameCm(x.Request.Cm360, candidate.Cm360));
                uploadCandidates.Add(new UploadCandidate(candidate.Cm360, CandidateFeatures(results, candidate.Cm360, _centerCm),
                    candidate.MeanScore, valid, true, BlockAggregates(candidate.Cm360)));
            }
            else if (snapshot?.Candidates.FirstOrDefault(x => LabelAnalysis.SameCm(x.Cm360, candidate.Cm360)) is { } item)
            {
                // 重启后没有内存结果：优先用会话快照里存下来的**契约特征**；快照没有就标成不完整，
                // 由 UploadPackage.Build 拒绝生成（宁可拒绝，也不发只有 cm360 的空壳）。
                bool complete = item.Features is { Count: > 0 };
                var features = complete
                    ? new Dictionary<string, double>(item.Features!)
                    : new Dictionary<string, double> { ["cm360"] = candidate.Cm360, ["logCm360"] = Math.Log(candidate.Cm360) };
                uploadCandidates.Add(new UploadCandidate(candidate.Cm360, features, candidate.MeanScore, item.ValidBlocks, complete,
                    item.BlockAggregates is { Count: > 0 } ? item.BlockAggregates : null));
            }
        }

        var labels = new List<(string Kind, JsonElement Record)>();
        foreach (string kind in new[] { LabelStore.FeelKind, LabelStore.PreferenceKind, LabelStore.MatchKind,
            LabelStore.AdoptionKind, LabelStore.RetestKind })
            foreach (JsonElement record in LabelStore.Read(kind)) labels.Add((kind, record));

        return new UploadRequest(session, uploadCandidates, labels, LabelAnalysis.LastSurvey());
    }

    private static int? TryInt(string text) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0 ? value : null;

    private static double? TryPercent(string text)
    {
        string cleaned = (text ?? "").Trim().TrimEnd('%', '％').Trim();
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value is >= 0 and <= 100 ? value : null;
    }

    private static double? TryDouble(string text) =>
        double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;

    private static string? NullText(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ===================== 左栏导航：每页内容自洽，左栏只做导航 =====================

    private sealed record NavPageItem(string Title, string Caption, Geometry Icon);
    private NavPageItem[] _navPages = [];
    private Grid[] _pageHosts = [];
    private string? _privacyPreview;

    private void BuildNavigation()
    {
        _pageHosts = [PageTest, PageFeel, PageMatch, PageRetest, PageSurvey, PagePrivacy];
        _navPages =
        [
            new("校准测试", "右侧参数 → 开始测试 → 下方读取推荐依据；预告、适应、切换不计分。", Geometry.Parse("M3,8 A5,5 0 1 1 13,8 A5,5 0 1 1 3,8 M8,1.6 L8,3.5 M8,12.5 L8,14.4 M1.6,8 L3.5,8 M12.5,8 L14.4,8")),
            new("手感评分", "本次每个候选档位打 0–10 分，并标出比当前设置更好还是更差（公式之外的标签）。", Geometry.Parse("M8,1.6 L9.76,5.7 L14.4,5.95 L10.85,8.95 L12.05,13.5 L8,11 L3.95,13.5 L5.15,8.95 L1.6,5.95 L6.24,5.7 Z")),
            new("对局成绩", "手工记录采用某档位后的真实段位与命中率——唯一不来自本软件计分的外部依据。", Geometry.Parse("M3,13.4 L3,8.6 M8,13.4 L8,3.6 M13,13.4 L13,10.6")),
            new("纵向复测", "登记采用的档位，之后打到同一档位会自动算出复测差值。", Geometry.Parse("M2.6,5.6 L13.4,5.6 M10.4,2.8 L13.6,5.6 L10.4,8.4 M13.4,10.4 L2.6,10.4 M5.6,7.6 L2.4,10.4 L5.6,13.2")),
            new("本地问卷", "装备 / 习惯 / 经验的分层变量，只写入本机，当前没有任何上传。", Geometry.Parse("M3.6,1.6 L9.5,1.6 L12.4,4.5 L12.4,14.4 L3.6,14.4 Z M9.5,1.6 L9.5,4.5 L12.4,4.5 M6,8 L10,8 M6,11 L10,11")),
            new("数据与隐私", "本机数据在哪、将来会上传什么；当前版本没有任何联网上传代码。", Geometry.Parse("M8,1.6 L13.4,3.6 L13.4,8 C13.4,11.4 11,13.7 8,14.4 C5,13.7 2.6,11.4 2.6,8 L2.6,3.6 Z")),
        ];
        NavList.ItemsSource = _navPages;
        NavList.Tag = false;          // 收起状态：只有图标
        NavMark.Visibility = Visibility.Visible;
        NavList.SelectedIndex = 0;
    }

    // 导航栏：默认收起（60px，只有图标），鼠标移入展开显示文字，移出收起。
    // 三处让步进更柔和、也消除抽搐：
    //   1) 时长 160ms → 280/220ms，缓动换成 Fluent 风格的 CubicEase(EaseOut)；
    //   2) 动画的是**覆盖层**的宽度（列宽固定 60），所以期间布局零重排、训练区不被 Resize；
    //   3) 迟滞：同一个目标态不重复触发；移出后延迟 140ms 再收起，避免边缘抖动来回抽。
    private bool _navExpanded;
    private long _navLeaveAt;

    private void NavRailEnter(object sender, MouseEventArgs e)
    {
        _navLeaveAt = 0;
        SetNavExpanded(true);
    }

    private void NavRailLeave(object sender, MouseEventArgs e)
    {
        _navLeaveAt = Stopwatch.GetTimestamp();
        _ = CollapseNavSoonAsync();
    }

    private async Task CollapseNavSoonAsync()
    {
        long requested = _navLeaveAt;
        await Task.Delay(140);                       // 迟滞窗口：期间鼠标回到栏内就取消收起
        if (_navLeaveAt != requested || NavRail.IsMouseOver) return;
        SetNavExpanded(false);
    }

    private void SetNavExpanded(bool expanded)
    {
        if (_navExpanded == expanded && Math.Abs(NavRail.Width - (expanded ? 248 : 60)) < 0.5) return;
        _navExpanded = expanded;
        double target = expanded ? 248 : 60;
        NavList.Tag = expanded;
        NavMark.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        // 收起时把整列往中间挪几像素让图标视觉居中；展开时文字要贴左
        NavList.Padding = expanded ? new Thickness(0) : new Thickness(4, 0, 0, 0);
        var animation = new System.Windows.Media.Animation.DoubleAnimation(target,
            TimeSpan.FromMilliseconds(expanded ? 280 : 220))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        NavRail.BeginAnimation(FrameworkElement.WidthProperty, animation);
    }

    private void NavChanged(object sender, SelectionChangedEventArgs e)
    {
        // 用 AddedItems 而不是 SelectedIndex：切换瞬间 SelectedIndex 可能是 -1，
        // 早退会把这一页的显示更新整个吃掉（真出现过"点了最后一页页面没变"）。
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is NavPageItem page) SwitchPage(Array.IndexOf(_navPages, page));
    }

    private void SwitchPage(int index)
    {
        if (index < 0 || index >= _pageHosts.Length) return;
        for (int i = 0; i < _pageHosts.Length; i++)
            _pageHosts[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        PageTitleText.Text = _navPages[index].Title;
        PageCaptionText.Text = _navPages[index].Caption;
        NavFootText.Text = _sessionId.Length > 0 ? $"上次会话：{_game.Id} · {_dpi:0} DPI" : "尚无会话记录";
        if (_pageHosts[index] == PagePrivacy) RefreshPrivacyPage();
    }

    private UploadRequest ScopedUploadRequest()
    {
        UploadRequest full = BuildUploadRequest();
        return new UploadRequest(ScopeSessionBox.IsChecked == true ? full.Session : null,
            ScopeSessionBox.IsChecked == true ? full.Candidates : [],
            ScopeLabelsBox.IsChecked == true ? full.Labels : [],
            ScopeSurveyBox.IsChecked == true ? full.Survey : null);
    }

    private void RefreshPrivacyPage()
    {
        InstallIdText.Text = $"匿名安装 ID：{UploadPackage.InstallId()}\n（随机数，不含任何硬件指纹；重置后与历史数据断开）";
        LocalDataSummaryText.Text = "当前本机数据：" + LocalData.Describe(LocalData.Summarize(DataDirectory));
        if (RedactionText.Text.Length == 0)
            RedactionText.Text = "会被剥掉的字段：\n" + string.Join("\n", UploadPackage.RedactionList.Select(x => "· " + x));
        try
        {
            var package = UploadPackage.Build(ScopedUploadRequest());
            _privacyPreview = package.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            PrivacyPreviewText.Text = _privacyPreview;
            PrivacyStatusText.Text = $"预览已生成：共 {_privacyPreview.Length} 字符。本版本不会自动发送任何内容。本机数据目录：{DataDirectory}";
        }
        catch (Exception ex)
        {
            _privacyPreview = null;
            PrivacyPreviewText.Text = "生成失败：" + ex.Message;
            PrivacyStatusText.Text = "生成失败，未产生任何文件。";
        }
    }

    private void PrivacyPreviewClicked(object sender, RoutedEventArgs e) => RefreshPrivacyPage();

    private void PrivacyExportClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_privacyPreview == null) { PrivacyStatusText.Text = "请先生成预览。"; return; }
            string path = Path.Combine(DataDirectory, $"upload-preview-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(path, _privacyPreview);
            PrivacyStatusText.Text = "已导出到本机：" + path;
        }
        catch (Exception ex) { PrivacyStatusText.Text = "导出失败：" + ex.Message; }
    }

    private void PrivacyCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_privacyPreview == null) { PrivacyStatusText.Text = "请先生成预览。"; return; }
            Clipboard.SetText(_privacyPreview);
            PrivacyStatusText.Text = "已复制到剪贴板（剪贴板是系统共享的，粘贴前请确认目标位置）。";
        }
        catch (Exception ex) { PrivacyStatusText.Text = "复制失败：" + ex.Message; }
    }

    private void PrivacyResetIdClicked(object sender, RoutedEventArgs e)
    {
        UploadPackage.ResetInstallId();
        PrivacyStatusText.Text = "安装 ID 已重置；旧 ID 对应的历史数据需要另行请求删除。";
        RefreshPrivacyPage();
    }

    // 删除本机数据：先把"将要删除什么"摊开（数量+体积），确认后才动手。
    private void DeleteDataClicked(object sender, RoutedEventArgs e)
    {
        bool labels = DeleteLabelsBox.IsChecked == true, traces = DeleteTracesBox.IsChecked == true;
        bool previews = DeletePreviewsBox.IsChecked == true, installId = DeleteInstallIdBox.IsChecked == true;
        if (!labels && !traces && !previews && !installId)
        { PrivacyStatusText.Text = "没有勾选任何要删除的类别。"; return; }
        LocalDataSummary summary = LocalData.Summarize(DataDirectory);
        var lines = new List<string>();
        if (labels) lines.Add($"· 标签 {summary.LabelRecords} 条（{LocalData.Size(summary.LabelBytes)}）");
        if (traces) lines.Add($"· 轨迹 {summary.TraceFiles} 个 + 历史 {summary.HistoryFiles} 个（{LocalData.Size(summary.TraceBytes + summary.HistoryBytes)}，不可恢复）");
        if (previews) lines.Add($"· 上传预览导出 {summary.PreviewFiles} 个（{LocalData.Size(summary.PreviewBytes)}）");
        if (installId) lines.Add("· 匿名安装 ID（换新 ID）");
        MessageBoxResult answer = MessageBox.Show(this,
            "将永久删除本机以下数据：\n\n" + string.Join("\n", lines) + "\n\n此操作不可撤销（轨迹是全部测量记录，删掉就没了）。",
            "确认删除本机数据", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) { PrivacyStatusText.Text = "已取消，没有删除任何文件。"; return; }
        (int files, List<string> details) = LocalData.Delete(DataDirectory, labels, traces, history: traces, previews, installId);
        PrivacyStatusText.Text = $"已删除 {files} 个文件。明细：{string.Join("；", details)}";
        if (installId) UploadPackage.ResetInstallId();   // 删掉 ID 文件后立刻补一个新的
        RefreshPrivacyPage();
    }

    // 渲染基准：跑一段演示回放，量出原生渲染线程的 FPS / P95 / 是否 vsync，并导出当前画面。
    // 只用于开发自检（--render-bench），不参与计分。
    internal async Task<string> RunRenderBenchAsync(string pngPath, int seconds, double distance = 10)
    {
        _dpi = 800; _fov = 90; _trainingDistance = Math.Clamp(distance, 2, 60); _centerCm = 36; _cm = 36; _blockYaw = 0;
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        _script = MotionScript.Create(_game.TrackingFamily ? MotionProfile.Mobile : MotionProfile.Tactical, 0,
            _trainingDistance, .6, _fov, Math.Max(.1, Arena.ActualWidth / Math.Max(1, Arena.ActualHeight)), _motionVersion);
        _scene.SetRefresh(_displayLease?.AppliedMode.RefreshHz ?? 240);
        _scene.Visibility = Visibility.Visible;
        _scene.SetCamera(_camera.Yaw, _camera.Pitch, _fov);
        _scene.SetHud("AimSense 渲染基准（演示回放，不计分）", "渲染基准运行中…", "");
        _scene.SetCrosshair(5, 3, 2, true, 0, 255, 255);
        _scene.SetPlayback(new(_script, TrialMode.Track, 0, Stopwatch.GetTimestamp(), 1.2, true));

        // 等渲染线程真的出过帧；出不来就把原因写进报告，绝不干等。
        long wait = Stopwatch.GetTimestamp();
        while (Since(wait) < 5 && _scene.PresentCount == 0 && _scene.Error == null) await Task.Delay(100);
        string state = $"就绪={_scene.Ready.IsCompleted} 错误={_scene.Error?.Message ?? "无"} 尺寸={_scene.SurfaceWidth}x{_scene.SurfaceHeight} 竞技区={Arena.ActualWidth:0}x{Arena.ActualHeight:0}";
        if (_scene.Error != null) return "初始化失败：" + _scene.Error.Message + " ｜ " + state;
        if (_scene.PresentCount == 0) return "渲染线程未出帧（5 秒内 0 次呈现）。" + state;

        long start = Stopwatch.GetTimestamp();
        while (Since(start) < seconds) await Task.Delay(200);
        string report = $"FPS={_scene.Metrics.Fps:0.0} P95={_scene.Metrics.P95Ms:0.00} ms 呈现={_scene.Metrics.Presents} "
            + $"设备={_scene.Metrics.Device} VSync={_scene.Metrics.VSync} MSAA4x={_scene.Multisample}（{_scene.MultisampleNote}） 人物距离={_trainingDistance:0.#}m "
            + $"停滞标记={_scene.LastStalledBlock} {state}";
        try
        {
            await _scene.CaptureFrameAsync(pngPath).WaitAsync(TimeSpan.FromSeconds(5));
            report += " ｜截图=" + Path.GetFileName(pngPath);
        }
        catch (Exception ex) { report += " ｜截图失败：" + ex.Message; }
        _scene.SetPlayback(null);
        return report;
    }

    // 仅用于 --ui-shot / --ui-privacy 的自动化截图路径：跳过"结束测试"确认后关闭。
    internal void RequestClose()
    {
        _allowClose = true;
        Close();
    }

    // 仅用于 --ui-preview 的外观自检：全部为示例文本，不读写任何标签文件，也不改变计分。
    private void FillPreviewSamples()
    {
        ResultText.Text = "（预览示例数据）\n点击 · 36.00 cm/360 · 87.4 表现分（失败扣 12.5、耗时扣 0.1），命中 5/6，错点 1，超时 0，命中耗时 0.42秒，过冲 1，回拉 2，恢复 240ms/3次，换向响应 310ms/4次\n跟随 · 31.10 cm/360 · 82.1 表现分（离靶扣 15.0、误差扣 2.9），贴靶 80%，角误差 0.35 个头部半径，过冲 2，回拉未统计\n\n综合候选 31.10–36.00 cm/360 · 加权表现 84.6 分\n预算内综合较好候选：33.12 cm/360 · CS2 · 800 DPI · 游戏灵敏度 1.2345 · eDPI 987.6";
        BuildFeelRows([
            new FeelCandidate(31.10, 1.3152, 1052.2, 82.1),
            new FeelCandidate(33.12, 1.2345, 987.6, 84.6),
            new FeelCandidate(36.00, 1.1358, 908.6, 87.4)], []);
        MatchDateBox.Text = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        MatchRankBox.Text = "黄金 3";
        MatchTrackingBox.Text = "42";
        MatchHeadshotBox.Text = "21";
        MatchMatchesBox.Text = "18";
        MatchNoteBox.Text = "换成 33.12 后长枪更稳";
        MatchListText.Text = "（预览示例数据）\n2026-10-12 · CS2 · 33.12 cm/360 · 段位 黄金 3 · 跟枪 42% · 爆头 21% · 18 场 · 换成 33.12 后长枪更稳\n2026-10-05 · CS2 · 31.10 cm/360 · 段位 黄金 2 · 跟枪 39% · 爆头 19% · 22 场";
        AdoptListText.Text = "（预览示例数据）\n2026-10-05 采用 CS2 · 31.10 cm/360（灵敏度 1.3152）· 基线 79.2 分（点击 81.0、跟随 77.4）\n    等待复测：重做同一档位后自动比较（要求同游戏、同评分版本、有共同测试项目）。\n2026-09-28 采用 CS2 · 36.00 cm/360（灵敏度 1.1358）· 基线 83.5 分（点击 86.9、跟随 80.1）\n    复测 87.4 分 · Δ +3.90 分 · Click+Track · 12 块 · 10-12 21:40";
        AdoptStatusText.Text = "（预览示例数据）已登记采用 33.12 cm/360。";
        RetestStatusText.Text = "（预览示例数据）等待复测 1 个：31.10 cm/360。";
        FeelStatusText.Text = "（预览示例数据）只保存改动过的档位（滑杆不是 5、比较不是“一样”，或写了备注）。";
        LabelPathText.Text = $"标签文件：{LabelStore.FilePath}\n手感 0 条 · 成对偏好 0 条 · 对局成绩 0 条 · 采用 0 条 · 复测 0 条";
    }

    internal async Task RenderPreviewAsync(string path)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        FillPreviewSamples();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(directory);
        string name = Path.GetFileNameWithoutExtension(full);
        string[] pages = ["test", "feel", "match", "retest", "survey", "privacy"];
        for (int index = 0; index < pages.Length; index++)
        {
            NavList.SelectedIndex = index;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await SaveWindowAsync(Path.Combine(directory, $"{name}-{index + 1}-{pages[index]}.png"));
        }
        // 再渲染一张"导航展开"态（悬停后的样子），便于核对
        NavRail.BeginAnimation(FrameworkElement.WidthProperty, null);
        NavRail.Width = 248; NavList.Tag = true; NavMark.Visibility = Visibility.Collapsed;
        NavList.SelectedIndex = 0;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await Task.Delay(120);
        await SaveWindowAsync(Path.Combine(directory, $"{name}-0-nav-expanded.png"));
        _allowClose = true; Close();
    }

    // 真实启动态截图（无示例数据），用于核对实际运行时的滚动位置与底色。
    internal async Task RenderLiveAsync(string path, int delayMs)
    {
        await Task.Delay(Math.Max(0, delayMs));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        await SaveWindowAsync(path);
        _allowClose = true; Close();
    }

    private async Task SaveWindowAsync(string path)
    {
        UpdateLayout();
        const int scale = 2;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * scale), (int)Math.Ceiling(ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (FileStream stream = File.Create(path)) encoder.Save(stream);
    }

    internal void OpenTraceImport(string? path = null) => new TraceImportWindow(this, path).Show();
    internal string? ResumeDisplayError(ResumeSessionState state)
    {
        try
        {
        var display=_displays.EnumerateDisplays().FirstOrDefault(d=>state.Display.DeviceId.Length>0 ? d.DeviceId==state.Display.DeviceId : d.DeviceName==state.Display.DeviceName);
        if(display==null) return "原测试显示器未连接。请连接原显示器后重新导入，不能更换显示条件混算成绩。";
        return _displays.EnumerateModes(display).Any(m=>m.Width==state.Display.Width &&m.Height==state.Display.Height &&Math.Abs(m.RefreshHz-state.Display.RefreshHz)<1)
            ? null : $"当前显示器不支持原来的{state.Display.Width}×{state.Display.Height} / {state.Display.RefreshHz:0.##}Hz。请恢复原显示条件后重新导入。";
        }
        catch(Exception ex) { return "记录已解析，但当前续测显示条件无法读取："+ex.Message; }
    }
    internal async Task RestartReviewWithCurrentDisplayAsync(TraceImportResult imported)
    {
        var source=imported.ResumeState??throw new InvalidOperationException("此记录不支持恢复。");
        if(source.Modes.Length!=3 ||source.Searches.Count!=3 ||source.Searches.Any(s=>!s.IsComplete||!s.HasEvidence))
            throw new InvalidOperationException("换显示条件重做复核需要三项完整的候选依据，请新建测试。");
        var mode=DisplayModeBox.SelectedItem as DisplayMode??throw new InvalidOperationException("请先在主界面选择显示模式。");
        var changed=source with
        {
            Display=new(mode.Width,mode.Height,mode.RefreshHz,mode.DeviceName,mode.DeviceId),
            MotionVersion=MotionScript.CurrentVersion,EvidenceVersion=CompositeValidation.CurrentEvidenceVersion,
            Validation=new(source.Game,source.Searches.ToDictionary(s=>s.Mode,s=>s.BestCm360),source.Seed),
            Blocks=source.Blocks.Where(b=>b.Result.Request.Purpose!=TrialPurpose.Validation).ToArray(),
            ArchivedReview=source.ArchivedReview.Concat(source.Blocks.Where(b=>b.Result.Request.Purpose==TrialPurpose.Validation)).ToArray(),
            SearchEvidenceIsPrior=true,PriorDisplay=source.PriorDisplay??source.Display
        };
        await ContinueTraceAsync(imported with {ResumeState=changed});
    }
    internal async Task ContinueTraceAsync(TraceImportResult imported)
    {
        if(_session || _stopping) throw new InvalidOperationException("请先结束当前测试。");
        var resume=imported.ResumeState;
        if(resume is not {CanContinue:true}) throw new InvalidOperationException("此记录没有可以恢复的未完成测试块。");
        if(ResumeDisplayError(resume) is {} displayError) throw new InvalidOperationException(displayError);
        RefreshHardware();
        var display=_displays.EnumerateDisplays().FirstOrDefault(d=>resume.Display.DeviceId.Length>0 ? d.DeviceId==resume.Display.DeviceId : d.DeviceName==resume.Display.DeviceName)
            ?? throw new InvalidOperationException("原测试显示器不可用，不能默默换显示器继续比较。");
        var mode=_displays.EnumerateModes(display).FirstOrDefault(m=>m.Width==resume.Display.Width && m.Height==resume.Display.Height && Math.Abs(m.RefreshHz-resume.Display.RefreshHz)<1);
        if(mode==null) throw new InvalidOperationException("当前显示器不支持原记录的分辨率和刷新率，请开始新会话。");
        DisplayBox.SelectedItem=DisplayBox.Items.Cast<DisplayDevice>().FirstOrDefault(d=>d.DeviceName==display.DeviceName);
        ResolutionBox.SelectedItem=$"{mode.Width} × {mode.Height}";
        DisplayModeBox.SelectedItem=DisplayModeBox.Items.Cast<DisplayMode>().FirstOrDefault(m=>DisplayModeService.SameTiming(m.Native,mode.Native));
        DpiBox.Text=resume.Dpi.ToString(CultureInfo.InvariantCulture); FovBox.Text=resume.Fov.ToString(CultureInfo.InvariantCulture);
        GameBox.SelectedItem=GameBox.Items.Cast<GameProfile>().FirstOrDefault(x=>string.Equals(x.Id,resume.Game.Id,StringComparison.OrdinalIgnoreCase));
        SensitivityBox.Text="";PadBox.Text="40";CharacterDistanceBox.SelectedIndex=3; TrainingDistanceBox.Text=resume.Distance.ToString(CultureInfo.InvariantCulture);
        _resumeState=resume;_resumeEntry=true;
        await StartTestAsync();
        if(!_session) {_resumeState=null;_resumeEntry=false;}
    }
    private void ArenaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCrosshair();
        if (InstructionOverlay != null) InstructionOverlay.Width = Math.Max(240, Math.Min(580, Arena.ActualWidth - 32));
        if (InstructionOverlay != null) InstructionOverlay.MaxHeight = Math.Max(160, Arena.ActualHeight - 32);
        if (_session && !_displayTransition && _fullScreen)
        {
            if (Stopwatch.GetTimestamp() > _layoutSettlingUntil && InBlock && _script != null && Math.Abs(Aspect - _script.Aspect) > .01) _ = StopSessionAsync("画面比例发生变化，当前块已排除。请重新选择显示模式。");
            else if (Mouse.Captured == Arena) ClipMouse();
        }
    }
    private void UpdateCrosshair()
    {
        if (CrosshairCanvas == null) return;
        if (_scene != null && _crossBrush is SolidColorBrush brush) _scene.SetCrosshair(_crossLength, _crossGap, _crossThickness, CrosshairDotBox.IsChecked == true, brush.Color.R, brush.Color.G, brush.Color.B);
        var dpi = VisualTreeHelper.GetDpi(this); double cx = Arena.ActualWidth / 2, cy = Arena.ActualHeight / 2;
        double length = _crossLength / dpi.DpiScaleX, gap = _crossGap / dpi.DpiScaleX, thickness = _crossThickness / dpi.DpiScaleX;
        void Line(System.Windows.Shapes.Line line, double x1, double y1, double x2, double y2) { line.X1 = x1; line.Y1 = y1; line.X2 = x2; line.Y2 = y2; line.Stroke = _crossBrush; line.StrokeThickness = thickness; }
        Line(CrosshairLeft, cx-gap-length, cy, cx-gap, cy); Line(CrosshairRight, cx+gap, cy, cx+gap+length, cy);
        Line(CrosshairTop, cx, cy-gap-length, cx, cy-gap); Line(CrosshairBottom, cx, cy+gap, cx, cy+gap+length);
        CrosshairDot.Width = CrosshairDot.Height = thickness; CrosshairDot.Fill = _crossBrush;
        CrosshairDot.Visibility = CrosshairDotBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(CrosshairDot, cx-thickness/2); Canvas.SetTop(CrosshairDot, cy-thickness/2);
    }
    private void SaveDesktopState()
    {
        _oldBounds = new(Left, Top, Width, Height); _oldStyle = WindowStyle; _oldResize = ResizeMode; _oldState = WindowState; _oldTopmost = Topmost;
        if (!GetClipCursor(out _oldClip) || !GetCursorPos(out _oldCursor)) throw new InvalidOperationException("无法保存鼠标位置和限制范围，未进入全屏。");
        _oldMinWidth = MinWidth; _oldMinHeight = MinHeight;
        _savedCursor = true;
    }
    private void EnterFullScreen(PhysicalDisplayBounds bounds)
    {
        _fullScreen = true; _fullScreenBounds = bounds;
        MinWidth = MinHeight = 0;
        ArenaHost.Child = null; FullScreenLayer.Children.Add(Arena); FullScreenLayer.Visibility = Visibility.Visible; MainShell.Visibility = Visibility.Collapsed;
        WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true;
        ApplyFullScreenBounds();
        UpdateLayout(); UpdateCrosshair(); Activate();
    }
    private void ApplyFullScreenBounds()
    {
        var bounds = _fullScreenBounds;
        nint hwnd = new WindowInteropHelper(this).Handle;
        // WPF's transform can lag GetDpiForWindow during a mode switch. Use the layout's
        // current transform, then reconcile again after WM_DPICHANGED has been processed.
        var transform = ((HwndSource)PresentationSource.FromVisual(this)).CompositionTarget.TransformToDevice;
        var logical = PhysicalWindowLayout.ToLogical(bounds, (uint)Math.Round(transform.M11 * 96));
        // Set WPF's own layout constraints as well as the HWND. Otherwise a queued WPF layout
        // can restore the old DIP width and leave a larger renderer cropped by the monitor.
        Left = logical.Left; Top = logical.Top; Width = logical.Width; Height = logical.Height;
        UpdateLayout();
        _layoutSettlingUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        if (!SetWindowPos(hwnd, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0064)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private async Task VerifyFullScreenAsync(CancellationToken cancellation)
    {
        var expected = _displayLease!.AppliedMode;
        int stable = 0;
        for (int i = 0; i < 100; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Loaded);
            string? mismatch = FullScreenMismatch();
            stable = mismatch == null ? stable+1 : 0;
            if (stable >= 10) break;
            if (i == 99) throw new InvalidOperationException((mismatch ?? "全屏模式未稳定") + "。已取消测试并恢复显示设置。");
            await Task.Delay(50, cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        var display = _displays.EnumerateDisplays().Single(d => d.DeviceName == expected.DeviceName && d.DeviceId == expected.DeviceId);
        var actual = _displays.ReadCurrent(display);
        if (!DisplayModeService.SameTiming(actual.Native, expected.Native)) throw new InvalidOperationException("实际显示模式与选项不一致，已取消测试并恢复原模式。");
    }
    private string? FullScreenMismatch()
    {
        var expected = _displayLease!.AppliedMode;
        var display = _displays.EnumerateDisplays().SingleOrDefault(d => d.DeviceName == expected.DeviceName && d.DeviceId == expected.DeviceId)
            ?? throw new InvalidOperationException("测试显示器已断开。");
        var actual = _displays.ReadCurrent(display);
        var monitor = _displays.ReadMonitorBounds(expected.DeviceName);
        if (!DisplayModeService.SameTiming(actual.Native, expected.Native)) return $"显示模式已变化：要求{expected}，实际{actual}";
        if (monitor != expected.Bounds) return $"显示器边界{monitor.Width}×{monitor.Height}与所选{expected.Width}×{expected.Height}不一致";
        if (!TryClientBounds(out var client) || client.Left != monitor.Left || client.Top != monitor.Top || client.Right-client.Left != monitor.Width || client.Bottom-client.Top != monitor.Height)
            return $"窗口未覆盖显示器：客户区{client.Right-client.Left}×{client.Bottom-client.Top}，显示器{monitor.Width}×{monitor.Height}";
        // Hidden native surfaces don't update their metrics; check them during active rendering.
        if (_scene.Visibility == Visibility.Visible && (_scene.SurfaceWidth != monitor.Width || _scene.SurfaceHeight != monitor.Height))
            return $"渲染区{_scene.SurfaceWidth}×{_scene.SurfaceHeight}未覆盖显示器{monitor.Width}×{monitor.Height}";
        return null;
    }
    private nint WindowMessages(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // WPF first consumes WM_DPICHANGED. Reapply physical bounds after its DIP layout settles.
        if (msg is 0x02E0 or 0x007E && _fullScreen)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!_fullScreen || _stopping) return;
                try { ApplyFullScreenBounds(); UpdateLayout(); if (Mouse.Captured == Arena) ClipMouse(); }
                catch (Exception ex) { _ = StopSessionAsync(ex.Message); }
            });
        return 0;
    }
    private void ExitFullScreen()
    {
        ReleaseMouse();
        if (_fullScreen)
        {
            _fullScreen = false; FullScreenLayer.Children.Remove(Arena); ArenaHost.Child = Arena; FullScreenLayer.Visibility = Visibility.Collapsed; MainShell.Visibility = Visibility.Visible;
            WindowStyle = _oldStyle; ResizeMode = _oldResize; Topmost = _oldTopmost; WindowState = WindowState.Normal;
            MinWidth = _oldMinWidth; MinHeight = _oldMinHeight;
            Left = _oldBounds.Left; Top = _oldBounds.Top; Width = _oldBounds.Width; Height = _oldBounds.Height; WindowState = _oldState;
            WindowBackdrop.Reapply(this);
        }
        if (_savedCursor)
        {
            // A desktop-wide clip must be released, not restored as a stale resolution rectangle.
            ClipCursor(0);
            SetCursorPos(_oldCursor.X, _oldCursor.Y); _savedCursor = false;
        }
    }
    private void CaptureTestMouse()
    {
        Arena.Focus();
        if (!Mouse.Capture(Arena, CaptureMode.SubTree)) { _ = StopSessionAsync("鼠标捕获失败，停止测试。"); return; }
        Mouse.OverrideCursor = Cursors.None; ClipMouse();
        if (TryClientBounds(out var rect)) SetCursorPos((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
    }
    private void ClipMouse()
    {
        if (!_fullScreen) return;
        if (!TryClientBounds(out var rect) || !ClipCursor(ref rect)) _ = StopSessionAsync("无法限制鼠标到测试窗口，停止测试。");
    }
    private bool TryClientBounds(out NativeRect rect)
    {
        var hwnd = new WindowInteropHelper(this).Handle; var origin = new NativePoint();
        if (!GetClientRect(hwnd, out rect) || !ClientToScreen(hwnd, ref origin) || rect.Right <= 0 || rect.Bottom <= 0) return false;
        rect.Right += origin.X; rect.Bottom += origin.Y; rect.Left = origin.X; rect.Top = origin.Y; return true;
    }
    private static NativeRect VirtualDesktopBounds() => new() { Left = GetSystemMetrics(76), Top = GetSystemMetrics(77), Right = GetSystemMetrics(76) + GetSystemMetrics(78), Bottom = GetSystemMetrics(77) + GetSystemMetrics(79) };
    private void ReleaseMouse() { Mouse.Capture(null); Mouse.OverrideCursor = null; ClipCursor(0); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool ClipCursor(ref NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool ClipCursor(nint rectangle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}


