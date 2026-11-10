using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shopkeeper.Core;
using UnityEngine.Networking;

namespace Shopkeeper.Runtime
{
    /// <summary>
    /// ILlmTransport over UnityWebRequest (portable to every Unity platform, unlike HttpClient).
    /// Talks only to the local proxy; the client never sees an API key.
    /// </summary>
    public sealed class UnityWebRequestTransport : ILlmTransport
    {
        readonly string _baseUrl;
        readonly int _timeoutSeconds;
        readonly string _proxyToken;

        public UnityWebRequestTransport(string baseUrl, int timeoutSeconds, string proxyToken = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _timeoutSeconds = timeoutSeconds;
            _proxyToken = proxyToken;
        }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<LlmResponse>();
            byte[] body = Encoding.UTF8.GetBytes(ProxyProtocol.SerializeRequest(request));

            var uwr = new UnityWebRequest(_baseUrl + ProxyProtocol.GeneratePath, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = _timeoutSeconds,
            };
            uwr.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(_proxyToken)) uwr.SetRequestHeader("X-Proxy-Token", _proxyToken);

            var reg = ct.Register(() => { try { uwr.Abort(); } catch (Exception) { /* already disposed */ } });
            uwr.SendWebRequest().completed += _ =>
            {
                reg.Dispose();
                try
                {
                    if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct);
                    else if (uwr.result == UnityWebRequest.Result.ConnectionError)
                        tcs.TrySetException(new LlmTransportException(
                            uwr.error != null && uwr.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                                ? $"timed out after {_timeoutSeconds}s"
                                : $"proxy unreachable at {_baseUrl} ({uwr.error}). Is proxy/server.py running?"));
                    else
                        tcs.TrySetResult(ProxyProtocol.ParseResponse(uwr.downloadHandler.text, uwr.responseCode));
                }
                catch (Exception e) { tcs.TrySetException(e); }
                finally { uwr.Dispose(); }
            };
            return tcs.Task;
        }

        /// <summary>GET /health. Returns the model name, or null if the proxy isn't reachable.</summary>
        public void CheckHealth(Action<string> onResult)
        {
            var uwr = UnityWebRequest.Get(_baseUrl + "/health");
            uwr.timeout = 3;
            uwr.SendWebRequest().completed += _ =>
            {
                string model = null;
                if (uwr.result == UnityWebRequest.Result.Success)
                {
                    try { model = (string)Newtonsoft.Json.Linq.JObject.Parse(uwr.downloadHandler.text)["model"]; }
                    catch (Exception) { model = "unknown"; }
                }
                uwr.Dispose();
                onResult(model);
            };
        }
    }
}
