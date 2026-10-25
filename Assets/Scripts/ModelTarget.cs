using System;
using UnityEngine;

/// <summary>
/// Virtual twin of the physical object: its landmarks and the parts the user can show or hide.
/// Whatever finds the physical object (landmark alignment today, a tracker later) writes this transform.
/// </summary>
public class ModelTarget : MonoBehaviour
{
    [Serializable]
    public class Landmark
    {
        public string title;
        [TextArea] public string hint;
        public Transform point; // position on the model, in model space
        public Renderer marker; // sphere drawn at the point
    }

    [Serializable]
    public class Group
    {
        public string name;
        public GameObject[] objects;
        public bool showWhileAligning; // the parts that also exist on the physical model
        public bool fades;             // follows the transparency slider (everything but the bones)
        [NonSerialized] public bool visible;
    }

    public Landmark[] landmarks;
    public Group[] groups;
    public Transform pathEntry, pathTarget; // ideal puncture path: where it meets the skin, and the target (set by PunctureGuideBuilder)

    bool _aligning, _hidden;

    // The scene decides what is visible at start (the prefab ships with the bones off).
    void Awake()
    {
        foreach (var g in groups) g.visible = g.objects[0].activeSelf;
    }

    /// <summary>While aligning, only the groups that exist on the physical model are shown. Afterwards the user's choices apply.</summary>
    public void SetAligning(bool aligning)
    {
        _aligning = aligning;
        Apply();
    }

    public void Toggle(int group)
    {
        groups[group].visible = !groups[group].visible;
        Apply();
    }

    /// <summary>Hides every part (the layer tester's cube replaces the model while its page is open). The user's choices come back when it is shown again.</summary>
    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        Apply();
    }

    void Apply()
    {
        if (groups == null) return;
        foreach (var g in groups)
            foreach (var go in g.objects)
                go.SetActive(!_hidden && (_aligning ? g.showWhileAligning : g.visible));
    }
}
