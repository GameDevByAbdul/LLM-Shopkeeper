using Shopkeeper.Core;
using UnityEngine;

namespace Shopkeeper.Runtime
{
    /// <summary>
    /// Procedural shopkeeper animation driven only by validated output (mood + intent).
    /// Placeholder art by design: swap the primitives for a rigged character and keep this interface.
    /// </summary>
    public sealed class ShopkeeperPuppet : MonoBehaviour
    {
        [SerializeField] Transform body;
        [SerializeField] Transform head;
        [SerializeField] Transform browL, browR;
        [SerializeField] Transform mouthL, mouthR;
        [SerializeField] Renderer headRenderer;
        [SerializeField] Light moodLight;

        Mood _mood = Mood.Neutral;
        Intent _intent = Intent.Chat;
        bool _thinking;
        float _reactT = 99f;

        Vector3 _bodyBase, _headBase, _browLBase, _browRBase;
        Color _skin;
        MaterialPropertyBlock _mpb;
        float _browAngle, _browY, _mouthAngle, _flush;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public void Bind(Transform body, Transform head, Transform browL, Transform browR, Transform mouthL, Transform mouthR, Renderer headRenderer, Light moodLight)
        {
            this.body = body; this.head = head; this.browL = browL; this.browR = browR;
            this.mouthL = mouthL; this.mouthR = mouthR; this.headRenderer = headRenderer; this.moodLight = moodLight;
        }

        void Awake()
        {
            _bodyBase = body.localPosition;
            _headBase = head.localPosition;
            _browLBase = browL.localPosition;
            _browRBase = browR.localPosition;
            _skin = headRenderer.sharedMaterial.GetColor(BaseColorId);
            _mpb = new MaterialPropertyBlock();
        }

        public void SetMood(Mood m) { _mood = m; }
        public void SetThinking(bool on) { _thinking = on; }

        public void React(Mood m, Intent i)
        {
            _mood = m;
            _intent = i;
            _reactT = 0f;
        }

        void Update()
        {
            float t = Time.time, dt = Time.deltaTime;
            _reactT += dt;
            float react = Mathf.Clamp01(1f - _reactT / 1.4f); // decays over 1.4s

            // Breathing.
            body.localPosition = _bodyBase + Vector3.up * (Mathf.Sin(t * 2.1f) * 0.012f);

            // Head motion: thinking idle + a short mood reaction.
            Vector3 headPos = _headBase + Vector3.up * (Mathf.Sin(t * 2.1f - 0.4f) * 0.015f);
            Vector3 euler = Vector3.zero;
            if (_thinking)
            {
                euler += new Vector3(-6f, 8f * Mathf.Sin(t * 1.3f), 10f * Mathf.Sin(t * 0.9f));
            }
            switch (_mood)
            {
                case Mood.Happy: headPos.y += Mathf.Abs(Mathf.Sin(_reactT * 11f)) * 0.07f * react; break;
                case Mood.Angry: euler.y += Mathf.Sin(_reactT * 42f) * 9f * react; break;
                case Mood.Annoyed: euler.y += Mathf.Sin(_reactT * 14f) * 5f * react; euler.x += 4f; break;
                case Mood.Amused: euler.z += 9f; break;
            }
            if (_intent == Intent.Accept) headPos.y += Mathf.Abs(Mathf.Sin(_reactT * 8f)) * 0.05f * react;
            head.localPosition = Vector3.Lerp(head.localPosition, headPos, 1f - Mathf.Exp(-dt * 18f));
            head.localRotation = Quaternion.Slerp(head.localRotation, Quaternion.Euler(euler), 1f - Mathf.Exp(-dt * 10f));

            // Face: brows (tilt + height) and a two-segment mouth (V = smile, inverted V = frown).
            float browTarget = 0f, browYTarget = 0f, mouthTarget = 0f, flushTarget = 0f;
            switch (_mood)
            {
                case Mood.Happy: browYTarget = 0.025f; mouthTarget = 22f; break;
                case Mood.Amused: browTarget = -8f; browYTarget = 0.015f; mouthTarget = 12f; break;
                case Mood.Annoyed: browTarget = 14f; mouthTarget = -10f; flushTarget = 0.25f; break;
                case Mood.Angry: browTarget = 26f; browYTarget = -0.015f; mouthTarget = -22f; flushTarget = 0.6f; break;
            }
            if (_thinking) { browTarget = -6f; browYTarget = 0.02f; mouthTarget = 0f; }
            float k = 1f - Mathf.Exp(-dt * 12f);
            _browAngle = Mathf.Lerp(_browAngle, browTarget, k);
            _browY = Mathf.Lerp(_browY, browYTarget, k);
            _mouthAngle = Mathf.Lerp(_mouthAngle, mouthTarget, k);
            _flush = Mathf.Lerp(_flush, flushTarget, k * 0.5f);

            browL.localPosition = _browLBase + Vector3.up * _browY;
            browR.localPosition = _browRBase + Vector3.up * _browY;
            browL.localRotation = Quaternion.Euler(0, 0, -_browAngle);
            browR.localRotation = Quaternion.Euler(0, 0, _browAngle);
            mouthL.localRotation = Quaternion.Euler(0, 0, -_mouthAngle);
            mouthR.localRotation = Quaternion.Euler(0, 0, _mouthAngle);

            // Angry/annoyed: face flushes red.
            headRenderer.GetPropertyBlock(_mpb);
            _mpb.SetColor(BaseColorId, Color.Lerp(_skin, new Color(0.85f, 0.32f, 0.28f), _flush));
            headRenderer.SetPropertyBlock(_mpb);

            if (moodLight)
            {
                Color target = _thinking ? new Color(0.55f, 0.6f, 1f) : UiKit.MoodColor(_mood);
                moodLight.color = Color.Lerp(moodLight.color, target, k * 0.5f);
            }
        }
    }
}
