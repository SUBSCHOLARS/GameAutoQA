using System.Collections.Generic;

namespace QaTestbed
{
    public enum GameAction { Up = 0, Down = 1, Left = 2, Right = 3, Interact = 4 }

    public enum GameEventType { Moved, Blocked, DoorOpened, KeyPicked, CoinPicked, Cleared }

    public struct GameEvent
    {
        public GameEventType Type;
        public int Color;
        public bool HadKey;

        public GameEvent(GameEventType type, int color = -1, bool hadKey = false)
        {
            Type = type; Color = color; HadKey = hadKey;
        }
    }

    /// <summary>
    /// ゲームのルール（1手ぶんの状態遷移）。UnityEngine に依存しない純粋な C# なので、
    /// Unity の外（コンソール）でも高速に実行でき、結果は乱数シードが同じなら毎回同じになる。
    /// バグは BugConfig で個別に ON/OFF できる。
    /// </summary>
    public static class GameSim
    {
        public const int CoinLogSize = 3;
        public const int ActionCount = 5;

        private static readonly int[] Dx = { 0, 0, -1, 1, 0 };
        private static readonly int[] Dy = { -1, 1, 0, 0, 0 };

        private static void Trigger(GameState s, BugId id) { s.TriggeredMask |= 1 << (int)id; }

        public static void Step(GameMap m, BugConfig bugs, GameState s, GameAction a, List<GameEvent> ev)
        {
            ev.Clear();
            s.EpisodeSteps++;
            if (s.Cleared) return;

            if (a == GameAction.Interact)
            {
                Interact(m, bugs, s, ev);
                return;
            }

            // H2: 凍結中は移動が効かない
            if (s.Frozen) { ev.Add(new GameEvent(GameEventType.Blocked)); return; }

            int nx = s.X + Dx[(int)a];
            int ny = s.Y + Dy[(int)a];
            if (!m.InBounds(nx, ny)) { ev.Add(new GameEvent(GameEventType.Blocked)); return; }

            char t = m.Get(nx, ny);
            switch (t)
            {
                case '#':
                    ev.Add(new GameEvent(GameEventType.Blocked));
                    return;

                case '1': // R1: 外壁の穴
                    if (bugs.Has(BugId.R1_WallHole)) { Trigger(s, BugId.R1_WallHole); break; }
                    ev.Add(new GameEvent(GameEventType.Blocked));
                    return;

                case '2': // R2: 鍵を2本以上持っているとすり抜ける壁
                    if (bugs.Has(BugId.R2_SecretWall) && s.HeldKeyCount >= 2) { Trigger(s, BugId.R2_SecretWall); break; }
                    ev.Add(new GameEvent(GameEventType.Blocked));
                    return;

                case 'R':
                case 'G':
                case 'B':
                {
                    int c = GameMap.ColorOfDoor(t);
                    if (s.DoorOpen[c]) break; // 開いている扉は通れる
                    if (s.KeyHeld[c])
                    {
                        // 体当たりで扉が開く（鍵は消費、プレイヤーはその場に留まる）
                        s.KeyHeld[c] = false;
                        s.DoorOpen[c] = true;
                        ev.Add(new GameEvent(GameEventType.DoorOpened, c, true));
                        return;
                    }
                    // S1: コインを持っていると、赤い扉が鍵なしで開いてしまう（条件判定の誤り）
                    if (c == GameMap.Red && bugs.Has(BugId.S1_RedDoorWithoutKey) && s.Coins >= 1)
                    {
                        Trigger(s, BugId.S1_RedDoorWithoutKey);
                        s.DoorOpen[c] = true;
                        ev.Add(new GameEvent(GameEventType.DoorOpened, c, false));
                        return;
                    }
                    ev.Add(new GameEvent(GameEventType.Blocked));
                    return;
                }
            }

            // ---- 移動が成立 ----
            int fromX = s.X, fromY = s.Y;
            s.X = nx; s.Y = ny;
            ev.Add(new GameEvent(GameEventType.Moved));

            // H1: 青い扉から奥の小部屋に入ると、背後で扉が閉じる（鍵は消費済みなので出られない）
            if (bugs.Has(BugId.H1_BlueRoomTrap)
                && fromX == m.DoorX[GameMap.Blue] && fromY == m.DoorY[GameMap.Blue]
                && m.IsBlueRoomInterior(nx, ny))
            {
                Trigger(s, BugId.H1_BlueRoomTrap);
                s.DoorOpen[GameMap.Blue] = false;
            }

            // 鍵を拾う
            int kc = GameMap.ColorOfKey(t);
            if (kc >= 0 && (s.KeyTakenMask & (1 << kc)) == 0)
            {
                s.KeyTakenMask |= 1 << kc;
                s.KeyHeld[kc] = true;
                s.KeyPickCount[kc]++;
                ev.Add(new GameEvent(GameEventType.KeyPicked, kc));
            }

            // コインを拾う
            if (t == 'c')
            {
                int ci = m.CoinIndexAt(nx, ny);
                if (ci >= 0 && (s.CoinTakenMask & (1 << ci)) == 0)
                {
                    s.CoinTakenMask |= 1 << ci;
                    if (bugs.Has(BugId.C2_CoinLogOverflow))
                    {
                        // C2: 記録用の配列は3枠しかないのに、範囲チェックを忘れている
                        if (s.Coins >= CoinLogSize) Trigger(s, BugId.C2_CoinLogOverflow);
                        s.CoinLog[s.Coins] = ci; // 4枚目で IndexOutOfRangeException
                    }
                    else if (s.Coins < CoinLogSize)
                    {
                        s.CoinLog[s.Coins] = ci;
                    }
                    s.Coins++;
                    ev.Add(new GameEvent(GameEventType.CoinPicked));
                }
            }

            // S2: 部屋の出口の床を踏むと、取ったはずの緑の鍵が復活する
            if (nx == m.RespawnX && ny == m.RespawnY && bugs.Has(BugId.S2_GreenKeyDuplicate)
                && (s.KeyTakenMask & (1 << GameMap.Green)) != 0)
            {
                Trigger(s, BugId.S2_GreenKeyDuplicate);
                s.KeyTakenMask &= ~(1 << GameMap.Green);
            }

            // H2: 特定の床を踏むと動けなくなる
            if (nx == m.FreezeX && ny == m.FreezeY && bugs.Has(BugId.H2_FreezeTile))
            {
                Trigger(s, BugId.H2_FreezeTile);
                s.Frozen = true;
            }
        }

        private static void Interact(GameMap m, BugConfig bugs, GameState s, List<GameEvent> ev)
        {
            if (s.X != m.ExitX || s.Y != m.ExitY)
            {
                ev.Add(new GameEvent(GameEventType.Blocked));
                return;
            }

            // クリア時に「最初に拾ったコイン」を表示する
            string firstCoin = s.Coins > 0 ? "coin#" + s.CoinLog[0] : null;
            if (firstCoin == null)
            {
                if (bugs.Has(BugId.C1_NullOnExit)) Trigger(s, BugId.C1_NullOnExit); // C1: null チェック漏れ
                else firstCoin = "none";
            }
            s.ResultText = "CLEAR first=" + firstCoin.ToUpperInvariant(); // C1 有効時は NullReferenceException
            s.Cleared = true;
            ev.Add(new GameEvent(GameEventType.Cleared));
        }

        public static GameAction ActionFromIndex(int i) { return (GameAction)i; }
    }
}
