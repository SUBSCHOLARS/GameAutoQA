using System;
using System.Collections.Generic;

namespace QaTestbed
{
    /// <summary>
    /// テスト用エージェント。Tick() 1回で必ず1手だけ進める（＝予算の消費を手法間で揃える）。
    /// </summary>
    public interface IAgent
    {
        string Name { get; }
        int ArchiveSize { get; }
        void Begin(Harness h, Random rng);
        void Tick(Harness h);
    }

    /// <summary>L0: ランダム入力。一定手数ごと、またはクリア・クラッシュでリセット。</summary>
    public sealed class RandomAgent : IAgent
    {
        public int MaxEpisodeSteps = 300;
        private Random rng;
        private int episodeSteps;

        public string Name { get { return "Random"; } }
        public int ArchiveSize { get { return 0; } }

        public void Begin(Harness h, Random rng)
        {
            this.rng = rng;
            h.Reset();
            episodeSteps = 0;
        }

        public void Tick(Harness h)
        {
            if (h.EpisodeDone || episodeSteps >= MaxEpisodeSteps)
            {
                h.Reset();
                episodeSteps = 0;
            }
            h.Step((GameAction)rng.Next(GameSim.ActionCount));
            episodeSteps++;
        }
    }

    /// <summary>
    /// Go-Explore の「何を新しい状態として保存するか」の粒度。
    /// Position         : 位置だけ（前回の論文の到達可能性テストに近い）
    /// PositionKeys     : 位置＋持っている鍵＋開いた扉
    /// Full             : 位置＋鍵＋扉＋床の鍵の有無＋コイン枚数＋凍結
    /// </summary>
    public enum CellMode { Position, PositionKeys, Full }

    /// <summary>LLM を使うエージェントの集計値（runs.csv に出す）。</summary>
    public interface ILlmAgentStats
    {
        int LlmCalls { get; }
        int LlmFallbacks { get; }
        double LlmMs { get; }
    }

    /// <summary>候補から1つ選んだ記録（LLM 版と、比較用のでたらめ版）。llm_decisions.csv に出す。</summary>
    public struct CandidateDecision
    {
        public long Step;
        public int Candidates;
        /// <summary>選んだ候補の番号（シャッフル後の並びで）。</summary>
        public int Choice;
        /// <summary>元の訪問回数ルールなら選ばれていた候補の番号（シャッフル後の並びで）。</summary>
        public int RuleIndex;
        public bool Fallback;
        public string ChosenState;
        public int ChosenTimesChosen, ChosenTimesSeen, MinSeen, MaxSeen;
        public string Reason;
    }

    /// <summary>
    /// L1: 手作りルールの Go-Explore。
    /// 選ぶ（訪問回数の少ないセルほど選ばれやすい）→ 戻る（スナップショット復元）→
    /// 探索する（ランダム行動を ExploreSteps 手）→ 保存する（新しいセルならアーカイブへ）。
    /// Llm を設定すると L2（どこに戻るか）を LLM が選ぶ:
    ///   訪問回数ルールで候補を LlmCandidates 個抽選 → 並びをシャッフル → LLM が理由つきで1つ選ぶ
    ///   → 答えが読めなければ元のルールで選ばれていた候補。
    /// UniformAmongCandidates=true は比較用: 同じ候補から LLM の代わりにでたらめに1つ選ぶ。
    /// </summary>
    public sealed class GoExploreAgent : IAgent, ILlmAgentStats
    {
        private sealed class Cell
        {
            public GameState Snapshot;
            public int TimesChosen;
            public int TimesSeen;
        }

        public CellMode Mode;
        public int ExploreSteps = 30;
        public double RepeatActionProb = 0.5;

        private Random rng;
        private readonly Dictionary<string, Cell> archive = new Dictionary<string, Cell>();
        private readonly List<Cell> cells = new List<Cell>();
        private int remaining;
        private GameAction lastAction;

        /// <summary>null なら元の Go-Explore。設定すると戻る先を LLM が選ぶ。</summary>
        public ILlmClient Llm;
        public int LlmCandidates = 8;
        public int LlmCalls { get; private set; }
        public int LlmFallbacks { get; private set; }
        public double LlmMs { get; private set; }
        /// <summary>比較用: 候補からでたらめに選ぶ（LLM を使わない）。</summary>
        public bool UniformAmongCandidates;
        /// <summary>比較用（LLM を使わないルール）: 候補のうち近くの未訪問マス（StateText.UnvisitedNearby）が最も多いものを選ぶ。同数ならでたらめ。</summary>
        public bool UnvisitedAmongCandidates;
        public readonly List<CandidateDecision> Decisions = new List<CandidateDecision>();
        /// <summary>true なら候補の説明に周りの様子（未訪問マス・鍵・扉・ゴールまでの歩数）を加える（--llm-describe rich）。</summary>
        public bool RichDescription;
        private string llmSystem;
        private bool UsesCandidates { get { return Llm != null || UniformAmongCandidates || UnvisitedAmongCandidates; } }

        public GoExploreAgent(CellMode mode) { Mode = mode; }
        public GoExploreAgent(CellMode mode, ILlmClient llm, int candidates) { Mode = mode; Llm = llm; LlmCandidates = candidates; }

        public string Name
        {
            get
            {
                string prefix = Llm != null ? "LlmGoExplore" : UniformAmongCandidates ? "UniformGoExplore"
                    : UnvisitedAmongCandidates ? "UnvisitedGoExplore" : "GoExplore";
                switch (Mode)
                {
                    case CellMode.Position: return prefix + "(Position)";
                    case CellMode.PositionKeys: return prefix + "(Position+Keys)";
                    default: return prefix + "(Full)";
                }
            }
        }

        public int ArchiveSize { get { return cells.Count; } }

        public void Begin(Harness h, Random rng)
        {
            this.rng = rng;
            archive.Clear();
            cells.Clear();
            h.Reset();
            AddIfNew(h.State);
            remaining = 0;
            LlmCalls = 0; LlmFallbacks = 0; LlmMs = 0;
            Decisions.Clear();
            if (Llm != null) llmSystem = LlmPrompts.System(h.Map, ExploreSteps, RichDescription);
        }

        public void Tick(Harness h)
        {
            if (remaining <= 0 || h.EpisodeDone)
            {
                // 選ぶ → 戻る
                var c = UsesCandidates ? SelectFromCandidates(h) : SelectCell();
                c.TimesChosen++;
                h.Restore(c.Snapshot);
                remaining = ExploreSteps;
                lastAction = (GameAction)rng.Next(GameSim.ActionCount);
            }

            // 探索する
            var action = rng.NextDouble() < RepeatActionProb ? lastAction : (GameAction)rng.Next(GameSim.ActionCount);
            lastAction = action;
            h.Step(action);
            remaining--;

            // 保存する（クラッシュ・クリアした状態は保存しない）
            if (!h.EpisodeDone) AddIfNew(h.State);
        }

        private void AddIfNew(GameState s)
        {
            string key = CellKey(s, Mode);
            Cell existing;
            if (archive.TryGetValue(key, out existing)) { existing.TimesSeen++; return; }
            var c = new Cell { Snapshot = s.Clone(), TimesChosen = 0, TimesSeen = 1 };
            archive.Add(key, c);
            cells.Add(c);
        }

        private Cell SelectCell()
        {
            double total = 0;
            for (int i = 0; i < cells.Count; i++) total += Weight(cells[i]);
            double r = rng.NextDouble() * total;
            for (int i = 0; i < cells.Count; i++)
            {
                r -= Weight(cells[i]);
                if (r <= 0) return cells[i];
            }
            return cells[cells.Count - 1];
        }

        /// <summary>重み付きで重複なしに k 個抽選する（1番目は SelectCell() と同じ分布）。</summary>
        private List<Cell> SampleCandidates(int k)
        {
            var pool = new List<Cell>(cells);
            var result = new List<Cell>();
            while (result.Count < k && pool.Count > 0)
            {
                double total = 0;
                for (int i = 0; i < pool.Count; i++) total += Weight(pool[i]);
                double r = rng.NextDouble() * total;
                int pick = pool.Count - 1;
                for (int i = 0; i < pool.Count; i++)
                {
                    r -= Weight(pool[i]);
                    if (r <= 0) { pick = i; break; }
                }
                result.Add(pool[pick]);
                pool.RemoveAt(pick);
            }
            return result;
        }

        private Cell SelectFromCandidates(Harness h)
        {
            long step = h.TotalSteps;
            var cands = SampleCandidates(LlmCandidates);
            if (cands.Count == 1) return cands[0];
            Cell rulePick = cands[0];

            // 並び順の癖を消すためにシャッフル（Fisher-Yates、実行のシードで決まる）
            for (int i = cands.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var t = cands[i]; cands[i] = cands[j]; cands[j] = t;
            }
            int ruleIndex = cands.IndexOf(rulePick);

            int choice;
            bool fallback = false;
            string reason = "";
            if (Llm == null && UnvisitedAmongCandidates)
            {
                var best = new List<int>();
                int bestN = -1;
                for (int i = 0; i < cands.Count; i++)
                {
                    int n = StateText.UnvisitedNearby(cands[i].Snapshot, h.Map, h.WasVisited);
                    if (n > bestN) { bestN = n; best.Clear(); }
                    if (n == bestN) best.Add(i);
                }
                choice = best[rng.Next(best.Count)];
                reason = "unvisited_nearby=" + bestN;
            }
            else if (Llm == null)
            {
                choice = rng.Next(cands.Count);
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("Candidates:\n");
                for (int i = 0; i < cands.Count; i++)
                {
                    sb.Append(i).Append(": ").Append(StateText.Describe(cands[i].Snapshot));
                    if (RichDescription)
                        sb.Append(" | ").Append(StateText.Surroundings(cands[i].Snapshot, h.Map, h.WasVisited)).Append(" |");
                    sb.Append(" chosen=").Append(cands[i].TimesChosen)
                      .Append(" seen=").Append(cands[i].TimesSeen).Append('\n');
                }
                sb.Append(LlmPrompts.AnswerFormat);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string resp = Llm.Complete(llmSystem, sb.ToString());
                LlmMs += sw.Elapsed.TotalMilliseconds;
                LlmCalls++;

                reason = MiniJson.GetString(resp, "reason") ?? "";
                int? n = MiniJson.GetInt(resp, "choice");
                if (n.HasValue && n.Value >= 0 && n.Value < cands.Count) choice = n.Value;
                else { choice = ruleIndex; fallback = true; LlmFallbacks++; }
            }

            int minSeen = int.MaxValue, maxSeen = 0;
            foreach (var c in cands) { minSeen = Math.Min(minSeen, c.TimesSeen); maxSeen = Math.Max(maxSeen, c.TimesSeen); }
            var picked = cands[choice];
            Decisions.Add(new CandidateDecision
            {
                Step = step, Candidates = cands.Count, Choice = choice, RuleIndex = ruleIndex, Fallback = fallback,
                ChosenState = StateText.Describe(picked.Snapshot), ChosenTimesChosen = picked.TimesChosen,
                ChosenTimesSeen = picked.TimesSeen, MinSeen = minSeen, MaxSeen = maxSeen, Reason = reason,
            });
            return picked;
        }

        private static double Weight(Cell c)
        {
            return 1.0 / Math.Sqrt(c.TimesChosen + 1) + 1.0 / Math.Sqrt(c.TimesSeen + 1);
        }

        public static string CellKey(GameState s, CellMode mode)
        {
            switch (mode)
            {
                case CellMode.Position:
                    return s.X + "," + s.Y;
                case CellMode.PositionKeys:
                    return s.X + "," + s.Y + "|k" + s.HeldMask + "|d" + s.DoorMask;
                default:
                    return s.X + "," + s.Y + "|k" + s.HeldMask + "|d" + s.DoorMask + "|t" + s.KeyTakenMask
                           + "|c" + s.Coins + "|f" + (s.Frozen ? 1 : 0);
            }
        }
    }

    /// <summary>L2 で LLM に渡す説明文（盤面込み）。実行中は変わらないので Ollama 側で使い回される。</summary>
    public static class LlmPrompts
    {
        /// <summary>--llm-describe rich のときに説明文へ足す1文。</summary>
        public const string RichNote =
"Each candidate also describes its surroundings: walking distances (in moves, closed doors block the way) " +
"to unvisited tiles, keys still on the floor, closed doors, the exit and coins.\n";

        public static string System(GameMap map, int exploreSteps, bool rich = false)
        {
            return
"You are a QA tester hunting for bugs in a small 2D grid game.\n" +
"Map legend: '#' wall, '.' floor, ' ' outside the level, 'S' start, 'E' exit, 'r' 'g' 'b' keys, " +
"'R' 'G' 'B' doors (opened by the key of the same colour, which is used up), 'c' coin, 'X' sealed room. " +
"x is the column, y is the row (0 at top).\n" +
"The player moves up/down/left/right and can interact. Clearing the game means interacting at 'E'.\n" +
"Typical bugs: getting through walls or out of bounds, doors opening without the right key, " +
"items that can be collected twice, crashes, and getting stuck so the exit can no longer be reached.\n\n" +
"Map (initial state):\n" + StateText.PublicMap(map) + "\n" +
"You are exploring with Go-Explore. You keep an archive of saved states. Each turn you pick one saved state; " +
"the game is restored to it and then " + exploreSteps + " random moves are made from there. " +
"Pick the state most likely to reveal a bug or a situation not yet explored. " +
"'chosen' is how often a state was picked before, 'seen' how often it was reached.\n" +
(rich ? RichNote : "") +
"Answer only with JSON of the form " + AnswerFormatJson + ".";
        }

        public const string AnswerFormatJson = "{\"reason\": \"<one short sentence>\", \"choice\": <candidate number>}";
        public const string AnswerFormat = "Reply with JSON " + AnswerFormatJson + ".";
    }

    public static class AgentFactory
    {
        public static readonly string[] DefaultKinds = { "Random", "GoExplore(Position)", "GoExplore(Position+Keys)", "GoExplore(Full)" };
        public static readonly string[] LlmKinds = { "LlmGoExplore(Position)", "LlmGoExplore(Position+Keys)", "LlmGoExplore(Full)" };
        /// <summary>比較用: LLM 版と同じ候補からでたらめに選ぶ。</summary>
        public static readonly string[] UniformKinds = { "UniformGoExplore(Position)", "UniformGoExplore(Position+Keys)", "UniformGoExplore(Full)" };

        /// <summary>比較用（LLM を使わないルール）: 同じ候補から近くの未訪問マスが最も多いものを選ぶ。</summary>
        public static readonly string[] UnvisitedKinds = { "UnvisitedGoExplore(Position)", "UnvisitedGoExplore(Position+Keys)", "UnvisitedGoExplore(Full)" };

        public static bool NeedsLlm(string kind) { return kind.StartsWith("Llm", StringComparison.Ordinal); }

        /// <summary>LLM を使う手法は llm が必要。</summary>
        public static IAgent Create(string kind, ILlmClient llm, int llmCandidates = 8, bool richDescription = false)
        {
            var a = CreateInner(kind, llm, llmCandidates);
            var ge = a as GoExploreAgent;
            if (ge != null) ge.RichDescription = richDescription;
            return a;
        }

        private static IAgent CreateInner(string kind, ILlmClient llm, int llmCandidates)
        {
            if (NeedsLlm(kind) && llm == null) throw new ArgumentException(kind + " には LLM の設定が必要です（--llm-model）");
            switch (kind)
            {
                case "LlmGoExplore(Position)": return new GoExploreAgent(CellMode.Position, llm, llmCandidates);
                case "LlmGoExplore(Position+Keys)": return new GoExploreAgent(CellMode.PositionKeys, llm, llmCandidates);
                case "LlmGoExplore(Full)": return new GoExploreAgent(CellMode.Full, llm, llmCandidates);
                case "UniformGoExplore(Position)": return new GoExploreAgent(CellMode.Position) { UniformAmongCandidates = true, LlmCandidates = llmCandidates };
                case "UniformGoExplore(Position+Keys)": return new GoExploreAgent(CellMode.PositionKeys) { UniformAmongCandidates = true, LlmCandidates = llmCandidates };
                case "UniformGoExplore(Full)": return new GoExploreAgent(CellMode.Full) { UniformAmongCandidates = true, LlmCandidates = llmCandidates };
                case "UnvisitedGoExplore(Position)": return new GoExploreAgent(CellMode.Position) { UnvisitedAmongCandidates = true, LlmCandidates = llmCandidates };
                case "UnvisitedGoExplore(Position+Keys)": return new GoExploreAgent(CellMode.PositionKeys) { UnvisitedAmongCandidates = true, LlmCandidates = llmCandidates };
                case "UnvisitedGoExplore(Full)": return new GoExploreAgent(CellMode.Full) { UnvisitedAmongCandidates = true, LlmCandidates = llmCandidates };
                default: return Create(kind);
            }
        }

        public static IAgent Create(string kind)
        {
            switch (kind)
            {
                case "Random": return new RandomAgent();
                case "GoExplore(Position)": return new GoExploreAgent(CellMode.Position);
                case "GoExplore(Position+Keys)": return new GoExploreAgent(CellMode.PositionKeys);
                case "GoExplore(Full)": return new GoExploreAgent(CellMode.Full);
                default: throw new ArgumentException("unknown agent kind: " + kind);
            }
        }
    }
}
