using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace QaTestbed
{
    public struct Detection
    {
        public string Signature;
        public int Bug;          // BugCatalog の index。分類できない場合は -1
        public long Step;        // 何手目で見つけたか（全エピソード通算）
        public double ElapsedMs; // 何ミリ秒で見つけたか
        public int X, Y;
    }

    public struct TriggerRecord
    {
        public int Bug;
        public long Step;
        public double ElapsedMs;
        public int X, Y;
    }

    /// <summary>
    /// テスト用の実行環境。エージェントも人間もこれを通してゲームを操作する。
    /// ・予算（ステップ数・経過時間）の計測
    /// ・オラクルによるバグ検出（バグごとに最初の1回だけ記録）
    /// ・バグを「起こした」記録（正解データ）
    /// ・網羅率（本来行ける床のうち、訪れた割合）
    /// ・スナップショットの保存と復元（Go-Explore の「戻る」）
    /// </summary>
    public sealed class Harness
    {
        public readonly GameMap Map;
        public readonly BugConfig Bugs;
        public GameState State { get; private set; }
        public bool EpisodeDone { get; private set; }
        public long TotalSteps { get; private set; }
        public int Episodes { get; private set; }

        /// <summary>何手ごとにハマり判定を行うか（0 で無効）。</summary>
        public int SoftlockInterval = 50;

        public readonly List<Detection> Detections = new List<Detection>();
        public readonly List<TriggerRecord> Triggers = new List<TriggerRecord>();
        public event Action<Detection> DetectionRecorded;
        public event Action<TriggerRecord> TriggerRecorded;

        private readonly Stopwatch clock = new Stopwatch();
        private readonly HashSet<string> seenDetectionKeys = new HashSet<string>();
        private int triggeredSoFar;
        private readonly List<GameEvent> events = new List<GameEvent>();
        private readonly List<string> signatures = new List<string>();
        private readonly SoftlockChecker softlock;

        private readonly bool[] intendedWalkable;   // バグなしで到達できる床
        private readonly bool[] visited;
        public readonly int IntendedTotal;
        public int IntendedVisited { get; private set; }
        public int UnintendedVisited { get; private set; }

        public double ElapsedMs { get { return clock.Elapsed.TotalMilliseconds; } }
        public double Coverage { get { return IntendedTotal == 0 ? 0 : (double)IntendedVisited / IntendedTotal; } }

        public Harness(GameMap map, BugConfig bugs, int softlockNodeLimit = 20000)
        {
            Map = map;
            Bugs = bugs;
            softlock = new SoftlockChecker(map, bugs, softlockNodeLimit);
            intendedWalkable = ComputeIntendedWalkable(map);
            visited = new bool[map.Width * map.Height];
            int n = 0;
            for (int i = 0; i < intendedWalkable.Length; i++) if (intendedWalkable[i]) n++;
            IntendedTotal = n;
            clock.Start();
            Reset();
        }

        public void Reset()
        {
            State = GameState.Initial(Map);
            EpisodeDone = false;
            Episodes++;
            MarkVisited();
        }

        public GameState Snapshot() { return State.Clone(); }

        public void Restore(GameState snapshot)
        {
            State = snapshot.Clone();
            EpisodeDone = State.Cleared;
        }

        public void Step(GameAction action)
        {
            if (EpisodeDone) return;
            TotalSteps++;
            try
            {
                GameSim.Step(Map, Bugs, State, action, events);
            }
            catch (Exception e)
            {
                RecordTriggers();
                Report(Oracles.Crash(e));
                EpisodeDone = true; // クラッシュしたらそのエピソードは終了
                return;
            }

            RecordTriggers();
            MarkVisited();

            string pos = Oracles.CheckPosition(Map, State);
            if (pos != null) Report(pos);

            signatures.Clear();
            Oracles.CheckEvents(events, signatures);
            Oracles.CheckState(State, signatures);
            for (int i = 0; i < signatures.Count; i++) Report(signatures[i]);

            if (State.Cleared) { EpisodeDone = true; return; }

            if (SoftlockInterval > 0 && TotalSteps % SoftlockInterval == 0)
            {
                bool? stuck = softlock.IsSoftlocked(State);
                if (stuck == true) Report("SOFTLOCK");
            }
        }

        /// <summary>今の状態がハマりかどうかを即座に判定する（人間のプレイ用など）。</summary>
        public bool CheckSoftlockNow()
        {
            if (EpisodeDone) return false;
            bool? stuck = softlock.IsSoftlocked(State);
            if (stuck == true) { Report("SOFTLOCK"); return true; }
            return false;
        }

        public bool IsDetected(int bug)
        {
            for (int i = 0; i < Detections.Count; i++) if (Detections[i].Bug == bug) return true;
            return false;
        }

        public int DetectedEnabledCount
        {
            get
            {
                int n = 0;
                for (int b = 0; b < BugCatalog.Count; b++) if (Bugs.Has(b) && IsDetected(b)) n++;
                return n;
            }
        }

        private void Report(string signature)
        {
            int bug = BugCatalog.Classify(Map, signature, State.X, State.Y);
            string key = bug >= 0 ? "BUG" + bug : signature + "@" + State.X + "," + State.Y;
            if (!seenDetectionKeys.Add(key)) return;
            var d = new Detection
            {
                Signature = signature, Bug = bug, Step = TotalSteps, ElapsedMs = ElapsedMs, X = State.X, Y = State.Y,
            };
            Detections.Add(d);
            if (DetectionRecorded != null) DetectionRecorded(d);
        }

        private void RecordTriggers()
        {
            int fresh = State.TriggeredMask & ~triggeredSoFar;
            if (fresh == 0) return;
            for (int b = 0; b < BugCatalog.Count; b++)
            {
                if ((fresh & (1 << b)) == 0) continue;
                var t = new TriggerRecord { Bug = b, Step = TotalSteps, ElapsedMs = ElapsedMs, X = State.X, Y = State.Y };
                Triggers.Add(t);
                if (TriggerRecorded != null) TriggerRecorded(t);
            }
            triggeredSoFar |= fresh;
        }

        private void MarkVisited()
        {
            if (!Map.InBounds(State.X, State.Y)) return;
            int i = State.Y * Map.Width + State.X;
            if (visited[i]) return;
            visited[i] = true;
            if (intendedWalkable[i]) IntendedVisited++;
            else UnintendedVisited++;
        }

        public bool WasVisited(int x, int y) { return Map.InBounds(x, y) && visited[y * Map.Width + x]; }
        public bool IsIntendedWalkable(int x, int y) { return Map.InBounds(x, y) && intendedWalkable[y * Map.Width + x]; }

        /// <summary>バグをすべて無効にした状態で、スタートから到達できる床を求める（網羅率の分母）。</summary>
        public static bool[] ComputeIntendedWalkable(GameMap map)
        {
            var result = new bool[map.Width * map.Height];
            var bugs = BugConfig.None;
            var visitedKeys = new HashSet<long>();
            var queue = new Queue<GameState>();
            var ev = new List<GameEvent>();
            var start = GameState.Initial(map);
            visitedKeys.Add(SoftlockChecker.AbstractKey(start));
            queue.Enqueue(start);
            result[start.Y * map.Width + start.X] = true;
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                for (int a = 0; a < 4; a++)
                {
                    var next = cur.Clone();
                    GameSim.Step(map, bugs, next, (GameAction)a, ev);
                    if (!visitedKeys.Add(SoftlockChecker.AbstractKey(next))) continue;
                    result[next.Y * map.Width + next.X] = true;
                    queue.Enqueue(next);
                }
            }
            return result;
        }
    }
}
