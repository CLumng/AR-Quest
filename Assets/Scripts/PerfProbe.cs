using UnityEngine;

// Cada segundo cuenta como corrio la app (fps, tiempo de frame, frames perdidos) y se lo manda al bridge del PC, que lo muestra y lo
// guarda en un CSV junto a las temperaturas del visor (esas solo las puede leer el PC, por adb). Se instala solo en la primera escena.
public class PerfProbe : MonoBehaviour
{
    const float Window = 1f;
    const float LateFactor = 1.5f;       // un frame que dura mas de 1,5 periodos de pantalla es un vsync perdido

    readonly HapticProtocol.PerfFrame _frame = new();
    float _sum, _max, _period = 1f / 72; // segundos acumulados, frame mas largo, periodo de pantalla (72 Hz hasta saber el real)
    int _n, _late;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (Application.platform != RuntimePlatform.Android) return;
        DontDestroyOnLoad(new GameObject(nameof(PerfProbe), typeof(PerfProbe)));
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        _sum += dt; _n++;
        if (dt > _max) _max = dt;
        if (dt > _period * LateFactor) _late++;
        if (_sum < Window) return;

        float hz = OVRPlugin.systemDisplayFrequency;
        _frame.fps = _n / _sum;
        _frame.frameMs = _sum / _n * 1000f;
        _frame.maxMs = _max * 1000f;
        _frame.refreshHz = hz;
        _frame.late = _late;
        HapticLinkClient.Current?.SendPerf(_frame);

        if (hz > 0) _period = 1f / hz;
        _sum = _max = 0; _n = _late = 0;
    }
}
