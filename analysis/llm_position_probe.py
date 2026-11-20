"""
L2 の LLM が「候補の中身」で選んでいるのか「番号（並び順）」で選んでいるのかを調べる。

前回の実験（--run の結果フォルダ）で LLM に実際に渡した候補リストを使い、
  1. 並び替えテスト: 同じ候補を「元の順・逆順・循環シフト」で並べ直して聞き、
     選ばれた候補（中身）が一致するか、番号が一致するかを数える
  2. 中身なしテスト: 8個とも同じ文章の候補を並べ、純粋な番号の好みを測る
をプロンプトの変種ごとに行う。

使い方:
  python analysis/llm_position_probe.py --run QaResults/20261115_200704 --n 40 \
      --variants baseline,letters,order_note --out QaResults/probe_position
結果: <out>/probe_raw.csv（全呼び出し）, <out>/probe_summary.csv, 標準出力に要約
同じ入力は <out>/probe_cache.jsonl から再利用する（再実行しても LLM を呼び直さない）。
"""
import argparse
import collections
import csv
import hashlib
import json
import os
import random
import re
import time
import urllib.request

OPTIONS = {"temperature": 0, "top_k": 1, "seed": 0, "num_ctx": 4096, "num_predict": 128}
LINE_RE = re.compile(r"^(\d+): (.*) chosen=(\d+) seen=(\d+)$")
LETTERS = "ABCDEFGHIJKLMNOP"
UNVISITED_RE = re.compile(r"(\d+) unvisited tiles within")
# --llm-describe rich の説明文（Agents.cs の LlmPrompts.RichNote と同じ文の書き出し）
RICH_NOTE_START = "Each candidate also describes its surroundings"


def unvisited_near(state):
    """rich の説明から「近くの未訪問マス数」を取り出す。basic なら None。"""
    if "nearby:" not in state:
        return None
    m = UNVISITED_RE.search(state)
    return int(m.group(1)) if m else 0


def load_prompts(run_dir, cache_path):
    """前回の実験で LLM に渡した候補リスト（reason つきの形式）を取り出す。"""
    meta = json.load(open(os.path.join(run_dir, "meta.json"), encoding="utf-8-sig"))
    opts = json.dumps(meta["llm_options"], separators=(",", ":"))
    out = []
    for line in open(cache_path, encoding="utf-8"):
        e = json.loads(line)
        if json.loads(e["options"]) != meta["llm_options"] or '"reason"' not in e["prompt"]:
            continue
        # 同じ生成設定の別の実験が混ざらないよう、候補の説明の形式（basic / rich）も揃える
        if ("nearby:" in e["prompt"]) != (meta.get("llm_describe", "basic") == "rich"):
            continue
        cands = []
        for l in e["prompt"].splitlines():
            m = LINE_RE.match(l)
            if m:
                cands.append({"state": m.group(2), "chosen": int(m.group(3)), "seen": int(m.group(4))})
        if len(cands) == 8:
            out.append(cands)
    return meta, out


def build(variant, system, cands):
    """変種ごとに system と user を組み立てる。返り値: (system, user, ラベル→並び位置 の辞書)"""
    labels = [LETTERS[i] if variant == "letters" else str(i) for i in range(len(cands))]
    sys_ = system
    if variant == "strip":
        # rich の候補から周りの様子を外し、basic と同じ見た目にする（同じ候補で前後比較するため）
        cands = [dict(c, state=c["state"].split(" | ")[0]) for c in cands]
        sys_ = "\n".join(l for l in sys_.split("\n") if not l.startswith(RICH_NOTE_START))
    if variant == "letters":
        sys_ = sys_.replace("<candidate number>", "\"<candidate letter>\"")
    if variant == "order_note":
        sys_ += "\nThe candidates are listed in random order; the position or number of a candidate carries no meaning."
    lines = ["Candidates:"]
    for lab, c in zip(labels, cands):
        lines.append("%s: %s chosen=%d seen=%d" % (lab, c["state"], c["chosen"], c["seen"]))
    fmt = '{"reason": "<one short sentence>", "choice": %s}' % (
        '"<candidate letter>"' if variant == "letters" else "<candidate number>")
    lines.append("Reply with JSON %s." % fmt)
    return sys_, "\n".join(lines) + "\n", {lab: i for i, lab in enumerate(labels)}


class Llm:
    def __init__(self, model, cache_path):
        self.model = model
        self.cache_path = cache_path
        self.cache = {}
        if os.path.exists(cache_path):
            for l in open(cache_path, encoding="utf-8"):
                e = json.loads(l)
                self.cache[e["key"]] = e["response"]
        self.calls = 0

    def complete(self, system, prompt):
        key = hashlib.sha256((self.model + "\n" + json.dumps(OPTIONS) + "\n" + system + "\n" + prompt).encode()).hexdigest()
        if key in self.cache:
            return self.cache[key]
        body = {"model": self.model, "stream": False, "format": "json", "options": OPTIONS,
                "messages": [{"role": "system", "content": system}, {"role": "user", "content": prompt}]}
        req = urllib.request.Request("http://localhost:11434/api/chat", json.dumps(body).encode(),
                                     {"Content-Type": "application/json"})
        r = json.load(urllib.request.urlopen(req, timeout=600))["message"]["content"]
        self.calls += 1
        self.cache[key] = r
        with open(self.cache_path, "a", encoding="utf-8") as f:
            f.write(json.dumps({"key": key, "model": self.model, "system": system, "prompt": prompt, "response": r},
                               ensure_ascii=False) + "\n")
        return r


def parse_choice(resp, label_to_pos):
    try:
        v = json.loads(resp).get("choice")
    except Exception:
        return None
    v = str(v).strip().strip('"').upper()
    return label_to_pos.get(v)


def pair_counts(by_item, perms, field):
    """並べ方を2通りずつ比べて、同じ候補（または同じ番号）を選んだ組の数。"""
    same = n = 0
    names = list(perms)
    for d in by_item.values():
        for i in range(len(names)):
            for j in range(i + 1, len(names)):
                a, b = d.get(names[i]), d.get(names[j])
                if a is None or b is None or a["pos"] == "" or b["pos"] == "":
                    continue
                n += 1
                same += a[field] == b[field]
    return same, n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", required=True)
    ap.add_argument("--cache", default="QaResults/llm_cache.jsonl")
    ap.add_argument("--n", type=int, default=40, help="並び替えテストに使う候補リストの数")
    ap.add_argument("--n-blank", type=int, default=24, help="中身なしテストの回数")
    ap.add_argument("--variants", default="baseline,letters,order_note")
    ap.add_argument("--model", default=None)
    ap.add_argument("--out", default="QaResults/probe_position")
    ap.add_argument("--seed", type=int, default=0)
    a = ap.parse_args()

    meta, prompts = load_prompts(a.run, a.cache)
    model = a.model or meta["llm_model"]
    rnd = random.Random(a.seed)
    sample = rnd.sample(prompts, min(a.n, len(prompts)))
    os.makedirs(a.out, exist_ok=True)
    llm = Llm(model, os.path.join(a.out, "probe_cache.jsonl"))
    system = meta["system_prompt"]
    k = 8
    perms = {"orig": list(range(k)), "reverse": list(range(k))[::-1], "shift3": [(i + 3) % k for i in range(k)]}

    raw = []
    t0 = time.time()
    for variant in a.variants.split(","):
        # 1. 並び替えテスト
        for pi, cands in enumerate(sample):
            for pname, perm in perms.items():
                shown = [cands[j] for j in perm]
                s, u, l2p = build(variant, system, shown)
                pos = parse_choice(llm.complete(s, u), l2p)
                raw.append({"variant": variant, "test": "permute", "item": pi, "perm": pname,
                            "pos": "" if pos is None else pos, "cand": "" if pos is None else perm[pos],
                            "is_min_seen": "" if pos is None else int(shown[pos]["seen"] == min(c["seen"] for c in cands)),
                            "is_max_unvisited": "" if pos is None or unvisited_near(cands[0]["state"]) is None
                            else int(unvisited_near(shown[pos]["state"]) == max(unvisited_near(c["state"]) for c in cands))})
        # 2. 中身なしテスト: 全候補を同じ文章にする
        for bi in range(a.n_blank):
            c = rnd.choice(rnd.choice(prompts))
            s, u, l2p = build(variant, system, [c] * k)
            pos = parse_choice(llm.complete(s, u), l2p)
            raw.append({"variant": variant, "test": "blank", "item": bi, "perm": "",
                         "pos": "" if pos is None else pos, "cand": "", "is_min_seen": "", "is_max_unvisited": ""})
        print("[%s] done, new LLM calls so far %d, %.0f s" % (variant, llm.calls, time.time() - t0), flush=True)

    with open(os.path.join(a.out, "probe_raw.csv"), "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(raw[0].keys()))
        w.writeheader()
        w.writerows(raw)

    # 要約
    summary = []
    for variant in a.variants.split(","):
        R = [r for r in raw if r["variant"] == variant]
        P = [r for r in R if r["test"] == "permute"]
        by_item = collections.defaultdict(dict)
        for r in P:
            by_item[r["item"]][r["perm"]] = r
        same_cand = same_pos = n = 0
        for d in by_item.values():
            if any(d[p]["pos"] == "" for p in perms):
                continue
            n += 1
            same_cand += len({d[p]["cand"] for p in perms}) == 1
            same_pos += len({d[p]["pos"] for p in perms}) == 1
        pos_hist = collections.Counter(r["pos"] for r in P if r["pos"] != "")
        blank_hist = collections.Counter(r["pos"] for r in R if r["test"] == "blank" and r["pos"] != "")
        valid = [r for r in P if r["pos"] != ""]
        minseen = sum(r["is_min_seen"] for r in valid)
        mu = [r["is_max_unvisited"] for r in valid if r["is_max_unvisited"] != ""]
        # でたらめに選んだときに「近くの未訪問マスが最多の候補」に当たる確率
        chance_mu = []
        for cands in sample:
            u = [unvisited_near(c["state"]) for c in cands]
            if u[0] is not None:
                chance_mu.append(sum(x == max(u) for x in u) / len(u))
        row = {"variant": variant, "items": n,
               "same_candidate_all3": same_cand, "same_position_all3": same_pos,
               "pick_min_seen": "%d/%d" % (minseen, len(valid)),
               "pick_max_unvisited": "%d/%d (chance %.1f)" % (sum(mu), len(mu), len(mu) * sum(chance_mu) / len(chance_mu)) if mu and chance_mu else "",
               "same_candidate_pairs": "%d/%d" % pair_counts(by_item, perms, "cand"),
               "same_position_pairs": "%d/%d" % pair_counts(by_item, perms, "pos"),
               "parse_fail": len(P) - len(valid),
               "pos_hist_permute": " ".join("%d:%d" % (i, pos_hist.get(i, 0)) for i in range(k)),
               "pos_hist_blank": " ".join("%d:%d" % (i, blank_hist.get(i, 0)) for i in range(k))}
        summary.append(row)
        print(row)
    with open(os.path.join(a.out, "probe_summary.csv"), "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(summary[0].keys()))
        w.writeheader()
        w.writerows(summary)
    json.dump({"model": model, "options": OPTIONS, "run": a.run, "n": a.n, "n_blank": a.n_blank, "seed": a.seed,
               "variants": a.variants}, open(os.path.join(a.out, "probe_meta.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
