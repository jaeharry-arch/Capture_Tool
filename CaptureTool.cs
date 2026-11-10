// CaptureTool.cs  -  고정 크기 화면 캡처 도구 (Windows, .NET Framework 4.x 내장 컴파일러용 / C# 5)
// 빌드: build.bat 더블클릭 → CaptureTool.exe 생성 (Python 등 별도 설치 불필요)
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
    const int B = 3;            // 테두리 두께 (캡처 영역 바깥)
    const int GAP = 1;          // 빨간 테두리와 캡처 영역 사이 투명 여유 (테두리 혼입 방지)
    const int MINS = 16;
    const int MAXS = 10000;
    const int HOTKEY_ID = 1;
    const uint VK_F9 = 0x78;
    static readonly Color KEY = Color.Magenta;                  // 투명 처리 색
    static readonly Color BORDER_COLOR = Color.FromArgb(229, 57, 53);
    static readonly Color FLASH_COLOR = Color.FromArgb(67, 160, 71);
    static readonly Color TB_BG = Color.FromArgb(43, 43, 43);
    static readonly Color BTN_BG = Color.FromArgb(66, 66, 66);
    static readonly Color FRAME_COLOR = Color.FromArgb(128, 128, 128);   // 바깥 회색 띠 (크기 조절)
    static readonly Color FRAME_LINE = Color.FromArgb(90, 90, 90);
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
    int tbHeight, GB;   // GB = 회색 띠 두께
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
    System.Windows.Forms.Timer flashTimer;
    Font fKor, fKorB, fNum, fWin;

    int S(int v) { return (int)Math.Round(v * scale); }

    public CaptureForm()
    {
        using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
        GB = S(8);

        Text = "캡처 도구";
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


        SyncInputs();
        Relayout();
        status.Text = "Ctrl+C 또는 F9로 캡처  |  저장 폴더: " + folder;
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
        btnMax.Click += delegate { ToggleMax(); };
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
    Rectangle frameRect;   // 회색 띠 바깥 경계

    void Relayout() { Relayout(null); }

    void Relayout(Point? loc)
    {
        Size r1 = row1.PreferredSize;
        int winW = btnMin.Width * 3;
        tbHeight = S(4) + Math.Max(r1.Height, btnMin.Height) + S(2) + status.Height + S(4);
        int tbW = S(4) + r1.Width + S(12) + winW;
        int fw = capW + 2 * (B + GAP + GB), fh = capH + 2 * (B + GAP + GB);
        int tbWidth = Math.Max(tbW, fw);

        tb.Bounds = new Rectangle(0, 0, tbWidth, tbHeight);
        btnClose.Location = new Point(tbWidth - btnMin.Width, 0);
        btnMax.Location = new Point(tbWidth - btnMin.Width * 2, 0);
        btnMin.Location = new Point(tbWidth - btnMin.Width * 3, 0);
        status.Bounds = new Rectangle(S(6), S(4) + Math.Max(r1.Height, btnMin.Height) + S(2),
                                      tbWidth - S(12), status.Height);

        frameRect = new Rectangle(0, tbHeight, fw, fh);
        regionRect = new Rectangle(GB + B + GAP, tbHeight + GB + B + GAP, capW, capH);

        Bounds = new Rectangle(loc ?? Location, new Size(tbWidth, tbHeight + fh));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        using (SolidBrush fr = new SolidBrush(FRAME_COLOR))
            g.FillRectangle(fr, frameRect);
        using (Pen ln = new Pen(FRAME_LINE))
            g.DrawRectangle(ln, frameRect.X, frameRect.Y, frameRect.Width - 1, frameRect.Height - 1);
        using (SolidBrush br = new SolidBrush(borderColor))
            g.FillRectangle(br, Rectangle.Inflate(regionRect, B + GAP, B + GAP));
        using (SolidBrush k = new SolidBrush(KEY))
            g.FillRectangle(k, Rectangle.Inflate(regionRect, GAP, GAP));

        // 모서리 표시 (오른쪽 아래)
        if (!maxed)
            using (Pen p = new Pen(Color.FromArgb(220, 220, 220), Math.Max(1f, scale)))
            {
                int R = frameRect.Right - 2, Bt = frameRect.Bottom - 2;
                for (int i = 1; i <= 2; i++)
                    g.DrawLine(p, R - GB * i, Bt, R, Bt - GB * i);
            }
    }

    // ---------- 회색 띠로 크기 조절 ----------
    // 반환: 0=없음, -1=위쪽 띠(창 이동), 그 외 1|2|4 조합
    int HitZone(Point pt)
    {
        if (!frameRect.Contains(pt) || Rectangle.Inflate(regionRect, B + GAP, B + GAP).Contains(pt)) return 0;
        int c = GB * 3;   // 모서리 판정 여유
        int z = 0;
        bool nearBottom = pt.Y >= frameRect.Bottom - c;
        if (pt.X < GB || (nearBottom && pt.X < c)) z |= 1;
        if (pt.X >= frameRect.Right - GB || (nearBottom && pt.X >= frameRect.Right - c)) z |= 2;
        if (pt.Y >= frameRect.Bottom - GB || ((z & 3) != 0 && nearBottom)) z |= 4;
        if (z == 0 && pt.Y < frameRect.Y + GB) return -1;
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
            capW = sb.Width - 2 * (B + GAP + GB);
            capH = sb.Height - tbHeight - 2 * (B + GAP + GB);
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
        if (!RegisterHotKey(Handle, HOTKEY_ID, 0x4000, VK_F9))    // MOD_NOREPEAT
            BeginInvoke((MethodInvoker)delegate {
                status.Text = "F9 전역 단축키 등록 실패 (다른 프로그램이 사용 중) - 버튼으로 캡처하세요";
            });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == HOTKEY_ID) DoCapture();   // WM_HOTKEY
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(Handle, HOTKEY_ID);
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
