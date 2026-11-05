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

    /// <summary>
    /// L1: 手作りルールの Go-Explore。
    /// 選ぶ（訪問回数の少ないセルほど選ばれやすい）→ 戻る（スナップショット復元）→
    /// 探索する（ランダム行動を ExploreSteps 手）→ 保存する（新しいセルならアーカイブへ）。
    /// </summary>
    public sealed class GoExploreAgent : IAgent
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

        public GoExploreAgent(CellMode mode) { Mode = mode; }

        public string Name
        {
            get
            {
                switch (Mode)
                {
                    case CellMode.Position: return "GoExplore(Position)";
                    case CellMode.PositionKeys: return "GoExplore(Position+Keys)";
                    default: return "GoExplore(Full)";
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
        }

        public void Tick(Harness h)
        {
            if (remaining <= 0 || h.EpisodeDone)
            {
                // 選ぶ → 戻る
                var c = SelectCell();
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

    public static class AgentFactory
    {
        public static readonly string[] DefaultKinds = { "Random", "GoExplore(Position)", "GoExplore(Position+Keys)", "GoExplore(Full)" };

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
