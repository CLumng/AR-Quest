using System.Text;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Checks the data path a model tracker needs: CPU camera image, intrinsics, frame rate.
/// Shows the latest frame on a quad and the numbers on a TextMesh.
/// </summary>
public class CameraFeedProbe : MonoBehaviour
{
    public ARCameraManager cameraManager;
    public Renderer preview;
    public TextMesh hud;
    public float previewHz = 5;

    Texture2D _tex;
    float _nextPreview, _fps;
    double _lastTs;
    int _frames;
    string _imageInfo = "no CPU image yet";
    readonly StringBuilder _sb = new();

    void Update()
    {
        if (cameraManager.TryAcquireLatestCpuImage(out var img))
        {
            using (img)
            {
                if (img.timestamp != _lastTs)
                {
                    if (_lastTs > 0) _fps = Mathf.Lerp(_fps, (float)(1 / (img.timestamp - _lastTs)), 0.1f);
                    _lastTs = img.timestamp;
                    _frames++;
                }
                if (Time.time >= _nextPreview) { _nextPreview = Time.time + 1 / previewHz; Show(img); }
            }
        }

        _sb.Clear()
           .AppendLine($"camera: {(cameraManager.subsystem != null ? cameraManager.subsystem.GetType().Name : "NO SUBSYSTEM")} running={cameraManager.subsystem?.running}")
           .AppendLine(_imageInfo)
           .AppendLine($"camera fps: {_fps:F1}  frames: {_frames}");
        if (cameraManager.TryGetIntrinsics(out var k))
            _sb.Append($"f=({k.focalLength.x:F1},{k.focalLength.y:F1}) c=({k.principalPoint.x:F1},{k.principalPoint.y:F1}) res={k.resolution}");
        else
            _sb.Append("intrinsics: n/a");
        hud.text = _sb.ToString();
    }

    void Show(XRCpuImage img)
    {
        const TextureFormat fmt = TextureFormat.RGBA32;
        var dim = img.dimensions / 2;
        if (_tex == null || _tex.width != dim.x || _tex.height != dim.y)
        {
            _tex = new Texture2D(dim.x, dim.y, fmt, false);
            preview.material.mainTexture = _tex;
            preview.transform.localScale = new Vector3(preview.transform.localScale.y * dim.x / dim.y, preview.transform.localScale.y, 1);
        }
        var p = new XRCpuImage.ConversionParams(img, fmt, XRCpuImage.Transformation.MirrorX) { outputDimensions = dim };
        img.Convert(p, _tex.GetRawTextureData<byte>());
        _tex.Apply();
        _imageInfo = $"cpu image {img.width}x{img.height} {img.format}";
    }
}
