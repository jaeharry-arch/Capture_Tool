// CaptureTool_V1.0.cs  -  고정 크기 화면 캡처 도구 (Windows, .NET Framework 4.x 내장 컴파일러용 / C# 5)
// 빌드: build.bat 더블클릭 → CaptureTool_V1.0.exe 생성 (Python 등 별도 설치 불필요)
// V1.0: 단일 빨간 테두리(크기 조절 겸용), 캡처 시 노란색 표시, 영상 녹화(MJPEG AVI) 추가
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int v);

    [STAThread]
    static void Main()
    {
        // 모니터별 DPI 인식 (배율이 다른 모니터에서도 화면 좌표 = 실제 픽셀 → 캡처 위치 어긋남 방지)
        bool ok = false;
        try { ok = SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }      // Per-Monitor V2 (Win10 1703+)
        if (!ok) try { ok = SetProcessDpiAwareness(2) == 0; } catch { }            // Per-Monitor (Win8.1+)
        if (!ok) try { SetProcessDPIAware(); } catch { }                            // System
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new CaptureForm());
    }
}

class CaptureForm : Form
{
    // ---------- 설정 ----------
    const int GAP = 1;          // 빨간 테두리와 캡처 영역 사이 투명 여유 (테두리 혼입 방지)
    const int MINS = 16;
    const int MAXS = 10000;
    const int HOTKEY_ID = 1;        // F9  캡처
    const int HOTKEY_REC = 2;       // F10 녹화 시작/정지
    const uint VK_F9 = 0x78;
    const uint VK_F10 = 0x79;
    const int REC_FPS = 15;                          // 녹화 프레임 수(초당)
    const long REC_SPLIT_BYTES = 1000L * 1024 * 1024; // 1GB 넘으면 다음 파일로 자동 분할
    static readonly Color KEY = Color.Magenta;                  // 투명 처리 색
    static readonly Color BORDER_COLOR = Color.FromArgb(229, 57, 53);
    static readonly Color FLASH_COLOR = Color.FromArgb(255, 214, 0);    // 캡처 순간 노란색
    static readonly Color TB_BG = Color.FromArgb(43, 43, 43);
    static readonly Color BTN_BG = Color.FromArgb(66, 66, 66);
    static readonly Color REC_BTN = Color.FromArgb(106, 27, 154);
    static readonly string[] PRESETS = {
        "640 x 480", "800 x 600", "1024 x 768", "1280 x 720", "1280 x 1024",
        "1600 x 900", "1920 x 1080", "500 x 500", "800 x 800", "1000 x 1000" };
    const string CUSTOM = "사용자 지정";

    // ---------- Win32 ----------
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint fmt, IntPtr hMem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr h);

    // ---------- 상태 ----------
    int capW = 800, capH = 600;
    string folder;
    bool maxed, syncing, busy, resizing;
    Point savedLoc, rsStart, rsLoc;
    int rsZone;   // 1=왼쪽, 2=오른쪽, 4=아래 (조합)
    Size savedSize, rsSize;
    int tbHeight, F;    // F = 빨간 테두리 두께 (드래그로 크기 조절)
    float scale = 1f;
    Rectangle regionRect;
    Color borderColor = BORDER_COLOR;
    readonly string iniPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CaptureTool", "settings.ini");

    // ---------- 컨트롤 ----------
    Panel tb;
    FlowLayoutPanel row1;
    Label status, btnMin, btnMax, btnClose;
    ComboBox preset;
    NumericUpDown nudW, nudH;
    System.Windows.Forms.Timer flashTimer, recTimer;
    Button bRec;
    Font fKor, fKorB, fNum, fWin;

    int S(int v) { return (int)Math.Round(v * scale); }

    public CaptureForm()
    {
        using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
        F = S(7);

        Text = "캡처 도구 V1.0";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        ShowInTaskbar = true;
        BackColor = KEY;
        TransparencyKey = KEY;
        DoubleBuffered = true;
        KeyPreview = true;

        fKor = new Font("Malgun Gothic", 9f);
        fKorB = new Font("Malgun Gothic", 9f, FontStyle.Bold);
        fNum = new Font("Arial", 9f);
        fWin = new Font("Segoe MDL2 Assets", 8f);

        LoadSettings();
        BuildToolbar();


        flashTimer = new System.Windows.Forms.Timer();
        flashTimer.Interval = 200;
        flashTimer.Tick += delegate { flashTimer.Stop(); borderColor = BORDER_COLOR; Invalidate(); };

        recTimer = new System.Windows.Forms.Timer();
        recTimer.Interval = 500;
        recTimer.Tick += delegate { UpdateRecUi(); };
        LocationChanged += delegate { UpdateRecOrigin(); };


        SyncInputs();
        Relayout();
        status.Text = "캡처: Ctrl+C / F9   녹화: F10  |  저장 폴더: " + folder;
    }

    // ---------- 툴바 ----------
    void BuildToolbar()
    {
        tb = new Panel();
        tb.BackColor = TB_BG;
        Controls.Add(tb);

        row1 = new FlowLayoutPanel();
        row1.AutoSize = true;
        row1.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        row1.WrapContents = false;
        row1.BackColor = TB_BG;
        row1.Location = new Point(S(4), S(4));
        row1.Margin = Padding.Empty;
        tb.Controls.Add(row1);

        preset = new ComboBox();
        preset.DropDownStyle = ComboBoxStyle.DropDownList;
        preset.Font = fNum;
        preset.Width = S(110);
        preset.Items.AddRange(PRESETS);
        preset.Items.Add(CUSTOM);
        preset.Margin = new Padding(0, 0, 0, 0);
        preset.SelectedIndexChanged += delegate
        {
            if (syncing) return;
            string v = (string)preset.SelectedItem;
            if (v == null || v == CUSTOM) return;
            string[] p = v.Split('x');
            SetCapSize(int.Parse(p[0].Trim()), int.Parse(p[1].Trim()));
        };
        row1.Controls.Add(preset);

        row1.Controls.Add(MkLabel("W"));
        nudW = MkNud(); row1.Controls.Add(nudW);
        row1.Controls.Add(MkLabel("H"));
        nudH = MkNud(); row1.Controls.Add(nudH);

        Button bCap = MkButton("● 캡처 (F9)", Color.FromArgb(198, 40, 40), fKorB);
        bCap.Margin = new Padding(S(12), 0, 0, 0);
        bCap.Click += delegate { DoCapture(); };
        row1.Controls.Add(bCap);

        bRec = MkButton("● 녹화 (F10)", REC_BTN, fKorB);
        bRec.Click += delegate { ToggleRecord(); };
        row1.Controls.Add(bRec);

        Button bFolder = MkButton("폴더 선택", BTN_BG, fKor);
        bFolder.Click += delegate { ChooseFolder(); };
        row1.Controls.Add(bFolder);

        Button bOpen = MkButton("폴더 열기", BTN_BG, fKor);
        bOpen.Click += delegate { OpenFolder(); };
        row1.Controls.Add(bOpen);

        // 최소화 / 최대화 / 종료  (Segoe MDL2 Assets 글리프)
        btnMin = MkWinBtn("\uE921", BTN_BG);
        btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
        btnMax = MkWinBtn("\uE922", BTN_BG);
        btnMax.Click += delegate { if (!recording) ToggleMax(); };
        btnClose = MkWinBtn("\uE8BB", Color.FromArgb(232, 17, 35));
        btnClose.Click += delegate { Close(); };

        status = new Label();
        status.AutoSize = false;
        status.AutoEllipsis = true;
        status.ForeColor = Color.FromArgb(170, 170, 170);
        status.BackColor = TB_BG;
        status.Font = fKor;
        status.Height = S(18);
        tb.Controls.Add(status);

        foreach (Control c in new Control[] { tb, row1, status })
            c.MouseDown += DragDown;
    }

    Label MkLabel(string t)
    {
        Label l = new Label();
        l.Text = t; l.AutoSize = true; l.ForeColor = Color.White; l.Font = fNum;
        l.Margin = new Padding(S(8), S(5), S(2), 0);
        l.MouseDown += DragDown;
        return l;
    }

    NumericUpDown MkNud()
    {
        NumericUpDown n = new NumericUpDown();
        n.Minimum = MINS; n.Maximum = MAXS; n.Increment = 10;
        n.Width = S(66); n.Font = fNum; n.TextAlign = HorizontalAlignment.Right;
        n.Margin = Padding.Empty;
        n.ValueChanged += delegate
        {
            if (syncing) return;
            SetCapSize((int)nudW.Value, (int)nudH.Value);
        };
        return n;
    }

    Button MkButton(string t, Color bg, Font f)
    {
        Button b = new Button();
        b.Text = t; b.Font = f; b.ForeColor = Color.White; b.BackColor = bg;
        b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
        b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(S(4), 0, S(4), 0);
        b.Margin = new Padding(S(4), 0, 0, 0);
        b.Cursor = Cursors.Hand;
        b.TabStop = false;
        return b;
    }

    Label MkWinBtn(string glyph, Color hover)
    {
        Label l = new Label();
        l.Text = glyph; l.Font = fWin; l.ForeColor = Color.White; l.BackColor = TB_BG;
        l.TextAlign = ContentAlignment.MiddleCenter;
        l.AutoSize = false; l.Size = new Size(S(34), S(26));
        l.Cursor = Cursors.Hand;
        l.MouseEnter += delegate { l.BackColor = hover; };
        l.MouseLeave += delegate { l.BackColor = TB_BG; };
        tb.Controls.Add(l);
        return l;
    }

    // ---------- 배치 / 그리기 ----------
    Rectangle frameRect;   // 빨간 테두리 바깥 경계

    void Relayout() { Relayout(null); }

    void Relayout(Point? loc)
    {
        Size r1 = row1.PreferredSize;
        int winW = btnMin.Width * 3;
        tbHeight = S(4) + Math.Max(r1.Height, btnMin.Height) + S(2) + status.Height + S(4);
        int tbW = S(4) + r1.Width + S(12) + winW;
        int fw = capW + 2 * (F + GAP), fh = capH + 2 * (F + GAP);
        int tbWidth = Math.Max(tbW, fw);

        tb.Bounds = new Rectangle(0, 0, tbWidth, tbHeight);
        btnClose.Location = new Point(tbWidth - btnMin.Width, 0);
        btnMax.Location = new Point(tbWidth - btnMin.Width * 2, 0);
        btnMin.Location = new Point(tbWidth - btnMin.Width * 3, 0);
        status.Bounds = new Rectangle(S(6), S(4) + Math.Max(r1.Height, btnMin.Height) + S(2),
                                      tbWidth - S(12), status.Height);

        frameRect = new Rectangle(0, tbHeight, fw, fh);
        regionRect = new Rectangle(F + GAP, tbHeight + F + GAP, capW, capH);

        Bounds = new Rectangle(loc ?? Location, new Size(tbWidth, tbHeight + fh));
        Invalidate();
        UpdateRecOrigin();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        using (SolidBrush br = new SolidBrush(borderColor))          // 단일 빨간 테두리 (캡처 시 노란색)
            g.FillRectangle(br, frameRect);
        using (SolidBrush k = new SolidBrush(KEY))                   // 투명 여유 + 캡처 영역
            g.FillRectangle(k, Rectangle.Inflate(regionRect, GAP, GAP));

        // 크기 조절 표시 (오른쪽 아래 모서리)
        if (!maxed && !recording)
            using (Pen p = new Pen(Color.FromArgb(200, 255, 255, 255), Math.Max(1f, scale)))
            {
                int R = frameRect.Right - 1, Bt = frameRect.Bottom - 1, L = F * 3;
                g.DrawLine(p, R - L, Bt - F / 2, R - F / 2, Bt - F / 2);
                g.DrawLine(p, R - F / 2, Bt - L, R - F / 2, Bt - F / 2);
            }
    }

    // ---------- 빨간 테두리로 크기 조절 ----------
    // 반환: 0=없음, -1=위쪽 테두리(창 이동), 그 외 1=왼쪽 | 2=오른쪽 | 4=아래 조합
    int HitZone(Point pt)
    {
        if (!frameRect.Contains(pt) || Rectangle.Inflate(regionRect, GAP, GAP).Contains(pt)) return 0;
        if (recording) return pt.Y < frameRect.Y + F ? -1 : 0;   // 녹화 중에는 크기 고정 (이동만)
        int c = F * 4;   // 모서리 판정 여유
        int z = 0;
        bool nearBottom = pt.Y >= frameRect.Bottom - c;
        if (pt.X < F || (nearBottom && pt.X < c)) z |= 1;
        if (pt.X >= frameRect.Right - F || (nearBottom && pt.X >= frameRect.Right - c)) z |= 2;
        if (pt.Y >= frameRect.Bottom - F || ((z & 3) != 0 && nearBottom)) z |= 4;
        if (z == 0 && pt.Y < frameRect.Y + F) return -1;
        return z;
    }

    Cursor ZoneCursor(int z)
    {
        if (z == -1) return Cursors.SizeAll;
        if (z == 5) return Cursors.SizeNESW;
        if (z == 6) return Cursors.SizeNWSE;
        if (z == 1 || z == 2) return Cursors.SizeWE;
        if (z == 4) return Cursors.SizeNS;
        return Cursors.Default;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || maxed) return;
        int z = HitZone(e.Location);
        if (z == -1) { DragDown(this, e); return; }
        if (z == 0) return;
        resizing = true; rsZone = z;
        rsStart = Cursor.Position; rsSize = new Size(capW, capH); rsLoc = Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!resizing)
        {
            Cursor = maxed ? Cursors.Default : ZoneCursor(HitZone(e.Location));
            return;
        }
        Point p = Cursor.Position;
        int dx = p.X - rsStart.X, dy = p.Y - rsStart.Y;
        Point loc = rsLoc;
        if ((rsZone & 2) != 0) capW = Clamp(rsSize.Width + dx);
        if ((rsZone & 1) != 0)
        {
            capW = Clamp(rsSize.Width - dx);
            loc.X = rsLoc.X + (rsSize.Width - capW);
        }
        if ((rsZone & 4) != 0) capH = Clamp(rsSize.Height + dy);
        SyncInputs(); Relayout(loc); ShowSize();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (resizing) { resizing = false; SaveSettings(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!resizing) Cursor = Cursors.Default;
    }

    // ---------- 크기 ----------
    int Clamp(int v) { return Math.Max(MINS, Math.Min(MAXS, v)); }

    void SetCapSize(int w, int h)
    {
        if (recording) { SyncInputs(); return; }   // 녹화 중 크기 변경 불가
        capW = Clamp(w); capH = Clamp(h);
        if (maxed) { maxed = false; btnMax.Text = "\uE922"; }
        SyncInputs(); Relayout(); ShowSize(); SaveSettings();
    }

    void SyncInputs()
    {
        syncing = true;
        nudW.Value = Clamp(capW);
        nudH.Value = Clamp(capH);
        string k = capW + " x " + capH;
        int idx = preset.Items.IndexOf(k);
        preset.SelectedIndex = idx >= 0 ? idx : preset.Items.Count - 1;
        syncing = false;
    }

    void ShowSize() { status.Text = string.Format("캡처 크기: {0} x {1} px", capW, capH); }

    void ToggleMax()
    {
        if (!maxed)
        {
            savedLoc = Location; savedSize = new Size(capW, capH);
            Rectangle sb = Screen.FromControl(this).Bounds;
            maxed = true;
            capW = sb.Width - 2 * (F + GAP);
            capH = sb.Height - tbHeight - 2 * (F + GAP);
            Location = sb.Location;
            btnMax.Text = "\uE923";
        }
        else
        {
            maxed = false;
            capW = savedSize.Width; capH = savedSize.Height;
            Location = savedLoc;
            btnMax.Text = "\uE922";
        }
        SyncInputs(); Relayout(); ShowSize();
    }

    // 창이 활성화된 상태에서 F9 또는 Ctrl+C → 캡처 (입력칸에 포커스가 있어도 우선 처리)
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F10)
        {
            BeginInvoke((MethodInvoker)delegate { ToggleRecord(); });
            return true;
        }
        if (keyData == (Keys.Control | Keys.C) || keyData == Keys.F9)
        {
            // 키 입력 처리가 끝난 뒤 캡처 (키 메시지 처리 중 클립보드 접근 충돌 방지)
            BeginInvoke((MethodInvoker)delegate { DoCapture(); });
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void DragDown(object s, MouseEventArgs e)
    {
        Activate();   // 상단 바 클릭 시 창 활성화 → Ctrl+C 사용 가능
        if (e.Button != MouseButtons.Left || maxed) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
        SaveSettings();
    }

    // ---------- 캡처 ----------
    void DoCapture()
    {
        if (busy || WindowState == FormWindowState.Minimized) return;
        busy = true;
        try
        {
            Point p = PointToScreen(regionRect.Location);
            using (Bitmap bmp = new Bitmap(capW, capH, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(p.X, p.Y, 0, 0, new Size(capW, capH), CopyPixelOperation.SourceCopy);

                Directory.CreateDirectory(folder);
                string name = string.Format("Capture_{0:yyyyMMdd_HHmmss_fff}_{1}x{2}.png", DateTime.Now, capW, capH);
                bmp.Save(Path.Combine(folder, name), ImageFormat.Png);

                string clip = "클립보드 복사됨";
                try { CopyToClipboard(bmp); }
                catch (Exception ex) { clip = "클립보드 실패: " + ex.Message; }
                status.Text = "저장: " + name + "  (" + clip + ")";
            }
            borderColor = FLASH_COLOR; Invalidate();
            flashTimer.Stop(); flashTimer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "캡처 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { busy = false; }
    }

    // ---------- 클립보드 (Win32 직접: CF_DIB + PNG) ----------
    static IntPtr ToHGlobal(byte[] data, int offset)
    {
        int n = data.Length - offset;
        IntPtr h = GlobalAlloc(0x0002, (UIntPtr)(uint)n);          // GMEM_MOVEABLE
        if (h == IntPtr.Zero) throw new Exception("메모리 할당 실패");
        IntPtr p = GlobalLock(h);
        Marshal.Copy(data, offset, p, n);
        GlobalUnlock(h);
        return h;
    }

    void CopyToClipboard(Bitmap bmp)
    {
        byte[] bmpBytes, pngBytes;
        using (MemoryStream ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Bmp); bmpBytes = ms.ToArray(); }
        using (MemoryStream ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); pngBytes = ms.ToArray(); }

        bool opened = false;
        for (int i = 0; i < 25 && !opened; i++)                     // 다른 프로그램이 점유 중이면 재시도
        {
            opened = OpenClipboard(Handle);
            if (!opened) System.Threading.Thread.Sleep(40);
        }
        if (!opened) throw new Exception("클립보드를 열 수 없습니다 (다른 프로그램이 사용 중)");
        try
        {
            EmptyClipboard();
            IntPtr hDib = ToHGlobal(bmpBytes, 14);                   // BMP 파일 헤더 제외 → DIB
            if (SetClipboardData(8, hDib) == IntPtr.Zero)            // CF_DIB
            {
                GlobalFree(hDib);
                throw new Exception("클립보드 쓰기 실패");
            }
            uint cfPng = RegisterClipboardFormat("PNG");             // Office 등은 PNG 우선 사용
            IntPtr hPng = ToHGlobal(pngBytes, 0);
            if (SetClipboardData(cfPng, hPng) == IntPtr.Zero) GlobalFree(hPng);
        }
        finally { CloseClipboard(); }
    }

    // ---------- 영상 녹화 (MJPEG AVI, 외부 프로그램 불필요) ----------
    volatile bool recording;
    volatile int recX, recY;          // 캡처 영역의 화면 좌표 (창 이동 시 갱신)
    int recW, recH;
    Thread recThread;
    DateTime recStart;

    void UpdateRecOrigin()
    {
        if (!IsHandleCreated) return;
        Point p = PointToScreen(regionRect.Location);
        recX = p.X; recY = p.Y;
    }

    void ToggleRecord()
    {
        if (recording) StopRecord(false); else StartRecord();
    }

    void StartRecord()
    {
        if (recording || WindowState == FormWindowState.Minimized) return;
        if (recThread != null && recThread.IsAlive) { status.Text = "이전 녹화 파일 저장 중... 잠시 후 다시 시도하세요"; return; }
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) { status.Text = "폴더 생성 실패: " + ex.Message; return; }

        recW = capW; recH = capH;
        UpdateRecOrigin();
        recStart = DateTime.Now;
        recording = true;

        preset.Enabled = nudW.Enabled = nudH.Enabled = false;
        bRec.Text = "■ 정지 00:00";
        bRec.BackColor = Color.FromArgb(198, 40, 40);
        Invalidate();
        recTimer.Start();

        string baseName = string.Format("Record_{0:yyyyMMdd_HHmmss}_{1}x{2}", recStart, recW, recH);
        string dir = folder;
        recThread = new Thread(delegate() { RecordLoop(dir, baseName); });
        recThread.IsBackground = true;
        recThread.Start();
    }

    void StopRecord(bool wait)
    {
        if (!recording) return;
        recording = false;
        if (wait && recThread != null) recThread.Join(5000);   // 종료 시 파일 마무리 대기
    }

    void UpdateRecUi()
    {
        if (!recording) return;
        TimeSpan t = DateTime.Now - recStart;
        bRec.Text = string.Format("■ 정지 {0:00}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        status.Text = string.Format("● 녹화 중 {0} x {1}  ({2} fps) — F10 또는 정지 버튼으로 종료", recW, recH, REC_FPS);
    }

    void RecordLoop(string dir, string baseName)
    {
        int part = 1, totalFrames = 0;
        string err = null;
        List<string> files = new List<string>();
        AviMjpegWriter avi = null;
        Stopwatch sw = Stopwatch.StartNew(), partSw = Stopwatch.StartNew();
        int partFrames = 0;
        try
        {
            ImageCodecInfo jpg = null;
            foreach (ImageCodecInfo ci in ImageCodecInfo.GetImageEncoders())
                if (ci.MimeType == "image/jpeg") jpg = ci;
            EncoderParameters ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);

            string path = Path.Combine(dir, baseName + ".avi");
            files.Add(path);
            avi = new AviMjpegWriter(path, recW, recH);

            using (Bitmap bmp = new Bitmap(recW, recH, PixelFormat.Format24bppRgb))
            using (Graphics g = Graphics.FromImage(bmp))
            using (MemoryStream ms = new MemoryStream())
            {
                double interval = 1000.0 / REC_FPS;
                long frameNo = 0;
                while (recording)
                {
                    int x = recX, y = recY;
                    try
                    {
                        g.CopyFromScreen(x, y, 0, 0, new Size(recW, recH), CopyPixelOperation.SourceCopy);
                        DrawCursor(g, x, y);
                    }
                    catch (System.ComponentModel.Win32Exception) { }   // 화면 잠금/UAC 등 일시적 실패 → 직전 프레임 유지
                    ms.SetLength(0);
                    bmp.Save(ms, jpg, ep);
                    avi.WriteFrame(ms.GetBuffer(), (int)ms.Length);
                    totalFrames++; partFrames++;

                    if (avi.Length > REC_SPLIT_BYTES)                // 용량 초과 → 다음 파일
                    {
                        avi.Close(partFrames / Math.Max(0.001, partSw.Elapsed.TotalSeconds));
                        part++;
                        path = Path.Combine(dir, baseName + "_part" + part + ".avi");
                        files.Add(path);
                        avi = new AviMjpegWriter(path, recW, recH);
                        partFrames = 0; partSw.Restart();
                    }

                    frameNo++;
                    long waitMs = (long)(frameNo * interval) - sw.ElapsedMilliseconds;
                    if (waitMs > 0) Thread.Sleep((int)waitMs);
                }
            }
        }
        catch (Exception ex) { err = ex.Message; recording = false; }
        finally
        {
            try { if (avi != null) avi.Close(partFrames / Math.Max(0.001, partSw.Elapsed.TotalSeconds)); }
            catch (Exception ex2) { if (err == null) err = ex2.Message; }
        }

        double secs = sw.Elapsed.TotalSeconds;
        try
        {
            BeginInvoke((MethodInvoker)delegate { OnRecordFinished(files, totalFrames, secs, err); });
        }
        catch { }   // 창이 이미 닫힌 경우
    }

    void OnRecordFinished(List<string> files, int frames, double secs, string err)
    {
        recTimer.Stop();
        preset.Enabled = nudW.Enabled = nudH.Enabled = true;
        bRec.Text = "● 녹화 (F10)";
        bRec.BackColor = REC_BTN;
        Invalidate();

        if (err != null)
        {
            status.Text = "녹화 오류: " + err;
            return;
        }
        string clip = "파일 클립보드 복사됨";
        try
        {
            StringCollection sc = new StringCollection();
            foreach (string f in files) sc.Add(f);
            Clipboard.SetFileDropList(sc);       // 탐색기/메신저/메일에 Ctrl+V → 파일 붙여넣기
        }
        catch (Exception ex) { clip = "클립보드 실패: " + ex.Message; }
        status.Text = string.Format("녹화 저장: {0}{1}  ({2:0.0}초, {3}프레임, {4})",
            Path.GetFileName(files[0]), files.Count > 1 ? " 외 " + (files.Count - 1) + "개" : "", secs, frames, clip);
    }

    // 마우스 커서를 녹화 프레임에 그림
    [StructLayout(LayoutKind.Sequential)] struct POINTAPI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINTAPI pt; }
    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public int fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    static void DrawCursor(Graphics g, int originX, int originY)
    {
        try
        {
            CURSORINFO ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
            if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0) return;   // CURSOR_SHOWING
            ICONINFO ii;
            int hx = 0, hy = 0;
            if (GetIconInfo(ci.hCursor, out ii))
            {
                hx = ii.xHotspot; hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            IntPtr hdc = g.GetHdc();
            try { DrawIconEx(hdc, ci.pt.X - originX - hx, ci.pt.Y - originY - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
            finally { g.ReleaseHdc(hdc); }
        }
        catch { }
    }

    // ---------- 폴더 ----------
    void ChooseFolder()
    {
        using (FolderBrowserDialog d = new FolderBrowserDialog())
        {
            d.Description = "캡처 저장 폴더 선택";
            if (Directory.Exists(folder)) d.SelectedPath = folder;
            if (d.ShowDialog(this) == DialogResult.OK)
            {
                folder = d.SelectedPath;
                status.Text = "저장 폴더: " + folder;
                SaveSettings();
            }
        }
    }

    void OpenFolder()
    {
        try { Directory.CreateDirectory(folder); Process.Start("explorer.exe", "\"" + folder + "\""); }
        catch (Exception ex) { status.Text = "폴더 열기 실패: " + ex.Message; }
    }

    // ---------- 전역 단축키 ----------
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        string fail = "";
        if (!RegisterHotKey(Handle, HOTKEY_ID, 0x4000, VK_F9)) fail += " F9";      // MOD_NOREPEAT
        if (!RegisterHotKey(Handle, HOTKEY_REC, 0x4000, VK_F10)) fail += " F10";
        if (fail.Length > 0)
            BeginInvoke((MethodInvoker)delegate {
                status.Text = "전역 단축키 등록 실패:" + fail + " (다른 프로그램이 사용 중) - 버튼을 사용하세요";
            });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)                                                 // WM_HOTKEY
        {
            int id = m.WParam.ToInt32();
            if (id == HOTKEY_ID) DoCapture();
            else if (id == HOTKEY_REC) ToggleRecord();
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(Handle, HOTKEY_ID);
        UnregisterHotKey(Handle, HOTKEY_REC);
        StopRecord(true);
        SaveSettings();
        base.OnFormClosed(e);
    }

    // ---------- 설정 저장 (%APPDATA%\CaptureTool\settings.ini) ----------
    void LoadSettings()
    {
        folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Captures");
        Point loc = new Point(100, 100);
        try
        {
            if (File.Exists(iniPath))
                foreach (string line in File.ReadAllLines(iniPath, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    int n;
                    if (k == "folder" && v.Length > 0) folder = v;
                    else if (k == "w" && int.TryParse(v, out n)) capW = Clamp(n);
                    else if (k == "h" && int.TryParse(v, out n)) capH = Clamp(n);
                    else if (k == "x" && int.TryParse(v, out n)) loc.X = n;
                    else if (k == "y" && int.TryParse(v, out n)) loc.Y = n;
                }
        }
        catch { }
        bool onScreen = false;
        foreach (Screen sc in Screen.AllScreens)
            if (sc.WorkingArea.Contains(new Point(loc.X + 20, loc.Y + 10))) onScreen = true;
        Location = onScreen ? loc : new Point(100, 100);
    }

    void SaveSettings()
    {
        try
        {
            Point loc = maxed ? savedLoc : Location;
            Size sz = maxed ? savedSize : new Size(capW, capH);
            if (WindowState == FormWindowState.Minimized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(iniPath));
            File.WriteAllText(iniPath, string.Format(
                "folder={0}\r\nw={1}\r\nh={2}\r\nx={3}\r\ny={4}\r\n",
                folder, sz.Width, sz.Height, loc.X, loc.Y), Encoding.UTF8);
        }
        catch { }
    }
}

// ---------- MJPEG AVI 기록기 (AVI 1.0, 외부 코덱 불필요 — Windows 기본 플레이어/VLC/PPT 재생) ----------
class AviMjpegWriter
{
    readonly FileStream fs;
    readonly BinaryWriter w;
    readonly int width, height;
    readonly List<int> offsets = new List<int>();
    readonly List<int> sizes = new List<int>();
    long riffSizePos, avihPos, strhPos, moviSizePos, moviFourccPos;
    int maxFrame;
    bool closed;

    public long Length { get { return fs.Position; } }

    public AviMjpegWriter(string path, int width, int height)
    {
        this.width = width; this.height = height;
        fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        w = new BinaryWriter(fs);

        Fcc("RIFF"); riffSizePos = fs.Position; w.Write(0); Fcc("AVI ");
        Fcc("LIST"); w.Write(4 + 8 + 56 + 12 + 8 + 56 + 8 + 40); Fcc("hdrl");

        Fcc("avih"); w.Write(56); avihPos = fs.Position;
        w.Write(0);              // dwMicroSecPerFrame (종료 시 기록)
        w.Write(0);              // dwMaxBytesPerSec
        w.Write(0);              // dwPaddingGranularity
        w.Write(0x10);           // dwFlags = AVIF_HASINDEX
        w.Write(0);              // dwTotalFrames
        w.Write(0);              // dwInitialFrames
        w.Write(1);              // dwStreams
        w.Write(0);              // dwSuggestedBufferSize
        w.Write(width); w.Write(height);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);

        Fcc("LIST"); w.Write(4 + 8 + 56 + 8 + 40); Fcc("strl");
        Fcc("strh"); w.Write(56); strhPos = fs.Position;
        Fcc("vids"); Fcc("MJPG");
        w.Write(0);              // dwFlags
        w.Write((short)0); w.Write((short)0);   // wPriority, wLanguage
        w.Write(0);              // dwInitialFrames
        w.Write(1000);           // dwScale
        w.Write(15000);          // dwRate (fps*1000, 종료 시 실제 값으로)
        w.Write(0);              // dwStart
        w.Write(0);              // dwLength
        w.Write(0);              // dwSuggestedBufferSize
        w.Write(-1);             // dwQuality
        w.Write(0);              // dwSampleSize
        w.Write((short)0); w.Write((short)0); w.Write((short)width); w.Write((short)height);

        Fcc("strf"); w.Write(40);
        w.Write(40); w.Write(width); w.Write(height);
        w.Write((short)1); w.Write((short)24);
        Fcc("MJPG");
        w.Write(width * height * 3);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);

        Fcc("LIST"); moviSizePos = fs.Position; w.Write(0);
        moviFourccPos = fs.Position; Fcc("movi");
    }

    void Fcc(string s) { w.Write(Encoding.ASCII.GetBytes(s)); }

    public void WriteFrame(byte[] data, int len)
    {
        offsets.Add((int)(fs.Position - moviFourccPos));
        sizes.Add(len);
        if (len > maxFrame) maxFrame = len;
        Fcc("00dc"); w.Write(len); w.Write(data, 0, len);
        if ((len & 1) == 1) w.Write((byte)0);
    }

    public void Close(double fps)
    {
        if (closed) return;
        closed = true;
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps)) fps = 15;
        int n = sizes.Count;
        long moviEnd = fs.Position;

        Fcc("idx1"); w.Write(n * 16);
        for (int i = 0; i < n; i++) { Fcc("00dc"); w.Write(0x10); w.Write(offsets[i]); w.Write(sizes[i]); }
        long end = fs.Position;

        fs.Position = riffSizePos; w.Write((int)(end - 8));
        fs.Position = moviSizePos; w.Write((int)(moviEnd - moviSizePos - 4));

        fs.Position = avihPos;
        w.Write((int)Math.Round(1000000.0 / fps));
        w.Write((int)(maxFrame * fps));
        fs.Position = avihPos + 16; w.Write(n);
        fs.Position = avihPos + 28; w.Write(maxFrame + 8);

        fs.Position = strhPos + 20; w.Write(1000); w.Write((int)Math.Round(fps * 1000));
        fs.Position = strhPos + 32; w.Write(n); w.Write(maxFrame + 8);

        w.Flush();
        fs.Close();
    }
}
