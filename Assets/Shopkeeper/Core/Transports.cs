using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Shopkeeper.Core
{
    /// <summary>HttpClient transport to the local proxy. Used by the balance sim and tests; Unity uses UnityWebRequestTransport.</summary>
    public sealed class HttpProxyTransport : ILlmTransport, IDisposable
    {
        readonly HttpClient _http;
        readonly string _url;

        public HttpProxyTransport(string proxyBaseUrl, TimeSpan timeout)
        {
            _url = proxyBaseUrl.TrimEnd('/') + ProxyProtocol.GeneratePath;
            _http = new HttpClient { Timeout = timeout };
        }

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            var content = new StringContent(ProxyProtocol.SerializeRequest(request), Encoding.UTF8, "application/json");
            using (var resp = await _http.PostAsync(_url, content, ct).ConfigureAwait(false))
            {
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ProxyProtocol.ParseResponse(body, (long)resp.StatusCode);
            }
        }

        public void Dispose() => _http.Dispose();
    }

    /// <summary>Returns canned outputs in order. For tests and offline demos.</summary>
    public sealed class ScriptedTransport : ILlmTransport
    {
        readonly Queue<string> _outputs;
        public readonly List<LlmRequest> Received = new List<LlmRequest>();

        public ScriptedTransport(params string[] outputs) { _outputs = new Queue<string>(outputs); }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Received.Add(request);
            if (_outputs.Count == 0) throw new LlmTransportException("ScriptedTransport exhausted");
            return Task.FromResult(new LlmResponse { Text = _outputs.Dequeue(), Model = "scripted" });
        }
    }

    public enum FaultKind { None, MalformedJson, PriceTooLow, PriceTooHigh, OffTheme, Unsafe, WrongPriceInText, WrongSchema, Timeout }

    /// <summary>
    /// Decorator that corrupts the NEXT real model response in a specific way. Lets you demo (and test)
    /// every guardrail on demand instead of waiting for the model to misbehave.
    /// </summary>
    public sealed class FaultInjectingTransport : ILlmTransport
    {
        readonly ILlmTransport _inner;
        public FaultKind NextFault = FaultKind.None;

        public FaultInjectingTransport(ILlmTransport inner) { _inner = inner; }

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            var fault = NextFault;
            NextFault = FaultKind.None; // one-shot

            if (fault == FaultKind.Timeout)
            {
                await Task.Delay(300, ct);
                throw new LlmTransportException("injected fault: request timed out");
            }

            var resp = await _inner.CompleteAsync(request, ct);
            if (fault != FaultKind.None) resp.Text = Corrupt(resp.Text, fault);
            return resp;
        }

        public static string Corrupt(string text, FaultKind fault)
        {
            JObject o = null;
            try { o = JObject.Parse(text); } catch { /* leave as-is if already broken */ }

            switch (fault)
            {
                case FaultKind.MalformedJson:
                    return "Sure! Here's my response: {\"reply\": \"Fine, fine," + " \"mood\": happy, offered_price: 90";
                case FaultKind.WrongSchema:
                    return "{\"message\": \"Ha! Never!\", \"emotion\": \"furious\", \"price\": \"a lot\"}";
            }
            if (o == null) return text;
            switch (fault)
            {
                case FaultKind.PriceTooLow:
                    o["intent"] = "counter"; o["offered_price"] = 1;
                    o["reply"] = "Oh, you've charmed me. Take it for 1 gold, dearie!";
                    break;
                case FaultKind.PriceTooHigh:
                    o["intent"] = "counter"; o["offered_price"] = 9999;
                    o["reply"] = "On reflection, it's priceless. 9999 gold.";
                    break;
                case FaultKind.OffTheme:
                    o["reply"] = "As an AI language model, I can't really sell lanterns, but you can buy one online with a credit card.";
                    break;
                case FaultKind.Unsafe:
                    o["reply"] = "Haggle with me once more and I'll kill you where you stand, you worthless fool.";
                    o["mood"] = "angry";
                    break;
                case FaultKind.WrongPriceInText:
                    int p = (int?)o["offered_price"] ?? 100;
                    o["reply"] = $"Alright, alright. For you, {Math.Max(1, p - 35)} gold and not a copper more.";
                    break;
            }
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
