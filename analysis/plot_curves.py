"""
実験結果（ExperimentRunner の出力フォルダ）から、検出曲線・網羅率曲線・集計表を作る。

使い方:
    pip install pandas matplotlib
    python plot_curves.py ../QaResults/20261101_001211

出力（同じフォルダ内）:
    fig_detection_curve.png   予算（ステップ数）ごとの検出率（平均と最小〜最大の幅）
    fig_coverage_curve.png    予算ごとの網羅率
    fig_coverage_vs_detection.png  最終網羅率 vs 検出率（1点 = 1回の実行）
    fig_bug_matrix.png        バグ × 手法 の「起こした率」と「気づいた率」
    summary_agents.csv        手法ごとの面積（AUC）・予算別の検出率
    summary_bugs.csv          バグごとの起こした率・気づいた率・中央値ステップ
"""
import sys
from pathlib import Path

import matplotlib.pyplot as plt
import pandas as pd

COLORS = ["#2a6fdb", "#e07a1f", "#3a9d5d", "#b8417a", "#6b5bd1", "#8c8c8c"]


def load(folder: Path):
    runs = pd.read_csv(folder / "runs.csv")
    prog = pd.read_csv(folder / "progress.csv")
    det = pd.read_csv(folder / "detections.csv")
    trig = pd.read_csv(folder / "triggers.csv")
    bugs = pd.read_csv(folder / "bugs.csv")
    prog["frac"] = prog["detected"] / prog["enabled_count"].clip(lower=1)
    return runs, prog, det, trig, bugs


def agent_order(runs):
    order = ["Random", "GoExplore(Position)", "GoExplore(Position+Keys)", "GoExplore(Full)"]
    present = list(runs["agent"].unique())
    return [a for a in order if a in present] + [a for a in present if a not in order]


def curve_plot(prog, agents, column, ylabel, title, path):
    fig, ax = plt.subplots(figsize=(8, 5))
    for i, a in enumerate(agents):
        g = prog[prog.agent == a].groupby("step")[column]
        mean, lo, hi = g.mean(), g.min(), g.max()
        c = COLORS[i % len(COLORS)]
        ax.plot(mean.index, mean.values, label=a, color=c, linewidth=2)
        ax.fill_between(mean.index, lo.values, hi.values, color=c, alpha=0.12)
    ax.set_xlabel("Budget (environment steps)")
    ax.set_ylabel(ylabel)
    ax.set_ylim(0, 1.02)
    ax.set_title(title)
    ax.grid(alpha=0.3)
    ax.legend(loc="lower right")
    fig.tight_layout()
    fig.savefig(path, dpi=160)
    plt.close(fig)


def summary_agents(runs, prog, agents, folder):
    budget = int(runs["budget"].max())
    checkpoints = sorted({c for c in [1000, 5000, budget // 2, budget] if c <= budget})
    rows = []
    for a in agents:
        p = prog[prog.agent == a]
        per_run = []
        for rid, g in p.groupby("run_id"):
            g = g.sort_values("step")
            # 面積（AUC）: 検出率の曲線の下の面積 / 予算 → 0〜1。早く見つけるほど大きい
            xs = [0] + list(g["step"])
            ys = [0] + list(g["frac"])
            area = sum((xs[i] - xs[i - 1]) * (ys[i] + ys[i - 1]) / 2 for i in range(1, len(xs)))
            row = {"run_id": rid, "auc": area / budget}
            for c in checkpoints:
                upto = g[g["step"] <= c]
                row[f"detected@{c}"] = upto["frac"].iloc[-1] if len(upto) else 0.0
            row["coverage"] = g["coverage"].iloc[-1]
            per_run.append(row)
        df = pd.DataFrame(per_run)
        out = {"agent": a, "runs": len(df)}
        for col in df.columns:
            if col == "run_id":
                continue
            out[col + "_mean"] = round(df[col].mean(), 3)
        out["total_ms_mean"] = round(runs[runs.agent == a]["total_ms"].mean(), 1)
        rows.append(out)
    res = pd.DataFrame(rows)
    res.to_csv(folder / "summary_agents.csv", index=False, encoding="utf-8-sig")
    return res


def summary_bugs(runs, det, trig, bugs, agents, folder):
    rows = []
    for a in agents:
        r = runs[runs.agent == a]
        for code in bugs["code"]:
            enabled = r[r["enabled"].astype(str).str.split("+").apply(lambda xs: code in xs)]
            n = len(enabled)
            if n == 0:
                continue
            ids = set(enabled["run_id"])
            t = trig[(trig.agent == a) & (trig.bug == code) & (trig.run_id.isin(ids))]
            d = det[(det.agent == a) & (det.bug == code) & (det.run_id.isin(ids))]
            rows.append({
                "agent": a, "bug": code, "runs": n,
                "triggered_rate": round(t["run_id"].nunique() / n, 2),
                "detected_rate": round(d["run_id"].nunique() / n, 2),
                "median_detect_step": d["step"].median() if len(d) else None,
            })
    res = pd.DataFrame(rows)
    res.to_csv(folder / "summary_bugs.csv", index=False, encoding="utf-8-sig")
    return res


def bug_matrix(sb, agents, codes, path):
    fig, axes = plt.subplots(1, 2, figsize=(11, 3.2 + 0.35 * len(agents)), sharey=True)
    for ax, col, title in [(axes[0], "triggered_rate", "Triggered (bug code ran)"),
                           (axes[1], "detected_rate", "Detected (oracle noticed)")]:
        m = sb.pivot(index="agent", columns="bug", values=col).reindex(index=agents, columns=codes)
        im = ax.imshow(m.values, vmin=0, vmax=1, cmap="Blues", aspect="auto")
        ax.set_xticks(range(len(codes)), codes)
        ax.set_yticks(range(len(agents)), agents)
        ax.set_title(title)
        for i in range(m.shape[0]):
            for j in range(m.shape[1]):
                v = m.values[i, j]
                if pd.notna(v):
                    ax.text(j, i, f"{v:.0%}", ha="center", va="center",
                            color="white" if v > 0.6 else "#1a1a1a", fontsize=9)
    fig.colorbar(im, ax=axes, shrink=0.8)
    fig.savefig(path, dpi=160, bbox_inches="tight")
    plt.close(fig)


def coverage_vs_detection(runs, agents, path):
    fig, ax = plt.subplots(figsize=(6.5, 5))
    for i, a in enumerate(agents):
        r = runs[runs.agent == a]
        frac = r["detected"] / r["enabled_count"].clip(lower=1)
        ax.scatter(r["final_coverage"], frac, label=a, color=COLORS[i % len(COLORS)], alpha=0.75, s=40)
    ax.plot([0, 1], [0, 1], color="#999", linestyle="--", linewidth=1)
    ax.set_xlabel("Final coverage (intended floor tiles visited)")
    ax.set_ylabel("Bugs detected / bugs enabled")
    ax.set_xlim(0, 1.02)
    ax.set_ylim(0, 1.02)
    ax.grid(alpha=0.3)
    ax.legend(loc="upper left", fontsize=8)
    ax.set_title("Coverage vs. detection (1 point = 1 run)")
    fig.tight_layout()
    fig.savefig(path, dpi=160)
    plt.close(fig)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    folder = Path(sys.argv[1])
    runs, prog, det, trig, bugs = load(folder)
    agents = agent_order(runs)
    codes = list(bugs["code"])

    curve_plot(prog, agents, "frac", "Bugs detected / bugs enabled",
               "Detection curve (mean; band = min-max over seeds)", folder / "fig_detection_curve.png")
    curve_plot(prog, agents, "coverage", "Coverage of intended floor tiles",
               "Coverage curve (mean; band = min-max over seeds)", folder / "fig_coverage_curve.png")
    coverage_vs_detection(runs, agents, folder / "fig_coverage_vs_detection.png")
    sa = summary_agents(runs, prog, agents, folder)
    sb = summary_bugs(runs, det, trig, bugs, agents, folder)
    bug_matrix(sb, agents, codes, folder / "fig_bug_matrix.png")

    pd.set_option("display.width", 200)
    print(sa.to_string(index=False))
    print()
    print(sb.pivot(index="agent", columns="bug", values="detected_rate").reindex(index=agents).to_string())
    print("\nfigures written to", folder)


if __name__ == "__main__":
    main()
