using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Shopkeeper.Core
{
    public sealed class LlmMessage
    {
        public string Role; // "user" | "model"
        public string Text;
        public LlmMessage(string role, string text) { Role = role; Text = text; }
    }

    /// <summary>Provider-agnostic request. The proxy maps it onto a concrete vendor API and adds the secret.</summary>
    public sealed class LlmRequest
    {
        public string System;
        public List<LlmMessage> Messages = new List<LlmMessage>();
        public float Temperature;
        public int MaxOutputTokens;
        /// <summary>JSON Schema for constrained decoding. Null = free-form text (validator still runs).</summary>
        public JObject ResponseSchema;
        /// <summary>Correlates client logs with proxy logs.</summary>
        public string RequestId;
    }

    public sealed class LlmResponse
    {
        public string Text;
        public string Model;
        public long ModelLatencyMs;
        public string FinishReason;
    }

    /// <summary>Anything that can turn an LlmRequest into raw text. Unity uses UnityWebRequest; the sim uses HttpClient.</summary>
    public interface ILlmTransport
    {
        Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
    }

    /// <summary>JSON wire format between the game and the local proxy (see proxy/server.py).</summary>
    public static class ProxyProtocol
    {
        public const string GeneratePath = "/v1/generate";

        public static string SerializeRequest(LlmRequest req)
        {
            var msgs = new JArray();
            foreach (var m in req.Messages) msgs.Add(new JObject { ["role"] = m.Role, ["text"] = m.Text });
            var o = new JObject
            {
                ["system"] = req.System,
                ["messages"] = msgs,
                ["temperature"] = req.Temperature,
                ["max_output_tokens"] = req.MaxOutputTokens,
                ["request_id"] = req.RequestId,
            };
            if (req.ResponseSchema != null) o["response_schema"] = req.ResponseSchema;
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <exception cref="LlmTransportException">Proxy returned an error payload or garbage.</exception>
        public static LlmResponse ParseResponse(string body, long httpStatus)
        {
            JObject o;
            try { o = JObject.Parse(body); }
            catch { throw new LlmTransportException($"proxy HTTP {httpStatus}: non-JSON body"); }
            if (httpStatus >= 400 || o["error"] != null)
                throw new LlmTransportException($"proxy HTTP {httpStatus}: {(string)o["error"] ?? "unknown error"}");
            return new LlmResponse
            {
                Text = (string)o["text"] ?? "",
                Model = (string)o["model"],
                ModelLatencyMs = (long?)o["latency_ms"] ?? 0,
                FinishReason = (string)o["finish_reason"],
            };
        }
    }

    public sealed class LlmTransportException : System.Exception
    {
        public LlmTransportException(string message) : base(message) { }
    }
}
