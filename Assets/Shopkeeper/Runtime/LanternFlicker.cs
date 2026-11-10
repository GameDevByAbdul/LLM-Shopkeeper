using UnityEngine;

namespace Shopkeeper.Runtime
{
    /// <summary>Warm, slightly irregular lantern flame.</summary>
    public sealed class LanternFlicker : MonoBehaviour
    {
        [SerializeField] float baseIntensity = 2.5f;
        Light _light;

        void Awake() => _light = GetComponent<Light>();

        void Update()
        {
            float t = Time.time;
            _light.intensity = baseIntensity * (0.85f + 0.15f * Mathf.PerlinNoise(t * 3.1f, 0.37f) + 0.05f * Mathf.Sin(t * 17f));
        }
    }
}
