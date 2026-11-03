# AR Quest — lumbar puncture in mixed reality with haptics

[Español](README.md) · English

A lumbar puncture (spinal needle) simulator for the **Meta Quest 3**. A 3D-printed physical model of the lumbar spine and pelvis is registered to its virtual twin through four landmarks. On top of it, the virtual needle goes through skin, fat, ligaments, ligamentum flavum, epidural space and dura, and the user feels each tissue's force on a **3D Systems Touch haptic stylus** connected to the PC.

The project started as a test of whether something like a Vuforia *model target* (recognise a physical object from its 3D model and track it) can be built on the Quest, since AR Foundation has no object tracking on Meta. The current solution is guided landmark registration; a reprojection view and silhouette tracking were explored, but their scripts are no longer in the project.

```
 Quest 3 (Unity app)   ──TCP 5555 over USB (adb reverse)──   PC: HapticBridge   ──USB──   Touch 3D Stylus
  · model + registration                                      · reads pose and buttons      · pose, 2 buttons,
  · needle, tissues, guide                                    · applies the force             inkwell (dock)
  · computes the force                                        · reads temperatures via adb
  · force graph and run CSV                                   · saves the CSVs
```

## 1. What it does

- **Model registration** with 4 points touched with the white ball on the right controller's tip (left wing, L5 spinous process, right wing, top of T11). Least-squares fit (Horn) and the RMS error on screen (< 4 mm good, ≥ 8 mm poor). Manual adjustment with the thumbsticks.
- **Needle** that follows the hand. Once through the skin it keeps a nearly straight track (it can bend at most 10°), stops at bone, and reports when it reaches the target (dural sac). At the target, A extracts fluid: the stylet head slides out and the fluid drips.
- **Haptics**: the stylus holds the needle and pushes back with each layer's force (see §6), a 300 N/m wall at bone and a 150 N/m side spring toward the track. Without the stylus, the controller vibrates with the tissue's resistance.
- **Help mode**: the needle is drawn on the ideal path and an indicator says whether the stylus is on it; outside the help box there is no force.
- **Force graph** in the headset at the end of the puncture (force against depth) and a **run CSV** saved on the PC.
- **Layer tester**: a 10 cm cube of a single tissue, to feel each layer on its own.
- **Performance**: the bridge window shows fps, frame times and the Quest's temperatures (read through adb, because the app cannot) and logs them to a CSV.
- **Characterisation** in Python: registration error, trajectory, end error, times and latency, with uncertainty (GUM), in an Excel workbook with charts.

## 2. Structure

| Folder / file | Contents |
|---|---|
| `Assets/Scripts/` | Headset app: `LandmarkAligner` (calibration and menu), `ModelTarget`, `RigidAlign`, `NeedleAction` (needle, bone, extraction, help mode), `NeedleHaptics` (tissue table and force formula), `LayerTester`, `FluidFlow`, `TissueFade`, `GlassPanel` / `GlassLook` (interface), `PerfProbe`, `CameraFeedProbe` |
| `Assets/Scripts/HapticLink/` | Link with the PC: `HapticProtocol` (message format; **compiled by both Unity and the bridge**), `HapticLinkClient`, `HapticStylus`, `ForceGraph` (graph and CSV), `HapticLinkTest` |
| `Assets/Editor/` | Scene builders (`ModelTargetSceneBuilder`, `PunctureGuideBuilder`, `NeedleHapticsBuilder`, `GlassPanelBuilder`…) and the self-checks (`*Check.cs`) |
| `Assets/Scenes/` | `ModelTargetTest` (the app, first in Build Settings), `HapticLinkTest` (link test, disabled), `SampleScene` |
| `Assets/Geometry/` | `Anatomy.prefab` (lumbar spine + pelvis, ~30×22×14 cm), tissue meshes and `spinalNeedle1.fbx` |
| `HapticBridge/` | PC bridge: `Bridge.cs` (engine: Touch through `hd.dll`, TCP server, sensors, CSV) and `HapticBridge.pyw` (tkinter window) |
| `Python/` | `IdealCurve.py` (ideal force curve and Unity's), `Characterization.py` (run analysis) |
| `Docs/CSV_y_graficas.md` | How each CSV variable is obtained and what each axis of each graph is (**in Spanish**) |

## 3. Requirements

**Headset:** Meta Quest 3 in developer mode, with a USB cable.

**Unity:** `6000.3.17f1` with the Android module. Packages (already in `Packages/manifest.json`): Meta XR SDK 207, AR Foundation 6.3.5, Meta OpenXR 2.6.1, OpenXR 1.16.1, Input System 1.19, URP 17.3. Package id: `com.crist.arquest`.

**PC (Windows):**
- 3D Systems **Touch Device Drivers** in `C:\Program Files\3D Systems\Touch Device Drivers` (the bridge loads `hd.dll` from there; the SDK is not needed).
- **.NET SDK 10** (`net10.0-windows`) to build the bridge.
- **Python 3.13** with `tkinter` (window) and, for the analysis, `numpy pandas scipy matplotlib openpyxl`.
- `adb` (the one in the Unity Hub is used if present; otherwise `--adb PATH`).

## 4. Getting started

1. **Build the bridge** (the window also rebuilds it by itself if the `.exe` is missing or older than the code):
   ```bash
   dotnet build HapticBridge -c Release
   ```
2. **Open the bridge window**: double-click `HapticBridge/HapticBridge.pyw` → **Start**. It shows the state of the headset and the stylus, the force limit (1 N by default) and the *Send force* switch. `--sim` simulates a stylus with no hardware. While no headset is connected it runs `adb reverse tcp:5555 tcp:5555` every 5 s.
3. **Build the scene** (only if it is rebuilt): `Tools > Model Target > Build Test Scene` (it includes the stylus and the graph; `Tools > Haptic Link > Add Stylus To Main Scene` and `Add Force Graph` add them to a scene that is already open). If the model changes, `Tools > Model Target > Build Puncture Guide` re-measures the landmarks, guide, tissue table and colliders.
4. **Build and install the APK** on the Quest (Android, `ModelTargetTest` first in Build Settings).
5. With the headset on USB and the bridge on *Start*, open the app.

## 5. Usage

1. **Calibrate.** Stand the physical model upright on its flat base, with its back facing you. Touch the four points the guide shows with the white ball on the right controller and press **TRIGGER** (or A) on each; check the RMS and accept (B redoes it).
2. **Menu:** **right GRIP**. Stick = move / adjust, TRIGGER or A = select, B = back, GRIP = close. Pages: *Menu* (tissue transparency, show / hide groups, layer tester), *Settings* (recalibrate, adjust alignment, interface size, text size and opacity, help mode, stylus force strength, move stylus) and *Layer tester*.
3. **Stylus.** With the stylus in its inkwell the needle rests at a "home" point on the model. It moves only from *Settings > Move stylus* (with the stylus in the inkwell): the left stick shifts it in the XZ plane on a cartesian pad (up = away from the user), the Y height is a level meter that starts in the middle (the *Height* row, right stick sideways) and *Reset to home* puts it back where it started. It is kept between sessions. Take it out, pull it back toward you as far as it goes, and push in along the path.
   - **Button 1** = A: extract fluid at the target / stop.
   - **Button 2**: arms and disarms the forces (they start disarmed; going back to the inkwell or losing the link disarms them).
4. **A whole puncture:** arm the forces → go through the skin (recording starts) → reach the target (≤ 5 mm) → A (the stylet head slides out and the fluid flows) → A (it stops) → take the needle out of the body → button 2 (forces off). The **force graph** appears; A closes it. The CSV is left in `Documents\AR Quest\Force runs\`.
5. **Analyse** the runs:
   ```bash
   python Python/Characterization.py
   ```
   It writes `caracterizacion.xlsx` and a `graficas/` folder next to the CSVs. For performance, open the `perf-*.csv` files in `Documents\AR Quest\Performance\`.

## 6. The needle force

The formula is the one in `Python/IdealCurve.py`: tip force + friction, per tissue, over the widths measured in the model.

| Tissue | k (N/m) | ρ (N·s/m) | μ (N) | Width in the model (mm) |
|---|---|---|---|---|
| Skin | 327 | 3.21 | 0.5 | 2.68 |
| Subcutaneous fat | 0 | 2.98 | 0.5 | 28.52 |
| Supraspinous + interspinous | 72 | 4.06 | 0.8 | 25.27 |
| Ligamentum flavum | 920 | 4.21 | 1.1 | 17.71 |
| Epidural space | 0 | 2.98 | 0.5 | 4.18 |
| Dura mater | 920 | 4.21 | 1.1 | 7.02 |

Form used by Unity (`NeedleHaptics.Force`), positive against the advance:

```
F = k·dx + ρ·v + μ_accumulated·slip
```

- `dx`: depth minus the start of the tissue, not capped; `v`: tip speed (+ going in).
- `μ_accumulated`: sum of the friction of every tissue already crossed.
- `slip = clamp(v / 3 mm/s, −1, 1)`: friction fades out below 3 mm/s so the hand's tremor does not flip it.
- Every term opposes the motion (`IdealCurve` subtracts `ρ·v`, about 0.01 N).
- A tissue's peak is `k × its width + accumulated friction`. Those above **3 N** (ligaments, flavum, epidural space, dura) are **scaled to 3 N keeping their shape**; skin and fat are left as they are. The stylus gets at most `maxForce` = 3 N.

`Python/IdealCurve.py` plots both curves (the file's own in black, Unity's in dashed red) for the literature table and for the model's:

```bash
python Python/IdealCurve.py             # plots
python Python/IdealCurve.py --selftest  # checks Unity's formula against IdealCurve and against NeedleHapticsCheck
```

## 7. Data it produces

| File | Contents | Documentation |
|---|---|---|
| `Documents\AR Quest\Force runs\run-*.csv` | one puncture, one frame per row (23 columns) | [`Docs/CSV_y_graficas.md`](Docs/CSV_y_graficas.md) §1 (Spanish) |
| `Documents\AR Quest\Performance\perf-*.csv` | fps, frame times, temperatures, clocks, battery (1 row/s) | [`Docs/CSV_y_graficas.md`](Docs/CSV_y_graficas.md) §2 (Spanish) |
| `caracterizacion.xlsx` + `graficas/*.png` | summary with uncertainty, registration, latency, procedure, trajectory | [`Docs/CSV_y_graficas.md`](Docs/CSV_y_graficas.md) §3–4 (Spanish) |

The puncture CSVs are also saved on the headset (`/sdcard/Android/data/com.crist.arquest/files/runs`) and sent again to the PC if the bridge was not connected.

## 8. Checks

With the Editor open, under `Tools`:

| Menu | What it tests |
|---|---|
| `Model Target > Self-check Rigid Alignment` | the registration solver |
| `Model Target > Self-check Needle Action` / `Needle Haptics` / `Needle Message` | needle, tissues and formula (includes the cross-check with `IdealCurve.py`) |
| `Model Target > Self-check Layer Tester` / `Tissue Fade` / `Glass Panel` / `Interface Settings` | layer tester, transparency, interface |
| `Haptic Link > Self-check` | protocol and the whole chain against the bridge in `--sim` (build the bridge first) |
| `Haptic Link > Self-check Stylus` | stylus, help mode, graph and CSV |

Outside Unity:

```bash
python HapticBridge/HapticBridge.pyw --selftest
python Python/Characterization.py --selftest
python Python/IdealCurve.py --selftest
```

## 9. Status and limits

- The self-checks run in the Editor. The haptic feel (layer stiffness, bone wall, side spring) and the help mode's tolerances (8 mm, 15°, 30 mm box) are parameters still to be tuned with real use.
- The latency `Characterization.py` computes is **software only**: it includes neither the display nor the Touch's servo. The TRE is an **estimate** (Fitzpatrick–West–Maurer), not a measurement.
- The Touch delivers about 3.3 N peak and ~0.88 N continuous: the 3 N stretches of the flavum and the dura are more than the device sustains, and the bridge window limits the real force (1 N by default).
- Kinetic friction fades out below 3 mm/s, so at slow insertion speeds (the `IdealCurve.py` curve assumes ~1.4 mm/s) the headset's force is lower than the ideal curve; the graph's white line uses whole friction.
- No licence is defined.
