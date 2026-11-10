"""
Render balance/summary.csv as balance/balance_chart.svg (stdlib only, no matplotlib).

Three small multiples sharing the x axis (temperature), one line per persona:
  1. average sale price (deals only, with 95% CI) against the design floor and list price
  2. deal rate
  3. how often the validator had to enforce the price floor (per LLM turn)

Usage: python tools/plot_balance.py [balance/summary.csv] [balance/balance_chart.svg]
"""
from __future__ import annotations

import csv
import json
import sys
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
SRC = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "balance" / "summary.csv"
DST = Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "balance" / "balance_chart.svg"

# Validated categorical slots 1-3 (light / dark), see README "Balance results".
SERIES = {
    "stingy": ("#2a78d6", "#3987e5", "circle"),
    "fair": ("#eb6834", "#d95926", "square"),
    "generous": ("#1baf7a", "#199e70", "diamond"),
}

W, H = 1110, 420
PANEL_W, PANEL_H = 268, 250
TOP, LEFT, GAP = 120, 70, 90


def load():
    rows = list(csv.DictReader(SRC.open(encoding="utf-8")))
    design = json.loads((ROOT / "Assets/StreamingAssets/Shopkeeper/design.json").read_text(encoding="utf-8"))
    item = next(i for i in design["items"] if i["id"] == "lantern")
    return rows, item


def f(row, key):
    v = row.get(key, "")
    return float(v) if v not in ("", "-", "NaN") else None


def marker(shape, x, y, cls, tip):
    t = f"<title>{escape(tip)}</title>"
    if shape == "circle":
        return f'<circle class="{cls} mk" cx="{x:.1f}" cy="{y:.1f}" r="5">{t}</circle>'
    if shape == "square":
        return f'<rect class="{cls} mk" x="{x - 4.5:.1f}" y="{y - 4.5:.1f}" width="9" height="9" rx="1.5">{t}</rect>'
    return f'<path class="{cls} mk" d="M{x:.1f},{y - 6:.1f} L{x + 6:.1f},{y:.1f} L{x:.1f},{y + 6:.1f} L{x - 6:.1f},{y:.1f} Z">{t}</path>'


def nice_ticks(lo, hi, n=5):
    """Round tick step (1/2/2.5/5 x 10^k) covering [lo, hi]; returns (lo, hi, ticks)."""
    import math
    raw = (hi - lo) / (n - 1)
    mag = 10 ** math.floor(math.log10(raw))
    step = next(m * mag for m in (1, 2, 2.5, 5, 10) if m * mag >= raw)
    lo2, hi2 = math.floor(lo / step) * step, math.ceil(hi / step) * step
    ticks, v = [], lo2
    while v <= hi2 + 1e-9:
        ticks.append(round(v, 10)); v += step
    return lo2, hi2, ticks


def panel(idx, title, subtitle, rows, temps, key, lo, hi, fmt, refs=(), ci_key=None, label_ends=False):
    x0 = LEFT + idx * (PANEL_W + GAP)
    y0 = TOP
    lo, hi, ticks = nice_ticks(lo, hi)
    sx = lambda t: x0 + 20 + (temps.index(t) / max(1, len(temps) - 1)) * (PANEL_W - 40)
    sy = lambda v: y0 + PANEL_H - (v - lo) / (hi - lo) * PANEL_H
    out = [f'<text class="t2" x="{x0}" y="{y0 - 34}">{escape(title)}</text>',
           f'<text class="t3" x="{x0}" y="{y0 - 16}">{escape(subtitle)}</text>']

    # Recessive grid + y labels (4 ticks).
    for v in ticks:
        y = sy(v)
        out.append(f'<line class="grid" x1="{x0}" x2="{x0 + PANEL_W}" y1="{y:.1f}" y2="{y:.1f}"/>')
        out.append(f'<text class="t3" x="{x0 - 8}" y="{y + 4:.1f}" text-anchor="end">{fmt(v)}</text>')
    for t in temps:
        out.append(f'<text class="t3" x="{sx(t):.1f}" y="{y0 + PANEL_H + 20}" text-anchor="middle">T {t:g}</text>')

    for value, label in refs:
        y = sy(value)
        out.append(f'<line class="ref" x1="{x0}" x2="{x0 + PANEL_W}" y1="{y:.1f}" y2="{y:.1f}"/>')
        out.append(f'<text class="t3 refl" x="{x0 + 4}" y="{y + 15:.1f}">{escape(label)}</text>')

    ends = []
    for persona, (_, _, shape) in SERIES.items():
        pts = []
        for t in temps:
            r = next((r for r in rows if r["persona"] == persona and float(r["temperature"]) == t), None)
            v = f(r, key) if r else None
            pts.append((t, v, r))
        # Break the line at missing points rather than bridging over them.
        d, pen = [], False
        for t, v, _ in pts:
            if v is None:
                pen = False
                continue
            d.append(f"{'L' if pen else 'M'}{sx(t):.1f},{sy(v):.1f}")
            pen = True
        pts = [p for p in pts if p[1] is not None]
        if not pts:
            continue
        out.append(f'<path class="s-{persona} ln" d="{" ".join(d)}"/>')
        for t, v, r in pts:
            if ci_key and f(r, ci_key):
                ci = f(r, ci_key)
                out.append(f'<line class="s-{persona} ci" x1="{sx(t):.1f}" x2="{sx(t):.1f}" y1="{sy(min(hi, v + ci)):.1f}" y2="{sy(max(lo, v - ci)):.1f}"/>')
            tip = f"{persona}, T {t:g}: {fmt(v)} (n={r['n']})"
            out.append(marker(shape, sx(t), sy(v), f"s-{persona}", tip))
        if label_ends:
            t, v, _ = pts[-1]
            ends.append([sy(v) + 4, sx(t) + 10, persona])
    # Direct labels: nudge apart so they never overlap.
    ends.sort()
    for i in range(1, len(ends)):
        ends[i][0] = max(ends[i][0], ends[i - 1][0] + 15)
    for y, x, persona in ends:
        out.append(f'<text class="t2v" x="{x:.1f}" y="{y:.1f}">{persona}</text>')
    return out


def main():
    rows, item = load()
    temps = sorted({float(r["temperature"]) for r in rows})
    prices = [f(r, "avg_final_price") for r in rows if f(r, "avg_final_price") is not None]
    p_lo = min(item["min_price"] - 10, min(prices) - 5)
    p_hi = max(item["list_price"] + 5, max(prices) + 5)
    clamp_hi = max(0.1, max(f(r, "floor_clamp_rate") or 0 for r in rows) * 1.15)
    n_total = sum(int(r["n"]) for r in rows)

    light = "".join(f".s-{p}{{--c:{c[0]}}}" for p, c in SERIES.items())
    dark = "".join(f".s-{p}{{--c:{c[1]}}}" for p, c in SERIES.items())
    css = f"""
    svg{{--surface:#fcfcfb;--ink:#0b0b0b;--ink2:#52514e;--ink3:#7a7974;--grid:#e8e7e3;--ref:#b9b7b0}}
    {light}
    @media (prefers-color-scheme: dark){{svg{{--surface:#1a1a19;--ink:#f0efec;--ink2:#c3c2b7;--ink3:#8f8e87;--grid:#2c2c2a;--ref:#55544f}} {dark}}}
    .bg{{fill:var(--surface)}} text{{font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif}}
    .t1{{font-size:20px;font-weight:600;fill:var(--ink)}} .t2{{font-size:15px;font-weight:600;fill:var(--ink)}}
    .t2v{{font-size:13px;font-weight:600;fill:var(--ink2)}} .t3{{font-size:12px;fill:var(--ink3)}} .refl{{fill:var(--ink2)}}
    .grid{{stroke:var(--grid);stroke-width:1}} .ref{{stroke:var(--ref);stroke-width:1.5;stroke-dasharray:5 4}}
    .ln{{fill:none;stroke:var(--c);stroke-width:2;stroke-linejoin:round}} .ci{{stroke:var(--c);stroke-width:2;stroke-linecap:round;opacity:.55}}
    .mk{{fill:var(--c);stroke:var(--surface);stroke-width:2}}
    """
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" '
             f'aria-label="Balance results: sale price, deal rate and floor enforcement by persona and temperature">',
             f"<style>{css}</style>", f'<rect class="bg" width="{W}" height="{H}" rx="8"/>',
             f'<text class="t1" x="{LEFT - 40}" y="36">How persona and temperature move the haggling economy</text>',
             f'<text class="t3" x="{LEFT - 40}" y="56">{n_total} scripted negotiations over the {escape(item["name"])} '
             f'(list {item["list_price"]}, hard floor {item["min_price"]}). Same buyers in every setting. '
             f'Whiskers = 95% CI.</text>']

    # Legend (always present for >= 2 series).
    lx = W - 330
    for i, (p, (_, _, shape)) in enumerate(SERIES.items()):
        x = lx + i * 105
        parts.append(marker(shape, x, 32, f"s-{p}", p))
        parts.append(f'<text class="t2v" x="{x + 12}" y="36">{p}</text>')

    parts += panel(0, "Average sale price", "gold, completed deals only", rows, temps, "avg_final_price", p_lo, p_hi,
                   lambda v: f"{v:.0f}", refs=[(item["list_price"], f"list {item['list_price']}"),
                                                (item["min_price"], f"floor {item['min_price']} (validator)")],
                   ci_key="price_ci95", label_ends=True)
    parts += panel(1, "Deal rate", "share of customers who bought", rows, temps, "deal_rate", 0, 1,
                   lambda v: f"{v * 100:.0f}%", label_ends=True)
    parts += panel(2, "Floor enforcement", "turns where the validator raised the price", rows, temps, "floor_clamp_rate",
                   0, clamp_hi, lambda v: f"{v * 100:.0f}%", label_ends=True)
    parts.append("</svg>")
    DST.write_text("\n".join(parts), encoding="utf-8")
    print(f"wrote {DST}")


if __name__ == "__main__":
    main()
