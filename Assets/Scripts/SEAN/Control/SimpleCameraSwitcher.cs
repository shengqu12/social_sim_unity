using UnityEngine;

/// Cycles between assigned cameras with the V key, by toggling Camera.enabled.
/// Attach anywhere (e.g. an empty "CameraSwitcher" GameObject in the scene),
/// then drag the cameras into the list in the Inspector.
public class SimpleCameraSwitcher : MonoBehaviour
{
    public Camera[] cameras;          // element 0 = first-person, 1 = third-person, ...
    public KeyCode switchKey = KeyCode.V;
    int _current = 0;

    void Start() => Activate(_current);

    void Update()
    {
        if (Input.GetKeyDown(switchKey))
        {
            _current = (_current + 1) % cameras.Length;
            Activate(_current);
        }
    }

    void Activate(int idx)
    {
        for (int i = 0; i < cameras.Length; i++)
            if (cameras[i] != null) cameras[i].enabled = (i == idx);
    }
}
