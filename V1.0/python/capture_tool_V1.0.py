# -*- coding: utf-8 -*-
"""
고정 크기 화면 캡처 도구 (Windows)
 - 프리셋 선택 / 숫자 입력 / 빨간 테두리 드래그로 캡처 크기 지정
 - 항상 위에 떠 있는 테두리 창 (최소화 / 최대화 / 종료 버튼만)
 - 캡처 → 지정 폴더에 PNG 저장 + 클립보드 복사 (Ctrl+V 바로 붙여넣기), 캡처 순간 테두리 노란색
 - 영상 녹화 → 지정 폴더에 MJPEG AVI 저장 + 파일을 클립보드에 복사
 - 단축키: F9 캡처 / F10 녹화 시작·정지 (전역), Ctrl+C 캡처 (창 활성화 시)

필요 패키지:  pip install pillow
exe 만들기:   pyinstaller --noconsole --onefile capture_tool_V1.0.py
"""
import ctypes
import ctypes.wintypes as wt
import datetime
import io
import json
import os
import queue
import struct
import threading
import time
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

# ---- DPI 인식 (Tk 생성 전에 호출해야 실제 픽셀 = 이미지 픽셀) ----
def _set_dpi_aware():
    try:   # Per-Monitor V2 (Win10 1703+)
        if ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):
            return
    except Exception:
        pass
    try:   # Per-Monitor
        if ctypes.windll.shcore.SetProcessDpiAwareness(2) == 0:
            return
    except Exception:
        pass
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass


_set_dpi_aware()

try:
    from PIL import ImageGrab
except ImportError:
    _r = tk.Tk(); _r.withdraw()
    messagebox.showerror("캡처 도구", "Pillow가 필요합니다.\n\npip install pillow")
    raise SystemExit

# ---------------- 설정 ----------------
TRANS = "#ff00fe"          # 투명 처리 색 (캡처 영역)
FR = 7                     # 빨간 테두리 두께 (드래그로 크기 조절)
GAP = 1                    # 빨간 테두리와 캡처 영역 사이 투명 여유
BORDER_COLOR = "#e53935"
FLASH_COLOR = "#ffd600"    # 캡처 순간 노란색
REC_BTN = "#6a1b9a"
REC_FPS = 15                       # 기본값 (옵션에서 변경)
QUALITIES = [(60, "60  낮음 — 용량 작음, 글자 경계가 뭉개짐"),
             (70, "70  보통"),
             (80, "80  기본 (권장) — 대부분 충분"),
             (90, "90  높음 — 작은 글씨 선명, 용량 약 1.5~2배")]
FPS_LIST = [(10, "10 fps — 끊김 보임, 용량 작음"),
            (15, "15 fps — 기본 (프로그램 조작 설명용)"),
            (20, "20 fps — 조금 더 부드러움"),
            (30, "30 fps — 부드러움 (용량·PC 부하 약 2배)")]
RECOMMEND = [("프로그램 조작 설명, 보고서용", 80, 15),
             ("해석 결과 애니메이션, 스크롤 많음", 80, 30),
             ("작은 글씨가 중요한 화면", 90, 15)]
REC_SPLIT_BYTES = 1000 * 1024 * 1024   # 1GB 넘으면 다음 파일로 자동 분할
TB_BG = "#2b2b2b"
TB_FG = "#eeeeee"
FONT = ("맑은 고딕", 9)
FONT_B = ("맑은 고딕", 9, "bold")
NUM_FONT = ("Arial", 9)
MIN_SIZE = 16
HOTKEY_VK = 0x78           # F9  캡처
HOTKEY_REC_VK = 0x79       # F10 녹화
CONFIG_PATH = os.path.join(os.path.expanduser("~"), ".capture_tool.json")

PRESETS = [
    "640 x 480", "800 x 600", "1024 x 768", "1280 x 720",
    "1280 x 1024", "1600 x 900", "1920 x 1080",
    "500 x 500", "800 x 800", "1000 x 1000",
]
CUSTOM = "사용자 지정"


# ---------------- 클립보드 (Pillow 외 추가 의존성 없음) ----------------
def copy_image_to_clipboard(img):
    buf = io.BytesIO()
    img.convert("RGB").save(buf, "BMP")
    data = buf.getvalue()[14:]          # BMP 파일 헤더 제거 → DIB

    k32 = ctypes.windll.kernel32
    u32 = ctypes.windll.user32
    k32.GlobalAlloc.argtypes = [wt.UINT, ctypes.c_size_t]
    k32.GlobalAlloc.restype = wt.HGLOBAL
    k32.GlobalLock.argtypes = [wt.HGLOBAL]
    k32.GlobalLock.restype = ctypes.c_void_p
    k32.GlobalUnlock.argtypes = [wt.HGLOBAL]
    k32.GlobalFree.argtypes = [wt.HGLOBAL]
    u32.OpenClipboard.argtypes = [wt.HWND]
    u32.SetClipboardData.argtypes = [wt.UINT, wt.HANDLE]
    u32.SetClipboardData.restype = wt.HANDLE

    h = k32.GlobalAlloc(0x0002, len(data))          # GMEM_MOVEABLE
    if not h:
        raise OSError("GlobalAlloc 실패")
    p = k32.GlobalLock(h)
    ctypes.memmove(p, data, len(data))
    k32.GlobalUnlock(h)

    for _ in range(20):                              # 다른 앱이 점유 중이면 재시도
        if u32.OpenClipboard(None):
            break
        time.sleep(0.03)
    else:
        k32.GlobalFree(h)
        raise OSError("클립보드를 열 수 없습니다")
    try:
        u32.EmptyClipboard()
        if not u32.SetClipboardData(8, h):           # CF_DIB
            k32.GlobalFree(h)
            raise OSError("SetClipboardData 실패")
    finally:
        u32.CloseClipboard()


def copy_files_to_clipboard(paths):
    """파일 목록을 클립보드에 (CF_HDROP) → 탐색기/메신저/메일에 Ctrl+V"""
    k32 = ctypes.windll.kernel32
    u32 = ctypes.windll.user32
    k32.GlobalAlloc.argtypes = [wt.UINT, ctypes.c_size_t]
    k32.GlobalAlloc.restype = wt.HGLOBAL
    k32.GlobalLock.argtypes = [wt.HGLOBAL]
    k32.GlobalLock.restype = ctypes.c_void_p
    k32.GlobalUnlock.argtypes = [wt.HGLOBAL]
    k32.GlobalFree.argtypes = [wt.HGLOBAL]
    u32.OpenClipboard.argtypes = [wt.HWND]
    u32.SetClipboardData.argtypes = [wt.UINT, wt.HANDLE]
    u32.SetClipboardData.restype = wt.HANDLE

    files = ("\0".join(paths) + "\0\0").encode("utf-16-le")
    data = struct.pack("<IiiII", 20, 0, 0, 0, 1) + files      # DROPFILES (fWide=1)
    h = k32.GlobalAlloc(0x0042, len(data))                     # GMEM_MOVEABLE | ZEROINIT
    p = k32.GlobalLock(h)
    ctypes.memmove(p, data, len(data))
    k32.GlobalUnlock(h)
    for _ in range(20):
        if u32.OpenClipboard(None):
            break
        time.sleep(0.03)
    else:
        k32.GlobalFree(h)
        raise OSError("클립보드를 열 수 없습니다")
    try:
        u32.EmptyClipboard()
        if not u32.SetClipboardData(15, h):                    # CF_HDROP
            k32.GlobalFree(h)
            raise OSError("SetClipboardData 실패")
    finally:
        u32.CloseClipboard()


# ---------------- MJPEG AVI 기록기 (외부 코덱 불필요) ----------------
class AviMjpegWriter:
    def __init__(self, path, width, height):
        self.f = open(path, "wb")
        self.w, self.h = width, height
        self.offsets, self.sizes = [], []
        self.max_frame = 0
        self.closed = False
        f = self.f
        f.write(b"RIFF"); self.riff_size_pos = f.tell(); f.write(b"\0" * 4); f.write(b"AVI ")
        f.write(b"LIST" + struct.pack("<I", 4 + 8 + 56 + 12 + 8 + 56 + 8 + 40) + b"hdrl")
        f.write(b"avih" + struct.pack("<I", 56)); self.avih_pos = f.tell()
        f.write(struct.pack("<10I4I", 0, 0, 0, 0x10, 0, 0, 1, 0, width, height, 0, 0, 0, 0))
        f.write(b"LIST" + struct.pack("<I", 4 + 8 + 56 + 8 + 40) + b"strl")
        f.write(b"strh" + struct.pack("<I", 56)); self.strh_pos = f.tell()
        f.write(b"vidsMJPG" + struct.pack("<IHHIIIIIIiI4h", 0, 0, 0, 0, 1000, 15000, 0, 0, 0, -1, 0,
                                           0, 0, width, height))
        f.write(b"strf" + struct.pack("<I", 40))
        f.write(struct.pack("<IiiHH", 40, width, height, 1, 24) + b"MJPG" +
                struct.pack("<IiiII", width * height * 3, 0, 0, 0, 0))
        f.write(b"LIST"); self.movi_size_pos = f.tell(); f.write(b"\0" * 4)
        self.movi_pos = f.tell(); f.write(b"movi")

    @property
    def length(self):
        return self.f.tell()

    def write_frame(self, jpeg_bytes):
        n = len(jpeg_bytes)
        self.offsets.append(self.f.tell() - self.movi_pos)
        self.sizes.append(n)
        self.max_frame = max(self.max_frame, n)
        self.f.write(b"00dc" + struct.pack("<I", n) + jpeg_bytes)
        if n & 1:
            self.f.write(b"\0")

    def close(self, fps):
        if self.closed:
            return
        self.closed = True
        if not fps or fps <= 0:
            fps = REC_FPS
        f, n = self.f, len(self.sizes)
        movi_end = f.tell()
        f.write(b"idx1" + struct.pack("<I", n * 16))
        for off, sz in zip(self.offsets, self.sizes):
            f.write(b"00dc" + struct.pack("<III", 0x10, off, sz))
        end = f.tell()
        f.seek(self.riff_size_pos); f.write(struct.pack("<I", end - 8))
        f.seek(self.movi_size_pos); f.write(struct.pack("<I", movi_end - self.movi_size_pos - 4))
        f.seek(self.avih_pos); f.write(struct.pack("<II", round(1_000_000 / fps), int(self.max_frame * fps)))
        f.seek(self.avih_pos + 16); f.write(struct.pack("<I", n))
        f.seek(self.avih_pos + 28); f.write(struct.pack("<I", self.max_frame + 8))
        f.seek(self.strh_pos + 20); f.write(struct.pack("<II", 1000, round(fps * 1000)))
        f.seek(self.strh_pos + 32); f.write(struct.pack("<II", n, self.max_frame + 8))
        f.close()


# ---------------- 전역 단축키 스레드 ----------------
class HotkeyThread(threading.Thread):
    def __init__(self, keys, q):
        super().__init__(daemon=True)
        self.keys, self.q = keys, q          # {id: (vk, 메시지, 이름)}

    def run(self):
        u32 = ctypes.windll.user32
        fail = [name for hid, (vk, _, name) in self.keys.items()
                if not u32.RegisterHotKey(None, hid, 0x4000, vk)]   # MOD_NOREPEAT
        if fail:
            self.q.put(("hotkey_fail", " ".join(fail)))
        msg = wt.MSG()
        while u32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == 0x0312 and msg.wParam in self.keys:  # WM_HOTKEY
                self.q.put(self.keys[msg.wParam][1])
        for hid in self.keys:
            u32.UnregisterHotKey(None, hid)


# ---------------- 메인 앱 ----------------
class CaptureApp:
    def __init__(self):
        self.cfg = self.load_cfg()
        self.w = int(self.cfg.get("w", 800))
        self.h = int(self.cfg.get("h", 600))
        self.x = int(self.cfg.get("x", 100))
        self.y = int(self.cfg.get("y", 100))
        self.folder = self.cfg.get(
            "folder", os.path.join(os.path.expanduser("~"), "Pictures", "Captures"))
        q, f = self.cfg.get("quality", 80), self.cfg.get("fps", REC_FPS)
        self.rec_quality = q if q in [v for v, _ in QUALITIES] else 80
        self.rec_fps = f if f in [v for v, _ in FPS_LIST] else REC_FPS
        self.maxed = False
        self.saved_geom = None
        self.busy = False
        self.recording = False
        self.rec_thread = None
        self.rec_origin = (0, 0)

        r = self.root = tk.Tk()
        r.title("캡처 도구 V1.0")
        r.overrideredirect(True)
        r.attributes("-topmost", True)
        r.config(bg=TRANS)
        r.attributes("-transparentcolor", TRANS)

        self.build_toolbar()
        self.frame = tk.Frame(r, bg=BORDER_COLOR)       # 단일 빨간 테두리 (드래그로 크기 조절)
        self.hole = tk.Frame(self.frame, bg=TRANS)      # 투명 여유 포함 구멍
        self.region = tk.Frame(self.hole, bg=TRANS)     # 실제 캡처 영역
        self.rs_zone = 0
        self.frame.bind("<Motion>", self.frame_hover)
        self.frame.bind("<ButtonPress-1>", self.frame_press)
        self.frame.bind("<B1-Motion>", self.frame_drag)
        self.frame.bind("<ButtonRelease-1>", self.frame_release)

        self.sync_inputs()
        self.layout()
        r.after(10, self.enable_taskbar)

        self.q = queue.Queue()
        HotkeyThread({1: (HOTKEY_VK, "capture", "F9"),
                      2: (HOTKEY_REC_VK, "record", "F10")}, self.q).start()
        r.after(50, self.poll_queue)
        r.bind("<F9>", lambda e: self.capture())
        r.bind("<F10>", lambda e: (self.toggle_record(), "break")[1])
        # 창이 활성화된 상태에서 Ctrl+C → 캡처 (한글 입력 상태 대비 대문자도 등록)
        r.bind("<Control-c>", self._ctrl_c)
        r.bind("<Control-C>", self._ctrl_c)
        self.set_status(f"캡처: Ctrl+C / F9   녹화: F10 (화질 {self.rec_quality} · {self.rec_fps}fps)"
                        f"  |  저장 폴더: {self.folder}")

    # ---------- UI ----------
    def build_toolbar(self):
        tb = self.tb = tk.Frame(self.root, bg=TB_BG)
        row1 = tk.Frame(tb, bg=TB_BG)
        row1.pack(fill="x", padx=4, pady=(4, 2))

        self.preset = ttk.Combobox(row1, values=PRESETS + [CUSTOM], width=12,
                                   state="readonly", font=NUM_FONT)
        self.preset.pack(side="left")
        self.preset.bind("<<ComboboxSelected>>", self.on_preset)
        self.preset.bind("<Control-c>", self._ctrl_c)
        self.preset.bind("<Control-C>", self._ctrl_c)

        self.w_var = tk.StringVar()
        self.h_var = tk.StringVar()
        for label, var in (("W", self.w_var), ("H", self.h_var)):
            tk.Label(row1, text=label, bg=TB_BG, fg=TB_FG, font=NUM_FONT
                     ).pack(side="left", padx=(8, 2))
            sp = tk.Spinbox(row1, textvariable=var, from_=MIN_SIZE, to=10000,
                            increment=10, width=6, font=NUM_FONT,
                            command=self.apply_inputs, justify="right")
            sp.pack(side="left")
            sp.bind("<Return>", lambda e: self.apply_inputs())
            sp.bind("<KP_Enter>", lambda e: self.apply_inputs())
            sp.bind("<Button-1>", lambda e: self.root.focus_force())
            # 입력칸의 기본 텍스트 복사보다 캡처를 먼저 처리
            sp.bind("<Control-c>", self._ctrl_c)
            sp.bind("<Control-C>", self._ctrl_c)

        self.mk_btn(row1, "적용", self.apply_inputs).pack(side="left", padx=(6, 0))
        self.mk_btn(row1, "● 캡처 (F9)", self.capture, bg="#c62828",
                    font=FONT_B).pack(side="left", padx=(10, 0))
        self.rec_btn = self.mk_btn(row1, "● 녹화 (F10)", self.toggle_record, bg=REC_BTN, font=FONT_B)
        self.rec_btn.pack(side="left", padx=(4, 0))
        self.mk_btn(row1, "📁 폴더", self.choose_folder).pack(side="left", padx=(4, 0))
        self.mk_btn(row1, "열기", self.open_folder).pack(side="left", padx=(4, 0))
        self.mk_btn(row1, "옵션", self.show_options).pack(side="left", padx=(4, 0))

        # 창 조작 버튼 (최소화 / 최대화 / 종료)
        self.btn_close = self.mk_win_btn(row1, "✕", self.quit, hover="#e81123")
        self.btn_max = self.mk_win_btn(row1, "□", self.toggle_max)
        self.btn_min = self.mk_win_btn(row1, "—", self.minimize)

        self.status = tk.Label(tb, text="", bg=TB_BG, fg="#9e9e9e",
                               font=FONT, anchor="w")
        self.status.pack(fill="x", padx=6, pady=(0, 4))

        for wdg in (tb, row1, self.status):
            wdg.bind("<ButtonPress-1>", self.drag_start)
            wdg.bind("<B1-Motion>", self.drag_move)
            wdg.bind("<ButtonRelease-1>", lambda e: self.save_cfg())

    def mk_btn(self, parent, text, cmd, bg="#424242", font=FONT):
        return tk.Button(parent, text=text, command=cmd, bg=bg, fg="white",
                         activebackground="#616161", activeforeground="white",
                         relief="flat", bd=0, padx=8, pady=2, font=font,
                         cursor="hand2")

    def mk_win_btn(self, parent, text, cmd, hover="#505050"):
        lb = tk.Label(parent, text=text, bg=TB_BG, fg=TB_FG, width=3,
                      font=("Arial", 10), cursor="hand2")
        lb.pack(side="right")
        lb.bind("<Enter>", lambda e: lb.config(bg=hover))
        lb.bind("<Leave>", lambda e: lb.config(bg=TB_BG))
        lb.bind("<ButtonRelease-1>", lambda e: cmd())
        return lb

    def layout(self):
        self.tb.update_idletasks()
        tbw, tbh = self.tb.winfo_reqwidth(), self.tb.winfo_reqheight()
        fw = self.w + 2 * (FR + GAP)
        fh = self.h + 2 * (FR + GAP)
        W = max(tbw, fw)
        self.tb.place(x=0, y=0, width=W, height=tbh)
        self.frame.place(x=0, y=tbh, width=fw, height=fh)
        self.hole.place(x=FR, y=FR, width=self.w + 2 * GAP, height=self.h + 2 * GAP)
        self.region.place(x=GAP, y=GAP, width=self.w, height=self.h)
        self.root.geometry(f"{W}x{tbh + fh}+{self.x}+{self.y}")

    # ---------- 크기 ----------
    def sync_inputs(self):
        self.w_var.set(str(self.w))
        self.h_var.set(str(self.h))
        key = f"{self.w} x {self.h}"
        self.preset.set(key if key in PRESETS else CUSTOM)

    def on_preset(self, _=None):
        v = self.preset.get()
        if v == CUSTOM:
            return
        w, h = (int(s) for s in v.split("x"))
        self.set_size(w, h)

    def apply_inputs(self):
        try:
            w, h = int(float(self.w_var.get())), int(float(self.h_var.get()))
        except ValueError:
            self.sync_inputs()
            self.set_status("숫자를 입력해 주세요")
            return
        self.set_size(w, h)

    def set_size(self, w, h):
        if self.recording:                  # 녹화 중 크기 변경 불가
            self.sync_inputs()
            return
        self.w, self.h = max(MIN_SIZE, w), max(MIN_SIZE, h)
        if self.maxed:                      # 최대화 중 크기 바꾸면 일반 모드로
            self.maxed = False
            self.btn_max.config(text="□")
        self.sync_inputs()
        self.layout()
        self.set_status(f"캡처 크기: {self.w} x {self.h} px")
        self.save_cfg()

    # ---------- 드래그 ----------
    def _ctrl_c(self, _=None):
        # 키 처리가 끝난 뒤 캡처 (Tk 기본 복사 동작과 클립보드 충돌 방지)
        self.root.after(30, self.capture)
        return "break"

    def drag_start(self, e):
        self.root.focus_force()   # 상단 바 클릭 시 창 활성화 → Ctrl+C 사용 가능
        self._dx = e.x_root - self.root.winfo_x()
        self._dy = e.y_root - self.root.winfo_y()

    def drag_move(self, e):
        if self.maxed:
            return
        self.x, self.y = e.x_root - self._dx, e.y_root - self._dy
        self.root.geometry(f"+{self.x}+{self.y}")

    # ---------- 빨간 테두리로 크기 조절 ----------
    def hit_zone(self, ex, ey):
        """1=왼쪽, 2=오른쪽, 4=아래 (조합), -1=위쪽 테두리(창 이동)"""
        fw, fh = self.frame.winfo_width(), self.frame.winfo_height()
        if self.recording:                  # 녹화 중에는 이동만
            return -1 if ey < FR else 0
        c = FR * 4
        near_bottom = ey >= fh - c
        z = 0
        if ex < FR or (near_bottom and ex < c):
            z |= 1
        if ex >= fw - FR or (near_bottom and ex >= fw - c):
            z |= 2
        if ey >= fh - FR or ((z & 3) and near_bottom):
            z |= 4
        if z == 0 and ey < FR:
            return -1
        return z

    CURSORS = {-1: "fleur", 1: "sb_h_double_arrow", 2: "sb_h_double_arrow",
               4: "sb_v_double_arrow", 5: "size_ne_sw", 6: "size_nw_se", 0: ""}

    def frame_hover(self, e):
        if not self.rs_zone:
            z = 0 if self.maxed else self.hit_zone(e.x, e.y)
            self.frame.config(cursor=self.CURSORS.get(z, ""))

    def frame_press(self, e):
        if self.maxed:
            return
        z = self.hit_zone(e.x, e.y)
        if z == -1:
            self.drag_start(e)
            self.rs_zone = -1
            return
        self.rs_zone = z
        self._rs = (e.x_root, e.y_root, self.w, self.h, self.x)

    def frame_drag(self, e):
        if self.rs_zone == -1:
            self.drag_move(e)
            return
        if not self.rs_zone:
            return
        sx, sy, w0, h0, x0 = self._rs
        dx, dy = e.x_root - sx, e.y_root - sy
        if self.rs_zone & 2:
            self.w = max(MIN_SIZE, w0 + dx)
        if self.rs_zone & 1:
            self.w = max(MIN_SIZE, w0 - dx)
            self.x = x0 + (w0 - self.w)
        if self.rs_zone & 4:
            self.h = max(MIN_SIZE, h0 + dy)
        self.sync_inputs()
        self.layout()
        self.set_status(f"캡처 크기: {self.w} x {self.h} px")

    def frame_release(self, e):
        if self.rs_zone:
            self.rs_zone = 0
            self.save_cfg()

    # ---------- 창 조작 ----------
    def hwnd(self):
        return ctypes.windll.user32.GetParent(self.root.winfo_id())

    def enable_taskbar(self):
        """테두리 없는 창도 작업표시줄에 보이게 (최소화 후 복원용)"""
        try:
            GWL_EXSTYLE, WS_EX_APPWINDOW, WS_EX_TOOLWINDOW = -20, 0x40000, 0x80
            u32 = ctypes.windll.user32
            h = self.hwnd()
            st = u32.GetWindowLongW(h, GWL_EXSTYLE)
            u32.SetWindowLongW(h, GWL_EXSTYLE, (st & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW)
            self.root.withdraw()
            self.root.after(10, self.root.deiconify)
        except Exception:
            pass

    def minimize(self):
        try:
            ctypes.windll.user32.ShowWindow(self.hwnd(), 6)   # SW_MINIMIZE
        except Exception:
            self.root.overrideredirect(False)
            self.root.iconify()
            self.root.bind("<Map>", self._restore_borderless)

    def _restore_borderless(self, _=None):
        if self.root.state() == "normal":
            self.root.unbind("<Map>")
            self.root.overrideredirect(True)
            self.root.attributes("-topmost", True)

    def toggle_max(self):
        if self.recording:
            return
        if not self.maxed:
            self.saved_geom = (self.x, self.y, self.w, self.h)
            self.tb.update_idletasks()
            tbh = self.tb.winfo_reqheight()
            sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
            self.maxed = True
            self.x, self.y = 0, 0
            self.w, self.h = sw - 2 * (FR + GAP), sh - tbh - 2 * (FR + GAP)
            self.btn_max.config(text="❐")
        else:
            self.maxed = False
            self.x, self.y, self.w, self.h = self.saved_geom
            self.btn_max.config(text="□")
        self.sync_inputs()
        self.layout()
        self.set_status(f"캡처 크기: {self.w} x {self.h} px")

    def quit(self):
        if self.recording:                  # 녹화 중 종료 → 파일 마무리
            self.recording = False
            if self.rec_thread:
                self.rec_thread.join(5)
        self.save_cfg()
        self.root.destroy()

    # ---------- 폴더 ----------
    def choose_folder(self):
        d = filedialog.askdirectory(initialdir=self.folder if os.path.isdir(self.folder) else None,
                                    title="캡처 저장 폴더 선택", parent=self.root)
        if d:
            self.folder = os.path.normpath(d)
            self.set_status(f"저장 폴더: {self.folder}")
            self.save_cfg()

    def open_folder(self):
        os.makedirs(self.folder, exist_ok=True)
        os.startfile(self.folder)

    # ---------- 캡처 ----------
    def capture(self):
        if self.busy:
            return
        self.busy = True
        try:
            self.root.update_idletasks()
            x, y = self.region.winfo_rootx(), self.region.winfo_rooty()
            img = ImageGrab.grab(bbox=(x, y, x + self.w, y + self.h), all_screens=True)

            os.makedirs(self.folder, exist_ok=True)
            now = datetime.datetime.now()
            name = f"Capture_{now:%Y%m%d_%H%M%S}_{now.microsecond // 1000:03d}_{self.w}x{self.h}.png"
            img.save(os.path.join(self.folder, name))

            clip = "클립보드 복사됨"
            try:
                copy_image_to_clipboard(img)
            except Exception as ex:
                clip = f"클립보드 실패: {ex}"
            self.set_status(f"✔ {name}  ({clip})")
            self.flash()
        except Exception as ex:
            messagebox.showerror("캡처 실패", str(ex), parent=self.root)
        finally:
            self.busy = False

    def flash(self):
        self.frame.config(bg=FLASH_COLOR)
        self.root.after(200, lambda: self.frame.config(bg=BORDER_COLOR))

    # ---------- 옵션 (녹화 화질 · 프레임 수 + 설명) ----------
    def show_options(self):
        if self.recording:
            self.set_status("녹화 중에는 옵션을 바꿀 수 없습니다")
            return
        win = tk.Toplevel(self.root)
        win.title("옵션")
        win.attributes("-topmost", True)
        win.resizable(False, False)
        win.transient(self.root)
        pad = dict(padx=14, sticky="w")
        W = 520

        tk.Label(win, text="녹화 설정", font=FONT_B).grid(row=0, column=0, columnspan=2, pady=(12, 4), **pad)
        tk.Label(win, text="화질 (JPEG 품질)", font=FONT).grid(row=1, column=0, pady=3, **pad)
        cq = ttk.Combobox(win, values=[t for _, t in QUALITIES], state="readonly", width=44, font=FONT)
        cq.current([v for v, _ in QUALITIES].index(self.rec_quality))
        cq.grid(row=1, column=1, padx=(0, 14), pady=3, sticky="w")
        tk.Label(win, text="프레임 수 (초당 장수)", font=FONT).grid(row=2, column=0, pady=3, **pad)
        cf = ttk.Combobox(win, values=[t for _, t in FPS_LIST], state="readonly", width=44, font=FONT)
        cf.current([v for v, _ in FPS_LIST].index(self.rec_fps))
        cf.grid(row=2, column=1, padx=(0, 14), pady=3, sticky="w")

        tk.Label(win, text="설명", font=FONT_B).grid(row=3, column=0, columnspan=2, pady=(12, 2), **pad)
        tk.Label(win, font=FONT, justify="left", wraplength=W, text=(
            "• 화질: 한 장 한 장이 얼마나 선명한지입니다. 높일수록 글자·선 경계가 또렷하고, 낮추면 경계 "
            "주변이 뭉개지거나 얼룩이 생깁니다. 엑셀 숫자·해석 결과 그림·코드처럼 글자가 많은 화면에서 "
            "차이가 잘 보입니다. 80이면 보통 충분하고, 90 이상은 차이가 작은데 용량이 1.5~2배로 늘어납니다.\n"
            "• 프레임 수: 1초에 몇 장을 찍는지입니다. 높일수록 마우스 이동·스크롤·애니메이션이 부드럽고, "
            "낮추면 뚝뚝 끊겨 보입니다. 15fps는 조작 설명용으로 충분하고, 30fps는 용량과 PC 부하가 약 2배입니다."
        )).grid(row=4, column=0, columnspan=2, **pad)

        tk.Label(win, text="용도별 추천 (행을 클릭하면 적용)", font=FONT_B).grid(
            row=5, column=0, columnspan=2, pady=(12, 2), **pad)
        tv = ttk.Treeview(win, columns=("use", "q", "fps"), show="headings", height=len(RECOMMEND),
                          selectmode="browse")
        for col, title, width, anchor in (("use", "용도", 330, "w"), ("q", "화질", 80, "center"),
                                          ("fps", "fps", 80, "center")):
            tv.heading(col, text=title)
            tv.column(col, width=width, anchor=anchor)
        for use, q, f in RECOMMEND:
            tv.insert("", "end", values=(use, q, f))
        tv.grid(row=6, column=0, columnspan=2, padx=14, sticky="we")

        def on_pick(_=None):
            sel = tv.selection()
            if not sel:
                return
            _, q, f = RECOMMEND[tv.index(sel[0])]
            cq.current([v for v, _ in QUALITIES].index(q))
            cf.current([v for v, _ in FPS_LIST].index(f))
        tv.bind("<<TreeviewSelect>>", on_pick)

        tk.Label(win, font=FONT, fg="#555555", justify="left", wraplength=W,
                 text="※ 영상 크기는 빨간 테두리 안의 캡처 크기(W×H)와 같습니다. 설정은 다음 녹화부터 적용됩니다."
                 ).grid(row=7, column=0, columnspan=2, pady=(6, 0), **pad)

        def ok():
            self.rec_quality = QUALITIES[cq.current()][0]
            self.rec_fps = FPS_LIST[cf.current()][0]
            self.save_cfg()
            self.set_status(f"녹화 설정: 화질 {self.rec_quality} · {self.rec_fps}fps")
            win.destroy()

        bf = tk.Frame(win)
        bf.grid(row=8, column=0, columnspan=2, sticky="e", padx=14, pady=12)
        tk.Button(bf, text="확인", width=9, command=ok).pack(side="left", padx=(0, 6))
        tk.Button(bf, text="취소", width=9, command=win.destroy).pack(side="left")
        win.bind("<Return>", lambda e: ok())
        win.bind("<Escape>", lambda e: win.destroy())
        win.update_idletasks()
        x = self.root.winfo_x() + 40
        y = self.root.winfo_y() + 40
        win.geometry(f"+{x}+{y}")
        win.grab_set()
        win.focus_force()

    # ---------- 영상 녹화 ----------
    def toggle_record(self):
        if self.recording:
            self.recording = False          # 스레드가 파일 마무리 후 "rec_done" 전달
        else:
            self.start_record()

    def start_record(self):
        if self.rec_thread and self.rec_thread.is_alive():
            self.set_status("이전 녹화 파일 저장 중... 잠시 후 다시 시도하세요")
            return
        try:
            os.makedirs(self.folder, exist_ok=True)
        except Exception as ex:
            self.set_status(f"폴더 생성 실패: {ex}")
            return
        self.root.update_idletasks()
        self.rec_origin = (self.region.winfo_rootx(), self.region.winfo_rooty())
        self.rec_size = (self.w, self.h)
        self.cur_fps, self.cur_quality = self.rec_fps, self.rec_quality   # 시작 시 값 고정
        self.rec_start = time.time()
        self.recording = True
        self.preset.config(state="disabled")
        base = f"Record_{datetime.datetime.now():%Y%m%d_%H%M%S}_{self.w}x{self.h}"
        self.rec_thread = threading.Thread(target=self.record_loop,
                                           args=(self.folder, base), daemon=True)
        self.rec_thread.start()
        self.update_rec_ui()

    def update_rec_ui(self):
        if not self.recording:
            return
        t = int(time.time() - self.rec_start)
        self.rec_btn.config(text=f"■ 정지 {t // 60:02d}:{t % 60:02d}", bg="#c62828")
        w, h = self.rec_size
        self.set_status(f"● 녹화 중 {w} x {h}  ({self.cur_fps} fps) — F10 또는 정지 버튼으로 종료")
        self.root.after(500, self.update_rec_ui)

    def record_loop(self, folder, base):
        w, h = self.rec_size
        files, err, total = [], None, 0
        part, part_frames = 1, 0
        t0 = part_t0 = time.perf_counter()
        avi = None
        try:
            path = os.path.join(folder, base + ".avi")
            files.append(path)
            avi = AviMjpegWriter(path, w, h)
            interval = 1.0 / self.cur_fps
            n = 0
            last = None
            while self.recording:
                x, y = self.rec_origin
                try:
                    img = ImageGrab.grab(bbox=(x, y, x + w, y + h), all_screens=True).convert("RGB")
                    last = img
                except OSError:             # 화면 잠금/UAC 등 일시적 실패 → 직전 프레임 유지
                    img = last
                    if img is None:
                        raise
                buf = io.BytesIO()
                img.save(buf, "JPEG", quality=self.cur_quality)
                avi.write_frame(buf.getvalue())
                total += 1
                part_frames += 1
                if avi.length > REC_SPLIT_BYTES:          # 용량 초과 → 다음 파일
                    avi.close(part_frames / max(0.001, time.perf_counter() - part_t0))
                    part += 1
                    path = os.path.join(folder, f"{base}_part{part}.avi")
                    files.append(path)
                    avi = AviMjpegWriter(path, w, h)
                    part_frames, part_t0 = 0, time.perf_counter()
                n += 1
                wait = t0 + n * interval - time.perf_counter()
                if wait > 0:
                    time.sleep(wait)
        except Exception as ex:
            err = str(ex)
            self.recording = False
        finally:
            try:
                if avi:
                    avi.close(part_frames / max(0.001, time.perf_counter() - part_t0))
            except Exception as ex2:
                err = err or str(ex2)
        self.q.put(("rec_done", files, total, time.perf_counter() - t0, err))

    def on_record_done(self, files, frames, secs, err):
        self.preset.config(state="readonly")
        self.rec_btn.config(text="● 녹화 (F10)", bg=REC_BTN)
        if err:
            self.set_status(f"녹화 오류: {err}")
            return
        clip = "파일 클립보드 복사됨"
        try:
            copy_files_to_clipboard(files)
        except Exception as ex:
            clip = f"클립보드 실패: {ex}"
        more = f" 외 {len(files) - 1}개" if len(files) > 1 else ""
        self.set_status(f"녹화 저장: {os.path.basename(files[0])}{more}  "
                        f"({secs:.1f}초, {frames}프레임, {clip})")

    def poll_queue(self):
        try:
            while True:
                m = self.q.get_nowait()
                if m == "capture":
                    self.capture()
                elif m == "record":
                    self.toggle_record()
                elif m[0] == "hotkey_fail":
                    self.set_status(f"전역 단축키 등록 실패: {m[1]} (다른 프로그램이 사용 중) — 버튼을 사용하세요")
                elif m[0] == "rec_done":
                    self.on_record_done(*m[1:])
        except queue.Empty:
            pass
        if self.recording:                  # 창 이동 시 녹화 위치 갱신 (메인 스레드에서 좌표 읽기)
            self.rec_origin = (self.region.winfo_rootx(), self.region.winfo_rooty())
        self.root.after(50, self.poll_queue)

    # ---------- 기타 ----------
    def set_status(self, text):
        self.status.config(text=text)

    def load_cfg(self):
        try:
            with open(CONFIG_PATH, encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return {}

    def save_cfg(self):
        if self.maxed and self.saved_geom:
            x, y, w, h = self.saved_geom
        else:
            x, y, w, h = self.x, self.y, self.w, self.h
        try:
            with open(CONFIG_PATH, "w", encoding="utf-8") as f:
                json.dump({"w": w, "h": h, "x": x, "y": y, "folder": self.folder,
                           "quality": self.rec_quality, "fps": self.rec_fps},
                          f, ensure_ascii=False, indent=2)
        except Exception:
            pass

    def run(self):
        self.root.mainloop()


if __name__ == "__main__":
    CaptureApp().run()
