"""
発表用：L2（LLM が戻り先を選ぶ）の選び方を図にする。LLM は呼ばない（記録済みのファイルだけを使う）。

使い方:
    python analysis/plot_llm_slides.py --run QaResults/20261115_200704 \
        --probe QaResults/probe_position_20261119 \
        --out QaResults/slides_20261119

出力:
    fig_choice_position.png  LLM が選んだ番号（0〜7）の回数。候補はシャッフル済みなので本来は均等
    fig_pick_least_seen.png  一番見られていない候補を選んだ率（LLM と UniformGoExplore）
    fig_position_probe.png   並び替えテスト（同じ候補を選び続けたか）と中身なしテスト（選ばれた番号）
"""
import argparse
from pathlib import Path

import matplotlib.pyplot as plt
import pandas as pd

COLORS = ["#2a6fdb", "#e07a1f", "#3a9d5d", "#b8417a", "#6b5bd1", "#8c8c8c"]
GRAY = "#8c8c8c"
VARIANT_LABEL = {"baseline": "元のプロンプト", "letters": "番号を A〜H に", "order_note": "「順番に意味はない」と追記"}

plt.rcParams["font.family"] = ["Hiragino Sans", "sans-serif"]
plt.rcParams["axes.spines.top"] = False
plt.rcParams["axes.spines.right"] = False


def choice_position(llm, uni, out):
    """LLM とでたらめ（UniformGoExplore）が選んだ番号の回数を並べる。"""
    c_llm = llm["choice"].value_counts().reindex(range(8), fill_value=0)
    c_uni = uni["choice"].value_counts().reindex(range(8), fill_value=0)
    even = len(llm) / 8
    fig, ax = plt.subplots(figsize=(7, 4))
    w = 0.38
    ax.bar([x - w / 2 for x in range(8)], c_llm.values, width=w, color=COLORS[0], label="LLM")
    ax.bar([x + w / 2 for x in range(8)], c_uni.values, width=w, color=GRAY, label="候補からでたらめ（Uniform）")
    ax.axhline(even, color=GRAY, ls="--", lw=1.2)
    ax.text(0.6, even + 1, f"均等なら {even:.1f} 回", ha="left", va="bottom", color=GRAY, fontsize=10)
    for x, v in zip(range(8), c_llm.values):
        ax.text(x - w / 2, v + 0.8, str(v), ha="center", va="bottom", fontsize=9, color=COLORS[0])
    ax.set_xticks(range(8))
    ax.set_xlabel("選んだ候補の番号（候補は毎回シャッフル済み）")
    ax.set_ylabel("回数")
    ax.set_title(f"選んだ番号の分布（各 {len(llm)} 回の選択、3シード）")
    ax.legend(frameon=False, fontsize=10, loc="upper center")
    fig.tight_layout()
    fig.savefig(out, dpi=160)
    plt.close(fig)
    return c_llm, c_uni


def pick_least_seen(llm, uni, out):
    vals = [llm["picked_min_seen"].mean() * 100, uni["picked_min_seen"].mean() * 100]
    fig, ax = plt.subplots(figsize=(5.5, 4))
    ax.bar(["LLM", "候補からでたらめ（Uniform）"], vals, color=[COLORS[0], GRAY], width=0.55)
    for i, v in enumerate(vals):
        ax.text(i, v + 1, f"{v:.1f}%", ha="center", va="bottom", fontsize=12)
    ax.set_ylim(0, 100)
    ax.set_ylabel("一番見られていない候補を選んだ率（%）")
    ax.set_title(f"一番見られていない候補を選んだ率（各 {len(llm)} 回）")
    fig.tight_layout()
    fig.savefig(out, dpi=160)
    plt.close(fig)
    return vals


def position_probe(raw, out):
    variants = [v for v in VARIANT_LABEL if v in raw["variant"].unique()]
    perm = raw[raw["test"] == "permute"]
    same = {}
    for v in variants:
        g = perm[perm["variant"] == v].groupby("item")["cand"].nunique()
        same[v] = ((g == 1).sum(), len(g))
    blank = raw[raw["test"] == "blank"]

    fig, (a1, a2) = plt.subplots(1, 2, figsize=(12, 4.3), gridspec_kw={"width_ratios": [1, 1.3]})
    xs = range(len(variants))
    rates = [same[v][0] / same[v][1] * 100 for v in variants]
    a1.bar(xs, rates, color=COLORS[0], width=0.55)
    for x, v in zip(xs, variants):
        a1.text(x, rates[x] + 1.5, f"{same[v][0]}/{same[v][1]}", ha="center", va="bottom", fontsize=11)
    a1.set_xticks(list(xs))
    a1.set_xticklabels([VARIANT_LABEL[v] for v in variants], fontsize=9)
    a1.set_ylim(0, 100)
    a1.set_ylabel("3通りの並びで同じ候補を選んだ率（%）")
    a1.set_title("並び替えテスト：同じ候補を並べ替えて聞く")

    width = 0.8 / len(variants)
    for i, v in enumerate(variants):
        c = blank[blank["variant"] == v]["pos"].value_counts().reindex(range(8), fill_value=0)
        a2.bar([p + (i - (len(variants) - 1) / 2) * width for p in range(8)], c.values,
               width=width, color=COLORS[i], label=VARIANT_LABEL[v])
    n_blank = blank.groupby("variant").size().iloc[0]
    a2.axhline(n_blank / 8, color=GRAY, ls="--", lw=1.2)
    a2.text(4, n_blank / 8 + 0.3, "均等なら 3 回", ha="center", va="bottom", color=GRAY, fontsize=9)
    a2.set_xticks(range(8))
    a2.set_xlabel("選ばれた番号")
    a2.set_ylabel("回数")
    a2.set_title(f"中身なしテスト：8個とも同じ候補（各 {n_blank} 回）")
    a2.legend(fontsize=9, frameon=False)
    fig.tight_layout()
    fig.savefig(out, dpi=160)
    plt.close(fig)
    return same


def rich_probe(folder, out):
    """詳しい説明（--llm-describe rich）あり／なし（strip）で、同じ候補への選び方を比べる。"""
    raw = pd.read_csv(folder / "probe_raw.csv", encoding="utf-8-sig")
    summ = pd.read_csv(folder / "probe_summary.csv", encoding="utf-8-sig").set_index("variant")
    perm = raw[(raw["test"] == "permute") & raw["pos"].notna()]
    labels = {"baseline": "詳しい説明あり", "strip": "説明なし（同じ候補）"}
    variants = [v for v in labels if v in perm["variant"].unique()]
    colors = {"baseline": COLORS[0], "strip": GRAY}

    # でたらめに選んだときに当たる率（probe_summary の "67/120 (chance 39.8)" から）
    s = summ.loc[variants[0], "pick_max_unvisited"]
    chance = float(s.split("chance ")[1].rstrip(")")) / float(s.split("/")[1].split(" ")[0]) * 100

    fig, (a1, a2, a3) = plt.subplots(1, 3, figsize=(15, 4.4), gridspec_kw={"width_ratios": [1, 0.8, 1.4]})
    mu = [perm[perm["variant"] == v]["is_max_unvisited"].mean() * 100 for v in variants]
    a1.bar([labels[v] for v in variants], mu, color=[colors[v] for v in variants], width=0.55)
    a1.axhline(chance, color=GRAY, ls="--", lw=1.2)
    a1.text(0.5, chance + 1.5, f"でたらめなら {chance:.0f}%", ha="center", va="bottom", color=GRAY, fontsize=9)
    for i, v in enumerate(mu):
        a1.text(i, v + 1.5, f"{v:.0f}%", ha="center", va="bottom", fontsize=12)
    a1.set_ylim(0, 100)
    a1.set_ylabel("率（%）")
    a1.set_title("近くの未訪問マスが最多の候補を選んだ率", fontsize=11)

    same = []
    for v in variants:
        g = perm[perm["variant"] == v].groupby("item")["cand"].nunique()
        same.append(((g == 1).sum(), len(g)))
    a2.bar([labels[v] for v in variants], [a / b * 100 for a, b in same], color=[colors[v] for v in variants], width=0.55)
    for i, (a, b) in enumerate(same):
        a2.text(i, a / b * 100 + 1.5, f"{a}/{b}", ha="center", va="bottom", fontsize=12)
    a2.set_ylim(0, 100)
    a2.set_title("3通りの並びで同じ候補を選んだ率", fontsize=11)

    w = 0.38
    for i, v in enumerate(variants):
        c = perm[perm["variant"] == v]["pos"].astype(int).value_counts().reindex(range(8), fill_value=0)
        a3.bar([p + (i - 0.5) * w for p in range(8)], c.values, width=w, color=colors[v], label=labels[v])
    n = len(perm[perm["variant"] == variants[0]])
    a3.axhline(n / 8, color=GRAY, ls="--", lw=1.2)
    a3.set_xticks(range(8))
    a3.set_xlabel("選ばれた番号")
    a3.set_ylabel("回数")
    a3.set_title(f"選んだ番号の分布（各 {n} 回、破線＝均等）", fontsize=11)
    a3.legend(fontsize=9, frameon=False, loc="upper left")
    fig.tight_layout()
    fig.savefig(out, dpi=160)
    plt.close(fig)
    return dict(zip(variants, mu)), chance, dict(zip(variants, same))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", default="QaResults/20261115_200704")
    ap.add_argument("--probe", default="QaResults/probe_position_20261119")
    ap.add_argument("--probe-rich", default="QaResults/probe_rich_20261119")
    ap.add_argument("--out", default="QaResults/slides_20261119")
    a = ap.parse_args()
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)

    dec = pd.read_csv(Path(a.run) / "llm_decisions.csv", encoding="utf-8-sig")
    raw = pd.read_csv(Path(a.probe) / "probe_raw.csv", encoding="utf-8-sig")

    # llm_decisions.csv には LlmGoExplore と UniformGoExplore の両方の記録が入っている
    llm = dec[dec["agent"].str.startswith("Llm")]
    uni = dec[dec["agent"].str.startswith("Uniform")]
    c_llm, c_uni = choice_position(llm, uni, out / "fig_choice_position.png")
    vals = pick_least_seen(llm, uni, out / "fig_pick_least_seen.png")
    same = position_probe(raw, out / "fig_position_probe.png")

    print("選んだ番号 LLM:", c_llm.to_dict(), " Uniform:", c_uni.to_dict())
    print(f"一番見られていない候補: LLM {vals[0]:.1f}% / Uniform {vals[1]:.1f}%")
    print(f"元のルールと同じ候補: LLM {llm['picked_rule_choice'].mean():.3f} / Uniform {uni['picked_rule_choice'].mean():.3f}")
    print("並び替えテストで同じ候補:", same)
    if Path(a.probe_rich).exists():
        mu, chance, same_r = rich_probe(Path(a.probe_rich), out / "fig_rich_probe.png")
        print(f"詳しい説明: 未訪問最多を選んだ率 {mu}（でたらめ {chance:.1f}%）、同じ候補 {same_r}")
    print("figures written to", out)


if __name__ == "__main__":
    main()
