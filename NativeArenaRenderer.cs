using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AimSense;

// The GL context, resources and SwapBuffers belong exclusively to the rendering thread.
// WPF owns settings and session control. It never performs GL calls or drives the present clock.
internal sealed class NativeArenaRenderer : HwndHost
{
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stop = new();
    private Thread? _thread;
    private nint _hwnd, _dc;
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ArenaPlayback? _playback;
    private ActorPose? _pose;
    private double _yaw, _pitch, _fov = 90, _refreshHz = 240;
    private bool _visible, _preview;
    private int _resolvedMask;
    private Crosshair _crosshair = new(5, 3, 2, true, 0, 1, 1);
    private string _hud = "", _hint = "", _direction = "";
    private RendererMetrics _metrics = new(0, 0, 0, "初始化", false);
    private Exception? _error;
    private long _lastStalledBlock;
    private long _presentCount;
    private int _surfaceWidth, _surfaceHeight;
    public int SurfaceWidth => Volatile.Read(ref _surfaceWidth);
    public int SurfaceHeight => Volatile.Read(ref _surfaceHeight);
    private CaptureRequest? _capture;
    private sealed record CaptureRequest(string Path, TaskCompletionSource Completion);
    internal Task CaptureFrameAsync(string path)
    {
        var request = new CaptureRequest(path, new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (Interlocked.CompareExchange(ref _capture, request, null) != null) throw new InvalidOperationException("已有待处理的场景导出。");
        return request.Completion.Task;
    }
    public long PresentCount => Interlocked.Read(ref _presentCount);
    public long LastStalledBlock => Interlocked.Read(ref _lastStalledBlock);
    public Task Ready => _ready.Task;
    public Exception? Error => Volatile.Read(ref _error);
    public RendererMetrics Metrics => Volatile.Read(ref _metrics);
    private int _multisampleFlag;
    // 是否真的拿到了 4× 多样本像素格式（由驱动决定，报告里据实写出，不猜）。
    public bool Multisample => Volatile.Read(ref _multisampleFlag) != 0;
    public string MultisampleNote { get; private set; } = "未尝试";
    internal sealed record RendererMetrics(double Fps, double P95Ms, long Presents, string Device, bool VSync);
    private sealed record Crosshair(double Length, double Gap, double Thickness, bool Dot, float R, float G, float B);

    public void SetCamera(double yaw, double pitch, double fov) { lock (_gate) { _yaw = yaw; _pitch = pitch; _fov = fov; } }
    public void SetActor(ActorPose pose, bool preview) { lock (_gate) { _pose = pose; _visible = true; _preview = preview; } }
    public void HideActor() { lock (_gate) _visible = false; }
    public void SetPlayback(ArenaPlayback? playback) { lock (_gate) { _playback = playback; _resolvedMask = 0; } }
    public void SetResolved(bool[] resolved) { int mask = 0; for (int i = 0; i < resolved.Length; i++) if (resolved[i]) mask |= 1 << i; lock (_gate) _resolvedMask = mask; }
    public void SetRefresh(double hz) { lock (_gate) _refreshHz = hz; }
    public void SetHud(string hud, string hint, string direction) { lock (_gate) { _hud = hud; _hint = hint; _direction = direction; } }
    public void SetCrosshair(double length, double gap, double thickness, bool dot, byte r, byte g, byte b)
    { lock (_gate) _crosshair = new(length, gap, thickness, dot, r / 255f, g / 255f, b / 255f); }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        _stop.Reset(); _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _error, null);
        _hwnd = Gl.CreateWindowExW(0, "STATIC", "AimSense native arena", 0x50000000 | 0x04000000 | 0x02000000,
            0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (_hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _dc = Gl.GetDC(_hwnd);
        _thread = new(RenderLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "AimSense GPU render" }; _thread.Start();
        return new(this, _hwnd);
    }
    protected override void DestroyWindowCore(HandleRef window)
    {
        _stop.Set();
        if (_thread != null && !_thread.Join(3000)) throw new InvalidOperationException("原生渲染线程未能停止。");
        Gl.ReleaseDC(window.Handle, _dc); Gl.DestroyWindow(window.Handle); _hwnd = _dc = 0;
    }
    protected override nint WndProc(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        if (msg == 0x84) { handled = true; return -1; } // HTTRANSPARENT: parent receives clicks and keeps RawInput focus.
        if (msg == 0x14) { handled = true; return 1; }
        return base.WndProc(hwnd, msg, wp, lp, ref handled);
    }

    // 试探 4× MSAA：先用一个临时窗口+临时上下文换取 wglChoosePixelFormatARB，
    // 再在真实 DC 上挑一个"带 4 个采样、硬件加速、双缓冲"的格式。任一步失败即返回 false。
    private bool TryEnableMultisample(ref Gl.PixelFormat format)
    {
        nint probeWindow = 0, probeDc = 0, probeContext = 0;
        try
        {
            if (_dc == 0) { MultisampleNote = "真实 DC 尚未建立"; return false; }
            probeWindow = Gl.CreateWindowExW(0, "STATIC", "AimSense gl probe", 0x80000000, 0, 0, 1, 1, 0, 0, 0, 0);
            if (probeWindow == 0) { MultisampleNote = "探测窗口创建失败"; return false; }
            probeDc = Gl.GetDC(probeWindow);
            if (probeDc == 0) { MultisampleNote = "探测 DC 获取失败"; return false; }
            var basic = new Gl.PixelFormat { Size = (ushort)Marshal.SizeOf<Gl.PixelFormat>(), Version = 1, Flags = 0x25, ColorBits = 32, DepthBits = 24, StencilBits = 8 };
            int basicIndex = Gl.ChoosePixelFormat(probeDc, ref basic);
            if (basicIndex == 0 || !Gl.SetPixelFormat(probeDc, basicIndex, ref basic)) { MultisampleNote = "探测像素格式设置失败"; return false; }
            probeContext = Gl.wglCreateContext(probeDc);
            if (probeContext == 0 || !Gl.wglMakeCurrent(probeDc, probeContext)) { MultisampleNote = "探测上下文创建失败"; return false; }
            nint address = Gl.wglGetProcAddress("wglChoosePixelFormatARB");
            if (address == 0 || address == 1 || address == 2 || address == 3 || address == -1) { MultisampleNote = "驱动未导出 wglChoosePixelFormatARB"; return false; }
            var choose = Marshal.GetDelegateForFunctionPointer<Gl.ChoosePixelFormatArb>(address);
            // 注意：wglChoosePixelFormatARB 是 WGL 扩展函数，调用线程必须**保持** current context，
            // 所以探测上下文要一直 current 到这里（原来提前 wglMakeCurrent(0,0) 会让它返回 0 个格式）。

            // 依次尝试 4× 与 2×；不带 WGL_ACCELERATION_ARB 过滤（混合显卡/Optimus 下该过滤常导致 0 个格式）。
            foreach (int samples in new[] { 4, 2 })
            {
                int[] attributes =
                [
                    0x2001, 1,        // WGL_DRAW_TO_WINDOW_ARB
                    0x2002, 1,        // WGL_SUPPORT_OPENGL_ARB
                    0x2011, 1,        // WGL_DOUBLE_BUFFER_ARB
                    0x2010, 0x202B,   // WGL_PIXEL_TYPE_ARB = WGL_TYPE_RGBA_ARB
                    0x2013, 32,       // WGL_COLOR_BITS_ARB
                    0x2022, 24,       // WGL_DEPTH_BITS_ARB
                    0x2023, 8,        // WGL_STENCIL_BITS_ARB
                    0x2041, 1,        // WGL_SAMPLE_BUFFERS_ARB
                    0x2042, samples,  // WGL_SAMPLES_ARB
                    0
                ];
                if (!choose(_dc, attributes, null, 1, out int chosen, out uint count) || count == 0) continue;
                if (!Gl.SetPixelFormat(_dc, chosen, ref format))
                { MultisampleNote = $"SetPixelFormat({samples}×, 格式 {chosen}) 失败，Win32={Marshal.GetLastWin32Error()}"; return false; }
                MultisampleNote = $"已启用 {samples}× MSAA（格式 {chosen}）";
                return true;
            }
            MultisampleNote = "本机驱动未返回多样本格式（4×/2× 均为 0），已回退到无 MSAA 的双缓冲格式";
            return false;
        }
        catch (Exception ex) { MultisampleNote = "异常：" + ex.Message; return false; }
        finally
        {
            Gl.wglMakeCurrent(0, 0);   // 必须先解除 current，才能删除探测上下文
            if (probeContext != 0) Gl.wglDeleteContext(probeContext);
            if (probeDc != 0) Gl.ReleaseDC(probeWindow, probeDc);
            if (probeWindow != 0) Gl.DestroyWindow(probeWindow);
        }
    }

    private void RenderLoop()
    {
        nint context = 0, font = 0, oldFont = 0;
        var glyphs = new Dictionary<char, uint>(); uint sphereHigh = 0, sphereLow = 0, tube = 0, ground = 0, hudList = 0;
        try
        {
            var format = new Gl.PixelFormat { Size = (ushort)Marshal.SizeOf<Gl.PixelFormat>(), Version = 1, Flags = 0x25, ColorBits = 32, DepthBits = 24, StencilBits = 8 };
            // 优先尝试 4× MSAA（WGL_ARB_pixel_format）：边缘锯齿是当前画面最显眼的粗糙来源。
            // 取不到扩展点或没有可用多样本格式时，原样退回 ChoosePixelFormat，行为与优化前一致。
            bool multisample = TryEnableMultisample(ref format);
            if (!multisample)
            {
                int index = Gl.ChoosePixelFormat(_dc, ref format);
                if (index == 0 || !Gl.SetPixelFormat(_dc, index, ref format)) throw new InvalidOperationException("无法创建双缓冲原生3D像素格式。");
            }
            MultisampleNote = MultisampleNote.Length == 0 ? "未尝试" : MultisampleNote;
            context = Gl.wglCreateContext(_dc);
            if (context == 0 || !Gl.wglMakeCurrent(_dc, context)) throw new InvalidOperationException("无法初始化原生3D渲染上下文。");
            if (multisample) Gl.glEnable(0x809D);   // GL_MULTISAMPLE
            Volatile.Write(ref _multisampleFlag, multisample ? 1 : 0);
            string device = Marshal.PtrToStringAnsi(Gl.glGetString(0x1F01)) ?? "未知GPU";
            if (device.Contains("GDI Generic", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("显卡未提供硬件OpenGL驱动，请安装显卡驱动后测试。");
            nint swapAddress = Gl.wglGetProcAddress("wglSwapIntervalEXT");
            bool sync = swapAddress != 0 && swapAddress != 1 && swapAddress != 2 && swapAddress != 3 && swapAddress != -1
                && Marshal.GetDelegateForFunctionPointer<Gl.SwapInterval>(swapAddress)(1);
            sphereHigh = BuildSphere(22, 30); sphereLow = BuildSphere(9, 12); tube = BuildTube();
            ground = BuildGround(); hudList = Gl.glGenLists(1);
            Gl.glEnable(0x0BA1); Gl.glEnable(0x0B57); Gl.glColorMaterial(0x0408, 0x1602);
            Gl.glLightfv(0x4000, 0x1200, [.35f, .35f, .35f, 1]); Gl.glLightfv(0x4000, 0x1201, [.8f, .8f, .8f, 1]);
            // 补光：与主光反向的弱方向光，避免背光面糊成一片黑（人物轮廓更容易判读）
            Gl.glEnable(0x4001);
            Gl.glLightfv(0x4001, 0x1200, [.10f, .11f, .14f, 1]); Gl.glLightfv(0x4001, 0x1201, [.38f, .36f, .32f, 1]);
            // 雾：只用来交代远处地面与围墙的深度层次；起点 34 米，6–16 米的判读距离完全不受影响
            Gl.glEnable(0x0B60); Gl.glFogi(0x0B65, 0x2601);
            Gl.glFogf(0x0B63, 34f); Gl.glFogf(0x0B64, 150f); Gl.glFogfv(0x0B66, [.066f, .106f, .16f, 1]);
            font = Gl.CreateFontW(-20, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Microsoft YaHei UI");
            if (font == 0) throw new InvalidOperationException("无法创建靶场提示字体。");
            oldFont = Gl.SelectObject(_dc, font);
            _metrics = new(0, 0, 0, device, sync); _ready.TrySetResult();
            var times = new Queue<double>(); long previous = 0, status = 0, presents = 0;
            string priorHud = "", priorHint = "", priorDirection = ""; int priorWidth = 0, priorHeight = 0;
            var camera = new FpsCamera();
            while (!_stop.IsSet)
            {
                if (!Gl.IsWindowVisible(_hwnd)) { _stop.Wait(20); previous = 0; continue; }
                if (!Gl.GetClientRect(_hwnd, out var client) || client.Right <= 1 || client.Bottom <= 1) { _stop.Wait(20); continue; }
                Volatile.Write(ref _surfaceWidth, client.Right); Volatile.Write(ref _surfaceHeight, client.Bottom);
                long now = Stopwatch.GetTimestamp();
                ArenaPlayback? playback; ActorPose? pose; bool visible, preview; int resolved;
                double yaw, pitch, fov, refresh; string hud, hint, direction; Crosshair cross;
                CaptureRequest? capture;
                lock (_gate)
                {
                    playback = _playback; pose = _pose; visible = _visible; preview = _preview; resolved = _resolvedMask;
                    yaw = _yaw; pitch = _pitch; fov = _fov; refresh = _refreshHz;
                    hud = _hud; hint = _hint; direction = _direction; cross = _crosshair;
                    capture = Interlocked.Exchange(ref _capture, null);
                }
                if (playback != null)
                {
                    var sample = playback.Sample(now, resolved); pose = sample.Pose; visible = !sample.Hidden; preview = sample.Preview;
                    if (playback.Demo)
                    {
                        var angles = camera.TargetAngles(pose); double amount = playback.Mode == TrialMode.Track ? 1 : Math.Clamp(sample.SlotTime / .6, 0, 1);
                        yaw = angles.Yaw * amount; pitch = angles.Pitch * amount;
                    }
                    else if (visible)
                    {
                        camera.Yaw = yaw; camera.Pitch = pitch;
                        if (!camera.Project(pose.HeadCenter, fov, (double)client.Right / client.Bottom).OnScreen)
                        {
                            var target = camera.TargetAngles(pose); double bearing = ((target.Yaw - yaw + 540) % 360 + 360) % 360 - 180;
                            direction = Math.Abs(bearing) > 5 ? (bearing > 0 ? "→ 人物在右侧" : "← 人物在左侧") : (target.Pitch > pitch ? "↑ 人物在上方" : "↓ 人物在下方");
                        }
                        else direction = "";
                    }
                }
                // UI panels must not conceal a head that remains hittable. Render the
                // environment, then HUD, then the actor, and finally the fixed crosshair.
                DrawWorld(client.Right, client.Bottom, yaw, pitch, fov, preview, ground);
                if (hud != priorHud || hint != priorHint || direction != priorDirection || client.Right != priorWidth || client.Bottom != priorHeight)
                {
                    // Create missing glyphs before compiling the HUD list: GL lists cannot be nested.
                    foreach (char c in hud + hint + direction + "P 暂停 / 继续 · Esc / 右键结束")
                    {
                        if (c is '\n' or '\r' || glyphs.ContainsKey(c)) continue;
                        uint list = Gl.glGenLists(1);
                        if (!Gl.wglUseFontBitmapsW(_dc, c, 1, list)) { Gl.glDeleteLists(list, 1); continue; }
                        glyphs[c] = list;
                    }
                    Gl.glNewList(hudList, 0x1300);
                    DrawHud(client.Right, client.Bottom, hud, hint, direction, glyphs); Gl.glEndList();
                    priorHud = hud; priorHint = hint; priorDirection = direction; priorWidth = client.Right; priorHeight = client.Bottom;
                }
                SetOverlay(client.Right, client.Bottom); Gl.glCallList(hudList);
                if (visible && pose != null)
                {
                    SetWorldView(client.Right, client.Bottom, yaw, pitch, fov);
                    DrawActor(pose, preview, sphereHigh, sphereLow, tube);
                }
                SetOverlay(client.Right, client.Bottom); DrawCrosshair(client.Right, client.Bottom, cross);
                if (capture != null) CaptureFrame(client.Right, client.Bottom, capture);
                if (!Gl.SwapBuffers(_dc)) throw new InvalidOperationException("原生3D交换缓冲失败，停止测试。");
                long presented = Stopwatch.GetTimestamp(); presents++;
                Interlocked.Exchange(ref _presentCount, presents);
                if (previous != 0)
                {
                    double ms = (presented - previous) * 1000d / Stopwatch.Frequency;
                    times.Enqueue(ms); while (times.Count > 480) times.Dequeue();
                    if (ms > 100 && playback is { Demo: false } && (now - playback.Started) / (double)Stopwatch.Frequency is >= 4 and < 16)
                        Interlocked.Exchange(ref _lastStalledBlock, playback.Started);
                }
                previous = presented;
                if ((presented - status) / (double)Stopwatch.Frequency >= .25 && times.Count > 0)
                {
                    double[] sorted = times.ToArray(); Array.Sort(sorted);
                    _metrics = new(1000 / sorted.Average(), sorted[(int)((sorted.Length - 1) * .95)], presents, device, sync); status = presented;
                }
                // Prevent an unlimited loop if the driver overrides vsync or doesn't expose it.
                double frameTarget = 1d / Math.Clamp(refresh, 30, 1000);
                double remaining = frameTarget - (Stopwatch.GetTimestamp() - now) / (double)Stopwatch.Frequency;
                if (!sync)
                {
                    if (remaining > .0015) _stop.Wait((int)((remaining - .0005) * 1000));
                    // 不再整段自旋：先让出时间片，只在最后 300 微秒自旋。
                    // 整段自旋会占满一个核，反过来和 WPF 线程抢 CPU，制造我们自己要避免的帧间隔抖动。
                    while (!_stop.IsSet && (Stopwatch.GetTimestamp() - now) / (double)Stopwatch.Frequency < frameTarget)
                    {
                        double left = frameTarget - (Stopwatch.GetTimestamp() - now) / (double)Stopwatch.Frequency;
                        if (left > .0003) Thread.Sleep(0); else Thread.SpinWait(16);
                    }
                }
            }
        }
        catch (Exception ex) { Volatile.Write(ref _error, ex); _ready.TrySetException(ex); }
        finally
        {
            Interlocked.Exchange(ref _capture, null)?.Completion.TrySetException(new InvalidOperationException("渲染已结束，未导出场景。"));
            if (context != 0)
            {
                foreach (uint list in glyphs.Values) Gl.glDeleteLists(list, 1);
                if (sphereHigh != 0) Gl.glDeleteLists(sphereHigh, 1);
                if (sphereLow != 0) Gl.glDeleteLists(sphereLow, 1);
                if (tube != 0) Gl.glDeleteLists(tube, 1);
                if (ground != 0) Gl.glDeleteLists(ground, 1); if (hudList != 0) Gl.glDeleteLists(hudList, 1);
                Gl.wglMakeCurrent(0, 0); Gl.wglDeleteContext(context);
            }
            if (oldFont != 0) Gl.SelectObject(_dc, oldFont); if (font != 0) Gl.DeleteObject(font);
        }
    }
    private static void DrawWorld(int width, int height, double yaw, double pitch, double fov, bool preview, uint ground)
    {
        Gl.glViewport(0, 0, width, height); Gl.glClearColor(.066f, .106f, .16f, 1); Gl.glClear(0x4100);
        SetWorldView(width,height,yaw,pitch,fov);Gl.glCallList(ground);
    }
    private static void SetWorldView(int width,int height,double yaw,double pitch,double fov)
    {
        Gl.glEnable(0x0B71); Gl.glDisable(0x0BE2); Gl.glDisable(0x0B50);
        Gl.glMatrixMode(0x1701); Gl.glLoadIdentity(); double right = .08 * Math.Tan(Math.Clamp(fov, 60, 140) * Math.PI / 360);
        Gl.glFrustum(-right, right, -right * height / width, right * height / width, .08, 160);
        Gl.glMatrixMode(0x1700); Gl.glLoadIdentity(); Gl.glRotated(-pitch, 1, 0, 0); Gl.glRotated(yaw, 0, 1, 0); Gl.glTranslated(0, -1.62, 0);
    }

    // 人物：躯干用高精度椭球（视觉主体），四肢用**圆柱管 + 关节球**而不是拉伸的球体。
    // 这样既像人体，又把四肢的三角形数从每段 1536 降到约 300（8 段共省下约 1 万个三角形/帧）。
    // 视觉改动一律不碰碰撞几何：头部椭球仍然是 ActorPose.HeadRadii，命中判定口径不变。
    private static void DrawActor(ActorPose pose, bool preview, uint sphereHigh, uint sphereLow, uint tube)
    {
        Gl.glEnable(0x0B50); Gl.glEnable(0x4000); Gl.glEnable(0x4001);
        // 主光跟随视角（略偏右上），补光在左下方，两个方向光叠加后体积感明显强于单一方向光
        Gl.glLightfv(0x4000, 0x1203, [.3f, .8f, .5f, 0]);
        Gl.glLightfv(0x4001, 0x1203, [-.5f, .2f, -.7f, 0]);
        float bodyR = preview ? .47f : .35f, bodyG = preview ? .82f : .48f, bodyB = preview ? 1f : .61f;
        float limbR = preview ? .42f : .28f, limbG = preview ? .78f : .40f, limbB = preview ? 1f : .52f;

        // 躯干与骨盆
        Gl.glColor3f(bodyR, bodyG, bodyB);
        Ellipsoid(pose.ChestCenter, new(.245f, .28f, .13f), pose.FacingYaw, sphereHigh);
        Ellipsoid(pose.PelvisCenter, new(.18f, .15f, .12f), pose.FacingYaw, sphereHigh);
        // 颈与肩轭：把"胸 + 头"连成人体，而不是两个悬空的球
        Gl.glColor3f(limbR, limbG, limbB);
        Tube(pose.Neck, pose.HeadCenter, .052, tube, sphereLow, .052);
        Tube(pose.LeftShoulder, pose.RightShoulder, .072, tube, sphereLow, .072);
        // 四肢：上臂/前臂/大腿/小腿各一根管，肘膝腕踝各一个关节球
        Limb(pose.LeftShoulder, pose.LeftElbow, .062, .068, tube, sphereLow);
        Limb(pose.LeftElbow, pose.LeftHand, .050, .050, tube, sphereLow);
        Limb(pose.RightShoulder, pose.RightElbow, .062, .068, tube, sphereLow);
        Limb(pose.RightElbow, pose.RightHand, .050, .050, tube, sphereLow);
        Limb(pose.LeftHip, pose.LeftKnee, .078, .080, tube, sphereLow);
        Limb(pose.LeftKnee, pose.LeftFoot, .060, .062, tube, sphereLow);
        Limb(pose.RightHip, pose.RightKnee, .078, .080, tube, sphereLow);
        Limb(pose.RightKnee, pose.RightFoot, .060, .062, tube, sphereLow);
        // 头部（碰撞椭球本体）+ 朝向面片
        Gl.glColor3f(preview ? .47f : .95f, preview ? .82f : .55f, preview ? 1f : .43f);
        Ellipsoid(pose.HeadCenter, ActorPose.HeadRadii, pose.FacingYaw, sphereHigh);
        HeadFace(pose.HeadCenter, pose.FacingYaw);
    }

    private static void Limb(Vector3 a, Vector3 b, double radius, double jointRadius, uint tube, uint sphere)
    {
        Tube(a, b, radius, tube);
        Ellipsoid(b, new((float)jointRadius, (float)jointRadius, (float)jointRadius), 0, sphere);
    }

    // 朝向面片：头部局部坐标系里贴一块深色面片，让"人物正对哪边"一眼可读
    private static void HeadFace(Vector3 head, double yaw)
    {
        Gl.glPushMatrix(); Gl.glTranslated(head.X, head.Y, -head.Z); Gl.glRotated(-yaw, 0, 1, 0);
        Gl.glColor3f(.12f, .15f, .19f);
        Gl.glBegin(7);
        Gl.glVertex3d(-.058, .042, .086); Gl.glVertex3d(.058, .042, .086);
        Gl.glVertex3d(.050, -.020, .090); Gl.glVertex3d(-.050, -.020, .090);
        Gl.glEnd();
        Gl.glPopMatrix();
    }
    private static void Ellipsoid(Vector3 position, Vector3 radii, double yaw, uint sphere)
    { Gl.glPushMatrix(); Gl.glTranslated(position.X, position.Y, -position.Z); Gl.glRotated(-yaw, 0, 1, 0); Gl.glScaled(radii.X, radii.Y, radii.Z); Gl.glCallList(sphere); Gl.glPopMatrix(); }
    // 圆管：单位半径、y ∈ [-1, 1]；调用方用 glScaled(radius, length/2, radius) 拉伸即可。
    // capRadius 必须由调用方给出——它是**半径**，不是显示列表句柄（曾经把句柄当半径缩放，画出 3 米大球）。
    private static void Tube(Vector3 a, Vector3 b, double radius, uint tube, uint capSphere = 0, double capRadius = 0)
    {
        var direction = b - a; double length = direction.Length(); if (length < .0001) return;
        var center = (a + b) / 2;
        Gl.glPushMatrix(); Gl.glTranslated(center.X, center.Y, -center.Z);
        var axis = Vector3.Normalize(new Vector3(direction.X, direction.Y, -direction.Z)); var cross = Vector3.Cross(Vector3.UnitY, axis);
        if (cross.LengthSquared() < 1e-10) { if (axis.Y < 0) Gl.glRotated(180, 1, 0, 0); }
        else Gl.glRotated(Math.Acos(Math.Clamp(axis.Y, -1, 1)) * 180 / Math.PI, cross.X, cross.Y, cross.Z);
        Gl.glPushMatrix(); Gl.glScaled(radius, length / 2, radius); Gl.glCallList(tube); Gl.glPopMatrix();
        if (capSphere != 0 && capRadius > 0)
        {
            Gl.glPushMatrix(); Gl.glTranslated(0, length / 2, 0); Gl.glScaled(capRadius, capRadius, capRadius); Gl.glCallList(capSphere); Gl.glPopMatrix();
            Gl.glPushMatrix(); Gl.glTranslated(0, -length / 2, 0); Gl.glScaled(capRadius, capRadius, capRadius); Gl.glCallList(capSphere); Gl.glPopMatrix();
        }
        Gl.glPopMatrix();
    }
    private static uint BuildSphere(int stacks, int slices)
    {
        uint list = Gl.glGenLists(1); Gl.glNewList(list, 0x1300);
        for (int row = 0; row < stacks; row++)
        {
            Gl.glBegin(0x0005);
            for (int col = 0; col <= slices; col++)
            {
                double lat0 = Math.PI * row / stacks, lat1 = Math.PI * (row + 1) / stacks, lon = 2 * Math.PI * col / slices;
                double nx = Math.Sin(lon), nz = Math.Cos(lon);   // 两个纬度共用同一组水平法线分量
                Gl.glNormal3d(Math.Sin(lat0) * nx, Math.Cos(lat0), Math.Sin(lat0) * nz);
                Gl.glVertex3d(Math.Sin(lat0) * nx, Math.Cos(lat0), Math.Sin(lat0) * nz);
                Gl.glNormal3d(Math.Sin(lat1) * nx, Math.Cos(lat1), Math.Sin(lat1) * nz);
                Gl.glVertex3d(Math.Sin(lat1) * nx, Math.Cos(lat1), Math.Sin(lat1) * nz);
            }
            Gl.glEnd();
        }
        Gl.glEndList(); return list;
    }
    // 单位圆管：y ∈ [-1, 1]，半径 1，法线沿水平径向。四面都要画（不加背面剔除时两侧都可见）。
    private static uint BuildTube()
    {
        uint list = Gl.glGenLists(1); Gl.glNewList(list, 0x1300);
        const int slices = 14;
        for (int pass = 0; pass < 2; pass++)
        {
            Gl.glBegin(0x0005);
            for (int i = 0; i <= slices; i++)
            {
                double a = 2 * Math.PI * i / slices, x = Math.Cos(a), z = Math.Sin(a);
                double dir = pass == 0 ? 1 : -1;   // 第二遍反向绕序，管壁内外都可见
                Gl.glNormal3d(x * dir, 0, z * dir); Gl.glVertex3d(x * dir, -1, z * dir);
                Gl.glNormal3d(x * dir, 0, z * dir); Gl.glVertex3d(x * dir, 1, z * dir);
            }
            Gl.glEnd();
        }
        Gl.glEndList(); return list;
    }
    private static uint BuildGround()
    {
        uint list = Gl.glGenLists(1); Gl.glNewList(list, 0x1300); Gl.glColor3f(.098f, .133f, .176f);
        WorldQuad(-140, -.02, -140, 140, -.02, -140, 140, -.02, 140, -140, -.02, 140);

        // Sparse solid references replace the old thin grid. All distances are world metres.
        // Raised surfaces prevent coplanar depth flicker. Low markers stay below head height.
        // 近场 1 米网格：只铺 ±10 米，颜色刻意压到接近地面，用来交代横向与纵深距离，
        // 又不至于像旧版细网格那样整片抢注意力（远处靠雾自然淡出，不会产生摩尔纹）。
        Gl.glColor3f(.118f, .157f, .203f);
        Gl.glBegin(0x0001);
        for (int i = -10; i <= 10; i++)
        {
            if (i == 0) continue;   // 中轴留给下面的星形标记
            Gl.glVertex3d(i, .010, -10); Gl.glVertex3d(i, .010, 10);
            Gl.glVertex3d(-10, .010, i); Gl.glVertex3d(10, .010, i);
        }
        Gl.glEnd();

        Gl.glColor3f(.16f, .205f, .25f);
        Gl.glBegin(0x0006); Gl.glVertex3d(0, .015, 0);
        for (int i = 0; i <= 8; i++) Gl.glVertex3d(Math.Cos(i * Math.PI / 4), .015, Math.Sin(i * Math.PI / 4));
        Gl.glEnd();
        Gl.glColor3f(.26f, .34f, .40f);
        WorldQuad(-.16, .025, 1.3, .16, .025, 1.3, .16, .025, 2.15, -.16, .025, 2.15);
        Gl.glBegin(4); Gl.glVertex3d(-.42, .025, -2.1); Gl.glVertex3d(.42, .025, -2.1); Gl.glVertex3d(0, .025, -2.8); Gl.glEnd();

        foreach (int distance in new[] { 6, 10, 16, 24, 40, 60 })
        {
            double angle = (distance switch { 6 => 40, 10 => 28, 16 => 20, 24 => 14, 40 => 9, _ => 5 }) * Math.PI / 180;
            double side = distance * Math.Sin(angle), depth = distance * Math.Cos(angle);
            foreach (int direction in new[] { -1, 1 })
            {
                double x = direction * side;
                Box(x - .53, 0, depth - .44, x + .53, .10, depth + .44, .15f, .19f, .23f);
                Box(x - .42, .10, depth - .28, x + .42, .88, depth + .28, .24f, .32f, .39f);
                Gl.glColor3f(.45f, .63f, .70f);
                WorldQuad(x - .42, .73, depth - .29, x + .42, .73, depth - .29,
                    x + .42, .86, depth - .29, x - .42, .86, depth - .29);
                Gl.glColor3f(.80f, .86f, .89f);
                string digits = distance.ToString(System.Globalization.CultureInfo.InvariantCulture);
                double digitWidth = .24, gap = .07, total = digits.Length * digitWidth + (digits.Length - 1) * gap;
                for (int i = 0; i < digits.Length; i++)
                    ReferenceDigit(digits[i] - '0', x - total / 2 + i * (digitWidth + gap), .23, depth - .30, digitWidth, .39);
            }
        }

        // The enclosing walls and tall columns are outside the supported target paths.
        Box(-120.3, 0, 119.7, 120.3, 7, 120.3, .13f, .19f, .25f);
        Box(-120.3, 0, -120.3, 120.3, 7, -119.7, .18f, .16f, .17f);
        Box(-120.3, 0, -119.7, -119.7, 7, 119.7, .14f, .20f, .19f);
        Box(119.7, 0, -119.7, 120.3, 7, 119.7, .19f, .18f, .14f);
        for (int cardinal = 0; cardinal < 4; cardinal++)
        {
            Gl.glPushMatrix(); Gl.glRotated(cardinal * 90, 0, 1, 0);
            Box(-11, 0, 118.9, 11, 5.5, 119.5, .21f, .27f, .33f);
            Box(-13, 0, 118.2, -11, 6.5, 119.5, .27f, .34f, .40f);
            Box(11, 0, 118.2, 13, 6.5, 119.5, .27f, .34f, .40f);
            Box(-11, 5.5, 118.2, 11, 6.5, 119.5, .27f, .34f, .40f);
            Gl.glColor3f(.55f, .65f, .72f); ReferenceDigit(cardinal + 1, -1, 1.5, 118.8, 2, 3);
            Gl.glPopMatrix();
        }
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4, x = 90 * Math.Sin(angle), z = 90 * Math.Cos(angle);
            Box(x - .65, 0, z - .65, x + .65, 4, z + .65, .23f, .29f, .35f);
            Box(x - .72, 3.45, z - .72, x + .72, 3.9, z + .72, .37f, .46f, .50f);
        }
        Gl.glEndList(); return list;
    }
    private static void WorldQuad(double ax, double ay, double az, double bx, double by, double bz,
        double cx, double cy, double cz, double dx, double dy, double dz)
    {
        Gl.glBegin(7); Gl.glVertex3d(ax, ay, -az); Gl.glVertex3d(bx, by, -bz);
        Gl.glVertex3d(cx, cy, -cz); Gl.glVertex3d(dx, dy, -dz); Gl.glEnd();
    }
    private static void Box(double x1, double y1, double z1, double x2, double y2, double z2, float r, float g, float b)
    {
        Gl.glColor3f(r, g, b);
        WorldQuad(x1,y1,z1, x2,y1,z1, x2,y2,z1, x1,y2,z1);
        WorldQuad(x2,y1,z2, x1,y1,z2, x1,y2,z2, x2,y2,z2);
        Gl.glColor3f(r * .78f, g * .78f, b * .78f);
        WorldQuad(x1,y1,z2, x1,y1,z1, x1,y2,z1, x1,y2,z2);
        WorldQuad(x2,y1,z1, x2,y1,z2, x2,y2,z2, x2,y2,z1);
        Gl.glColor3f(r * 1.12f, g * 1.12f, b * 1.12f);
        WorldQuad(x1,y2,z1, x2,y2,z1, x2,y2,z2, x1,y2,z2);
    }
    private static void ReferenceDigit(int digit, double x, double y, double z, double w, double h)
    {
        ReadOnlySpan<byte> segments = [0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F];
        double t = w * .20;
        void Rect(double a, double b, double c, double d) => WorldQuad(x+a,y+b,z, x+c,y+b,z, x+c,y+d,z, x+a,y+d,z);
        byte mask = segments[digit];
        if ((mask & 1) != 0) Rect(0, h-t, w, h);
        if ((mask & 2) != 0) Rect(w-t, h/2, w, h);
        if ((mask & 4) != 0) Rect(w-t, 0, w, h/2);
        if ((mask & 8) != 0) Rect(0, 0, w, t);
        if ((mask & 16) != 0) Rect(0, 0, t, h/2);
        if ((mask & 32) != 0) Rect(0, h/2, t, h);
        if ((mask & 64) != 0) Rect(0, h/2-t/2, w, h/2+t/2);
    }
    private static void CaptureFrame(int width, int height, CaptureRequest request)
    {
        try
        {
            byte[] pixels = new byte[checked(width * height * 4)], flipped = new byte[pixels.Length];
            Gl.glReadPixels(0, 0, width, height, 0x80E1, 0x1401, pixels);
            int stride = width * 4;
            for (int row = 0; row < height; row++) Buffer.BlockCopy(pixels, row * stride, flipped, (height - 1 - row) * stride, stride);
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, flipped, stride);
            bitmap.Freeze(); var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(request.Path); png.Save(stream); request.Completion.SetResult();
        }
        catch (Exception ex) { request.Completion.TrySetException(ex); }
    }
    private static void SetOverlay(int width, int height)
    { Gl.glDisable(0x0B71); Gl.glDisable(0x0B50); Gl.glMatrixMode(0x1701); Gl.glLoadIdentity(); Gl.glOrtho(0, width, 0, height, -1, 1); Gl.glMatrixMode(0x1700); Gl.glLoadIdentity(); }
    private static void DrawHud(int width, int height, string hud, string hint, string direction, Dictionary<char, uint> glyphs)
    {
        SetOverlay(width, height); Gl.glColor3f(.09f, .14f, .21f); Quad(10, height - 190, Math.Min(width - 10, 1050), height - 10);
        Gl.glColor3f(.9f, .95f, 1); Text(hud, 22, height - 38, glyphs);
        Text("P 暂停 / 继续 · Esc / 右键结束", Math.Max(22, width - 380), height - 30, glyphs);
        Text(hint, Math.Max(16, width / 2 - hint.Length * 6), 30, glyphs);
        Gl.glColor3f(1, .88f, .64f); Text(direction, Math.Max(16, width / 2 - direction.Length * 8), 64, glyphs);
    }
    private static void Text(string text, int x, int y, Dictionary<char, uint> glyphs)
    {
        Gl.glRasterPos2i(x, y);
        foreach (char c in text)
        {
            if (c == '\n') { y -= 26; Gl.glRasterPos2i(x, y); } else if (glyphs.TryGetValue(c, out uint list)) Gl.glCallList(list);
        }
    }
    private static void Quad(double left, double bottom, double right, double top)
    { Gl.glBegin(7); Gl.glVertex2d(left, bottom); Gl.glVertex2d(right, bottom); Gl.glVertex2d(right, top); Gl.glVertex2d(left, top); Gl.glEnd(); }
    private static void DrawCrosshair(int width, int height, Crosshair c)
    {
        double x = width / 2d, y = height / 2d, t = c.Thickness / 2;
        Gl.glColor3f(c.R, c.G, c.B);
        Quad(x-c.Gap-c.Length, y-t, x-c.Gap, y+t); Quad(x+c.Gap, y-t, x+c.Gap+c.Length, y+t);
        Quad(x-t, y+c.Gap, x+t, y+c.Gap+c.Length); Quad(x-t, y-c.Gap-c.Length, x+t, y-c.Gap);
        if (c.Dot) Quad(x-t, y-t, x+t, y+t);
    }

    private static class Gl
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct PixelFormat
        {
            public ushort Size, Version; public uint Flags;
            public byte PixelType, ColorBits, RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift, AlphaBits, AlphaShift,
                AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits, DepthBits, StencilBits, AuxBuffers, LayerType, Reserved;
            public uint LayerMask, VisibleMask, DamageMask;
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate bool SwapInterval(int interval);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate bool ChoosePixelFormatArb(nint dc, int[] attributes, float[]? floatValues, uint maxFormats, out int format, out uint formats);
        [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
        [DllImport("gdi32.dll")] internal static extern int ChoosePixelFormat(nint dc, ref PixelFormat format);
        [DllImport("gdi32.dll")] internal static extern bool SetPixelFormat(nint dc, int index, ref PixelFormat format);
        [DllImport("gdi32.dll")] internal static extern bool SwapBuffers(nint dc);
        [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] internal static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
        [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint obj);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint obj);
        [DllImport("opengl32.dll")] internal static extern nint wglCreateContext(nint dc);
        [DllImport("opengl32.dll")] internal static extern bool wglMakeCurrent(nint dc, nint context);
        [DllImport("opengl32.dll")] internal static extern bool wglDeleteContext(nint context);
        [DllImport("opengl32.dll", CharSet=CharSet.Ansi)] internal static extern nint wglGetProcAddress(string name);
        [DllImport("opengl32.dll")] internal static extern bool wglUseFontBitmapsW(nint dc, uint first, uint count, uint listBase);
        [DllImport("opengl32.dll")] internal static extern nint glGetString(uint name);
        [DllImport("opengl32.dll")] internal static extern uint glGenLists(int range);
        [DllImport("opengl32.dll")] internal static extern void glNewList(uint list, uint mode);
        [DllImport("opengl32.dll")] internal static extern void glEndList();
        [DllImport("opengl32.dll")] internal static extern void glCallList(uint list);
        [DllImport("opengl32.dll")] internal static extern void glDeleteLists(uint list, int range);
        [DllImport("opengl32.dll")] internal static extern void glViewport(int x, int y, int w, int h);
        [DllImport("opengl32.dll")] internal static extern void glClearColor(float r, float g, float b, float a);
        [DllImport("opengl32.dll")] internal static extern void glClear(uint mask);
        [DllImport("opengl32.dll")] internal static extern void glEnable(uint capability);
        [DllImport("opengl32.dll")] internal static extern void glDisable(uint capability);
        [DllImport("opengl32.dll")] internal static extern void glMatrixMode(uint mode);
        [DllImport("opengl32.dll")] internal static extern void glLoadIdentity();
        [DllImport("opengl32.dll")] internal static extern void glFrustum(double l, double r, double b, double t, double n, double f);
        [DllImport("opengl32.dll")] internal static extern void glOrtho(double l, double r, double b, double t, double n, double f);
        [DllImport("opengl32.dll")] internal static extern void glRotated(double angle, double x, double y, double z);
        [DllImport("opengl32.dll")] internal static extern void glTranslated(double x, double y, double z);
        [DllImport("opengl32.dll")] internal static extern void glScaled(double x, double y, double z);
        [DllImport("opengl32.dll")] internal static extern void glPushMatrix();
        [DllImport("opengl32.dll")] internal static extern void glPopMatrix();
        [DllImport("opengl32.dll")] internal static extern void glBegin(uint mode);
        [DllImport("opengl32.dll")] internal static extern void glEnd();
        [DllImport("opengl32.dll")] internal static extern void glVertex3d(double x, double y, double z);
        [DllImport("opengl32.dll")] internal static extern void glVertex2d(double x, double y);
        [DllImport("opengl32.dll")] internal static extern void glColor3f(float r, float g, float b);
        [DllImport("opengl32.dll")] internal static extern void glColorMaterial(uint face, uint mode);
        [DllImport("opengl32.dll")] internal static extern void glLightfv(uint light, uint name, float[] values);
        [DllImport("opengl32.dll")] internal static extern void glFogf(uint name, float value);
        [DllImport("opengl32.dll")] internal static extern void glFogi(uint name, int value);
        [DllImport("opengl32.dll")] internal static extern void glFogfv(uint name, float[] values);
        [DllImport("opengl32.dll")] internal static extern void glNormal3d(double x, double y, double z);
        [DllImport("opengl32.dll")] internal static extern void glRasterPos2i(int x, int y);
        [DllImport("opengl32.dll")] internal static extern void glReadPixels(int x, int y, int w, int h, uint format, uint type, [Out] byte[] pixels);
    }
}
