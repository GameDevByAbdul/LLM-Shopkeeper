# Grizelda's Curios: a guardrailed LLM haggling NPC

[![CI](https://github.com/GameDevByAbdul/LLM-Shopkeeper/actions/workflows/ci.yml/badge.svg)](https://github.com/GameDevByAbdul/LLM-Shopkeeper/actions/workflows/ci.yml)

A Unity 6 demo where you haggle with a shopkeeper NPC driven by an LLM. The model only *proposes* a turn as structured JSON (`reply`, `mood`, `offered_price`, `intent`). A **deterministic C# validation layer** decides what actually happens. It enforces the schema, clamps prices to design-set bounds, blocks unsafe or off-theme text, and falls back to scripted lines when the output can't be trusted. An in-game debugger shows raw and validated output side by side. An offline simulator runs batches of scripted negotiations to measure **how generation parameters shift the game's economy**.

![Guardrail debugger: the model tried to sell for 1 gold; the validator clamped it to the 80 gold floor and rewrote the line](docs/img/guardrail_price_floor.png)

> 🎬 **Demo video (90 s):** https://youtu.be/5t3RLhy_cWE

## What this demonstrates

| Feature | Where |
|---|---|
| LLM returns structured JSON and never touches game state | [`NegotiationSession`](Assets/Shopkeeper/Core/NegotiationSession.cs) owns all state; it mutates only from a validated [`ShopkeeperTurn`](Assets/Shopkeeper/Core/ValidationModels.cs) |
| Deterministic validation: schema, price clamps, unsafe/off-theme text, scripted fallback | [`ResponseValidator`](Assets/Shopkeeper/Core/ResponseValidator.cs), [`TextGuard`](Assets/Shopkeeper/Core/TextGuard.cs), [`design.json`](Assets/StreamingAssets/Shopkeeper/design.json) |
| In-game debug panel: raw vs validated, what the guardrail changed | [`GuardrailDebugPanel`](Assets/Shopkeeper/Runtime/GuardrailDebugPanel.cs) (F1), with one-shot **fault injection** to demo every rule on demand |
| Tunable temperature and personality presets; 50 scripted negotiations per setting | [`tools/BalanceSim`](tools/BalanceSim/Program.cs) → [`balance/`](balance/) |
| API key never in the client; low latency; loading state | [`proxy/server.py`](proxy/server.py) (stdlib Python), hedged requests, "thinking" state on UI and puppet |

## Architecture

```mermaid
flowchart LR
  subgraph Client["Unity client (no secrets)"]
    UI["ShopUI + GuardrailDebugPanel"] --> Ctrl["ShopDemoController"]
    Ctrl --> Sess["NegotiationSession<br/>owns ALL game state"]
    Sess --> PB["PromptBuilder"]
    PB --> T["UnityWebRequestTransport<br/>wrapped by FaultInjectingTransport"]
    T -- "raw model text" --> V["ResponseValidator<br/>parse, schema, rules, text"]
    V -- "validated ShopkeeperTurn" --> Sess
    Sess --> View["Puppet mood + UI"]
  end
  D[["design.json<br/>prices, personas, denylists, fallback lines"]] --> Sess
  T -- "HTTP 127.0.0.1:8787<br/>provider-agnostic JSON" --> PX
  subgraph Proxy["proxy/server.py (holds the API key)"]
    PX["validate + cap request"] --> RH["retry, hedge to fallback model"]
  end
  RH -- "key in header, never URL" --> LLM[("Gemini API")]
  subgraph Offline["Offline tooling"]
    Sim["tools/BalanceSim<br/>scripted buyers"]
    Tests["59 NUnit tests<br/>Unity EditMode + dotnet test"]
  end
  Sim -- "same Core source files" --> Sess
  Sim -- HTTP --> PX
  Tests --> V
```

**One turn, end to end**

1. The player types or picks a line. **Game code** (not the model) parses any offer from it: digits or words, e.g. "seventy-five" → 75 ([`OfferParser`](Assets/Shopkeeper/Core/OfferParser.cs)).
2. [`PromptBuilder`](Assets/Shopkeeper/Core/PromptBuilder.cs) sends persona + item + an authoritative `GAME STATE` line + the fenced player text. History replays **validated** turns only, so the model never builds on its own rejected output. The hard min/max prices are never shown to the model.
3. The proxy adds the key, enforces caps (tokens ≤ 400, temperature 0–2, payload ≤ 64 KB), and calls the model with JSON-schema constrained decoding.
4. [`ResponseValidator`](Assets/Shopkeeper/Core/ResponseValidator.cs) runs four deterministic stages and records every change as a `GuardrailFix`.
5. Only the validated turn reaches the state machine, the UI and the puppet animation.

**Core is engine-free by design.** `Shopkeeper.Core` has `noEngineReferences: true`, so the exact same source files compile into Unity, into `dotnet test`, and into the balance simulator. The sim therefore measures the guardrail the game ships, not a copy of it.

## The guardrail

Constrained decoding (JSON schema) guarantees *shape*, not *meaning*, so validation happens either way. Every rule is a pure function of (raw text, game state):

| Stage | Rule id | Severity | What it does |
|---|---|---|---|
| Parse | `code_fence_stripped`, `json_extracted_from_prose` | info/warn | Repairs ```` ```json ```` fences and chatter around the object |
| | `malformed_json`, `empty_response` | **blocked** | Strict parse (no duplicate keys, no trailing content) fails → scripted fallback that holds the price |
| Schema | `schema_violation` | **blocked** | Missing/invalid `reply` or `intent` → fallback |
| | `enum_normalized`, `mood_defaulted` | info/warn | `"Counter-Offer"` → `counter`, `"irritated"` → `annoyed`; unknown mood → neutral |
| | `price_type_coerced`, `price_rounded`, `price_missing` | warn | `"100 gold"` → 100, 99.6 → 100, missing → hold current ask |
| Rules | `price_below_floor`, `price_above_ceiling` | **blocked** | Clamp to design min/max |
| | `accept_below_floor` | **blocked** | Model agreed to sell under the floor → counter at the floor |
| | `ask_increase_blocked` | warn | Asking price can't go up mid-haggle (design flag) |
| | `counter_at_or_below_offer`, `offer_meets_ask` | warn | Countering at or under the player's offer is really an accept |
| | `accept_without_offer`, `walk_away_too_early` | warn | Intent inconsistent with game state → downgraded |
| Text | `unsafe_text`, `off_theme_text` | **blocked** | Data-driven denylists (profanity/threats; "as an AI", real-world money, URLs…) |
| | `price_text_mismatch` | warn | Any price *spoken* in the reply (digits **or words**, e.g. "one hundred and twenty") must equal the validated price, the player's offer, the previous ask or the list price |
| | `format_leak`, `text_contradicts_fixed_turn` | warn | JSON/field names in dialogue, or text that no longer matches a corrected intent → scripted line for the corrected intent |
| | `reply_truncated` | info | Cut at a sentence boundary to the UI budget |
| Transport | `transport_error` | **blocked** | Timeout / proxy down → fallback, the game keeps going |

Text fixes replace only the line, using a hand-written template for the *corrected* intent and price, so what Grizelda says always matches what the game does. Tests include a 2,000-case randomized sweep asserting that no model output can push a price outside the bounds or close an accept without an offer ([tests](Assets/Shopkeeper/Tests/EditMode/ResponseValidatorTests.cs)).

| | |
|---|---|
| ![Jailbreak attempt](docs/img/jailbreak.png) | ![Malformed JSON falls back](docs/img/malformed_json.png) |
| A live jailbreak attempt: the model stays in character, and the price stays at the floor anyway | Injected malformed JSON → `REJECTED: scripted fallback used`; the haggle continues |

## Balance results

![Balance results by persona and temperature](balance/balance_chart.svg)

**Setup.** 3 personas × 3 temperatures (0.2 / 1.0 / 1.8), item: Enchanted Lantern (list 120, hidden hard floor 80). The same 7 scripted buyers face every setting (paired design: same seeds, budgets, opening offers and concession steps), so differences come from the model, not the customers. That's 63 negotiations and 330 LLM turns on `gemini-3.1-flash-lite` with JSON-schema output. The run stopped at the free tier's 500 requests/day cap. Only buyers that completed in all 9 settings are reported; 15 negotiations hit quota errors and were excluded.

| Persona | Temp | n | Deal rate | Avg final price (±95% CI) | % of list | Revenue / customer | Avg rounds | Floor clamps / turn | Text replaced / turn | Fallbacks / turn |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| stingy | 0.2 | 7 | 57% | 100.3 ± 7.4 | 84% | 57.3 | 6.6 | 0% | 0% | 0% |
| stingy | 1.0 | 7 | 43% | 104.3 ± 5.8 | 87% | 44.7 | 6.6 | 0% | 2% | 0% |
| stingy | 1.8 | 7 | 43% | 106.0 ± 6.0 | 88% | 45.4 | 6.9 | 0% | 0% | 0% |
| fair | 0.2 | 7 | 100% | 82.7 ± 2.0 | 69% | 82.7 | 4.6 | 19% | 19% | 0% |
| fair | 1.0 | 7 | 100% | 81.6 ± 1.5 | 68% | 81.6 | 4.3 | 27% | 27% | 0% |
| fair | 1.8 | 7 | 100% | 82.0 ± 1.7 | 68% | 82.0 | 4.0 | 18% | 18% | 0% |
| generous | 0.2 | 7 | 100% | 80.0 ± 0.0 | 67% | 80.0 | 4.9 | 71% | 71% | 0% |
| generous | 1.0 | 7 | 100% | 80.0 ± 0.0 | 67% | 80.0 | 4.7 | 67% | 67% | 0% |
| generous | 1.8 | 7 | 100% | 80.0 ± 0.0 | 67% | 80.0 | 4.7 | 67% | 67% | 0% |

Raw data: [`balance/runs.csv`](balance/runs.csv) (one row per negotiation), [`balance/summary.csv`](balance/summary.csv).

**What it shows**

1. **The persona prompt moves the economy far more than temperature does.** Stingy sells at 84–88% of list, but only 43–57% of customers buy. Fair and Generous close every deal at 67–69% of list.
2. **The "highest price" persona earns the least.** Revenue per customer is about 45–57 gold for Stingy versus about 82 for Fair. A designer tuning a shopkeeper by sale price alone would pick the wrong personality. That's the kind of question this sim exists to answer before players do.
3. **For Generous, the design floor, not the model, sets the price.** In 67–71% of its turns the model tried to sell below 80 gold (`accept_below_floor`), and every Generous sale landed exactly on the floor. Without the validator, this persona would leak the economy. With it, the floor becomes the real balance lever.
4. **Constrained decoding fixed the *shape*, not the *meaning*.** Fallbacks were 0%: every response was valid JSON. Yet the validator still intervened on up to 71% of turns, all of them semantic (prices, intent, spoken numbers). Schema mode alone isn't a guardrail.
5. **Temperature had no effect within the noise at this sample size.** Stingy's average price drifts up 100 → 104 → 106 as temperature rises, but the confidence intervals overlap. Telling a real effect apart would take the full 50 buyers per setting, which `--resume` completes after the daily quota resets.

## Latency

- **Model choice from measurement.** I benchmarked the available flash-tier models in JSON mode. `gemini-3.5-flash-lite` with `thinkingLevel: minimal` gave ~1.0–1.6 s per turn. `3.1-flash-lite` ranged from 1.5 to 10 s under load, and the flagship flash model returned 503 "high demand".
- **Hedged requests.** If the primary hasn't answered within 2.5 s, the proxy races the same request on the fallback model and returns whichever answers first. That trades a little extra spend on slow calls for a much tighter tail. Demo session: typical turns 0.9–2.6 s; the slowest hedged turn was 4.6 s.
- **Small, bounded prompts.** About 750–1,050 input tokens, ~70 output tokens, and history capped at 6 turns.
- **Warm connections.** Keep-alive TLS connection per proxy thread; the key goes in a header.
- **No streaming, deliberately.** Text can't be shown before it's validated, or the guardrail would be cosmetic. Instead, the UI shows a "Grizelda is thinking…" state with a timer, inputs are disabled, and the puppet plays a head-tilt thinking idle.
- **Failures are bounded.** 15 s client timeout → scripted fallback. Player cancel (Leave) aborts the request and rolls back the round.

## Keeping the key out of the client

- The key lives in `proxy/.env` (gitignored; template: [`proxy/.env.example`](proxy/.env.example)). Unity only knows `http://127.0.0.1:8787`.
- The proxy binds to loopback only, and refuses to bind elsewhere unless `PROXY_CLIENT_TOKEN` is set.
- The key is sent upstream in the `x-goog-api-key` header, never in the URL. It's never logged or echoed (covered by a test), and errors never return stack traces.
- The proxy enforces caps, so a modified client can't burn the quota with huge requests.
- CI fails if anything that looks like a Google API key is committed.
- **For production** I'd move the prompt templates server-side, so the client sends only `{persona_id, item_id, player_line, session_id}` and can't repurpose the endpoint. I'd add per-player auth and rate limits, plus a moderation model behind the deterministic denylist.

## Run it

**Requirements:** Unity 6000.3, Python 3.10+, a Gemini API key (or none: there's a mock mode), and the .NET 10 SDK for tests and the sim.

```bash
# 1. Proxy (stdlib only, nothing to pip install)
cp proxy/.env.example proxy/.env      # paste your key into LLM_API_KEY
python proxy/server.py                # or: LLM_PROVIDER=mock python proxy/server.py  (offline)

# 2. Unity: open the project, then menu  Shopkeeper > Build Demo Scene  (generates Assets/Scenes/ShopDemo.unity)
#    Press Play. F1 toggles the guardrail debugger.

# 3. Tests (same 59 tests also appear in Unity's Test Runner, EditMode)
dotnet test tools/Core.Tests
python -m unittest proxy/test_server.py

# 4. Balance sim (free tier: keep --rpm under your per-model quota; --resume continues an interrupted run)
dotnet run --project tools/BalanceSim -- --runs 50 --temps 0.2,1.0,1.8 --rpm 14 --resume
python tools/plot_balance.py
```

Headless build + smoke test: `Unity -batchmode -projectPath . -executeMethod Shopkeeper.EditorTools.BuildTools.BuildAllFromCommandLine`, then run `Builds/Windows/ShopkeeperDemo.exe -autopilot <dir>`. It plays a scripted haggle with fault injections against the live proxy and saves screenshots plus a log.

## Repo layout

```
Assets/Shopkeeper/Core/         engine-free C#: validator, text guard, prompt builder, session, transports, scripted buyer
Assets/Shopkeeper/Runtime/      Unity: controller, uGUI built in code, IMGUI debugger, puppet, UnityWebRequest transport, autopilot
Assets/Shopkeeper/Editor/       scene generator, build script
Assets/Shopkeeper/Tests/        NUnit tests (run in Unity and in dotnet)
Assets/StreamingAssets/Shopkeeper/design.json   designer data: items + price bounds, personas, denylists, fallback lines
proxy/                          local LLM proxy + tests
tools/BalanceSim/               balance simulator (compiles Core sources directly)
tools/Core.Tests/               dotnet test runner for the Unity tests
tools/plot_balance.py           chart generator (stdlib SVG)
balance/                        experiment outputs (raw per-turn logs are gitignored)
```

## Trade-offs and next steps

- **Denylists are a floor, not a ceiling.** They're deterministic and auditable but blunt. Next would be a moderation classifier and an in-world "did she stay in character" judge, used offline in the sim to track drift.
- **The price-mention heuristic is conservative.** When unsure, it swaps in a scripted line. The sim's `text replaced / turn` column measures that cost directly.
- **The puppet is primitives** on purpose, behind a `SetMood/React/SetThinking` interface that a rigged character with blend shapes could implement unchanged.
- **More balance axes:** run the sim per item and per buyer archetype. Add a "regret" metric (how far below the buyer's budget they paid) and run the experiment across models.
