using System;
using System.Collections.Generic;
using System.Text;

namespace QaTestbed
{
    /// <summary>
    /// 状態や盤面を LLM に渡す文章にする。
    /// バグの仕込み場所（'1'〜'4'）は見た目どおりの壁・床に置き換え、LLM に答えを見せない。
    /// </summary>
    public static class StateText
    {
        /// <summary>例: "pos=(5,8) keys_held=[green] doors_open=[green] keys_taken=[green] coins=2 frozen=no"</summary>
        public static string Describe(GameState s)
        {
            var sb = new StringBuilder();
            sb.Append("pos=(").Append(s.X).Append(',').Append(s.Y).Append(')');
            sb.Append(" keys_held=").Append(Colors(s.HeldMask));
            sb.Append(" doors_open=").Append(Colors(s.DoorMask));
            sb.Append(" keys_taken=").Append(Colors(s.KeyTakenMask));
            sb.Append(" coins=").Append(s.Coins);
            sb.Append(" frozen=").Append(s.Frozen ? "yes" : "no");
            return sb.ToString();
        }

        /// <summary>
        /// 周りの様子を文章にする（--llm-describe rich 用）。プレイヤーが画面から分かることと探索の記録だけを使う:
        ///   見た目どおりの地形（'1' '2' は壁、'3' '4' は床）・閉じた扉は通れない として、その状態の位置から歩数を数える。
        /// 例: "nearby: 5 unvisited tiles within 6 steps (closest 2 away); green key 3 steps; red door 7 steps (closed, no red key);
        ///      exit not reachable; 4 coins left (closest 2 steps)"
        /// </summary>
        public static string Surroundings(GameState s, GameMap map, Func<int, int, bool> wasVisited, int radius = 6)
        {
            int w = map.Width;
            int[] doorDist;
            int[] dist = WalkDistances(s, map, out doorDist);
            int closestUnvisited;
            int unvisitedNear = CountUnvisited(dist, map, wasVisited, radius, out closestUnvisited);

            var parts = new List<string>();
            parts.Add(closestUnvisited < 0
                ? "no unvisited tiles reachable"
                : unvisitedNear + " unvisited tiles within " + radius + " steps (closest " + closestUnvisited + " away)");
            for (int c = 0; c < 3; c++)
            {
                string col = GameMap.ColorNames[c];
                if ((s.KeyTakenMask & (1 << c)) == 0 && map.KeyX[c] >= 0)
                {
                    int dd = dist[map.KeyY[c] * w + map.KeyX[c]];
                    parts.Add(col + " key " + (dd < 0 ? "not reachable" : dd + " steps"));
                }
                if (map.DoorX[c] >= 0 && !s.DoorOpen[c])
                    parts.Add(col + " door " + (doorDist[c] < 0 ? "not reachable" : doorDist[c] + " steps")
                        + " (closed, " + (s.KeyHeld[c] ? "holding " + col + " key" : "no " + col + " key") + ")");
            }
            int ed = dist[map.ExitY * w + map.ExitX];
            parts.Add("exit " + (ed < 0 ? "not reachable" : ed + " steps"));
            int left = 0, closestCoin = -1;
            for (int i = 0; i < map.CoinX.Count; i++)
            {
                if ((s.CoinTakenMask & (1 << i)) != 0) continue;
                left++;
                int dd = dist[map.CoinY[i] * w + map.CoinX[i]];
                if (dd >= 0 && (closestCoin < 0 || dd < closestCoin)) closestCoin = dd;
            }
            parts.Add(left + " coins left" + (left == 0 ? "" : closestCoin < 0 ? " (none reachable)" : " (closest " + closestCoin + " steps)"));
            return "nearby: " + string.Join("; ", parts.ToArray());
        }

        /// <summary>
        /// 近くの未訪問マスの数（歩数 radius 以内）。UnvisitedGoExplore（LLM を使わない比較用のルール）が使う。
        /// Surroundings() の "N unvisited tiles within ..." と同じ値。
        /// </summary>
        public static int UnvisitedNearby(GameState s, GameMap map, Func<int, int, bool> wasVisited, int radius = 6)
        {
            int[] doorDist;
            int closest;
            return CountUnvisited(WalkDistances(s, map, out doorDist), map, wasVisited, radius, out closest);
        }

        /// <summary>見た目どおりの地形で、位置から各マスまでの歩数（到達不能は -1）。閉じた扉は通れず、扉までの歩数を doorDist に入れる。</summary>
        private static int[] WalkDistances(GameState s, GameMap map, out int[] doorDist)
        {
            int w = map.Width;
            var dist = new int[w * map.Height];
            for (int i = 0; i < dist.Length; i++) dist[i] = -1;
            doorDist = new int[] { -1, -1, -1 };
            var q = new Queue<int>();
            dist[s.Y * w + s.X] = 0;
            q.Enqueue(s.Y * w + s.X);
            int[] dx = { 0, 0, -1, 1 }, dy = { -1, 1, 0, 0 };
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                int cx = cur % w, cy = cur / w;
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dx[d], ny = cy + dy[d];
                    if (!map.InBounds(nx, ny) || dist[ny * w + nx] >= 0) continue;
                    char t = map.Get(nx, ny);
                    if (GameMap.IsWallChar(t) || t == ' ' || t == 'X') continue;
                    int door = GameMap.ColorOfDoor(t);
                    if (door >= 0 && !s.DoorOpen[door])
                    {
                        if (doorDist[door] < 0) doorDist[door] = dist[cur] + 1;
                        continue;
                    }
                    dist[ny * w + nx] = dist[cur] + 1;
                    q.Enqueue(ny * w + nx);
                }
            }
            return dist;
        }

        private static int CountUnvisited(int[] dist, GameMap map, Func<int, int, bool> wasVisited, int radius, out int closest)
        {
            int near = 0;
            closest = -1;
            for (int y = 0; y < map.Height; y++)
                for (int x = 0; x < map.Width; x++)
                {
                    int dd = dist[y * map.Width + x];
                    if (dd < 0 || wasVisited(x, y)) continue;
                    if (dd <= radius) near++;
                    if (closest < 0 || dd < closest) closest = dd;
                }
            return near;
        }

        private static string Colors(int mask)
        {
            var list = new List<string>();
            for (int c = 0; c < 3; c++) if ((mask & (1 << c)) != 0) list.Add(GameMap.ColorNames[c]);
            return "[" + string.Join(",", list.ToArray()) + "]";
        }

        /// <summary>初期状態の盤面（プレイヤーから見える形）。各行の先頭に y 座標をつける。</summary>
        public static string PublicMap(GameMap map)
        {
            var sb = new StringBuilder();
            sb.Append("    ");
            for (int x = 0; x < map.Width; x++) sb.Append((char)('0' + x % 10));
            sb.Append('\n');
            for (int y = 0; y < map.Height; y++)
            {
                sb.Append(y.ToString().PadLeft(2)).Append("  ");
                for (int x = 0; x < map.Width; x++)
                {
                    char c = map.Get(x, y);
                    switch (c)
                    {
                        case '1': case '2': c = '#'; break;
                        case '3': case '4': c = '.'; break;
                    }
                    sb.Append(c);
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
