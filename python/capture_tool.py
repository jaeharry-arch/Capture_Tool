# -*- coding: utf-8 -*-
"""
고정 크기 화면 캡처 도구 (Windows)
 - 프리셋 선택 / 숫자 입력 / 모서리 드래그로 캡처 크기 지정
 - 항상 위에 떠 있는 테두리 창 (최소화 / 최대화 / 종료 버튼만)
 - 캡처 → 지정 폴더에 PNG 저장 + 클립보드 복사 (Ctrl+V 바로 붙여넣기)
 - 전역 단축키 F9 로 캡처 (다른 창에서 작업 중이어도 동작)

필요 패키지:  pip install pillow
exe 만들기:   pyinstaller --noconsole --onefile capture_tool.py
"""
import ctypes
import ctypes.wintypes as wt
import datetime
import io
import json
import os
import queue
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
BORDER = 3                 # 테두리 두께 (캡처 영역 바깥)
GAP = 1                    # 빨간 테두리와 캡처 영역 사이 투명 여유
GB = 8                     # 바깥 회색 띠 두께 (드래그로 크기 조절)
FRAME_COLOR = "#808080"
BORDER_COLOR = "#e53935"
FLASH_COLOR = "#43a047"
TB_BG = "#2b2b2b"
TB_FG = "#eeeeee"
FONT = ("맑은 고딕", 9)
FONT_B = ("맑은 고딕", 9, "bold")
NUM_FONT = ("Arial", 9)
MIN_SIZE = 16
HOTKEY_VK = 0x78           # F9
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


# ---------------- 전역 단축키 스레드 ----------------
class HotkeyThread(threading.Thread):
    def __init__(self, vk, q):
        super().__init__(daemon=True)
        self.vk, self.q = vk, q

    def run(self):
        u32 = ctypes.windll.user32
        if not u32.RegisterHotKey(None, 1, 0x4000, self.vk):   # MOD_NOREPEAT
            self.q.put("hotkey_fail")
            return
        msg = wt.MSG()
        while u32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == 0x0312:                            # WM_HOTKEY
                self.q.put("capture")
        u32.UnregisterHotKey(None, 1)


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
        self.maxed = False
        self.saved_geom = None
        self.busy = False

        r = self.root = tk.Tk()
        r.title("캡처 도구")
        r.overrideredirect(True)
        r.attributes("-topmost", True)
        r.config(bg=TRANS)
        r.attributes("-transparentcolor", TRANS)

        self.build_toolbar()
        self.frame = tk.Frame(r, bg=FRAME_COLOR)
        self.border = tk.Frame(self.frame, bg=BORDER_COLOR)
        self.hole = tk.Frame(self.border, bg=TRANS)     # 투명 여유 포함 구멍
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
        HotkeyThread(HOTKEY_VK, self.q).start()
        r.after(50, self.poll_queue)
        r.bind("<F9>", lambda e: self.capture())
        # 창이 활성화된 상태에서 Ctrl+C → 캡처 (한글 입력 상태 대비 대문자도 등록)
        r.bind("<Control-c>", self._ctrl_c)
        r.bind("<Control-C>", self._ctrl_c)
        self.set_status(f"Ctrl+C 또는 F9로 캡처  |  저장 폴더: {self.folder}")

    # ---------- UI ----------
    def build_toolbar(self):
        tb = self.tb = tk.Frame(self.root, bg=TB_BG)
        row1 = tk.Frame(tb, bg=TB_BG)
        row1.pack(fill="x", padx=4, pady=(4, 2))

        self.preset = ttk.Combobox(row1, values=PRESETS + [CUSTOM], width=12,
                                   state="readonly", font=NUM_FONT)
        self.preset.pack(side="left")
        self.preset.bind("<<ComboboxSelected>>", self.on_preset)

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

        self.mk_btn(row1, "적용", self.apply_inputs).pack(side="left", padx=(6, 0))
        self.mk_btn(row1, "● 캡처 (F9)", self.capture, bg="#c62828",
                    font=FONT_B).pack(side="left", padx=(10, 0))
        self.mk_btn(row1, "📁 폴더", self.choose_folder).pack(side="left", padx=(4, 0))
        self.mk_btn(row1, "열기", self.open_folder).pack(side="left", padx=(4, 0))

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
        fw = self.w + 2 * (BORDER + GAP + GB)
        fh = self.h + 2 * (BORDER + GAP + GB)
        W = max(tbw, fw)
        self.tb.place(x=0, y=0, width=W, height=tbh)
        self.frame.place(x=0, y=tbh, width=fw, height=fh)
        self.border.place(x=GB, y=GB, width=self.w + 2 * (BORDER + GAP),
                          height=self.h + 2 * (BORDER + GAP))
        self.hole.place(x=BORDER, y=BORDER, width=self.w + 2 * GAP, height=self.h + 2 * GAP)
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
        self.capture()
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

    # ---------- 회색 띠로 크기 조절 ----------
    def hit_zone(self, ex, ey):
        """1=왼쪽, 2=오른쪽, 4=아래 (조합), -1=위쪽 띠(창 이동)"""
        fw, fh = self.frame.winfo_width(), self.frame.winfo_height()
        c = GB * 3
        near_bottom = ey >= fh - c
        z = 0
        if ex < GB or (near_bottom and ex < c):
            z |= 1
        if ex >= fw - GB or (near_bottom and ex >= fw - c):
            z |= 2
        if ey >= fh - GB or ((z & 3) and near_bottom):
            z |= 4
        if z == 0 and ey < GB:
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
        if not self.maxed:
            self.saved_geom = (self.x, self.y, self.w, self.h)
            self.tb.update_idletasks()
            tbh = self.tb.winfo_reqheight()
            sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
            self.maxed = True
            self.x, self.y = 0, 0
            self.w, self.h = sw - 2 * (BORDER + GAP + GB), sh - tbh - 2 * (BORDER + GAP + GB)
            self.btn_max.config(text="❐")
        else:
            self.maxed = False
            self.x, self.y, self.w, self.h = self.saved_geom
            self.btn_max.config(text="□")
        self.sync_inputs()
        self.layout()
        self.set_status(f"캡처 크기: {self.w} x {self.h} px")

    def quit(self):
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
        self.border.config(bg=FLASH_COLOR)
        self.root.after(200, lambda: self.border.config(bg=BORDER_COLOR))

    def poll_queue(self):
        try:
            while True:
                m = self.q.get_nowait()
                if m == "capture":
                    self.capture()
                elif m == "hotkey_fail":
                    self.set_status("F9 전역 단축키 등록 실패 (다른 프로그램이 사용 중) — 버튼으로 캡처하세요")
        except queue.Empty:
            pass
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
                json.dump({"w": w, "h": h, "x": x, "y": y, "folder": self.folder},
                          f, ensure_ascii=False, indent=2)
        except Exception:
            pass

    def run(self):
        self.root.mainloop()


if __name__ == "__main__":
    CaptureApp().run()
