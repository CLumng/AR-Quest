"""Haptic Bridge window: Start / Stop, what the headset link and the stylus are doing, the force limit, and the headset's performance.

The bridge itself (the Touch through hd.dll, the TCP link to the headset, the force, the temperatures read through adb and the performance
log) is Bridge.cs; this window runs it as a child process (HapticBridge.exe --ui) and only shows what it says. Double-click this file, or:
python HapticBridge.pyw [--sim] [--autostart]
Options: --sim  --autostart  --port N  --max-force N  --no-force  --no-adb; any other (--adb PATH, --tau MS, --runs-dir PATH, --perf-dir PATH,
--sensors-file PATH) goes to the bridge as it is.
--selftest starts the simulated stylus, checks the window against it and exits (0 = fine).
"""
import argparse
import ctypes
import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import tkinter as tk
import traceback
from tkinter import messagebox, ttk

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(HERE, "bin", "Release", "net10.0-windows", "HapticBridge.exe")
SOURCES = [os.path.join(HERE, "Bridge.cs"), os.path.join(HERE, "HapticBridge.csproj"),
           os.path.join(HERE, "..", "Assets", "Scripts", "HapticLink", "HapticProtocol.cs")]
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
GREEN, AMBER, GRAY, RED = "#2ea043", "#bf8700", "#6e7681", "#d1242f"
MAX_LIMIT = 3.0
THERMAL = ("none", "light", "moderate", "severe", "critical", "emergency", "shutdown")  # Android's thermal status, 0..6
SENSORS_FIXTURE = os.path.join(HERE, "tests", "quest3-sensors.txt")                   # a real reading of a Quest 3, for --selftest


def num(v, spec=".1f"):
    return "-" if v is None else format(v, spec)


def heat(value, limit):
    """Colour of a temperature against where the OS starts to throttle: green below 80 % of it, amber below 92 %, red from there."""
    if value is None or not limit:
        return ""
    return GREEN if value < 0.8 * limit else AMBER if value < 0.92 * limit else RED


def build():
    """dotnet build when the exe is missing or older than its sources (an old exe is easy to mistake for the new one). None = fine, else the error."""
    if os.path.exists(EXE) and os.path.getmtime(EXE) >= max(os.path.getmtime(s) for s in SOURCES):
        return None
    p = subprocess.run(["dotnet", "build", HERE, "-c", "Release", "-nologo", "-v", "q"], capture_output=True, text=True, creationflags=NO_WINDOW)
    return None if p.returncode == 0 else (p.stdout + p.stderr).strip()


class App:
    def __init__(self, root, args, extra):
        self.root, self.extra = root, extra
        self.proc = self.reader = self.last = self.error = None
        self.q = queue.Queue()
        self.stopping = False
        root.title("Haptic Bridge")
        root.report_callback_exception = lambda *e: self.log("".join(traceback.format_exception(*e)))
        k = root.winfo_fpixels("1i") / 96  # the screen's scale, so the window is not tiny on a high-DPI display
        root.geometry(f"{int(520 * k)}x{int(900 * k)}")
        root.minsize(int(520 * k), int(780 * k))
        ttk.Style().configure("Big.TButton", font=("Segoe UI Semibold", 11), padding=(8, 10))

        self.sim = tk.BooleanVar(value=args.sim)
        self.usb = tk.BooleanVar(value=not args.no_adb)
        self.port = tk.StringVar(value=str(args.port))
        self.force = tk.BooleanVar(value=not args.no_force)
        self.limit = tk.StringVar(value=f"{args.max_force:.1f}")
        self.text = {n: tk.StringVar(value="-") for n in ("link", "traffic", "tip", "buttons", "applied", "app", "cpu", "gpu", "battery", "board", "clocks", "log")}

        main = ttk.Frame(root, padding=12)
        main.pack(fill="both", expand=True)
        head = ttk.Frame(main)
        head.pack(fill="x", pady=(0, 8))
        words = ttk.Frame(head)
        words.pack(side="left", fill="x", expand=True)
        self.status = ttk.Label(words, font=("Segoe UI Semibold", 14))
        self.status.pack(anchor="w")
        self.hint = ttk.Label(words, foreground=GRAY, wraplength=int(290 * k))
        self.hint.pack(anchor="w")
        self.button = ttk.Button(head, text="Start", width=9, style="Big.TButton", command=self.toggle)
        self.button.pack(side="right")

        mono = {"font": ("Consolas", 10)}
        box = self.box(main, "Headset")
        self.add(box, "Link", ttk.Label, textvariable=self.text["link"], **mono)
        self.add(box, "Traffic", ttk.Label, textvariable=self.text["traffic"], **mono)
        box = self.box(main, "Performance")
        self.add(box, "App", ttk.Label, textvariable=self.text["app"], **mono)
        self.heat = {n: self.add(box, title, ttk.Label, textvariable=self.text[n], **mono)  # coloured by how close they are to throttling
                     for n, title in (("cpu", "CPU °C"), ("gpu", "GPU °C"), ("battery", "Battery °C"))}
        self.add(box, "Board °C", ttk.Label, textvariable=self.text["board"], **mono)
        self.add(box, "Clocks", ttk.Label, textvariable=self.text["clocks"], **mono)
        self.add(box, "Log", ttk.Label, textvariable=self.text["log"], **mono)
        box = self.box(main, "Stylus")
        self.add(box, "Tip (mm)", ttk.Label, textvariable=self.text["tip"], **mono)
        self.add(box, "Buttons", ttk.Label, textvariable=self.text["buttons"], **mono)
        box = self.box(main, "Force")
        self.add(box, "Applied", ttk.Label, textvariable=self.text["applied"], **mono)
        self.bar = self.add(box, None, ttk.Progressbar, maximum=100)
        self.add(box, None, ttk.Checkbutton, text="Send force to the stylus", variable=self.force)
        self.add(box, "Limit (N)", ttk.Spinbox, from_=0.1, to=MAX_LIMIT, increment=0.1, format="%.1f", width=6, textvariable=self.limit)
        box = self.box(main, "Start-up options")
        self.startup = [self.add(box, None, ttk.Checkbutton, text="Simulated stylus (no hardware)", variable=self.sim),
                        self.add(box, None, ttk.Checkbutton, text="USB headset (runs adb reverse)", variable=self.usb),
                        self.add(box, "Port", ttk.Spinbox, from_=1024, to=65535, width=7, textvariable=self.port)]

        logs = ttk.Frame(main)
        logs.pack(fill="both", expand=True)
        self.log_box = tk.Text(logs, height=6, state="disabled", font=("Consolas", 9), bg="white", relief="solid", bd=1, wrap="word")
        scroll = ttk.Scrollbar(logs, command=self.log_box.yview)
        self.log_box.configure(yscrollcommand=scroll.set)
        scroll.pack(side="right", fill="y")
        self.log_box.pack(side="left", fill="both", expand=True)

        self.limit.trace_add("write", self.push_limit)
        self.force.trace_add("write", lambda *_: self.send("force on" if self.force.get() else "force off"))
        root.protocol("WM_DELETE_WINDOW", self.close)
        self.render()
        self.pump()
        if args.autostart:
            root.after(100, self.toggle)

    @staticmethod
    def box(parent, title):
        box = ttk.LabelFrame(parent, text=title, padding=(8, 2, 8, 6))
        box.pack(fill="x", pady=(0, 8))
        box.columnconfigure(1, weight=1)
        return box

    @staticmethod
    def add(box, name, kind, **options):
        """A row in a box: "name  widget", or the widget across the whole width when there is no name."""
        widget, row = kind(box, **options), box.grid_size()[1]
        if name is None:
            widget.grid(row=row, column=0, columnspan=2, sticky="ew", pady=3)
        else:
            ttk.Label(box, text=name, foreground=GRAY).grid(row=row, column=0, sticky="w", padx=(0, 16), pady=3)
            widget.grid(row=row, column=1, sticky="w", pady=3)
        return widget

    # ---- the bridge process ----

    def toggle(self):
        if self.proc:
            self.stopping = True
            self.send("quit")
            self.render()
            proc = self.proc
            self.root.after(4000, lambda: proc.poll() is None and proc.kill())  # it normally leaves by itself in well under a second
            return
        try:
            port = int(self.port.get())
        except ValueError:
            return messagebox.showwarning("Haptic Bridge", "The port must be a number.")
        self.set_status("Building the bridge", AMBER, "Only the first time, or after the source changed.")
        self.root.config(cursor="watch")
        self.root.update()
        try:
            failed = build()
        except OSError as e:
            failed = f"dotnet could not run: {e}"
        self.root.config(cursor="")
        if failed:
            self.render()
            return messagebox.showerror("Haptic Bridge", f"The bridge did not build:\n\n{failed[-1500:]}")
        cmd = [EXE, "--ui", "--max-force", f"{self.limit_value():.2f}", "--port", str(port)] + self.extra
        cmd += (["--sim"] if self.sim.get() else []) + ([] if self.usb.get() else ["--no-adb"]) + ([] if self.force.get() else ["--no-force"])
        self.last, self.error, self.stopping = None, None, False
        try:
            self.proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                         text=True, encoding="utf-8", errors="replace", creationflags=NO_WINDOW)
        except OSError as e:
            self.render()
            return messagebox.showerror("Haptic Bridge", f"The bridge did not start:\n\n{e}")
        self.reader = threading.Thread(target=lambda p=self.proc: [self.q.put(line) for line in p.stdout], daemon=True)
        self.reader.start()
        self.render()

    def send(self, line):
        if self.proc and self.proc.poll() is None:
            try:
                self.proc.stdin.write(line + "\n")
                self.proc.stdin.flush()
            except OSError:
                pass  # it is leaving: pump() sees the exit

    def limit_value(self):
        try:
            return min(max(float(self.limit.get()), 0.1), MAX_LIMIT)
        except ValueError:
            return 1.0

    def push_limit(self, *_):
        try:
            float(self.limit.get())  # half-typed text ("", "-") is not sent
        except ValueError:
            return
        self.send(f"limit {self.limit_value():.2f}")

    def pump(self):
        if self.proc:
            self.drain()
            code = self.proc.poll()
            if code is not None:
                self.reader.join(0.3)  # the last lines; not for ever: an adb server the bridge started may keep the pipe open
                self.drain()
                self.exited(code)
        self.root.after(50, self.pump)

    def drain(self):
        try:
            while True:
                self.message(self.q.get_nowait())
        except queue.Empty:
            pass

    def message(self, line):
        try:
            m = json.loads(line)
        except ValueError:
            m = None
        if not isinstance(m, dict):
            return self.log(line.strip())  # not the bridge's JSON: a crash report, say
        if "log" in m:
            self.log(m["log"])
        elif "error" in m:
            self.error = m["error"]
        elif "status" in m:
            self.last = m["status"]
            self.render()

    def exited(self, code):
        self.proc = self.last = None
        self.render()
        if self.error:
            messagebox.showwarning("Haptic Bridge", self.error)
        elif code and not self.stopping:
            self.log(f"The bridge stopped by itself (exit code {code}).")

    def close(self):
        if self.proc:
            self.send("quit")
            try:
                self.proc.wait(4)
            except subprocess.TimeoutExpired:
                self.proc.kill()
        self.root.destroy()

    # ---- what is shown ----

    def log(self, line):
        box = self.log_box
        box.configure(state="normal")
        box.insert("end", f"{time.strftime('%H:%M:%S')}  {line}\n")
        if int(box.index("end-1c").split(".")[0]) > 300:
            box.delete("1.0", "101.0")
        box.configure(state="disabled")
        box.see("end")

    def set_status(self, status, color, hint):
        self.status.configure(text="● " + status, foreground=color)
        self.hint.configure(text=hint)

    def render(self):
        s, running = self.last, self.proc is not None
        self.button.configure(text="Stop" if running else "Start", state="disabled" if running and self.stopping else "normal")
        for w in self.startup:
            w.state(["disabled" if running else "!disabled"])
        if not running:
            self.set_status("Stopped", GRAY, "Press Start, then open the app on the headset.")
        elif not s:
            self.set_status("Starting", GRAY, "")
        elif not s["connected"]:
            self.set_status("Waiting for the headset", AMBER, s["adbState"] or "Open the app on the headset.")
        else:
            self.set_status("Headset connected", GREEN, "The headset is asking for force." if s["asked"] else "The headset is not asking for force.")
        if not (running and s):
            for v in self.text.values():
                v.set("-")
            for w in self.heat.values():
                w.configure(foreground="")
            self.text["applied"].set("0.00 N")
            self.bar["value"] = 0
            return
        t = self.text
        t["link"].set((s["remote"] or "") if s["connected"] else "not connected")
        t["traffic"].set(f"{s['outPerSecond']:.0f}/s out, {s['inPerSecond']:.0f}/s in" + (f", last {s['commandAgeMs']:.0f} ms ago" if s["commandAgeMs"] >= 0 else "")
                         if s["connected"] else "-")
        t["tip"].set(f"{s['m'][12]:7.1f} {s['m'][13]:7.1f} {s['m'][14]:7.1f}" + ("  (simulated)" if s["simulated"] else "") + (f"  HD errors: {s['hdErrors']}" if s["hdErrors"] else ""))
        b = s["buttons"]
        t["buttons"].set(f"1 {'ON ' if b & 1 else 'off'}  2 {'ON ' if b & 2 else 'off'}" + ("  in the inkwell" if s["inkwell"] else ""))
        t["applied"].set(f"{s['force']:.2f} N  ({s['fx']:.2f} {s['fy']:.2f} {s['fz']:.2f})")
        self.bar["value"] = min(max(s["force"] / max(self.limit_value(), 0.01) * 100, 0), 100)
        self.render_performance(s)

    def render_performance(self, s):
        """The app's last second (sent by the headset) and the headset's sensors (read by the bridge through adb)."""
        t, p, z = self.text, s.get("perf"), s.get("sensors")
        t["app"].set(f"{p['fps']:.1f} fps  {p['frameMs']:.1f} ms  max {p['maxMs']:.1f}  late {p['late']}" + (f"  {p['refreshHz']:.0f} Hz" if p["refreshHz"] else "")
                     if p else "no data from the app")
        if not z:
            for n in ("cpu", "gpu", "battery", "board", "clocks"):
                t[n].set("no reading (needs adb)" if n == "cpu" else "-")
            for w in self.heat.values():
                w.configure(foreground="")
        else:
            limit = lambda v: f"  throttles at {v:.0f}" if v else ""
            t["cpu"].set(f"{num(z['cpuMax'])} max  {num(z['cpuAvg'])} avg" + limit(z["cpuLimit"]))
            t["gpu"].set(f"{num(z['gpuMax'])} max  {num(z['gpuAvg'])} avg" + limit(z["gpuLimit"]))
            t["battery"].set(f"{num(z['battery'])} cell  {num(z['battVirt'])} virtual" + limit(z["battVirtLimit"])
                             + (f"  {z['batteryPct']}%" if z["batteryPct"] is not None else "") + ("  charging" if z["charging"] else ""))
            t["board"].set(f"SoC {num(z['soc'])}  PCB {num(z['pcb'])}  surface {num(z['surface'])}  display {num(z['display'])}")
            status = z["status"]
            t["clocks"].set(f"CPU {num(z['cpuMHz'], '.0f')} MHz  GPU {num(z['gpuMHz'], '.0f')} MHz {num(z['gpuBusy'], '.0f')}%  fan {num(z['fan'], '.0f')}"
                            f"  thermal {THERMAL[status] if status is not None and 0 <= status < len(THERMAL) else '-'}")
            self.heat["cpu"].configure(foreground=heat(z["cpuMax"], z["cpuLimit"]))
            self.heat["gpu"].configure(foreground=heat(z["gpuMax"], z["gpuLimit"]))
            self.heat["battery"].configure(foreground=heat(z["battVirt"], z["battVirtLimit"]))
        t["log"].set(f"{os.path.basename(s['perfFile'])}  ({s['perfRows']} rows)" if s.get("perfFile") else "-")


def selftest(root, app):
    """Drives the window against the simulated stylus: start, the tip moves, limit and switch reach the bridge, stop. Returns the exit code."""
    def wait(cond, what, seconds=10):
        end = time.time() + seconds
        while not cond():
            assert time.time() < end, f"timed out waiting for: {what}"
            root.update()
            time.sleep(0.02)
    perf_dir = tempfile.mkdtemp(prefix="HapticBridgeSelfTest-")
    try:
        app.sim.set(True)
        app.usb.set(False)
        app.extra = ["--sensors-file", SENSORS_FIXTURE, "--perf-dir", perf_dir]  # a real Quest 3 reading instead of adb
        app.toggle()
        wait(lambda: app.last, "the first status")
        assert app.status.cget("text") == "● Waiting for the headset", app.status.cget("text")
        wait(lambda: app.last["sensors"] and app.last["perfRows"] >= 2, "the sensors and two rows of the log")
        assert app.text["cpu"].get() == "40.6 max  40.3 avg  throttles at 89", app.text["cpu"].get()
        assert app.text["battery"].get() == "37.0 cell  39.3 virtual  throttles at 58  99%  charging", app.text["battery"].get()
        assert "thermal none" in app.text["clocks"].get() and "CPU 1920 MHz" in app.text["clocks"].get(), app.text["clocks"].get()
        assert str(app.heat["cpu"].cget("foreground")) == GREEN and app.text["app"].get() == "no data from the app"
        lines = open(app.last["perfFile"], encoding="utf-8").read().splitlines()
        assert lines[0].count(";") == lines[1].count(";") == 24 and lines[1].split(";")[2] == "0", lines[:2]  # 25 columns, no app numbers yet
        tip = app.last["m"][12]
        wait(lambda: abs(app.last["m"][12] - tip) > 1, "the simulated tip to move")
        app.limit.set("2.5")
        app.force.set(False)
        wait(lambda: app.last["maxForce"] == 2.5 and not app.last["forceOutput"], "the limit and the switch to reach the bridge")
        assert "(simulated)" in app.text["tip"].get() and app.button.cget("text") == "Stop"
        proc = app.proc
        app.toggle()
        wait(lambda: app.proc is None, "the bridge to stop")
        assert proc.returncode == 0 and app.status.cget("text") == "● Stopped" and app.button.cget("text") == "Start", proc.returncode
    except Exception:
        traceback.print_exc()
        if app.proc:
            app.proc.kill()
        return 1
    finally:
        shutil.rmtree(perf_dir, ignore_errors=True)
    print("OK")
    return 0


def main():
    ap = argparse.ArgumentParser(description="Haptic Bridge window", epilog="Any other option goes to the bridge as it is (--adb PATH, --tau MS, --runs-dir PATH).")
    ap.add_argument("--sim", action="store_true")
    ap.add_argument("--autostart", action="store_true")
    ap.add_argument("--no-adb", action="store_true")
    ap.add_argument("--no-force", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--port", type=int, default=5555)  # HapticProtocol.Port
    ap.add_argument("--max-force", type=float, default=1.0)
    args, extra = ap.parse_known_args()
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)  # sharp text on a high-DPI display
    except (AttributeError, OSError):
        pass
    root = tk.Tk()
    app = App(root, args, extra)
    if args.selftest:
        code = selftest(root, app)
        root.destroy()
        sys.exit(code)
    root.mainloop()


if __name__ == "__main__":
    main()
