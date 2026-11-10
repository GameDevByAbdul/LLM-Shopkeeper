Item: **Enchanted Lantern** (list 120, hard bounds 80..150). Up to 7 negotiations per setting; the same scripted buyers (seeds 1000..1006) face every setting. Model: gemini-3.1-flash-lite. 330 LLM turns. 0 negotiation(s) excluded for transport/quota errors. (Latency is omitted: sim round trips include client-side rate-limit queueing.)

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

Guardrail rules that fired (all settings): `accept_below_floor` x67, `text_contradicts_fixed_turn` x67, `price_text_mismatch` x21, `price_below_floor` x20
