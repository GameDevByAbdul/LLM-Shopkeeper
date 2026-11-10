using System;
using System.Linq;
using Shopkeeper.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Shopkeeper.Runtime
{
    /// <summary>
    /// In-game guardrail debugger (IMGUI: it's a dev tool). Shows raw model output next to the validated
    /// turn, every rule the validator applied, the price track against design bounds, and live tunables.
    /// Toggle with F1.
    /// </summary>
    public sealed class GuardrailDebugPanel : MonoBehaviour
    {
        public const float Width = 820f;

        ShopDemoController _demo;
        ShopUI _ui;
        bool _visible = true;
        int _viewTurn = -1; // -1 follows the latest turn
        Vector2 _rawScroll, _valScroll, _fixScroll;

        GUIStyle _slider, _thumb, _panel, _h1, _h2, _label, _dim, _mono, _monoBox, _btn, _btnOn, _chip;
        Texture2D _texPanel, _texBox, _texWhite, _texBtn, _texBtnOn;
        Font _monoFont;

        static readonly string[] FaultLabels = { "None", "Malformed JSON", "Price 1g", "Price 9999g", "Off-theme", "Unsafe", "Wrong price in text", "Wrong schema", "Timeout" };

        public bool Visible { get => _visible; set => _visible = value; }

        public void Init(ShopDemoController demo)
        {
            _demo = demo;
            _demo.TurnCompleted += _ => { _viewTurn = -1; _rawScroll = _valScroll = _fixScroll = Vector2.zero; };
            _demo.SessionChanged += () => _viewTurn = -1;
        }

        void Start() => _ui = FindAnyObjectByType<ShopUI>();

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f1Key.wasPressedThisFrame) _visible = !_visible;
            if (_ui) _ui.SetRightInset(_visible ? Width + 20f : 0f); // canvas and IMGUI both scale by height, so units match
        }

        void OnGUI()
        {
            if (!_visible || _demo == null || _demo.Session == null) return;
            EnsureStyles();

            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float vw = Screen.width / scale;
            var area = new Rect(vw - Width - 10f, 10f, Width, 1060f);

            GUILayout.BeginArea(area, _panel);
            DrawHeader();
            GUILayout.Space(6);
            DrawControls();
            GUILayout.Space(8);
            DrawPriceTrack();
            GUILayout.Space(8);
            DrawTurnInspector();
            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------------------------------

        void DrawHeader()
        {
            var s = _demo.Session;
            GUILayout.BeginHorizontal();
            GUILayout.Label("GUARDRAIL DEBUGGER", _h1);
            GUILayout.FlexibleSpace();
            GUILayout.Label("F1 to hide", _dim);
            GUILayout.EndHorizontal();

            var turns = s.Turns;
            double avgMs = turns.Count > 0 ? turns.Average(t => t.RoundTripMs) : 0;
            string model = _demo.ProxyModel ?? "proxy offline";
            GUILayout.Label($"model <b>{model}</b>   ·   avg round trip <b>{avgMs / 1000.0:0.00}s</b>   ·   " +
                            $"fixes <b>{s.GuardrailFixCount}</b>   ·   fallbacks <b>{s.FallbackCount}</b>", _label);
        }

        void DrawControls()
        {
            var d = _demo.Design;
            GUILayout.Label("GENERATION", _h2);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Persona", _label, GUILayout.Width(110));
            foreach (var p in d.Personas)
                if (GUILayout.Button(p.DisplayName, p.Id == _demo.PersonaId ? _btnOn : _btn)) _demo.SetPersona(p.Id);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Item", _label, GUILayout.Width(110));
            foreach (var i in d.Items)
                if (GUILayout.Button(i.Name, i.Id == _demo.ItemId ? _btnOn : _btn)) _demo.SetItem(i.Id);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Temperature  <b>{_demo.Temperature:0.00}</b>", _label, GUILayout.Width(200));
            float t = GUILayout.HorizontalSlider(_demo.Temperature, 0f, 2f, _slider, _thumb, GUILayout.Width(330));
            if (!Mathf.Approximately(t, _demo.Temperature)) _demo.SetTemperature(Mathf.Round(t * 20f) / 20f);
            GUILayout.Space(16);
            bool so = GUILayout.Toggle(_demo.StructuredOutput, _demo.StructuredOutput ? "JSON schema: ON" : "JSON schema: OFF", _btn, GUILayout.Width(200));
            if (so != _demo.StructuredOutput) _demo.SetStructuredOutput(so);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("INJECT FAULT INTO NEXT REPLY", _h2);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Restart haggle", _btn, GUILayout.Width(160))) _demo.NewSession();
            GUILayout.EndHorizontal();
            int current = (int)_demo.Faults.NextFault;
            int picked = GUILayout.SelectionGrid(current, FaultLabels, 5, _btn);
            if (picked != current) _demo.Faults.NextFault = (FaultKind)picked;
        }

        void DrawPriceTrack()
        {
            var s = _demo.Session;
            var item = s.Item;
            GUILayout.Label($"PRICE TRACK   <color=#ffffff80>design bounds {item.MinPrice}..{item.MaxPrice}, list {item.ListPrice}</color>", _h2);
            var r = GUILayoutUtility.GetRect(Width - 40, 54);
            float lo = Mathf.Min(item.MinPrice * 0.5f, s.PlayerOffer ?? item.MinPrice) , hi = item.MaxPrice * 1.1f;
            Func<float, float> X = v => r.x + 10 + (Mathf.Clamp(v, lo, hi) - lo) / (hi - lo) * (r.width - 20);

            Fill(new Rect(r.x, r.y + 22, r.width, 10), new Color(1, 1, 1, 0.08f));
            Fill(new Rect(X(item.MinPrice), r.y + 22, X(item.MaxPrice) - X(item.MinPrice), 10), new Color(0.4f, 0.7f, 0.45f, 0.35f));
            Fill(new Rect(X(item.MinPrice) - 1, r.y + 14, 3, 26), UiKit.Bad);
            Fill(new Rect(X(item.MaxPrice) - 1, r.y + 14, 3, 26), UiKit.Bad);

            var turn = SelectedTurn();
            int? rawPrice = turn != null ? RawPrice(turn) : null;
            if (rawPrice.HasValue && turn.Validation.Turn.OfferedPrice != rawPrice.Value)
                Marker(X(rawPrice.Value), r, new Color(1f, 0.35f, 0.3f), $"model said {rawPrice}", top: true);
            if (s.PlayerOffer.HasValue) Marker(X(s.PlayerOffer.Value), r, new Color(0.5f, 0.77f, 1f), $"offer {s.PlayerOffer}", top: false);
            Marker(X(s.CurrentAsk), r, UiKit.Gold, $"ask {s.CurrentAsk}", top: true);
        }

        void DrawTurnInspector()
        {
            var s = _demo.Session;
            if (s.Turns.Count == 0)
            {
                GUILayout.Label("No turns yet. Say something to the shopkeeper.", _dim);
                return;
            }

            int idx = _viewTurn < 0 ? s.Turns.Count - 1 : Mathf.Clamp(_viewTurn, 0, s.Turns.Count - 1);
            var t = s.Turns[idx];

            GUILayout.BeginHorizontal();
            GUILayout.Label($"TURN {t.Round}", _h2);
            if (GUILayout.Button("<", _btn, GUILayout.Width(36)) && idx > 0) _viewTurn = idx - 1;
            if (GUILayout.Button(">", _btn, GUILayout.Width(36))) _viewTurn = idx + 1 >= s.Turns.Count ? -1 : idx + 1;
            GUILayout.Space(10);
            string served = t.Response != null ? $"{t.Response.Model}, model {t.Response.ModelLatencyMs}ms" : "no response";
            GUILayout.Label($"round trip <b>{t.RoundTripMs}ms</b>  <color=#ffffff80>({served})</color>", _label);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Label($"Player: \"{Trunc(t.PlayerText, 90)}\"   <color=#7fc4ff>parsed offer: {(t.PlayerOffer.HasValue ? t.PlayerOffer + "g" : "none")}</color>", _label);

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width((Width - 50) / 2));
            GUILayout.Label("RAW MODEL OUTPUT", _h2);
            string raw = t.TransportError != null ? $"<transport error>\n{t.TransportError}" : JsonText.PrettyOrRaw(t.Validation.Raw);
            _rawScroll = GUILayout.BeginScrollView(_rawScroll, _monoBox, GUILayout.Height(250));
            GUILayout.Label(raw, _mono);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.Space(10);

            GUILayout.BeginVertical(GUILayout.Width((Width - 50) / 2));
            GUILayout.Label("VALIDATED (WHAT THE GAME USED)", _h2);
            _valScroll = GUILayout.BeginScrollView(_valScroll, _monoBox, GUILayout.Height(250));
            GUILayout.Label(t.Validation.Turn.ToJson(indented: true), _mono);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            var fixes = t.Validation.Fixes;
            string verdict = fixes.Count == 0 ? "<color=#8fd99a>PASSED: no changes</color>"
                : t.Validation.UsedFallback ? "<color=#ff8a80>REJECTED: scripted fallback used</color>"
                : t.Validation.MaxSeverity == FixSeverity.Blocked ? "<color=#ff8a80>OVERRIDDEN by design rules</color>"
                : t.Validation.MaxSeverity == FixSeverity.Warning ? "<color=#ffc164>REPAIRED</color>"
                : "<color=#a9c7ff>NORMALIZED</color>";
            GUILayout.Label($"GUARDRAIL CHANGES ({fixes.Count})   {verdict}", _h2);
            _fixScroll = GUILayout.BeginScrollView(_fixScroll, _monoBox, GUILayout.ExpandHeight(true));
            foreach (var f in fixes)
            {
                string sev = f.Severity == FixSeverity.Blocked ? "<color=#ff8a80>BLOCKED </color>"
                           : f.Severity == FixSeverity.Warning ? "<color=#ffc164>REPAIRED</color>"
                           : "<color=#a9c7ff>NORMAL  </color>";
                GUILayout.Label($"{sev}  <b>{f.Rule}</b>  <color=#ffffff80>{f.Field}</color>", _label);
                GUILayout.Label($"     <color=#ff9e99>{Escape(Trunc(f.Before, 140))}</color>\n  -> <color=#9fe0a8>{Escape(Trunc(f.After, 140))}</color>", _label);
                GUILayout.Space(4);
            }
            GUILayout.EndScrollView();
        }

        // ---- helpers ----------------------------------------------------------------------------

        TurnRecord SelectedTurn()
        {
            var s = _demo.Session;
            if (s.Turns.Count == 0) return null;
            return s.Turns[_viewTurn < 0 ? s.Turns.Count - 1 : Mathf.Clamp(_viewTurn, 0, s.Turns.Count - 1)];
        }

        static int? RawPrice(TurnRecord t)
        {
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(t.Validation.Raw);
                var tok = o["offered_price"];
                return tok != null && tok.Type == Newtonsoft.Json.Linq.JTokenType.Integer ? (int?)tok : null;
            }
            catch (Exception) { return null; }
        }

        void Marker(float x, Rect r, Color c, string label, bool top)
        {
            Fill(new Rect(x - 4, r.y + 18, 8, 18), c);
            var old = GUI.contentColor;
            GUI.contentColor = c;
            float lx = Mathf.Clamp(x - 60, r.x, r.xMax - 120);
            GUI.Label(new Rect(lx, top ? r.y - 2 : r.y + 36, 120, 18), label, _chip);
            GUI.contentColor = old;
        }

        void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _texWhite);
            GUI.color = old;
        }

        // Rich-text labels would interpret "<" in model output as markup; swap in a look-alike glyph.
        static string Escape(string s) => (s ?? "").Replace('<', '‹').Replace('>', '›');
        static string Trunc(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n - 3) + "...";

        static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        void EnsureStyles()
        {
            if (_panel != null) return;
            _texPanel = Tex(new Color(0.06f, 0.06f, 0.08f, 0.93f));
            _texBox = Tex(new Color(0f, 0f, 0f, 0.45f));
            _texWhite = Tex(Color.white);
            _texBtn = Tex(new Color(1f, 1f, 1f, 0.10f));
            _texBtnOn = Tex(new Color(0.98f, 0.76f, 0.30f, 0.85f));
            _monoFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Cascadia Mono", "Menlo", "Courier New" }, 16);

            var ink = new Color(0.92f, 0.92f, 0.95f);
            _panel = new GUIStyle { normal = { background = _texPanel }, padding = new RectOffset(16, 16, 12, 12) };
            _h1 = new GUIStyle { font = UiKit.Font, fontSize = 24, fontStyle = FontStyle.Bold, normal = { textColor = UiKit.Gold }, richText = true };
            _h2 = new GUIStyle { font = UiKit.Font, fontSize = 15, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.75f, 0.78f, 0.85f) }, richText = true, margin = new RectOffset(0, 0, 6, 4) };
            _label = new GUIStyle { font = UiKit.Font, fontSize = 16, normal = { textColor = ink }, richText = true, wordWrap = true, margin = new RectOffset(0, 0, 2, 2) };
            _dim = new GUIStyle(_label) { normal = { textColor = new Color(1, 1, 1, 0.5f) }, fontSize = 14, alignment = TextAnchor.UpperCenter, wordWrap = false };
            _chip = new GUIStyle(_label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false, normal = { textColor = Color.white } };
            _mono = new GUIStyle { font = _monoFont, fontSize = 15, normal = { textColor = new Color(0.85f, 0.95f, 0.85f) }, wordWrap = true, richText = false };
            _monoBox = new GUIStyle { normal = { background = _texBox }, padding = new RectOffset(10, 10, 8, 8) };
            _btn = new GUIStyle
            {
                font = UiKit.Font, fontSize = 15, alignment = TextAnchor.MiddleCenter, normal = { background = _texBtn, textColor = ink },
                hover = { background = Tex(new Color(1f, 1f, 1f, 0.2f)), textColor = Color.white },
                active = { background = _texBtnOn, textColor = Color.black },
                onNormal = { background = _texBtnOn, textColor = new Color(0.1f, 0.07f, 0.02f) }, onHover = { background = _texBtnOn, textColor = Color.black },
                padding = new RectOffset(10, 10, 6, 6), margin = new RectOffset(3, 3, 3, 3),
            };
            _slider = new GUIStyle { normal = { background = _texBtn }, fixedHeight = 6, margin = new RectOffset(4, 4, 12, 4) };
            _thumb = new GUIStyle { normal = { background = _texBtnOn }, active = { background = _texWhite }, fixedWidth = 14, fixedHeight = 22, margin = new RectOffset(0, 0, -8, 0) };
            _btnOn = new GUIStyle(_btn) { normal = { background = _texBtnOn, textColor = new Color(0.1f, 0.07f, 0.02f) }, fontStyle = FontStyle.Bold };
        }
    }
}
