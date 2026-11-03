"""Characterisation of the integrated system (headset + PC bridge + Touch stylus) from the runs the force graph saves as CSV
(Assets/Scripts/HapticLink/ForceGraph.cs: one file per puncture, Documents\\AR Quest\\Force runs).

  per calibration : fiducial registration error (FRE, measured) and the target registration error (TRE) estimated from it
  per puncture    : tip trajectory, depth reached, error against the ideal path and the target, execution time, link latency
Every quantity is reported as mean, spread (s) and expanded uncertainty of the mean U (GUM: type A from the spread over runs, type B from the
instrument's resolution, k from Student's t for 95 %). Output: one Excel workbook (its "Gráficas" sheet has the pictures) and the same pictures as
PNG in a "graficas" folder next to it: summary mean ± U, tip trajectory against the ideal path, procedure per run, latency, registration.

    python Characterization.py [csv folder or files ...] [-o out.xlsx]
    python Characterization.py --selftest
"""
import argparse
import sys
import tempfile
from pathlib import Path

import numpy as np
import pandas as pd
from scipy import stats

RUNS = Path.home() / "Documents" / "AR Quest" / "Force runs"

# Type B (nominal values: replace them with the instrument's own calibration if there is one)
TOUCH_RES_MM = 0.055      # Touch position resolution; the CSV shows 1-2 ticks of this size per frame
SEND_MS = 8.0             # HapticLinkClient.SendIntervalMs: a force waits 0-8 ms for the writer's next tick, which the link's round trip does not count
MOVED_MM, TURNED_DEG = 1.0, 0.5   # a model moved more than this since its landmark fit (manual adjustment, refinement) is no longer described by that fit

COLUMNS = ["time_s", "depth_mm", "tissue", "phase", "formula_N", "along_N", "side_N", "state", "tip_x_mm", "tip_y_mm", "tip_z_mm", "off_mm",
           "target_mm", "angle_deg", "rtt_ms", "age_ms", "frame_ms", "meta_reg_res_mm", "meta_reg_moved_mm", "meta_reg_turned_deg",
           "meta_landmarks_mm", "meta_entry_mm", "meta_target_mm"]


def vec(s):
    return np.array(str(s).split(), float)


def load(path):
    """One run: its frames and its constants (the meta_ columns of the first row)."""
    df = pd.read_csv(path, sep=";")
    absent = [c for c in COLUMNS if c not in df.columns]
    if absent or not (df.phase == "insertion").any():
        raise ValueError(f"{Path(path).name}: falta la columna {absent[0] if absent else 'de inserción'} (CSV anterior a la caracterización)")
    f = df.iloc[0]
    meta = {"entry": vec(f.meta_entry_mm) if isinstance(f.meta_entry_mm, str) else None,
            "target": vec(f.meta_target_mm) if isinstance(f.meta_target_mm, str) else None,
            "landmarks": np.array([vec(p) for p in f.meta_landmarks_mm.split("|")]) if isinstance(f.meta_landmarks_mm, str) else None,
            "res": vec(f.meta_reg_res_mm) if isinstance(f.meta_reg_res_mm, str) else None,
            "moved": f.meta_reg_moved_mm, "turned": f.meta_reg_turned_deg}
    return df, meta


def tre(landmarks, point, fre):
    """Expected RMS target registration error at `point` for a fit of N fiducials with residual `fre` (Fitzpatrick, West & Maurer 1998):
    <FLE^2> = N/(N-2) <FRE^2>,  <TRE^2> = <FLE^2>/N (1 + 1/3 sum_k d_k^2/f_k^2), d_k = distance of the point from the fiducials' principal axis k,
    f_k = RMS distance of the fiducials from it. Assumes isotropic, independent localisation errors: an estimate, not a measurement."""
    n = len(landmarks)
    c = landmarks.mean(0)
    x, p = landmarks - c, point - c
    _, axes = np.linalg.eigh(x.T @ x / n)
    ratio = 0.0
    for e in axes.T:
        ratio += (p @ p - (p @ e) ** 2) / np.mean((x ** 2).sum(1) - (x @ e) ** 2)
    return float(np.sqrt(fre ** 2 * n / (n - 2) / n * (1 + ratio / 3)))


def procedure(df):
    """The metrics of one puncture. The trajectory is sampled by depth (mm of advance), not by time, so a pause does not weigh more."""
    ins = df[df.phase == "insertion"]
    last = ins.iloc[-1]
    prof = ins.groupby(np.floor(ins.depth_mm))[["off_mm", "angle_deg"]].mean()
    reached = df.state.isin(["Reached", "Extracting"])
    return {
        "t_total": df.time_s.iloc[-1], "t_insertion": last.time_s, "t_fluid": df.time_s.iloc[-1] - last.time_s,
        "t_target": df.time_s[reached].iloc[0] if reached.any() else np.nan,
        "depth": ins.depth_mm.max(), "end_err": last.target_mm, "min_err": ins.target_mm.min(), "entry_err": ins.off_mm.iloc[0],
        "off_mean": prof.off_mm.mean(), "off_rms": np.sqrt((prof.off_mm ** 2).mean()), "off_max": ins.off_mm.max(),
        "angle_mean": prof.angle_deg.mean(), "angle_end": last.angle_deg,
        "bone_hits": int(((df.state == "Blocked") & (df.state.shift() != "Blocked")).sum()), "reached": bool(reached.any()),
    }


def latency_frames(df):
    """Per frame: the link's round trip, the age of the stylus' pose when it was read, the frame, and two estimates of the whole chain
    (software only; the display's own delay is not measured): force = age + round trip + the writer's mean wait (SEND_MS / 2), picture = half a
    round trip + age + a frame."""
    d = df[df.rtt_ms > 0]   # 0 = the link had not measured one yet
    return pd.DataFrame({"rtt": d.rtt_ms, "age": d.age_ms, "frame": d.frame_ms, "haptic": d.age_ms + d.rtt_ms + SEND_MS / 2,
                         "visual": d.rtt_ms / 2 + d.age_ms + d.frame_ms})


def summary_row(group, label, unit, values, kind, frame_ms):
    """mean, s, u_A = s/sqrt(n), u_B, u_c, k = t(0.975, n-1), U = k u_c over the runs (or calibrations) in `values`."""
    ub = {"pos": TOUCH_RES_MM / np.sqrt(12), "time": np.sqrt(2) * frame_ms / np.sqrt(12) / 1000, "tick": SEND_MS / np.sqrt(12), "none": 0.0}[kind]
    x = np.array([v for v in values if np.isfinite(v)], float)
    n = len(x)
    mean = x.mean() if n else np.nan
    s = x.std(ddof=1) if n > 1 else np.nan
    ua = s / np.sqrt(n) if n > 1 else np.nan
    uc = np.hypot(ua, ub) if n > 1 else np.nan
    k = stats.t.ppf(0.975, n - 1) if n > 1 else np.nan
    text = f"{mean:.2f} ± {k * uc:.2f} {unit}" if n > 1 else (f"{mean:.2f} {unit} (n = 1: sin dispersión)" if n else "sin datos")
    return {"Grupo": group, "Magnitud": label, "Unidad": unit, "n": n, "Media": mean, "s": s, "u_A": ua, "u_B": ub, "u_c": uc,
            "k (t, 95 %)": k, "U (95 %)": k * uc, "Resultado": text}


def analyse(files):
    runs = []
    for f in files:
        try:
            runs.append((Path(f).stem, *load(f)))
        except (ValueError, pd.errors.ParserError) as e:
            print("se omite:", e)
    if not runs:
        raise SystemExit("no hay corridas con el formato de caracterización")
    names = [r[0] for r in runs]
    frames = [r[1] for r in runs]
    frame_ms = float(np.mean([f.frame_ms.mean() for f in frames]))

    # ---- procedure, one row per run
    proc = pd.DataFrame([procedure(f) for f in frames], index=names)

    # ---- latency: per run, and every frame together (its tail matters for the haptics)
    lat_frames = [latency_frames(f) for f in frames]
    lat = pd.DataFrame([{k: v for c in ("rtt", "age", "frame", "haptic", "visual") for k, v in
                         {f"{c}_mean": l[c].mean(), f"{c}_p95": l[c].quantile(0.95)}.items()} | {"frames": len(l)} for l in lat_frames], index=names)
    pooled = pd.concat(lat_frames)

    # ---- registration: one row per calibration (runs with the same residuals share a fit)
    cal = {}
    for name, _, meta in runs:
        if meta["res"] is not None and meta["landmarks"] is not None and meta["target"] is not None:
            cal.setdefault(tuple(meta["res"]), {"runs": [], "meta": meta})["runs"].append(name)
    reg_rows = []
    for i, (res, c) in enumerate(cal.items(), 1):
        m, res = c["meta"], np.array(res)
        fre = float(np.sqrt(np.mean(res ** 2)))
        reg_rows.append({"Calibración": f"C{i}", "Corridas": ", ".join(c["runs"]), **{f"Residuo {j + 1} (mm)": r for j, r in enumerate(res)},
                         "FRE (mm)": fre, "Residuo máx (mm)": res.max(), "TRE objetivo est. (mm)": tre(m["landmarks"], m["target"], fre),
                         "TRE entrada est. (mm)": tre(m["landmarks"], m["entry"], fre) if m["entry"] is not None else np.nan,
                         "Modelo movido (mm)": m["moved"], "Giro (°)": m["turned"], "Vigente": m["moved"] <= MOVED_MM and m["turned"] <= TURNED_DEG})
    reg = pd.DataFrame(reg_rows)
    ok = reg[reg["Vigente"]] if len(reg) else reg

    # ---- the summary
    S = lambda g, l, u, v, k: summary_row(g, l, u, v, k, frame_ms)
    rows = []
    if len(ok):
        rows += [S("Registro (por calibración)", "FRE: RMS de los residuos de los landmarks", "mm", ok["FRE (mm)"], "none"),
                 S("Registro (por calibración)", "Residuo máximo", "mm", ok["Residuo máx (mm)"], "none"),
                 S("Registro (por calibración)", "TRE estimado en el objetivo", "mm", ok["TRE objetivo est. (mm)"], "none"),
                 S("Registro (por calibración)", "TRE estimado en la entrada", "mm", ok["TRE entrada est. (mm)"], "none")]
    for key, label, kind in [("rtt", "Ida y vuelta del enlace (comando → estado)", "none"), ("age", "Edad de la pose del stylus al leerla", "none"),
                             ("frame", "Duración del cuadro", "none"),
                             ("haptic", "Latencia de extremo a extremo, fuerza (edad + ida y vuelta + 4 ms)", "tick"),
                             ("visual", "Latencia de extremo a extremo, imagen sin pantalla (½ ida y vuelta + edad + cuadro)", "tick")]:
        rows.append(S("Latencia (por corrida)", label + " — media", "ms", lat[f"{key}_mean"], kind))
        rows.append(S("Latencia (por corrida)", label + " — percentil 95", "ms", lat[f"{key}_p95"], kind))
    for key, label, unit, kind in [
            ("depth", "Profundidad alcanzada", "mm", "pos"), ("end_err", "Error final punta–objetivo", "mm", "pos"),
            ("min_err", "Error mínimo punta–objetivo", "mm", "pos"), ("entry_err", "Error en la entrada (distancia a la trayectoria ideal)", "mm", "pos"),
            ("off_mean", "Desviación lateral media respecto a la trayectoria ideal", "mm", "pos"), ("off_rms", "Desviación lateral RMS", "mm", "pos"),
            ("off_max", "Desviación lateral máxima", "mm", "pos"), ("angle_mean", "Ángulo medio respecto a la trayectoria ideal", "°", "none"),
            ("angle_end", "Ángulo final respecto a la trayectoria ideal", "°", "none"), ("t_total", "Tiempo de ejecución total", "s", "time"),
            ("t_insertion", "Tiempo de inserción", "s", "time"), ("t_target", "Tiempo hasta alcanzar el objetivo", "s", "time"),
            ("t_fluid", "Tiempo de extracción de líquido", "s", "time"), ("bone_hits", "Contactos con hueso", "", "none")]:
        rows.append(S("Procedimiento (por corrida)", label, unit, proc[key].astype(float), kind))
    resumen = pd.DataFrame(rows)

    # ---- trajectory: the tip by mm of depth, each run and their mean / spread
    bins = [f[f.phase == "insertion"].groupby(np.floor(f[f.phase == "insertion"].depth_mm))[["off_mm", "tip_x_mm", "tip_y_mm", "tip_z_mm"]].mean() for f in frames]
    cat = pd.concat(bins, keys=names, axis=1)
    traj = pd.DataFrame({"Profundidad (mm)": cat.index})
    traj.index = cat.index
    for n in names:
        traj[f"desviación {n} (mm)"] = cat[n]["off_mm"]
    for col, label in [("off_mm", "desviación"), ("tip_x_mm", "punta x"), ("tip_y_mm", "punta y"), ("tip_z_mm", "punta z")]:
        x = cat.xs(col, axis=1, level=1)
        traj[f"{label} media (mm)"], traj[f"{label} s (mm)"] = x.mean(axis=1), x.std(axis=1, ddof=1)
    traj["corridas"] = cat.xs("off_mm", axis=1, level=1).count(axis=1)

    out_proc = proc.rename(columns={
        "t_total": "Tiempo total (s)", "t_insertion": "Tiempo inserción (s)", "t_fluid": "Tiempo extracción (s)", "t_target": "Tiempo hasta objetivo (s)",
        "depth": "Profundidad (mm)", "end_err": "Error final punta–objetivo (mm)", "min_err": "Error mínimo punta–objetivo (mm)",
        "entry_err": "Error en la entrada (mm)", "off_mean": "Desviación media (mm)", "off_rms": "Desviación RMS (mm)", "off_max": "Desviación máx (mm)",
        "angle_mean": "Ángulo medio (°)", "angle_end": "Ángulo final (°)", "bone_hits": "Contactos con hueso", "reached": "Llegó al objetivo"})
    out_proc.insert(0, "Corrida", names)
    out_lat = lat.rename(columns=lambda c: c.replace("_mean", " media (ms)").replace("_p95", " p95 (ms)")
                         .replace("rtt", "ida y vuelta").replace("age", "edad pose").replace("frame", "cuadro").replace("haptic", "háptica").replace("visual", "imagen"))
    out_lat.insert(0, "Corrida", names)
    pool = pd.DataFrame([{"Magnitud": {"rtt": "Ida y vuelta", "age": "Edad de la pose", "frame": "Cuadro", "haptic": "Fuerza (edad + ida y vuelta + 4 ms)",
                                       "visual": "Imagen sin pantalla"}[c], "Cuadros": len(pooled), "Media (ms)": pooled[c].mean(), "s (ms)": pooled[c].std(),
                          "Mediana (ms)": pooled[c].median(), "p95 (ms)": pooled[c].quantile(0.95), "p99 (ms)": pooled[c].quantile(0.99), "Máx (ms)": pooled[c].max()}
                         for c in pooled.columns]) if len(pooled) else pd.DataFrame()
    data = pd.concat([f.drop(columns=[c for c in COLUMNS if c.startswith("meta_")]).assign(corrida=n) for n, f in zip(names, frames)])
    data.insert(0, "corrida", data.pop("corrida"))
    sheets = {"Resumen": resumen, "Registro": reg, "Latencia": out_lat, "Latencia (todos los cuadros)": pool, "Procedimiento": out_proc,
              "Trayectoria": traj, "Datos": data, "Notas": notes(len(names), len(cal), len(ok))}
    return sheets, figures(names, frames, [r[2] for r in runs], proc, lat_frames, reg, resumen)


def notes(n_runs, n_cal, n_valid):
    lines = [
        f"Corridas analizadas: {n_runs}. Calibraciones distintas: {n_cal}, vigentes (modelo no movido más de {MOVED_MM} mm / {TURNED_DEG}° desde el ajuste): {n_valid}.",
        "",
        "INCERTIDUMBRE (GUM). Para cada magnitud, sobre las corridas (o calibraciones): media, s (desviación estándar muestral), u_A = s/√n,",
        "u_B (distribución rectangular: ancho/√12), u_c = √(u_A² + u_B²), U = k·u_c con k = t de Student (95 %, n−1 g.l.). Con n = 1 no hay dispersión y no se da U.",
        "u_B nominales: posiciones = resolución del Touch 0,055 mm; tiempos de ejecución = un cuadro por marca, ×√2 en una duración; latencia de fuerza/imagen = 8 ms",
        "(el comando espera de 0 a 8 ms al siguiente turno del hilo de envío); ida y vuelta, edad y cuadro se miden con Stopwatch (resolución despreciable): u_B = 0.",
        "",
        "GRÁFICAS (hoja 'Gráficas' y PNG en la carpeta graficas/): resumen media ± U; trayectoria de la punta respecto a la recta ideal (media ± s donde hay al menos",
        "la mitad de las corridas); procedimiento por corrida (barras, media y banda ± U); latencia (histograma de todos los cuadros y dispersión por corrida); registro.",
        "",
        "REGISTRO. FRE = RMS de los residuos de los landmarks tras el ajuste (medido). TRE = error de registro esperado en el objetivo / entrada,",
        "ESTIMADO con Fitzpatrick-West-Maurer (1998) a partir del FRE y de la geometría de los landmarks (supone error de localización isotrópico e independiente).",
        "No es una medición directa: para medirlo hay que tocar un punto independiente de la calibración. 'Vigente' = el modelo no se movió desde el ajuste.",
        "",
        "LATENCIA (solo software; no incluye el retardo de la pantalla ni se mide con fotodiodo/cámara).",
        "  Ida y vuelta = del comando al estado que lo confirma (incluye los 8 ms de periodo del puente). Edad = antigüedad de la pose del stylus al leerla.",
        "  Fuerza (extremo a extremo) ≈ edad + ida y vuelta + 4 ms (espera media del hilo de envío, que la ida y vuelta no cuenta). Imagen sin pantalla ≈ ½ ida y vuelta + edad + un cuadro.",
        "  Son estimaciones de modelo (suponen enlace simétrico), no medidas directas. Pose y fuerza no pasan por el mismo reloj en el stylus: el retardo de la pantalla y del servo no están.",
        "  Las muestras de un cuadro están correlacionadas dentro de una corrida: la incertidumbre se calcula sobre las medias por corrida, no sobre los cuadros.",
        "",
        "PROCEDIMIENTO (en el marco del modelo virtual; no depende del registro con el modelo físico). Trayectoria ideal = recta entrada → objetivo.",
        "  Profundidad = mm de aguja bajo la piel. Error final = distancia punta–objetivo al empezar la extracción. Error en la entrada = distancia a la recta en el",
        "  primer cuadro dentro. Desviación = distancia de la punta a la recta, promediada por mm de avance. Tiempo total = desde que la aguja atraviesa la piel hasta",
        "  que se detiene la extracción; hasta objetivo = primer cuadro con la punta a ≤ 5 mm del objetivo.",
    ]
    return pd.DataFrame({"Notas": lines})


# ---- figures: one surface, text in ink (never the series colour), hairline solid grid, categorical hues in a fixed order (dataviz palette)
INK, MUTED, GRID, SURFACE = "#0b0b0b", "#52514e", "#e4e3df", "#fcfcfb"
CAT = ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#6250d6", "#e34948"]
BLUE, ORANGE = CAT[0], CAT[1]


def path_frame(df, meta):
    """The insertion's tip as (advance a, vertical, horizontal) in mm about the ideal path: a runs from the entry to the target along it
    (length L), the other two are the tip's offset from it, vertical = the model's +Y (head) projected across the path. Zero = on the path."""
    ins = df[df.phase == "insertion"]
    d = meta["target"] - meta["entry"]
    length = float(np.linalg.norm(d))
    d = d / length
    up = np.array([0.0, 1.0, 0.0]) - d[1] * d
    up = up / np.linalg.norm(up) if np.linalg.norm(up) > 1e-6 else np.array([1.0, 0.0, 0.0])
    p = ins[["tip_x_mm", "tip_y_mm", "tip_z_mm"]].to_numpy() - meta["entry"]
    return p @ d, p @ up, p @ np.cross(d, up), length


def binned(x, y, size):
    """Mean of y in each mm of x (index = mm), NaN where the run never was."""
    out = np.full(size, np.nan)
    idx = np.floor(np.asarray(x)).astype(int)
    y = np.asarray(y)
    for g in np.unique(idx[(idx >= 0) & (idx < size)]):
        out[g] = y[idx == g].mean()
    return out


def figures(names, frames, metas, proc, lat_frames, reg, resumen):
    """The results as pictures: summary (mean ± U), tip trajectory, procedure per run, latency, registration."""
    import warnings
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.lines import Line2D
    plt.rcParams.update({"font.size": 9, "figure.facecolor": SURFACE, "axes.facecolor": SURFACE, "savefig.facecolor": SURFACE, "text.color": INK,
                         "axes.edgecolor": GRID, "legend.frameon": False})
    R = resumen.set_index("Magnitud")
    n = len(names)

    def axes(ax, title, xlabel="", ylabel="", sub=""):
        ax.set_title(title, loc="left", fontsize=10, fontweight="bold", pad=16 if sub else 8)
        if sub:
            ax.text(0, 1.02, sub, transform=ax.transAxes, fontsize=8, color=MUTED)
        ax.set_xlabel(xlabel, color=MUTED)
        ax.set_ylabel(ylabel, color=MUTED)
        ax.grid(True, color=GRID, lw=0.6)
        ax.set_axisbelow(True)
        ax.spines[["top", "right"]].set_visible(False)
        ax.tick_params(colors=MUTED, length=0)

    def ticks(ax, count):   # run numbers, thinned when there are many
        step = max(1, int(np.ceil(count / 12)))
        ax.set_xticks(np.arange(1, count + 1, step))

    def spread(ax, grid, rows, color=BLUE):
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            y = np.array(rows)
            mean, sd = np.nanmean(y, 0), (np.nanstd(y, 0, ddof=1) if len(y) > 1 else np.zeros(y.shape[1]))
            few = (~np.isnan(y)).sum(0) < np.ceil(len(y) / 2)   # where most runs have not got yet, a mean of the few would jump
            mean[few] = sd[few] = np.nan
        for r in y:
            ax.plot(grid, r, color=MUTED, lw=0.7, alpha=0.4)
        ax.fill_between(grid, mean - sd, mean + sd, color=color, alpha=0.18, lw=0)
        ax.plot(grid, mean, color=color, lw=2)

    figs = {}

    # ---- summary: mean ± U of the headline quantities
    def forest(ax, items, title, xlabel):
        rows = [(short, R.loc[long]) for long, short in items if long in R.index and R.loc[long, "n"] > 0]
        ax.set_visible(bool(rows))
        if not rows:
            return
        y = np.arange(len(rows))[::-1]
        for yi, (short, r) in zip(y, rows):
            if np.isfinite(r["U (95 %)"]):
                ax.errorbar(r["Media"], yi, xerr=r["U (95 %)"], fmt="none", ecolor=BLUE, elinewidth=2, capsize=3)
            ax.plot(r["Media"], yi, "o", color=BLUE, ms=7, mec=SURFACE, mew=1.5)
            ax.text(1.03, yi, r["Resultado"], transform=ax.get_yaxis_transform(), va="center", fontsize=8)
        ax.set_yticks(y, [s for s, _ in rows])
        ax.set_xlim(left=0)
        axes(ax, title, xlabel)
        ax.grid(False, axis="y")
    fig, (a1, a2) = plt.subplots(1, 2, figsize=(13, 4.4), gridspec_kw={"width_ratios": [1, 1], "wspace": 1.05})
    forest(a1, [("FRE: RMS de los residuos de los landmarks", "FRE (registro)"), ("TRE estimado en el objetivo", "TRE en el objetivo (est.)"),
                ("TRE estimado en la entrada", "TRE en la entrada (est.)"), ("Error final punta–objetivo", "Error final punta–objetivo"),
                ("Error mínimo punta–objetivo", "Error mínimo punta–objetivo"), ("Error en la entrada (distancia a la trayectoria ideal)", "Error en la entrada"),
                ("Desviación lateral media respecto a la trayectoria ideal", "Desviación lateral media"), ("Desviación lateral RMS", "Desviación lateral RMS"),
                ("Desviación lateral máxima", "Desviación lateral máxima")], "Registro y procedimiento", "mm")
    forest(a2, [("Ida y vuelta del enlace (comando → estado) — media", "Ida y vuelta del enlace"), ("Edad de la pose del stylus al leerla — media", "Edad de la pose"),
                ("Latencia de extremo a extremo, fuerza (edad + ida y vuelta + 4 ms) — media", "Latencia de fuerza (extremo a extremo)"),
                ("Latencia de extremo a extremo, imagen sin pantalla (½ ida y vuelta + edad + cuadro) — media", "Latencia de imagen (sin pantalla)")],
           "Latencia", "ms")
    fig.suptitle(f"Resumen: media ± U (95 %) sobre {n} corridas", x=0.02, ha="left", fontsize=11, fontweight="bold")
    figs["resumen"] = fig

    # ---- tip trajectory: distance to the ideal path by depth, and the tip's offset from it on both axes by advance
    paths = [path_frame(f, m) if m["entry"] is not None and m["target"] is not None else None for f, m in zip(frames, metas)]
    full = all(p is not None for p in paths)
    fig, ax = plt.subplots(1, 3 if full else 1, figsize=(13 if full else 5, 4), squeeze=False)
    ax = ax[0]
    size = int(max(f.depth_mm.max() for f in frames)) + 2
    ins = [f[f.phase == "insertion"] for f in frames]
    spread(ax[0], np.arange(size), [binned(i.depth_mm, i.off_mm, size) for i in ins])
    axes(ax[0], "Distancia a la recta ideal", "Profundidad bajo la piel (mm)", "mm")
    ax[0].set_ylim(bottom=0)
    ax[0].legend(handles=[Line2D([], [], color=MUTED, alpha=0.5, lw=1, label="cada corrida"), Line2D([], [], color=BLUE, lw=2, label="media ± s")], loc="upper left")
    if full:
        L = max(p[3] for p in paths)
        size = int(L) + 2
        for k, label in ((1, "Desviación vertical (+ = hacia la cabeza)"), (2, "Desviación horizontal")):
            spread(ax[k], np.arange(size), [binned(p[0], p[k], size) for p in paths])
            for p in paths:
                ax[k].plot(p[0][-1], p[k][-1], "o", color=MUTED, ms=3)
            ax[k].axhline(0, color=INK, lw=0.8)
            ax[k].plot([L], [0], "o", ms=9, mfc="none", mec=ORANGE, mew=2)
            axes(ax[k], label, "Avance desde la entrada (mm)", "mm")
            ax[k].set_xlim(0, L * 1.03)
    fig.suptitle("Trayectoria de la punta", x=0.02, ha="left", fontsize=11, fontweight="bold")
    fig.text(0.01, 0.0, "Media ± s donde hay al menos la mitad de las corridas · línea negra = trayectoria ideal · ○ = objetivo · puntos = fin de cada corrida",
             color=MUTED, fontsize=8)
    fig.tight_layout(rect=(0, 0.04, 1, 0.94))
    figs["trayectoria"] = fig

    # ---- procedure per run: bars with the mean and its expanded uncertainty
    def runs_bar(ax, vals, label, title, unit):
        r = R.loc[label]
        x = np.arange(1, n + 1)
        ax.bar(x, vals, width=0.6, color=BLUE, zorder=2)
        if np.isfinite(r["U (95 %)"]):
            ax.axhspan(r["Media"] - r["U (95 %)"], r["Media"] + r["U (95 %)"], color=ORANGE, alpha=0.3, lw=0, zorder=1)
        ax.axhline(r["Media"], color=INK, lw=1, zorder=3)
        axes(ax, title, "Corrida", unit, sub=r["Resultado"])
        ticks(ax, n)
    fig, axs = plt.subplots(2, 2, figsize=(11, 7))
    runs_bar(axs[0, 0], proc["depth"].to_numpy(float), "Profundidad alcanzada", "Profundidad alcanzada", "mm")
    runs_bar(axs[0, 1], proc["end_err"].to_numpy(float), "Error final punta–objetivo", "Error final punta–objetivo", "mm")
    runs_bar(axs[1, 0], proc["entry_err"].to_numpy(float), "Error en la entrada (distancia a la trayectoria ideal)", "Error en la entrada", "mm")
    ax = axs[1, 1]
    t_ins, t_fl = proc["t_insertion"].to_numpy(float), proc["t_fluid"].to_numpy(float)
    ax.bar(np.arange(1, n + 1), t_ins, width=0.6, color=BLUE, ec=SURFACE, lw=1, label="inserción", zorder=2)
    ax.bar(np.arange(1, n + 1), t_fl, width=0.6, bottom=t_ins, color=CAT[2], ec=SURFACE, lw=1, label="extracción de líquido", zorder=2)
    r = R.loc["Tiempo de ejecución total"]
    if np.isfinite(r["U (95 %)"]):
        ax.axhspan(r["Media"] - r["U (95 %)"], r["Media"] + r["U (95 %)"], color=ORANGE, alpha=0.3, lw=0, zorder=1)
    ax.axhline(r["Media"], color=INK, lw=1, zorder=3)
    axes(ax, "Tiempo de ejecución", "Corrida", "s", sub="total: " + r["Resultado"])
    ticks(ax, n)
    ax.set_ylim(top=ax.get_ylim()[1] * 1.2)
    ax.legend(loc="upper left", ncol=2)
    fig.text(0.01, 0.005, "Barras: cada corrida · línea: media · banda naranja: ± U (95 %)", color=MUTED, fontsize=8)
    fig.tight_layout(rect=(0, 0.02, 1, 1))
    figs["procedimiento"] = fig

    # ---- latency: every frame together (and its tail), and the spread inside each run
    with_lat = [(i + 1, l) for i, l in enumerate(lat_frames) if len(l)]
    if with_lat:
        pooled = pd.concat([l for _, l in with_lat])
        fig, (a1, a2) = plt.subplots(1, 2, figsize=(12, 4), gridspec_kw={"width_ratios": [3, 2]})
        top = pooled[["haptic", "visual"]].quantile(0.999).max()
        bins = np.linspace(pooled[["haptic", "visual"]].min().min(), top, 50)
        for col, color, label, height in (("haptic", BLUE, "Fuerza (edad + ida y vuelta + 4 ms)", 0.95), ("visual", ORANGE, "Imagen, sin pantalla", 0.85)):
            w = np.ones(len(pooled)) * 100 / len(pooled)
            a1.hist(pooled[col], bins=bins, weights=w, color=color, alpha=0.3, histtype="stepfilled", label=label)
            a1.hist(pooled[col], bins=bins, weights=w, color=color, histtype="step", lw=1.5)
            p95 = pooled[col].quantile(0.95)
            a1.axvline(p95, color=color, lw=1.2)
            a1.annotate(f" p95 {p95:.1f} ms", (p95, height), xycoords=("data", "axes fraction"), fontsize=8, va="center")
        axes(a1, "Latencia de extremo a extremo (todos los cuadros)", "ms", "% de cuadros")
        a1.set_ylim(top=a1.get_ylim()[1] * 1.3)
        a1.legend(loc="upper left")
        bp = a2.boxplot([l["haptic"] for _, l in with_lat], positions=[i for i, _ in with_lat], whis=(5, 95), showfliers=False, patch_artist=True,
                        medianprops={"color": INK}, widths=0.5)
        for b in bp["boxes"]:
            b.set(facecolor=BLUE, alpha=0.35, edgecolor=BLUE)
        axes(a2, "Latencia de fuerza por corrida", "Corrida", "ms", sub="caja: cuartiles · bigotes: percentiles 5–95")
        ticks(a2, n)
        fig.tight_layout()
        figs["latencia"] = fig

    # ---- registration: each landmark's residual, and FRE against the TRE estimated at the target and the entry, per calibration
    if len(reg):
        fig, (a1, a2) = plt.subplots(1, 2, figsize=(12, 4))
        res = [c for c in reg.columns if c.startswith("Residuo ") and "máx" not in c]
        x = np.arange(len(reg))
        labels = [c + ("" if v else "*") for c, v in zip(reg["Calibración"], reg["Vigente"])]
        alpha = np.where(reg["Vigente"], 1.0, 0.35)

        def grouped(ax, cols, names_, title):
            w = 0.8 / len(cols)
            for j, (c, nm) in enumerate(zip(cols, names_)):
                for xi, v, al in zip(x, reg[c], alpha):
                    ax.bar(xi + (j - (len(cols) - 1) / 2) * w, v, w * 0.9, color=CAT[j], alpha=al, label=nm if xi == 0 else None)
            axes(ax, title, "Calibración", "mm")
            ax.set_xticks(x, labels)
            ax.set_ylim(top=ax.get_ylim()[1] * 1.25)
            ax.legend(loc="upper left", ncol=len(cols))
        grouped(a1, res, [f"Landmark {j + 1}" for j in range(len(res))], "Residuo de cada landmark")
        grouped(a2, ["FRE (mm)", "TRE objetivo est. (mm)", "TRE entrada est. (mm)"], ["FRE", "TRE objetivo (est.)", "TRE entrada (est.)"], "Error de registro")
        if not reg["Vigente"].all():
            fig.text(0.01, 0.0, "* el modelo se movió tras el ajuste: no entra en el resumen", color=MUTED, fontsize=8)
        fig.tight_layout(rect=(0, 0.03, 1, 1))
        figs["registro"] = fig
    return figs


def save_figs(figs, folder):
    import matplotlib.pyplot as plt
    folder.mkdir(parents=True, exist_ok=True)
    paths = []
    for name, fig in figs.items():
        paths.append(folder / f"{name}.png")
        fig.savefig(paths[-1], dpi=100, bbox_inches="tight")
        plt.close(fig)
    return paths


def style(ws, text=False):
    from openpyxl.styles import Alignment, Font, PatternFill
    for c in ws[1]:
        c.font = Font(bold=True, color="FFFFFF")
        c.fill = PatternFill("solid", fgColor="305496")
        c.alignment = Alignment(wrap_text=True, vertical="center")
    ws.freeze_panes = "A2"
    for col in ws.columns:
        width = max(len(str(c.value)) if c.value is not None else 0 for c in list(col)[:200])
        ws.column_dimensions[col[0].column_letter].width = 120 if text else min(max(width + 2, 10), 46)


def write(sheets, path, pngs=()):
    from openpyxl.chart import Reference, ScatterChart, Series
    with pd.ExcelWriter(path, engine="openpyxl") as xw:
        for name, df in sheets.items():
            df.round(4).to_excel(xw, sheet_name=name[:31], index=False)
            style(xw.sheets[name[:31]], text=name == "Notas")
        ws = xw.sheets["Trayectoria"]
        chart = ScatterChart()
        chart.title, chart.style, chart.height, chart.width = "Desviación lateral de la punta respecto a la trayectoria ideal", 13, 10, 20
        chart.x_axis.title, chart.y_axis.title = "Profundidad (mm)", "Distancia a la recta ideal (mm)"
        chart.x_axis.delete = chart.y_axis.delete = False
        x = Reference(ws, min_col=1, min_row=2, max_row=ws.max_row)
        for col in range(2, ws.max_column + 1):
            head = str(ws.cell(1, col).value)
            if head.startswith("desviación") and "s (mm)" not in head:
                s = Series(Reference(ws, min_col=col, min_row=1, max_row=ws.max_row), x, title_from_data=True)
                s.marker.symbol = "none"
                s.graphicalProperties.line.width = 40000 if "media" in head else 12000
                chart.series.append(s)
        ws.add_chart(chart, "A" + str(ws.max_row + 3))
        if pngs:
            from openpyxl.drawing.image import Image
            pics = xw.book.create_sheet("Gráficas", 1)
            pics.sheet_view.showGridLines = False
            row = 1
            for png in pngs:
                img = Image(str(png))
                pics.add_image(img, f"A{row}")
                row += int(img.height / 20) + 2   # a default row is 20 px


def files_from(paths):
    out = []
    for p in map(Path, paths):
        out += sorted(p.glob("run-*.csv")) if p.is_dir() else [p]
    return out


def fake_run(path, seed):
    """A run in the CSV's format with known numbers, for the self-test."""
    rng = np.random.default_rng(seed)
    n = 600
    depth = np.concatenate([np.linspace(2, 86, 500), np.full(100, 86.0)])   # 500 frames in, then the needle stands still for the fluid
    ins = np.arange(n) < 500
    land = "28 -2 56|-16 18 47|-63 -1 54|-7 212 15"
    df = pd.DataFrame({"time_s": np.arange(1, n + 1) * 0.0139, "depth_mm": depth, "tissue": "Skin",
                       "phase": np.where(ins, "insertion", "fluid"), "formula_N": 0.1, "along_N": 0.1, "side_N": 0.0,
                       "state": np.where(np.arange(n) < 480, "Inside", np.where(ins, "Reached", "Extracting")),
                       "tip_x_mm": 1.5, "tip_y_mm": 0.0, "tip_z_mm": depth, "off_mm": 1.5, "target_mm": 88 - depth + 0.5 * seed,
                       "angle_deg": 2.0, "rtt_ms": 10 + rng.normal(0, 1, n), "age_ms": 4.5, "frame_ms": 13.9})
    for c in COLUMNS[17:]:
        df[c] = ""
    df.loc[0, COLUMNS[17:]] = [f"{1 + 0.1 * seed} 1.2 0.8 1", "0", "0", land, "0 0 0", "0 0 88"]
    df.to_csv(path, sep=";", index=False, columns=COLUMNS)


def selftest():
    x = summary_row("g", "m", "u", [1, 2, 3], "none", 13.9)
    assert abs(x["Media"] - 2) < 1e-9 and abs(x["s"] - 1) < 1e-9 and abs(x["U (95 %)"] - 4.3027 / np.sqrt(3)) < 1e-3, x
    land = np.array([[28, -2, 56], [-16, 18, 47], [-63, -1, 54], [-7, 212, 15.0]])
    assert abs(tre(land, land.mean(0), 1.0) - 1 / np.sqrt(2)) < 1e-9   # at the centroid: FLE/sqrt(N) = FRE/sqrt(N-2)
    assert tre(land, np.array([0, 300, 0.0]), 1.0) > tre(land, land.mean(0), 1.0)   # farther from the fiducials, worse
    with tempfile.TemporaryDirectory() as d:
        for seed in range(6):
            fake_run(Path(d) / f"run-{seed}.csv", seed)
        sheets, figs = analyse(files_from([d]))
        pngs = save_figs(figs, Path(d) / "graficas")
        assert [p.stem for p in pngs] == ["resumen", "trayectoria", "procedimiento", "latencia", "registro"] and all(p.stat().st_size > 10000 for p in pngs)
        r = sheets["Resumen"].set_index("Magnitud")
        assert abs(r.loc["Profundidad alcanzada", "Media"] - 86) < 1e-9 and abs(r.loc["Desviación lateral RMS", "Media"] - 1.5) < 1e-9
        assert abs(r.loc["Latencia de extremo a extremo, fuerza (edad + ida y vuelta + 4 ms) — media", "Media"] - 18.5) < 0.5
        assert abs(r.loc["Tiempo de ejecución total", "Media"] - 0.0139 * 600) < 1e-6 and r.loc["Contactos con hueso", "Media"] == 0
        assert abs(r.loc["Tiempo hasta alcanzar el objetivo", "Media"] - 0.0139 * 481) < 1e-6      # the first 'Reached' row is the 481st
        assert r.loc["FRE: RMS de los residuos de los landmarks", "n"] == 6 and sheets["Procedimiento"]["Llegó al objetivo"].all()
        assert abs(r.loc["Error final punta–objetivo", "Media"] - (2 + 0.5 * 2.5)) < 1e-9          # 88 - 86 + 0.5 * seed, seeds 0..5
        assert abs(r.loc["Error en la entrada (distancia a la trayectoria ideal)", "Media"] - 1.5) < 1e-9
        try:
            write(sheets, Path(d) / "t.xlsx", pngs)
            with pd.ExcelFile(Path(d) / "t.xlsx") as book:
                assert book.sheet_names[:2] == ["Resumen", "Gráficas"]
            print("selftest ok (con Excel)")
        except ImportError:
            print("selftest ok (sin Excel: falta openpyxl, pip install openpyxl)")


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("paths", nargs="*", default=[RUNS], help="CSV files or folders with run-*.csv (default: Documents\\AR Quest\\Force runs)")
    ap.add_argument("-o", "--out", help="Excel to write (default: caracterizacion.xlsx next to the CSVs)")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()
    sys.stdout.reconfigure(errors="replace")   # a console that is not UTF-8 must not stop the run over an arrow
    if a.selftest:
        return selftest()
    files = files_from(a.paths)
    if not files:
        raise SystemExit(f"no hay CSV en {a.paths}")
    sheets, figs = analyse(files)
    pd.set_option("display.width", 200, "display.max_colwidth", 80)
    print(sheets["Resumen"][["Grupo", "Magnitud", "n", "Resultado"]].to_string(index=False))
    out = Path(a.out) if a.out else (files[0].parent / "caracterizacion.xlsx")
    pngs = save_figs(figs, out.parent / "graficas")
    try:
        write(sheets, out, pngs)
    except ImportError:
        raise SystemExit("falta openpyxl para escribir el Excel: pip install openpyxl")
    print("\nExcel:", out, "\nGráficas:", out.parent / "graficas")


if __name__ == "__main__":
    sys.exit(main())
