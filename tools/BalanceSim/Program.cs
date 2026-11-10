using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Shopkeeper.Core;

namespace Shopkeeper.BalanceSim
{
    /// <summary>
    /// Runs N scripted negotiations per (persona x temperature) setting through the real proxy + real
    /// validator, and reports how generation parameters move the in-game economy.
    ///
    ///   dotnet run --project tools/BalanceSim -- --runs 50 --personas stingy,fair,generous --temps 0.2,0.8,1.4
    /// </summary>
    static class Program
    {
        sealed class Options
        {
            public string Proxy = "http://127.0.0.1:8787";
            public int Runs = 50;
            public string[] Personas = { "stingy", "fair", "generous" };
            public float[] Temps = { 0.2f, 0.8f, 1.4f };
            public string Item = "lantern";
            public int Concurrency = 8;
            public int SeedBase = 1000;
            public string Out = "balance";
            public string Design = "Assets/StreamingAssets/Shopkeeper/design.json";
            public double Rpm = 0;          // client-side throttle (free tiers: ~14)
            public bool Resume = false;     // skip negotiations already in raw/runs.jsonl
        }

        sealed class RunResult
        {
            public string Item; public string Persona; public float Temp; public int Run; public int Seed; public int Budget;
            public NegotiationState Outcome; public int? FinalPrice; public int Rounds; public int Turns;
            public int Fixes; public int Fallbacks; public int BlockedFixes; public int FloorClamps; public int TextReplaced;
            public int TransportErrors; public List<long> LatenciesMs = new List<long>();
            public Dictionary<string, int> RuleCounts = new Dictionary<string, int>();
            public HashSet<string> Models = new HashSet<string>();

            public bool Valid => TransportErrors == 0;
            public string Key => MakeKey(Persona, Temp, Run);
            public static string MakeKey(string p, float t, int run) => $"{p}|{t.ToString("0.00", CultureInfo.InvariantCulture)}|{run}";

            public JObject ToJson() => new JObject
            {
                ["item"] = Item, ["persona"] = Persona, ["temp"] = Temp, ["run"] = Run, ["seed"] = Seed, ["budget"] = Budget,
                ["outcome"] = Outcome.ToString(), ["final_price"] = FinalPrice, ["rounds"] = Rounds, ["turns"] = Turns,
                ["fixes"] = Fixes, ["fallbacks"] = Fallbacks, ["blocked"] = BlockedFixes, ["floor_clamps"] = FloorClamps,
                ["text_replaced"] = TextReplaced, ["transport_errors"] = TransportErrors,
                ["latencies_ms"] = new JArray(LatenciesMs), ["rules"] = JObject.FromObject(RuleCounts), ["models"] = new JArray(Models),
            };

            public static RunResult FromJson(JObject j) => new RunResult
            {
                Item = (string)j["item"], Persona = (string)j["persona"], Temp = (float)j["temp"], Run = (int)j["run"], Seed = (int)j["seed"],
                Budget = (int)j["budget"], Outcome = (NegotiationState)Enum.Parse(typeof(NegotiationState), (string)j["outcome"]),
                FinalPrice = (int?)j["final_price"], Rounds = (int)j["rounds"], Turns = (int)j["turns"], Fixes = (int)j["fixes"],
                Fallbacks = (int)j["fallbacks"], BlockedFixes = (int)j["blocked"], FloorClamps = (int)j["floor_clamps"],
                TextReplaced = (int)j["text_replaced"], TransportErrors = (int)j["transport_errors"],
                LatenciesMs = j["latencies_ms"].Select(x => (long)x).ToList(),
                RuleCounts = j["rules"].ToObject<Dictionary<string, int>>(), Models = new HashSet<string>(j["models"].Select(x => (string)x)),
            };
        }

        static async Task<int> Main(string[] args)
        {
            var o = Parse(args);
            string root = FindRepoRoot();
            var design = DesignConfig.FromJson(File.ReadAllText(Path.Combine(root, o.Design)));
            var item = design.GetItem(o.Item);
            var guard = new TextGuard(design.TextGuard);
            string outDir = Path.Combine(root, o.Out);
            Directory.CreateDirectory(Path.Combine(outDir, "raw"));

            using var http = new HttpProxyTransport(o.Proxy, TimeSpan.FromSeconds(30));
            ILlmTransport transport = new RetryingTransport(o.Rpm > 0 ? (ILlmTransport)new RateLimitedTransport(http, o.Rpm) : http, attempts: 5);

            var settings = (from p in o.Personas from t in o.Temps select (p, t)).ToList();
            Console.WriteLine($"Balance sim: {settings.Count} settings x {o.Runs} runs, item={item.Id} (list {item.ListPrice}, bounds {item.MinPrice}..{item.MaxPrice}), proxy={o.Proxy}");

            var rawLock = new object();
            string rawPath = Path.Combine(outDir, "raw", "turns.jsonl");
            string runsPath = Path.Combine(outDir, "raw", "runs.jsonl");
            var results = new List<RunResult>();
            if (o.Resume && File.Exists(runsPath))
            {
                results = File.ReadAllLines(runsPath).Where(l => l.Trim().Length > 0).Select(l => RunResult.FromJson(JObject.Parse(l)))
                    .Where(r => r.Item == item.Id && r.Valid && r.Run < o.Runs).ToList(); // --runs K on a stopped run = summarize the first K buyers
                Console.WriteLine($"Resuming: {results.Count} valid negotiations already done.");
            }
            else
            {
                File.WriteAllText(rawPath, "");
                File.WriteAllText(runsPath, "");
            }
            var doneKeys = new HashSet<string>(results.Select(r => r.Key));

            var gate = new SemaphoreSlim(o.Concurrency);
            var sw = Stopwatch.StartNew();
            int done = 0;

            // Interleave settings run-by-run, so a run cut short by quota leaves every setting with a similar N.
            var jobs = (from i in Enumerable.Range(0, o.Runs) from st in settings select (st.p, st.t, i))
                .Where(j => !doneKeys.Contains(RunResult.MakeKey(j.p, j.t, j.i))).ToList();
            int total = jobs.Count;
            Console.WriteLine($"{total} negotiations to run.");

            var tasks = new List<Task>();
            foreach (var (persona, temp, run) in jobs)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var r = await RunOne(design, item.Id, persona, temp, o.SeedBase + run, run, transport, guard, rawPath, rawLock);
                        lock (rawLock) File.AppendAllText(runsPath, r.ToJson().ToString(Newtonsoft.Json.Formatting.None) + "\n");
                        lock (results) results.Add(r);
                        int n = Interlocked.Increment(ref done);
                        if (n % 10 == 0 || n == total)
                            Console.WriteLine($"  {n}/{total} negotiations  ({sw.Elapsed.TotalSeconds:0}s)");
                    }
                    finally { gate.Release(); }
                }));
            }
            await Task.WhenAll(tasks);

            results = results.OrderBy(r => Array.IndexOf(o.Personas, r.Persona)).ThenBy(r => r.Temp).ThenBy(r => r.Run).ToList();
            WriteRunsCsv(Path.Combine(outDir, "runs.csv"), results);
            var summary = Summarize(results, item, o);
            WriteSummaryCsv(Path.Combine(outDir, "summary.csv"), summary);
            string md = SummaryMarkdown(summary, results, item, o, sw.Elapsed);
            File.WriteAllText(Path.Combine(outDir, "summary.md"), md);
            Console.WriteLine();
            Console.WriteLine(md);
            return 0;
        }

        static async Task<RunResult> RunOne(DesignConfig design, string itemId, string persona, float temp, int seed, int run,
                                            ILlmTransport transport, TextGuard guard, string rawPath, object rawLock)
        {
            var s = new NegotiationSession(design, itemId, persona, temp, seed, guard);
            var buyer = new ScriptedBuyer(s.Item, seed);
            var r = new RunResult { Item = itemId, Persona = persona, Temp = temp, Run = run, Seed = seed, Budget = buyer.Budget };

            for (int guardIter = 0; s.IsOpen && guardIter < 32; guardIter++)
            {
                var action = buyer.Next(s);
                if (action.Kind == BuyerActionKind.AcceptAsk) { s.AcceptAsk(); break; }
                if (action.Kind == BuyerActionKind.Leave) { s.Leave(); break; }

                var t = await s.SayAsync(action.Line, transport);
                r.Turns++;
                r.LatenciesMs.Add(t.RoundTripMs);
                if (t.TransportError != null) r.TransportErrors++;
                if (t.Response?.Model != null) r.Models.Add(t.Response.Model);
                var v = t.Validation;
                r.Fixes += v.Fixes.Count;
                if (v.UsedFallback) r.Fallbacks++;
                if (v.TextReplaced) r.TextReplaced++;
                if (v.HasRule("price_below_floor") || v.HasRule("accept_below_floor")) r.FloorClamps++;
                foreach (var f in v.Fixes)
                {
                    if (f.Severity == FixSeverity.Blocked) r.BlockedFixes++;
                    r.RuleCounts[f.Rule] = r.RuleCounts.TryGetValue(f.Rule, out int c) ? c + 1 : 1;
                }

                var line = new JObject
                {
                    ["persona"] = persona, ["temp"] = temp, ["run"] = run, ["round"] = t.Round,
                    ["player"] = t.PlayerText, ["offer"] = t.PlayerOffer, ["ask_before"] = t.AskBefore, ["ask_after"] = t.AskAfter,
                    ["raw"] = v.Raw, ["validated"] = v.Turn.ToJObject(), ["fixes"] = new JArray(v.Fixes.Select(f => f.ToString())),
                    ["ms"] = t.RoundTripMs, ["model"] = t.Response?.Model, ["error"] = t.TransportError,
                };
                lock (rawLock) File.AppendAllText(rawPath, line.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            }

            r.Outcome = s.State;
            r.FinalPrice = s.FinalPrice;
            r.Rounds = s.Round;
            return r;
        }

        // ---- reporting --------------------------------------------------------------------------

        sealed class Summary
        {
            public string Persona; public float Temp; public int N; public int Deals;
            public double DealRate, AvgPrice, PriceSd, PriceCi95, PctOfList, RevenuePerCustomer, AvgRounds;
            public double FixesPerTurn, FallbackRate, FloorClampRate, TextReplacedRate, ShopkeeperWalkRate;
            public long P50Ms, P95Ms; public int TransportErrors; public int Excluded;
        }

        static List<Summary> Summarize(List<RunResult> results, ItemDef item, Options o)
        {
            var list = new List<Summary>();
            foreach (var g in results.GroupBy(r => (r.Persona, r.Temp)))
            {
                int excluded = g.Count(r => !r.Valid);
                var rs = g.Where(r => r.Valid).ToList();
                if (rs.Count == 0) continue;
                var prices = rs.Where(r => r.Outcome == NegotiationState.Deal).Select(r => (double)r.FinalPrice.Value).ToList();
                double avg = prices.Count > 0 ? prices.Average() : double.NaN;
                double sd = prices.Count > 1 ? Math.Sqrt(prices.Sum(p => (p - avg) * (p - avg)) / (prices.Count - 1)) : 0;
                int turns = rs.Sum(r => r.Turns);
                var lat = rs.SelectMany(r => r.LatenciesMs).OrderBy(x => x).ToList();
                list.Add(new Summary
                {
                    Persona = g.Key.Persona, Temp = g.Key.Temp, N = rs.Count, Deals = prices.Count,
                    DealRate = (double)prices.Count / rs.Count,
                    AvgPrice = avg, PriceSd = sd, PriceCi95 = prices.Count > 1 ? 1.96 * sd / Math.Sqrt(prices.Count) : 0,
                    PctOfList = avg / item.ListPrice,
                    RevenuePerCustomer = rs.Average(r => r.Outcome == NegotiationState.Deal ? r.FinalPrice.Value : 0),
                    AvgRounds = rs.Average(r => r.Rounds),
                    FixesPerTurn = turns > 0 ? (double)rs.Sum(r => r.Fixes) / turns : 0,
                    FallbackRate = turns > 0 ? (double)rs.Sum(r => r.Fallbacks) / turns : 0,
                    FloorClampRate = turns > 0 ? (double)rs.Sum(r => r.FloorClamps) / turns : 0,
                    TextReplacedRate = turns > 0 ? (double)rs.Sum(r => r.TextReplaced) / turns : 0,
                    ShopkeeperWalkRate = (double)rs.Count(r => r.Outcome == NegotiationState.ShopkeeperWalkedAway) / rs.Count,
                    P50Ms = Pct(lat, 0.50), P95Ms = Pct(lat, 0.95),
                    TransportErrors = g.Sum(r => r.TransportErrors), Excluded = excluded,
                });
            }
            return list.OrderBy(s => Array.IndexOf(o.Personas, s.Persona)).ThenBy(s => s.Temp).ToList();
        }

        static long Pct(List<long> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(p * sorted.Count))];

        static string F(double v, string fmt = "0.0") => double.IsNaN(v) ? "-" : v.ToString(fmt, CultureInfo.InvariantCulture);
        static string P(double v) => double.IsNaN(v) ? "-" : (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

        static void WriteRunsCsv(string path, List<RunResult> rs)
        {
            var sb = new StringBuilder("persona,temperature,run,seed,buyer_budget,outcome,final_price,rounds,turns,fixes,blocked_fixes,floor_clamps,text_replaced,fallbacks,transport_errors,mean_latency_ms,models,rules\n");
            foreach (var r in rs)
                sb.AppendLine(string.Join(",", r.Persona, F(r.Temp, "0.0"), r.Run, r.Seed, r.Budget, r.Outcome, r.FinalPrice?.ToString() ?? "",
                    r.Rounds, r.Turns, r.Fixes, r.BlockedFixes, r.FloorClamps, r.TextReplaced, r.Fallbacks, r.TransportErrors,
                    r.LatenciesMs.Count > 0 ? (long)r.LatenciesMs.Average() : 0, string.Join("|", r.Models),
                    string.Join("|", r.RuleCounts.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}"))));
            File.WriteAllText(path, sb.ToString());
        }

        static void WriteSummaryCsv(string path, List<Summary> ss)
        {
            var sb = new StringBuilder("persona,temperature,n,excluded,deals,deal_rate,avg_final_price,price_sd,price_ci95,pct_of_list,revenue_per_customer,avg_rounds,fixes_per_turn,floor_clamp_rate,text_replaced_rate,fallback_rate,shopkeeper_walk_rate,p50_ms,p95_ms,transport_errors\n");
            foreach (var s in ss)
                sb.AppendLine(string.Join(",", s.Persona, F(s.Temp), s.N, s.Excluded, s.Deals, F(s.DealRate, "0.000"), F(s.AvgPrice, "0.00"), F(s.PriceSd, "0.00"),
                    F(s.PriceCi95, "0.00"), F(s.PctOfList, "0.000"), F(s.RevenuePerCustomer, "0.00"), F(s.AvgRounds, "0.00"), F(s.FixesPerTurn, "0.000"),
                    F(s.FloorClampRate, "0.000"), F(s.TextReplacedRate, "0.000"), F(s.FallbackRate, "0.000"), F(s.ShopkeeperWalkRate, "0.000"), s.P50Ms, s.P95Ms, s.TransportErrors));
            File.WriteAllText(path, sb.ToString());
        }

        static string SummaryMarkdown(List<Summary> ss, List<RunResult> rs, ItemDef item, Options o, TimeSpan elapsed)
        {
            var sb = new StringBuilder();
            var models = string.Join(", ", rs.SelectMany(r => r.Models).Distinct());
            int excluded = rs.Count(r => !r.Valid);
            sb.AppendLine($"Item: **{item.Name}** (list {item.ListPrice}, hard bounds {item.MinPrice}..{item.MaxPrice}). " +
                          $"Up to {o.Runs} negotiations per setting; the same scripted buyers (seeds {o.SeedBase}..{o.SeedBase + o.Runs - 1}) face every setting. " +
                          $"Model: {models}. {rs.Sum(r => r.Turns)} LLM turns. " +
                          $"{excluded} negotiation(s) excluded for transport/quota errors. (Latency is omitted: sim round trips include client-side rate-limit queueing.)");
            sb.AppendLine();
            sb.AppendLine("| Persona | Temp | n | Deal rate | Avg final price (±95% CI) | % of list | Revenue / customer | Avg rounds | Floor clamps / turn | Text replaced / turn | Fallbacks / turn |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var s in ss)
                sb.AppendLine($"| {s.Persona} | {F(s.Temp)} | {s.N} | {P(s.DealRate)} | {F(s.AvgPrice)} ± {F(s.PriceCi95)} | {P(s.PctOfList)} | {F(s.RevenuePerCustomer)} | {F(s.AvgRounds)} | {P(s.FloorClampRate)} | {P(s.TextReplacedRate)} | {P(s.FallbackRate)} |");
            sb.AppendLine();
            var rules = rs.Where(r => r.Valid).SelectMany(r => r.RuleCounts).GroupBy(k => k.Key).Select(g => (g.Key, g.Sum(x => x.Value))).OrderByDescending(x => x.Item2).ToList();
            sb.AppendLine("Guardrail rules that fired (all settings): " + (rules.Count == 0 ? "none" : string.Join(", ", rules.Select(x => $"`{x.Key}` x{x.Item2}"))));
            return sb.ToString();
        }

        // ---- plumbing ---------------------------------------------------------------------------

        /// <summary>The sim retries transport failures so network noise doesn't masquerade as model behavior.</summary>
        sealed class RetryingTransport : ILlmTransport
        {
            readonly ILlmTransport _inner; readonly int _attempts;
            public RetryingTransport(ILlmTransport inner, int attempts) { _inner = inner; _attempts = attempts; }

            public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
            {
                for (int a = 1; ; a++)
                {
                    try { return await _inner.CompleteAsync(request, ct); }
                    catch (Exception e) when (a < _attempts && !(e is OperationCanceledException && ct.IsCancellationRequested))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(40, 5 * Math.Pow(2, a - 1))), ct);
                    }
                }
            }
        }

        /// <summary>Spaces calls evenly to stay under a requests-per-minute quota (cheaper than eating 429s).</summary>
        sealed class RateLimitedTransport : ILlmTransport
        {
            readonly ILlmTransport _inner; readonly TimeSpan _interval;
            readonly SemaphoreSlim _lock = new SemaphoreSlim(1);
            DateTime _next = DateTime.MinValue;
            public RateLimitedTransport(ILlmTransport inner, double rpm) { _inner = inner; _interval = TimeSpan.FromSeconds(60.0 / rpm); }

            public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
            {
                await _lock.WaitAsync(ct);
                try
                {
                    var now = DateTime.UtcNow;
                    if (_next > now) await Task.Delay(_next - now, ct);
                    _next = DateTime.UtcNow + _interval;
                }
                finally { _lock.Release(); }
                return await _inner.CompleteAsync(request, ct);
            }
        }

        static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string v = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--proxy": o.Proxy = v; i++; break;
                    case "--runs": o.Runs = int.Parse(v); i++; break;
                    case "--personas": o.Personas = v.Split(','); i++; break;
                    case "--temps": o.Temps = v.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); i++; break;
                    case "--item": o.Item = v; i++; break;
                    case "--concurrency": o.Concurrency = int.Parse(v); i++; break;
                    case "--seed-base": o.SeedBase = int.Parse(v); i++; break;
                    case "--out": o.Out = v; i++; break;
                    case "--rpm": o.Rpm = double.Parse(v, CultureInfo.InvariantCulture); i++; break;
                    case "--resume": o.Resume = true; break;
                    default: throw new ArgumentException("unknown option " + args[i]);
                }
            }
            return o;
        }

        static string FindRepoRoot()
        {
            string dir = Directory.GetCurrentDirectory();
            while (dir != null && !File.Exists(Path.Combine(dir, "Assets", "StreamingAssets", "Shopkeeper", "design.json")))
                dir = Path.GetDirectoryName(dir);
            return dir ?? throw new DirectoryNotFoundException("Run from inside the repo.");
        }
    }
}
