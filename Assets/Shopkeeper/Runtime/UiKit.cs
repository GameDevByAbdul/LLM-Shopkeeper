using System;
using UnityEngine;
using UnityEngine.UI;

namespace Shopkeeper.Runtime
{
    /// <summary>Small helpers for building uGUI from code (no prefabs to keep in sync).</summary>
    public static class UiKit
    {
        static Font _font;
        static Sprite _rounded;

        public static Font Font => _font ? _font : (_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

        public static readonly Color Ink = new Color(0.96f, 0.92f, 0.84f);
        public static readonly Color InkDim = new Color(0.96f, 0.92f, 0.84f, 0.6f);
        public static readonly Color Panel = new Color(0.10f, 0.07f, 0.06f, 0.88f);
        public static readonly Color PanelLight = new Color(0.20f, 0.15f, 0.12f, 0.95f);
        public static readonly Color Gold = new Color(0.98f, 0.76f, 0.30f);
        public static readonly Color Good = new Color(0.36f, 0.72f, 0.42f);
        public static readonly Color Bad = new Color(0.80f, 0.33f, 0.30f);

        /// <summary>Procedural 9-sliced rounded rectangle, so we don't depend on editor-only built-in sprites.</summary>
        public static Sprite Rounded
        {
            get
            {
                if (_rounded) return _rounded;
                const int size = 32, r = 10;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                var px = new Color32[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, r, size - r), cy = Mathf.Clamp(y + 0.5f, r, size - r);
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    byte a = (byte)(Mathf.Clamp01(r - d + 0.5f) * 255);
                    px[y * size + x] = new Color32(255, 255, 255, a);
                }
                tex.SetPixels32(px);
                tex.Apply();
                _rounded = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
                return _rounded;
            }
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>Anchor to a normalized region and inset by pixel offsets.</summary>
        public static RectTransform Place(this RectTransform rt, float minX, float minY, float maxX, float maxY,
                                          float left = 0, float bottom = 0, float right = 0, float top = 0)
        {
            rt.anchorMin = new Vector2(minX, minY);
            rt.anchorMax = new Vector2(maxX, maxY);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        public static Image Box(Transform parent, string name, Color color, bool rounded = true)
        {
            var rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            if (rounded) { img.sprite = Rounded; img.type = Image.Type.Sliced; }
            return img;
        }

        public static Text Label(Transform parent, string name, string text, int size, Color color,
                                 TextAnchor align = TextAnchor.UpperLeft, FontStyle style = FontStyle.Normal)
        {
            var rt = Rect(name, parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.fontStyle = style;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            t.raycastTarget = false;
            return t;
        }

        public static Button Button(Transform parent, string name, string label, Color bg, Action onClick, int fontSize = 22)
        {
            var img = Box(parent, name, bg);
            var btn = img.gameObject.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f);
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            btn.colors = colors;
            btn.onClick.AddListener(() => onClick());
            Label(img.transform, "Label", label, fontSize, Ink, TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform.Place(0, 0, 1, 1, 10, 4, 10, 4);
            return btn;
        }

        public static void SetLabel(this Button b, string text) => b.GetComponentInChildren<Text>().text = text;

        public static InputField Input(Transform parent, string name, string placeholder, int fontSize = 24)
        {
            var bg = Box(parent, name, new Color(0.97f, 0.93f, 0.85f));
            var field = bg.gameObject.AddComponent<InputField>();
            var text = Label(bg.transform, "Text", "", fontSize, new Color(0.15f, 0.1f, 0.08f), TextAnchor.MiddleLeft);
            text.rectTransform.Place(0, 0, 1, 1, 18, 4, 18, 4);
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.supportRichText = false;
            var ph = Label(bg.transform, "Placeholder", placeholder, fontSize, new Color(0.15f, 0.1f, 0.08f, 0.45f), TextAnchor.MiddleLeft, FontStyle.Italic);
            ph.rectTransform.Place(0, 0, 1, 1, 18, 4, 18, 4);
            field.textComponent = text;
            field.placeholder = ph;
            field.characterLimit = 200;
            field.lineType = InputField.LineType.SingleLine;
            field.caretColor = new Color(0.15f, 0.1f, 0.08f);
            field.customCaretColor = true;
            return field;
        }

        public static Color MoodColor(Core.Mood m)
        {
            switch (m)
            {
                case Core.Mood.Happy: return new Color(0.40f, 0.75f, 0.45f);
                case Core.Mood.Amused: return new Color(0.62f, 0.50f, 0.85f);
                case Core.Mood.Annoyed: return new Color(0.92f, 0.60f, 0.25f);
                case Core.Mood.Angry: return new Color(0.88f, 0.30f, 0.28f);
                default: return new Color(0.62f, 0.60f, 0.56f);
            }
        }
    }
}
