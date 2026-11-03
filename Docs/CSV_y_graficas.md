# Variables de los CSV y ejes de las gráficas

Este documento explica (1) cómo se obtiene cada columna de los dos CSV que genera el sistema, (2) cómo se calculan las variables derivadas que `Python/Characterization.py` pone en el Excel y (3) qué significa cada eje de cada gráfica.

Convenciones de todos los CSV: separador `;`, decimal `.`, saltos de línea CRLF, UTF-8 sin BOM, una celda vacía donde el dato no existe.

| Archivo | Quién lo escribe | Dónde queda | Qué contiene |
|---|---|---|---|
| `run-AAAAMMDD-HHMMSS.csv` | el visor (`ForceGraph.Csv`), y el puente lo guarda en el PC | PC: `Documents\AR Quest\Force runs\` · visor: `/sdcard/Android/data/com.crist.arquest/files/runs/` | una punción completa, **una fila por cuadro del visor** (~72 Hz, ~13,9 ms) |
| `perf-AAAAMMDD-HHMMSS.csv` | el puente (`Bridge.cs`) | PC: `Documents\AR Quest\Performance\` | rendimiento del visor, **una fila por segundo**, un archivo por cada Start del puente |

---

## 1. CSV de una punción (`run-*.csv`)

Se graba desde el cuadro en que la punta de la aguja atraviesa la piel **con las fuerzas armadas** (botón 2 del stylus) hasta el momento en que se detiene la extracción de líquido. Si las fuerzas se apagan o la aguja sale del modelo antes de ese momento, la corrida se descarta y no hay archivo.

Las 17 primeras columnas están en todas las filas. Las 6 últimas (`meta_*`) son constantes de la corrida y **solo la primera fila las trae**.

Marco del modelo (columnas `tip_*`, `meta_*`): origen en el origen del prefab `Anatomy`, **+Y hacia la cabeza, +Z hacia la espalda, +X hacia la cadera izquierda**, en mm.

### 1.1 Tiempo y profundidad

| Columna | Unidad | Cómo se obtiene |
|---|---|---|
| `time_s` | s | Suma de las duraciones de cuadro (`Time.deltaTime`) desde que la aguja atravesó la piel. La primera fila vale la duración de su propio cuadro (~0,0139 s). |
| `depth_mm` | mm | `NeedleAction.Depth × 1000`: longitud de aguja bajo la piel, medida **a lo largo de la trayectoria de la aguja** desde el punto donde su eje cruza la piel. En modo ayuda es la proyección de la punta del stylus sobre la trayectoria ideal, desde la entrada y acotada entre 0 y la longitud entrada→objetivo (~86 mm). Retroceder la disminuye. No es la distancia en línea recta a la piel. |
| `tissue` | texto | Tejido en el que está la punta: el último de la tabla de capas cuyo `start ≤ depth`. Vacío antes de la piel. Los inicios de cada capa se midieron sobre las mallas del modelo siguiendo la trayectoria ideal (`NeedleHapticsBuilder`: piel ≈ 1,0 mm, grasa ≈ 3,7 mm, supraespinoso ≈ 32,2 mm, interespinoso ≈ 42,3 mm, flavum ≈ 57,5 mm, epidural ≈ 75,2 mm, duramadre ≈ 79,4 mm). Supraespinoso e interespinoso son **un mismo tejido** para la fórmula, pero aparecen con nombre distinto. |
| `phase` | texto | `fluid` si el estado de la aguja es `Extracting` (cabeza del estilete saliendo + líquido fluyendo); `insertion` en cualquier otro caso. |
| `state` | texto | Estado de `NeedleAction` en ese cuadro: `Inside` (dentro, libre), `Blocked` (la punta está contra un hueso), `Reached` (punta a ≤ 5 mm del objetivo, `reachRadius`), `Extracting` (extrayendo líquido). `Outside` y `Withdrawing` no se graban: la corrida empieza al entrar y termina al empezar a retirar. |

### 1.2 Fuerzas

Signo común: **positivo = en contra del avance** (la aguja es empujada hacia afuera); negativo = hacia adentro.

| Columna | Unidad | Cómo se obtiene |
|---|---|---|
| `formula_N` | N | `−NeedleHaptics.Axial`: la fórmula de tejidos evaluada con la **velocidad real de la mano** (derivada cuadro a cuadro de `depth`, suavizada con constante de tiempo 0,1 s). Incluye el escalado a 3 N por tejido, pero no el límite del stylus. Vale 0 mientras la aguja está bloqueada por hueso y durante la fase de líquido (en esos casos la fuerza no sale de la tabla). Como depende de la velocidad real, **muestra los escalones del encoder del Touch y el apagado/encendido de la fricción**; la curva ideal (sección 3.1) no. |
| `along_N` | N | Fuerza **enviada al stylus**, proyectada sobre el eje de la aguja: `−(SentWorld · Dir)`. `SentWorld` es la fuerza que pidió `NeedleAction`, ya limitada por `HapticStylus.Flush` (módulo ≤ `maxForce` = 3 N, × `strength`, y el filtro `damping` si es > 0; en la escena está en 0). En tejido coincide con `formula_N`. En bloqueo por hueso es el muro (`−pushed × 300 N/m`); en la fase `fluid` es el resorte que sostiene el stylus en su sitio (`300 N/m`). **Ojo:** es lo que el visor *pidió*. El puente aplica además su propio límite (ventana, por defecto 1 N) y la fuerza real también está acotada por el Touch (~3,3 N pico, ~0,88 N continuo). |
| `side_N` | N | Módulo de la componente de la fuerza enviada **perpendicular** a la aguja: `\|F − (F·d)d\|`. Es el resorte lateral de 150 N/m que devuelve la mano a la trayectoria de la aguja (`StylusForce`: `−lateral × 150`). Siempre ≥ 0 (no lleva signo). |

La fórmula (tabla por tejido, ver `README.md` y `Python/IdealCurve.py`):

`F = k·dx + ρ·v + μ·deslizamiento`, con `dx` = profundidad menos el inicio del tejido (sin tope), `ρ·v` amortiguación, `μ` = suma de las fricciones cinéticas de todos los tejidos atravesados, y `deslizamiento = clamp(v / 3 mm/s, −1, 1)`. Los tejidos cuyo pico supera 3 N se escalan a 3 N conservando la forma.

### 1.3 Trayectoria de la punta y error respecto a la trayectoria ideal

La trayectoria ideal es la recta **entrada → objetivo** de la guía de punción (`pathEntry`, `pathTarget`), medida sobre las mallas: pasa por el espacio L3–L4, 7° hacia craneal, ~85 mm de piel a objetivo.

| Columna | Unidad | Cómo se obtiene |
|---|---|---|
| `tip_x_mm`, `tip_y_mm`, `tip_z_mm` | mm | Posición de la punta (`NeedleAction.Tip`) convertida al marco del modelo (`model.InverseTransformPoint × 1000`). Es la **trayectoria de la punta**. |
| `off_mm` | mm | Distancia de la punta a la **recta** ideal: `\|(tip − entrada) × eje\|`, con `eje` el vector unitario entrada → objetivo. Es la desviación lateral (no cuenta cuánto avanzó). |
| `target_mm` | mm | Distancia euclídea de la punta al objetivo: `\|tip − objetivo\|`. |
| `angle_deg` | ° | Ángulo entre la dirección de la aguja y el eje de la trayectoria ideal: `Vector3.Angle(dir, eje)`. |

Estas cuatro se calculan en el marco del **modelo virtual**: no dependen de qué tan bien esté registrado con el modelo físico.

### 1.4 Tiempos del enlace y del cuadro

Se miden con `Stopwatch` en el visor.

| Columna | Unidad | Cómo se obtiene |
|---|---|---|
| `rtt_ms` | ms | Ida y vuelta del enlace: tiempo desde que el visor **envía** un comando (con número de secuencia) hasta que llega el estado del puente que lo confirma (`ack`). Incluye el periodo de 8 ms con que el puente emite estados; **no** incluye lo que el comando esperó en el visor al siguiente turno del hilo de envío (0–8 ms). `0` = todavía no se ha medido. |
| `age_ms` | ms | Antigüedad del último estado del stylus en el instante en que el cuadro lo leyó (`ahora − hora de recepción`). Es cuánto "viejo" estaba lo que se dibujó. |
| `frame_ms` | ms | Duración del cuadro: `Time.deltaTime × 1000`. A 72 Hz son 13,89 ms. |

### 1.5 Constantes de la corrida (solo en la primera fila)

| Columna | Unidad | Cómo se obtiene |
|---|---|---|
| `meta_reg_res_mm` | mm | Residuo de cada landmark tras el último ajuste de calibración, separados por espacio y en el orden: ala izquierda, L5, ala derecha, extremo superior. Residuo = distancia entre el punto tocado y el mismo punto del modelo ya colocado (`RigidAlign.Solve`, Horn 1987). |
| `meta_reg_moved_mm` | mm | Cuánto se ha desplazado el modelo desde ese ajuste (ajuste manual o refinamiento por silueta). Si es grande, los residuos ya no describen dónde está el modelo. |
| `meta_reg_turned_deg` | ° | Lo mismo, pero giro. |
| `meta_landmarks_mm` | mm | Coordenadas `x y z` de cada landmark **en el marco del modelo**; los 4 puntos van separados por `\|`. Se usan para estimar el TRE. |
| `meta_entry_mm` | mm | `x y z` de la entrada de la trayectoria ideal, en el marco del modelo. |
| `meta_target_mm` | mm | `x y z` del objetivo (centro de la bola en el saco dural), en el marco del modelo. |

---

## 2. CSV de rendimiento (`perf-*.csv`)

Una fila por segundo. Dos fuentes: la app (llega por el enlace) y los sensores del visor (los lee el PC por `adb shell`, porque la app no tiene permiso en Android). Si falta una fuente, sus celdas quedan vacías: sin adb solo hay fps; con la app cerrada solo hay temperaturas (así se registra el enfriamiento).

| Columna | Unidad | Cómo se obtiene |
|---|---|---|
| `time` | fecha/hora del PC | Reloj del PC al escribir la fila. |
| `elapsed_s` | s | Segundos desde la primera fila del archivo (`Stopwatch` del PC). |
| `app` | 0/1 | 1 si la fila trae los números de la app (`fps`…`refresh_hz`). |
| `fps` | cuadros/s | `PerfProbe`: nº de cuadros de la última ventana de 1 s ÷ su duración. |
| `frame_ms` | ms | Duración media del cuadro en esa ventana: `suma(dt)/n × 1000`. |
| `frame_max_ms` | ms | El cuadro más largo de la ventana. |
| `late_frames` | cuadros | Cuadros de la ventana que duraron más de **1,5 periodos de pantalla** (un vsync perdido). |
| `refresh_hz` | Hz | Frecuencia real de la pantalla: `OVRPlugin.systemDisplayFrequency`. |
| `cpu_max_c`, `cpu_avg_c` | °C | Máximo y media de los sensores de tipo CPU del bloque *Current temperatures from HAL* de `dumpsys thermalservice` (el bloque *Cached* está desactualizado y no se usa). |
| `gpu_max_c`, `gpu_avg_c` | °C | Igual, sensores de tipo GPU. |
| `soc_c` | °C | Sensor `soc-usr`. |
| `pcb_max_c` | °C | Máximo de los sensores cuyo nombre empieza por `pcb`. |
| `surface_c` | °C | Estimación de temperatura de superficie del SO (`surf-virt-usr`). |
| `display_c` | °C | Estimación de la pantalla (`disp-virt`). |
| `battery_c` | °C | Sensor `battery`; si no existe, `dumpsys battery` (`temperature` ÷ 10). |
| `batt_virt_c` | °C | `batt-virt-usr`: la temperatura virtual de batería **por la que el SO hace throttling** (umbral ligero ≈ 58 °C en el Quest 3). |
| `thermal_status` | 0–6 | `Thermal Status` de Android: 0 ninguno, 1 ligero, 2 moderado, 3 severo… 6 apagado. |
| `fan` | nivel | Valor del dispositivo de enfriamiento cuyo nombre contiene `fan`. |
| `cpu_mhz` | MHz | La mayor de las frecuencias actuales de los núcleos (`scaling_cur_freq` ÷ 1000). |
| `gpu_mhz` | MHz | `kgsl-3d0/gpuclk` ÷ 10⁶. |
| `gpu_busy_pct` | % | `kgsl-3d0/gpu_busy_percentage`. |
| `battery_pct` | % | `level` de `dumpsys battery`. |
| `charging` | 0/1 | 1 si `dumpsys battery` dice que está alimentado por AC, USB, inalámbrico o dock. |

Una misma lectura de la app puede repetirse en dos filas seguidas, o saltarse una: el bucle del PC y el del visor miden ~1 s cada uno y no están sincronizados.

No hay script de gráficas para este CSV todavía: los ejes naturales son `elapsed_s` (X) contra temperatura (°C), fps o frecuencia (Y).

---

## 3. Variables derivadas (`Python/Characterization.py` → Excel)

`python Characterization.py [carpeta o archivos]` lee los `run-*.csv` y escribe `caracterizacion.xlsx` más la carpeta `graficas/` junto a ellos. Se omiten con aviso los CSV anteriores al formato de 23 columnas.

### 3.1 Por corrida (hoja *Procedimiento*)

| Variable | Cómo se calcula |
|---|---|
| Tiempo total | `time_s` de la última fila (inserción + líquido). |
| Tiempo inserción | `time_s` de la última fila con `phase = insertion`. |
| Tiempo extracción | total − inserción. |
| Tiempo hasta objetivo | `time_s` de la primera fila con `state` en `Reached` o `Extracting` (punta a ≤ 5 mm del objetivo). Vacío si nunca llegó. |
| Profundidad | máximo de `depth_mm` en la inserción. |
| Error final punta–objetivo | `target_mm` de la última fila de inserción (al empezar la extracción). |
| Error mínimo punta–objetivo | mínimo de `target_mm` en la inserción. |
| Error en la entrada | `off_mm` de la primera fila (distancia a la recta ideal al atravesar la piel). |
| Desviación media / RMS | La inserción se divide en tramos de **1 mm de profundidad** (`floor(depth_mm)`), se promedia `off_mm` en cada tramo y se toma la media (o la raíz de la media de los cuadrados) de esos promedios. Así una pausa larga no pesa más que un avance rápido. |
| Desviación máxima | máximo de `off_mm`. |
| Ángulo medio / final | media por tramos de `angle_deg` / valor de la última fila. |
| Contactos con hueso | nº de transiciones a `state = Blocked` (flancos de subida). |

### 3.2 Latencia (hojas *Latencia*)

Solo se usan los cuadros con `rtt_ms > 0`. Por cuadro:

- **Fuerza, extremo a extremo** = `age_ms + rtt_ms + 4 ms` (4 ms = media del tiempo que el comando espera al hilo de envío, que `rtt` no cuenta).
- **Imagen sin pantalla** = `rtt_ms / 2 + age_ms + frame_ms`.

Son **estimaciones de software** que suponen un enlace simétrico: no incluyen el retardo de la pantalla ni el del servo del Touch, y no se miden con fotodiodo ni cámara. Se informa media y percentil 95 por corrida; la hoja *Latencia (todos los cuadros)* agrupa todos los cuadros (media, s, mediana, p95, p99, máximo).

### 3.3 Registro (hoja *Registro*)

Una fila por calibración (las corridas con los mismos residuos comparten ajuste).

- **FRE** (medido) = `√(media(residuo²))` de los residuos de `meta_reg_res_mm`.
- **TRE** (estimado, **no medido**) en el objetivo y en la entrada, con Fitzpatrick–West–Maurer (1998): `⟨FLE²⟩ = N/(N−2) ⟨FRE²⟩` y `⟨TRE²⟩ = ⟨FLE²⟩/N · (1 + ⅓ Σₖ dₖ²/fₖ²)`, donde `N` = nº de landmarks, `dₖ` = distancia del punto al eje principal *k* de los landmarks y `fₖ` = distancia RMS de los landmarks a ese eje. Supone error de localización isotrópico e independiente. Para medir el TRE de verdad habría que tocar un punto que no se haya usado en la calibración.
- **Vigente** = el modelo no se movió más de 1 mm ni 0,5° desde el ajuste. Solo las calibraciones vigentes entran al resumen.

### 3.4 Resumen con incertidumbre (hoja *Resumen*)

Para cada magnitud, sobre las corridas (o calibraciones), siguiendo la GUM:

| Símbolo | Cálculo |
|---|---|
| `n` | nº de corridas con dato |
| media, `s` | media y desviación estándar muestral (`ddof = 1`) |
| `u_A` | `s / √n` (tipo A) |
| `u_B` | tipo B, distribución rectangular `ancho/√12`: posiciones = resolución del Touch 0,055 mm; tiempos de ejecución = un cuadro por marca × √2; latencias de fuerza e imagen = 8 ms (periodo del hilo de envío); el resto 0 |
| `u_c` | `√(u_A² + u_B²)` |
| `k` | t de Student, 95 %, `n − 1` grados de libertad |
| `U (95 %)` | `k · u_c` |
| Resultado | texto `media ± U unidad` |

Con `n = 1` no hay dispersión y no se da `U`. Las muestras de un mismo cuadro están correlacionadas dentro de una corrida, por eso la incertidumbre se calcula sobre las **medias por corrida**, no sobre los cuadros.

### 3.5 Otras hojas

*Trayectoria*: por mm de profundidad, `off_mm` de cada corrida y media/`s` de `off_mm` y de `tip_x/y/z_mm`, con un gráfico de dispersión. *Datos*: todos los cuadros de todas las corridas (sin las columnas `meta_*`). *Notas*: este método en texto. *Gráficas*: los cinco PNG de la sección 4.3.

---

## 4. Ejes de cada gráfica

### 4.1 Gráfica de fuerza en el visor (`ForceGraph`)

Tarjeta de 0,75 m frente al usuario que aparece al terminar una punción con fuerzas, **cuando la aguja ya está fuera del modelo y las fuerzas apagadas**. A / botón 1 del stylus la cierran. Solo dibuja la inserción (las filas `fluid` no).

| Elemento | Qué es |
|---|---|
| **Eje X: Profundidad (mm)** | de 0 a lo más profundo que llegó la punta, en pasos redondos (1, 2, 5 × 10ⁿ; unos 8 marcas); la última marca lleva la unidad "mm". Si el usuario retrocedió, la línea amarilla vuelve atrás en X. |
| **Eje Y: Fuerza (N)** | fuerza **a lo largo de la aguja**, positiva en contra del avance. Va del mínimo al máximo de ambas líneas, en pasos redondos; la línea del 0 es más clara. Negativo = la fuerza empuja hacia adentro (pasa al retroceder: la fricción cambia de signo). La última marca lleva "N". |
| Bandas de color | un tejido de la tabla, desde donde empieza hasta donde empieza el siguiente (el último, hasta el borde). Línea punteada vertical donde empieza cada tejido salvo el primero. La leyenda lleva los nombres en su color. |
| **Línea blanca** | curva ideal: `NeedleHaptics.Ideal(depth)`, la fórmula con avance uniforme (toda la profundidad en 60 s) y fricción completa, 400 puntos. Depende solo de la profundidad. |
| **Línea amarilla** | `along_N` del CSV contra `depth_mm`: la fuerza realmente enviada al stylus. |
| Texto inferior | pico de la curva ideal y en qué tejido, tiempo total de la corrida, y el pico de lo enviado. |

### 4.2 `Python/IdealCurve.py`

Dos figuras: *Ideal Needle Force* (anchos de la tabla de la literatura: 3, 6, 30, 3, 6, 6 mm) y *Modeled Needle Force* (anchos medidos en el modelo: 2,68, 28,52, 25,27, 17,71, 4,18, 7,02 mm).

- **Eje X: Depth (mm)**, 0 → suma de los anchos de la tabla.
- **Eje Y: Force (N)**, positiva en contra del avance.
- Bandas de color y líneas punteadas: cada capa, como en el visor.
- **Negra**: la fórmula de `IdealCurve` (`force_needle`), a velocidad uniforme (profundidad total en 60 s).
- **Roja discontinua**: la fórmula de Unity (`force_ideal_unity`), con los tejidos > 3 N escalados a 3 N. Es la misma curva que la línea blanca del visor.

La caída vertical de la línea negra en el último punto es del original (en `x` exactamente igual a la profundidad total `force_needle` cae a solo la fricción); la curva de Unity no la tiene.

### 4.3 Gráficas de `Characterization.py` (carpeta `graficas/`, hoja *Gráficas*)

| Archivo | Ejes |
|---|---|
| `resumen.png` | Dos diagramas de bosque. Cada **fila (eje Y)** es una magnitud; **eje X** = valor, en **mm** (izquierda: FRE, TRE, errores y desviaciones) o en **ms** (derecha: latencias). Punto = media; barra = ± `U` (95 %); a la derecha de cada fila, el texto "media ± U". |
| `trayectoria.png` (3 paneles) | **1.** X = profundidad bajo la piel (mm, por tramos de 1 mm); Y = distancia a la recta ideal (mm, ≥ 0). **2.** X = avance desde la entrada a lo largo de la trayectoria (mm, proyección de la punta sobre el eje entrada→objetivo); Y = desviación **vertical** (mm), positiva hacia la cabeza (+Y del modelo proyectado perpendicular a la trayectoria). **3.** Igual, con la desviación **horizontal** (perpendicular a la trayectoria y a la vertical; positiva hacia el lado +X del modelo, el de la cadera izquierda). En todos: gris = cada corrida, azul = media, banda = ± `s`, solo donde hay al menos la mitad de las corridas. En 2 y 3, la línea negra en 0 es la trayectoria ideal, ○ naranja = objetivo (en X = longitud de la trayectoria), puntos grises = final de cada corrida. |
| `procedimiento.png` (4 paneles) | **X = número de corrida** en todos. **Y**: profundidad alcanzada (mm); error final punta–objetivo (mm); error en la entrada (mm); tiempo de ejecución (s, barra apilada: inserción azul + extracción verde). Línea negra = media, banda naranja = ± `U` (95 %). |
| `latencia.png` | Izquierda: **X = latencia (ms)**, **Y = % de cuadros** (histograma de todos los cuadros juntos), series fuerza (azul) e imagen (naranja), línea vertical = percentil 95. Derecha: **X = corrida**, **Y = latencia de fuerza (ms)**, diagrama de caja: caja = cuartiles, raya = mediana, bigotes = percentiles 5–95, sin atípicos. |
| `registro.png` | Izquierda: **X = calibración** (C1, C2…; con `*` si el modelo se movió después), **Y = residuo (mm)**, una barra por landmark. Derecha: **X = calibración**, **Y = mm**, barras FRE, TRE en el objetivo (est.) y TRE en la entrada (est.). Las calibraciones no vigentes salen atenuadas. |
| Gráfico del Excel (hoja *Trayectoria*) | X = profundidad (mm), Y = distancia a la recta ideal (mm); una línea fina por corrida y una gruesa por la media. |
