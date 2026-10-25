using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// What the user can tune about the interface from the menu: how big the cards are (Size), how big their text is (TextSize, on its own:
/// the cards keep their size and the text wraps and grows inside them) and how opaque they are (the passthrough shows through a less
/// opaque card, but the text gets harder to read over a busy room). Kept between sessions in PlayerPrefs.
/// The sliders work on a 0..1 position (SizeT, TextSizeT, OpacityT); the real values are Size, TextSize and Opacity.
/// </summary>
public static class GlassSettings
{
    public const float MinSize = 0.7f, MaxSize = 1.3f, DefaultSize = 1f;
    public const float MinText = 0.8f, MaxText = 1.3f, DefaultText = 1f; // not below 0.8: the key chips are 44 units tall and their pill needs 34
    public const float MinOpacity = 0.4f, MaxOpacity = 1f, DefaultOpacity = 0.9f;
    const string SizeKey = "glass.size", TextKey = "glass.text", OpacityKey = "glass.opacity";

    public static event Action Changed;

    static float _size = -1, _text = -1, _opacity = -1; // -1 = not read yet

    public static float Size { get { Load(); return _size; } }
    public static float TextSize { get { Load(); return _text; } }
    public static float Opacity { get { Load(); return _opacity; } }

    public static float SizeT
    {
        get => Mathf.InverseLerp(MinSize, MaxSize, Size);
        set => Set(ref _size, SizeKey, Mathf.Lerp(MinSize, MaxSize, Mathf.Clamp01(value)));
    }

    public static float TextSizeT
    {
        get => Mathf.InverseLerp(MinText, MaxText, TextSize);
        set => Set(ref _text, TextKey, Mathf.Lerp(MinText, MaxText, Mathf.Clamp01(value)));
    }

    public static float OpacityT
    {
        get => Mathf.InverseLerp(MinOpacity, MaxOpacity, Opacity);
        set => Set(ref _opacity, OpacityKey, Mathf.Lerp(MinOpacity, MaxOpacity, Mathf.Clamp01(value)));
    }

    public static void Reset()
    {
        Load();
        Set(ref _size, SizeKey, DefaultSize);
        Set(ref _text, TextKey, DefaultText);
        Set(ref _opacity, OpacityKey, DefaultOpacity);
    }

    /// <summary>Writes the values to disk; called when the menu closes, not on every change of a slider.</summary>
    public static void Save() => PlayerPrefs.Save();

    static void Load()
    {
        if (_size >= 0) return;
        _size = Mathf.Clamp(PlayerPrefs.GetFloat(SizeKey, DefaultSize), MinSize, MaxSize);
        _text = Mathf.Clamp(PlayerPrefs.GetFloat(TextKey, DefaultText), MinText, MaxText);
        _opacity = Mathf.Clamp(PlayerPrefs.GetFloat(OpacityKey, DefaultOpacity), MinOpacity, MaxOpacity);
    }

    static void Set(ref float field, string key, float value)
    {
        Load();
        if (Mathf.Approximately(field, value)) return;
        field = value;
        PlayerPrefs.SetFloat(key, value);
        Changed?.Invoke();
    }
}

/// <summary>
/// On a card's Canvas: scales it and sets the opacity of its background from GlassSettings, now and whenever they change.
/// The text of a card that is not drawn by a GlassPanel is listed here with its size at 100 %, and follows the text size.
/// </summary>
public class GlassLook : MonoBehaviour
{
    public Image card;               // the background; its colour keeps its RGB, the alpha is the setting
    public float baseScale = 0.001f; // the Canvas scale at size 100 %
    public TMP_Text[] texts;         // optional: texts sized by the text setting
    public float[] sizes;            // their font sizes at 100 %

    void OnEnable()
    {
        Apply();
        GlassSettings.Changed += Apply;
    }

    void OnDisable() => GlassSettings.Changed -= Apply;

    public void Apply()
    {
        transform.localScale = Vector3.one * (baseScale * GlassSettings.Size);
        var c = card.color;
        card.color = new Color(c.r, c.g, c.b, GlassSettings.Opacity);
        if (texts == null) return;
        for (int i = 0; i < texts.Length; i++) texts[i].fontSize = sizes[i] * GlassSettings.TextSize;
    }
}
