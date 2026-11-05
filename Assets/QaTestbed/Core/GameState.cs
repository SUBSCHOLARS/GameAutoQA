using System;

namespace QaTestbed
{
    /// <summary>
    /// ゲームの動的な状態。Clone() で丸ごとコピーできる（＝スナップショット保存・復元が可能）。
    /// </summary>
    public sealed class GameState
    {
        public int X, Y;
        public readonly bool[] DoorOpen = new bool[3];
        public readonly bool[] KeyHeld = new bool[3];
        public readonly int[] KeyPickCount = new int[3];
        /// <summary>bit c: 色 c の鍵が床から取られている</summary>
        public int KeyTakenMask;
        /// <summary>bit i: i 番目のコインが取られている</summary>
        public int CoinTakenMask;
        public int Coins;
        public readonly int[] CoinLog = new int[GameSim.CoinLogSize];
        public bool Frozen;
        public bool Cleared;
        public int EpisodeSteps;
        /// <summary>正解データ: bit i = バグ i のコードが実行された（＝バグを「起こした」）</summary>
        public int TriggeredMask;
        public string ResultText = "";

        public static GameState Initial(GameMap map)
        {
            var s = new GameState();
            s.X = map.StartX;
            s.Y = map.StartY;
            return s;
        }

        public GameState Clone()
        {
            var c = new GameState();
            c.X = X; c.Y = Y;
            Array.Copy(DoorOpen, c.DoorOpen, 3);
            Array.Copy(KeyHeld, c.KeyHeld, 3);
            Array.Copy(KeyPickCount, c.KeyPickCount, 3);
            c.KeyTakenMask = KeyTakenMask;
            c.CoinTakenMask = CoinTakenMask;
            c.Coins = Coins;
            Array.Copy(CoinLog, c.CoinLog, CoinLog.Length);
            c.Frozen = Frozen;
            c.Cleared = Cleared;
            c.EpisodeSteps = EpisodeSteps;
            c.TriggeredMask = TriggeredMask;
            c.ResultText = ResultText;
            return c;
        }

        public int HeldKeyCount
        {
            get { int n = 0; for (int i = 0; i < 3; i++) if (KeyHeld[i]) n++; return n; }
        }

        public int HeldMask
        {
            get { int m = 0; for (int i = 0; i < 3; i++) if (KeyHeld[i]) m |= 1 << i; return m; }
        }

        public int DoorMask
        {
            get { int m = 0; for (int i = 0; i < 3; i++) if (DoorOpen[i]) m |= 1 << i; return m; }
        }
    }
}
