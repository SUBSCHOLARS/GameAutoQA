using System;
using System.Collections.Generic;

namespace QaTestbed
{
    /// <summary>
    /// 自動判定（オラクル）。どのバグが仕込まれているかは見ずに、
    /// ゲームの仕様（こうなってはいけない）だけで異常を検出する。
    /// </summary>
    public static class Oracles
    {
        /// <summary>到達不能なはずの位置にいないか。</summary>
        public static string CheckPosition(GameMap m, GameState s)
        {
            char t = m.Get(s.X, s.Y);
            if (t == ' ') return "UNREACHABLE:void";
            if (t == 'X') return "UNREACHABLE:sealed";
            if (GameMap.IsWallChar(t)) return "UNREACHABLE:inwall";
            return null;
        }

        /// <summary>イベントの仕様違反（鍵なしで扉が開いた、など）。</summary>
        public static void CheckEvents(List<GameEvent> events, List<string> outSignatures)
        {
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e.Type == GameEventType.DoorOpened && !e.HadKey)
                    outSignatures.Add("INVARIANT:door_without_key:" + GameMap.ColorNames[e.Color]);
            }
        }

        /// <summary>状態の不変条件（同じ鍵は1回しか拾えない、など）。</summary>
        public static void CheckState(GameState s, List<string> outSignatures)
        {
            for (int c = 0; c < 3; c++)
                if (s.KeyPickCount[c] > 1)
                    outSignatures.Add("INVARIANT:duplicate_key:" + GameMap.ColorNames[c]);
        }

        public static string Crash(Exception e) { return "CRASH:" + e.GetType().Name; }
    }

    /// <summary>
    /// ハマり（ソフトロック）判定: 今の状態から、どんな操作をしてもゴールに辿り着けないかを
    /// 実際の遷移関数を使った幅優先探索で調べる。結果は抽象化した状態ごとにキャッシュする。
    /// 近似: 抽象状態にはコインの「枚数」だけを含め、どのコインを取ったかは含めない
    /// （このマップではコインがゴールへの経路を塞がないため問題にならない）。
    /// </summary>
    public sealed class SoftlockChecker
    {
        private readonly GameMap map;
        private readonly BugConfig bugs;
        private readonly int nodeLimit;
        private readonly Dictionary<long, bool?> cache = new Dictionary<long, bool?>();
        private readonly List<GameEvent> scratch = new List<GameEvent>();

        public int CacheSize { get { return cache.Count; } }

        public SoftlockChecker(GameMap map, BugConfig bugs, int nodeLimit = 20000)
        {
            this.map = map; this.bugs = bugs; this.nodeLimit = nodeLimit;
        }

        public static long AbstractKey(GameState s)
        {
            long k = s.X & 0xFF;
            k = (k << 8) | (uint)(s.Y & 0xFF);
            k = (k << 3) | (uint)s.DoorMask;
            k = (k << 3) | (uint)s.HeldMask;
            k = (k << 3) | (uint)(s.KeyTakenMask & 7);
            k = (k << 6) | (uint)Math.Min(s.Coins, 63);
            k = (k << 1) | (s.Frozen ? 1u : 0u);
            return k;
        }

        /// <summary>true = ハマっている / false = まだゴールに行ける / null = 探索上限に達して不明</summary>
        public bool? IsSoftlocked(GameState start)
        {
            if (start.Cleared) return false;
            long startKey = AbstractKey(start);
            bool? cached;
            if (cache.TryGetValue(startKey, out cached)) return cached;

            bool? result = Search(start);
            cache[startKey] = result;
            return result;
        }

        private bool? Search(GameState start)
        {
            if (start.X == map.ExitX && start.Y == map.ExitY) return false;
            var visited = new HashSet<long>();
            var queue = new Queue<GameState>();
            visited.Add(AbstractKey(start));
            queue.Enqueue(start.Clone());

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                for (int a = 0; a < 4; a++) // 移動のみ（ゴールのマスに着けば十分）
                {
                    var next = cur.Clone();
                    try { GameSim.Step(map, bugs, next, (GameAction)a, scratch); }
                    catch (Exception) { continue; } // クラッシュした枝は打ち切り
                    if (next.X == map.ExitX && next.Y == map.ExitY) return false;
                    long k = AbstractKey(next);
                    bool? known;
                    if (cache.TryGetValue(k, out known) && known == false) return false;
                    if (!visited.Add(k)) continue;
                    if (visited.Count > nodeLimit) return null;
                    queue.Enqueue(next);
                }
            }
            return true;
        }
    }
}
