# AR Quest — punción lumbar en realidad mixta con háptica

Español · [English](README.en.md)

Simulador de punción lumbar (aguja espinal) para **Meta Quest 3**. Un modelo físico impreso de columna lumbar y pelvis se registra con su gemelo virtual mediante cuatro puntos de referencia; sobre él, la aguja virtual atraviesa piel, grasa, ligamentos, flavum, espacio epidural y duramadre, y el usuario siente la fuerza de cada tejido en un **stylus háptico 3D Systems Touch** conectado al PC.

El proyecto nació para probar si se puede hacer en el Quest algo parecido a un *model target* de Vuforia (reconocer un objeto físico a partir de su modelo 3D y seguirlo), ya que AR Foundation no trae seguimiento de objetos en Meta. La solución actual es el registro guiado por landmarks; la vista de reproyección y el seguimiento por silueta se exploraron, pero sus scripts ya no están en el proyecto.

```
 Quest 3 (app Unity)  ──TCP 5555 por USB (adb reverse)──  PC: HapticBridge  ──USB──  Touch 3D Stylus
  · modelo + registro                                       · lee pose y botones        · pose, 2 botones,
  · aguja, tejidos, guía                                    · aplica la fuerza             tintero (dock)
  · calcula la fuerza                                       · lee temperaturas por adb
  · gráfica y CSV de la corrida                             · guarda los CSV
```

## 1. Qué hace

- **Registro del modelo** con 4 puntos tocados con la bola blanca del control derecho (ala izquierda, espina de L5, ala derecha, extremo superior de T11); ajuste por mínimos cuadrados (Horn) y RMS en pantalla (< 4 mm bueno, ≥ 8 mm malo). Ajuste manual con los sticks.
- **Aguja** que sigue la mano; una vez dentro de la piel sigue una trayectoria casi recta (puede curvarse como máximo 10°), se detiene en el hueso y avisa al llegar al objetivo (saco dural). Al llegar, A extrae líquido: sale la cabeza del estilete y gotea el fluido.
- **Háptica**: el stylus sostiene la aguja y empuja con la fuerza de cada capa (ver §6); un muro de 300 N/m en el hueso y un resorte lateral de 150 N/m a la trayectoria. Sin stylus, el control vibra según la resistencia.
- **Modo ayuda**: la aguja se dibuja sobre la trayectoria ideal y un indicador dice si el stylus está sobre ella; fuera de la caja de ayuda no hay fuerza.
- **Gráfica de fuerza** en el visor al terminar la punción (fuerza contra profundidad) y **CSV de la corrida** guardado en el PC.
- **Probador de capas**: un cubo de 10 cm de un solo tejido para sentir cada capa por separado.
- **Rendimiento**: la ventana del puente muestra fps, tiempos de cuadro y temperaturas del Quest (leídas por adb, porque la app no puede) y las guarda en un CSV.
- **Caracterización** en Python: error de registro, trayectoria, error final, tiempos y latencia, con incertidumbre (GUM), en un Excel con gráficas.

## 2. Estructura

| Carpeta / archivo | Contenido |
|---|---|
| `Assets/Scripts/` | App del visor: `LandmarkAligner` (calibración y menú), `ModelTarget`, `RigidAlign`, `NeedleAction` (aguja, hueso, extracción, modo ayuda), `NeedleHaptics` (tabla de tejidos y fórmula de fuerza), `LayerTester`, `FluidFlow`, `TissueFade`, `GlassPanel`/`GlassLook` (interfaz), `PerfProbe`, `CameraFeedProbe` |
| `Assets/Scripts/HapticLink/` | Enlace con el PC: `HapticProtocol` (formato de los mensajes; **se compila en Unity y en el puente**), `HapticLinkClient`, `HapticStylus`, `ForceGraph` (gráfica y CSV), `HapticLinkTest` |
| `Assets/Editor/` | Constructores de escenas (`ModelTargetSceneBuilder`, `PunctureGuideBuilder`, `NeedleHapticsBuilder`, `GlassPanelBuilder`…) y las autocomprobaciones (`*Check.cs`) |
| `Assets/Scenes/` | `ModelTargetTest` (la app, primera en Build Settings), `HapticLinkTest` (prueba del enlace, desactivada), `SampleScene` |
| `Assets/Geometry/` | `Anatomy.prefab` (columna lumbar + pelvis, ~30×22×14 cm), mallas de tejidos y `spinalNeedle1.fbx` |
| `HapticBridge/` | Puente del PC: `Bridge.cs` (motor: Touch por `hd.dll`, servidor TCP, sensores, CSV) y `HapticBridge.pyw` (ventana en tkinter) |
| `Graphs/` | `IdealCurve.py` (curva de fuerza ideal y la de Unity), `Characterization.py` (análisis de las corridas) |
| `Docs/CSV_y_graficas.md` | Cómo se obtiene cada variable de los CSV y qué es cada eje de cada gráfica |

## 3. Requisitos

**Visor:** Meta Quest 3 con modo desarrollador y cable USB.

**Unity:** `6000.3.17f1` con módulo Android. Paquetes (ya en `Packages/manifest.json`): Meta XR SDK 207, AR Foundation 6.3.5, Meta OpenXR 2.6.1, OpenXR 1.16.1, Input System 1.19, URP 17.3. Identificador del paquete: `com.crist.arquest`.

**PC (Windows):**
- Controladores del **Touch Device Drivers** de 3D Systems en `C:\Program Files\3D Systems\Touch Device Drivers` (el puente carga `hd.dll` de ahí; no hace falta el SDK).
- **.NET SDK 10** (`net10.0-windows`) para compilar el puente.
- **Python 3.13** con `tkinter` (ventana) y, para el análisis, `numpy pandas scipy matplotlib openpyxl`.
- `adb` (se usa el del Hub de Unity si está; si no, `--adb RUTA`).

## 4. Puesta en marcha

1. **Compilar el puente** (la ventana también lo recompila sola si el `.exe` falta o es más viejo que el código):
   ```bash
   dotnet build HapticBridge -c Release
   ```
2. **Abrir la ventana del puente**: doble clic en `HapticBridge/HapticBridge.pyw` → **Start**. Muestra el estado del visor y del stylus, el límite de fuerza (1 N por defecto) y el interruptor *Send force*. Con `--sim` simula un stylus sin hardware. Mientras no haya visor, ejecuta `adb reverse tcp:5555 tcp:5555` cada 5 s.
3. **Construir la escena** (solo si se rehace): `Tools > Model Target > Build Test Scene` (incluye el stylus y la gráfica; `Tools > Haptic Link > Add Stylus To Main Scene` y `Add Force Graph` los añaden a una escena ya abierta). Si cambia el modelo, `Tools > Model Target > Build Puncture Guide` vuelve a medir landmarks, guía, tabla de tejidos y colisiones.
4. **Compilar e instalar el APK** en el Quest (Android, escena `ModelTargetTest` primera).
5. Con el visor por USB y el puente en *Start*, abrir la app.

## 5. Uso

1. **Calibrar.** Poner el modelo físico de pie sobre su base plana, con la espalda hacia el usuario. Tocar con la bola blanca del control derecho los cuatro puntos que marca la guía y pulsar **TRIGGER** (o A) en cada uno; revisar el RMS y aceptar (B repite).
2. **Menú:** **GRIP derecho**. Stick = mover / ajustar, TRIGGER o A = seleccionar, B = atrás, GRIP = cerrar. Páginas: *Menu* (transparencia de tejidos, mostrar/ocultar grupos, probador de capas), *Settings* (recalibrar, ajustar alineación, tamaño, texto y opacidad de la interfaz, modo ayuda, fuerza del stylus, mover el stylus) y *Layer tester*.
3. **Stylus.** Con el stylus en su tintero la aguja descansa en un punto "home" sobre el modelo. Solo se mueve desde *Settings > Move stylus* (con el stylus en el tintero): el stick izquierdo lo desplaza en el plano XZ sobre un plano cartesiano (arriba = alejándose del usuario), la altura Y es un medidor que empieza en el medio (fila *Height*, stick derecho a los lados) y *Reset to home* lo devuelve al punto de partida. Se guarda entre sesiones. Al sacarlo, retirarlo hacia el usuario todo lo que dé y empujar a lo largo de la trayectoria.
   - **Botón 1** = A: extraer líquido al llegar al objetivo / detener.
   - **Botón 2**: arma y desarma las fuerzas (empiezan desarmadas; volver al tintero o perder el enlace las desarma).
4. **Una punción completa:** armar fuerzas → atravesar la piel (empieza la grabación) → llegar al objetivo (≤ 5 mm) → A (sale la cabeza del estilete y fluye el líquido) → A (se detiene) → sacar la aguja del cuerpo → botón 2 (fuerzas fuera). Aparece la **gráfica de fuerza**; A la cierra. El CSV queda en `Documents\AR Quest\Force runs\`.
5. **Analizar** las corridas:
   ```bash
   python Python/Characterization.py
   ```
   Escribe `caracterizacion.xlsx` y la carpeta `graficas/` junto a los CSV. Para el rendimiento, abrir los `perf-*.csv` de `Documents\AR Quest\Performance\`.

## 6. La fuerza de la aguja

La fórmula es la del archivo `Python/IdealCurve.py`: fuerza de la punta + fricción, por tejido, sobre los anchos medidos en el modelo.

| Tejido | k (N/m) | ρ (N·s/m) | μ (N) | Ancho en el modelo (mm) |
|---|---|---|---|---|
| Piel | 327 | 3,21 | 0,5 | 2,68 |
| Grasa subcutánea | 0 | 2,98 | 0,5 | 28,52 |
| Supra e interespinoso | 72 | 4,06 | 0,8 | 25,27 |
| Ligamento amarillo (flavum) | 920 | 4,21 | 1,1 | 17,71 |
| Espacio epidural | 0 | 2,98 | 0,5 | 4,18 |
| Duramadre | 920 | 4,21 | 1,1 | 7,02 |

Forma que usa Unity (`NeedleHaptics.Force`), positiva en contra del avance:

```
F = k·dx + ρ·v + μ_acumulada·deslizamiento
```

- `dx`: profundidad menos el inicio del tejido, sin tope; `v`: velocidad de la punta (+ hacia adentro).
- `μ_acumulada`: suma de la fricción de todos los tejidos ya atravesados.
- `deslizamiento = clamp(v / 3 mm/s, −1, 1)`: la fricción se desvanece por debajo de 3 mm/s para que el temblor de la mano no la invierta.
- Todo término se opone al movimiento (`IdealCurve` resta `ρ·v`, unos 0,01 N).
- El pico de cada tejido es `k × su ancho + fricción acumulada`. Los que pasan de **3 N** (ligamentos, flavum, epidural, duramadre) se **escalan a 3 N conservando la forma**; piel y grasa quedan como están. El stylus recibe como máximo `maxForce` = 3 N.

`Python/IdealCurve.py` dibuja las dos curvas (la del archivo en negro, la de Unity en rojo discontinuo) para la tabla de la literatura y la del modelo:

```bash
python Python/IdealCurve.py             # gráficas
python Python/IdealCurve.py --selftest  # comprueba la fórmula de Unity contra IdealCurve y contra NeedleHapticsCheck
```

## 7. Datos que genera

| Archivo | Contenido | Documentación |
|---|---|---|
| `Documents\AR Quest\Force runs\run-*.csv` | una punción, un cuadro por fila (23 columnas) | [`Docs/CSV_y_graficas.md`](Docs/CSV_y_graficas.md) §1 |
| `Documents\AR Quest\Performance\perf-*.csv` | fps, tiempos de cuadro, temperaturas, frecuencias, batería (1 fila/s) | [`Docs/CSV_y_graficas.md`](Docs/CSV_y_graficas.md) §2 |
| `caracterizacion.xlsx` + `graficas/*.png` | resumen con incertidumbre, registro, latencia, procedimiento, trayectoria | [`Docs/CSV_y_graficas.md`](Docs/CSV_y_graficas.md) §3–4 |

Los CSV de punción se guardan también en el visor (`/sdcard/Android/data/com.crist.arquest/files/runs`) y se reenvían al PC si el puente no estaba conectado.

## 8. Comprobaciones

Con el Editor abierto, en `Tools`:

| Menú | Qué prueba |
|---|---|
| `Model Target > Self-check Rigid Alignment` | solver del registro |
| `Model Target > Self-check Needle Action` / `Needle Haptics` / `Needle Message` | aguja, tejidos y fórmula (incluye el cruce con `IdealCurve.py`) |
| `Model Target > Self-check Layer Tester` / `Tissue Fade` / `Glass Panel` / `Interface Settings` | probador de capas, transparencia, interfaz |
| `Haptic Link > Self-check` | protocolo y cadena completa contra el puente en `--sim` (hay que compilar el puente antes) |
| `Haptic Link > Self-check Stylus` | stylus, modo ayuda, gráfica y CSV |

Fuera de Unity:

```bash
python HapticBridge/HapticBridge.pyw --selftest
python Python/Characterization.py --selftest
python Python/IdealCurve.py --selftest
```

## 9. Estado y límites

- Las autocomprobaciones corren en el Editor. La sensación háptica (rigidez de las capas, muro de hueso, resorte lateral) y las tolerancias del modo ayuda (8 mm, 15°, caja de 30 mm) son parámetros por ajustar con uso real.
- La latencia que calcula `Characterization.py` es de **software**: no incluye la pantalla ni el servo del Touch. El TRE es una **estimación** (Fitzpatrick–West–Maurer), no una medición.
- El Touch entrega unos 3,3 N pico y ~0,88 N continuos: los tramos de 3 N del flavum y la duramadre superan lo que el dispositivo sostiene; la ventana del puente limita la fuerza real (1 N por defecto).
- La fricción cinética se desvanece por debajo de 3 mm/s, de modo que a velocidades de inserción lentas (la curva de `IdealCurve.py` supone ~1,4 mm/s) la fuerza del visor es menor que la curva ideal; la línea blanca de la gráfica usa la fricción completa.
- No hay licencia definida.
