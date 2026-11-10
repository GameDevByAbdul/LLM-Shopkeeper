using System;
using System.IO;
using System.Threading;
using Shopkeeper.Core;
using UnityEngine;

namespace Shopkeeper.Runtime
{
    /// <summary>
    /// Composition root for the demo scene: loads design data, wires transport -> session -> UI/puppet,
    /// and exposes the tunables the debug panel edits.
    /// </summary>
    public sealed class ShopDemoController : MonoBehaviour
    {
        [Header("Proxy (holds the API key; the client never does)")]
        [SerializeField] string proxyUrl = "http://127.0.0.1:8787";
        [SerializeField] int requestTimeoutSeconds = 15;

        [Header("Starting setup")]
        [SerializeField] string itemId = "lantern";
        [SerializeField] string personaId = "stingy";
        [SerializeField, Range(0f, 2f)] float temperature = 0.8f;
        [SerializeField] bool structuredOutput = true;

        [Header("Scene references")]
        [SerializeField] ShopkeeperPuppet puppet;

        public DesignConfig Design { get; private set; }
        public NegotiationSession Session { get; private set; }
        public FaultInjectingTransport Faults { get; private set; }
        public TurnRecord LastTurn { get; private set; }
        public string ProxyModel { get; private set; }
        public bool ProxyChecked { get; private set; }
        public float ThinkingSince { get; private set; }
        public string LoadError { get; private set; }

        public string ItemId => itemId;
        public string PersonaId => personaId;
        public float Temperature => temperature;
        public bool StructuredOutput => structuredOutput;
        public bool IsThinking => Session != null && Session.IsBusy;

        public event Action<TurnRecord> TurnCompleted;
        public event Action SessionChanged;

        UnityWebRequestTransport _http;
        CancellationTokenSource _cts;
        ShopUI _ui;
        int _sessionCounter;

        void Awake()
        {
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, "Shopkeeper", "design.json");
                Design = DesignConfig.FromJson(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                LoadError = "design.json failed to load: " + e.Message;
                Debug.LogError(LoadError);
                return;
            }

            _http = new UnityWebRequestTransport(proxyUrl, requestTimeoutSeconds);
            Faults = new FaultInjectingTransport(_http);

            _ui = ShopUI.Create(this);
            gameObject.AddComponent<GuardrailDebugPanel>().Init(this);
            NewSession();
            RefreshProxyStatus();
        }

        public void RefreshProxyStatus()
        {
            ProxyChecked = false;
            _http.CheckHealth(model =>
            {
                ProxyModel = model;
                ProxyChecked = true;
                SessionChanged?.Invoke();
            });
        }

        public void NewSession()
        {
            CancelPending();
            _sessionCounter++;
            Session = new NegotiationSession(Design, itemId, personaId, temperature, seed: Environment.TickCount ^ _sessionCounter)
            {
                UseStructuredOutput = structuredOutput,
            };
            LastTurn = null;
            if (puppet) puppet.SetMood(Mood.Neutral);
            SessionChanged?.Invoke();
        }

        public void SetPersona(string id) { if (id == personaId) return; personaId = id; NewSession(); }
        public void SetItem(string id) { if (id == itemId) return; itemId = id; NewSession(); }

        public void SetTemperature(float t)
        {
            temperature = Mathf.Clamp(t, 0f, 2f);
            if (Session != null) Session.Temperature = temperature; // takes effect next turn
        }

        public void SetStructuredOutput(bool on)
        {
            structuredOutput = on;
            if (Session != null) Session.UseStructuredOutput = on;
        }

        public async void Say(string text)
        {
            if (Session == null || !Session.CanSpeak || string.IsNullOrWhiteSpace(text)) return;

            _cts = new CancellationTokenSource();
            var session = Session;
            ThinkingSince = Time.realtimeSinceStartup;
            _ui.OnPlayerSpoke(text);
            if (puppet) puppet.SetThinking(true);

            TurnRecord rec;
            try
            {
                rec = await session.SayAsync(text, Faults, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (this && puppet) puppet.SetThinking(false);
            }

            if (!this || session != Session) return; // scene unloaded or haggle restarted meanwhile
            LastTurn = rec;
            if (puppet) puppet.React(rec.Validation.Turn.Mood, rec.Validation.Turn.Intent);
            if (rec.Validation.Changed) Debug.Log($"[Guardrail] round {rec.Round}: {string.Join("; ", rec.Validation.Fixes)}");
            TurnCompleted?.Invoke(rec);
        }

        public void AcceptAsk()
        {
            if (Session == null || !Session.IsOpen || Session.IsBusy) return;
            Session.AcceptAsk();
            if (puppet) puppet.React(Mood.Happy, Intent.Accept);
            SessionChanged?.Invoke();
        }

        public void Leave()
        {
            CancelPending();
            Session?.Leave();
            if (puppet) puppet.React(Mood.Annoyed, Intent.Chat);
            SessionChanged?.Invoke();
        }

        void CancelPending()
        {
            if (_cts == null) return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        void OnDestroy() => CancelPending();
    }
}
