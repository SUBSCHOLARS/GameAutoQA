using System;
using System.Collections.Generic;
using System.Text;

namespace QaTestbed
{
    public enum BugId
    {
        R1_WallHole = 0,
        R2_SecretWall = 1,
        S1_RedDoorWithoutKey = 2,
        S2_GreenKeyDuplicate = 3,
        C1_NullOnExit = 4,
        C2_CoinLogOverflow = 5,
        H1_BlueRoomTrap = 6,
        H2_FreezeTile = 7,
    }

    public enum BugCategory { Reachability, State, Crash, Softlock }

    public sealed class BugInfo
    {
        public BugId Id;
        public string Code;
        public BugCategory Category;
        public string CategoryJa;
        public string Title;
        public string Trigger;
        public string Oracle;

        public BugInfo(BugId id, string code, BugCategory cat, string catJa, string title, string trigger, string oracle)
        {
            Id = id; Code = code; Category = cat; CategoryJa = catJa; Title = title; Trigger = trigger; Oracle = oracle;
        }
    }

    public static class BugCatalog
    {
        public const int Count = 8;

        public static readonly BugInfo[] All =
        {
            new BugInfo(BugId.R1_WallHole, "R1", BugCategory.Reachability, "到達系",
                "外壁の穴", "大広間の西の外壁の1マスに当たり判定がない", "到達不能なはずの位置（壁の中・盤外）にいる"),
            new BugInfo(BugId.R2_SecretWall, "R2", BugCategory.Reachability, "到達系",
                "条件付きの壁抜け", "鍵を2本以上持って、赤い鍵の部屋の東の壁に体当たりする", "到達不能なはずの位置（封鎖された部屋）にいる"),
            new BugInfo(BugId.S1_RedDoorWithoutKey, "S1", BugCategory.State, "状態系",
                "鍵なしで赤い扉が開く", "コインを1枚以上持って、赤い鍵なしで赤い扉に体当たりする", "鍵を持たずに扉が開いた"),
            new BugInfo(BugId.S2_GreenKeyDuplicate, "S2", BugCategory.State, "状態系",
                "緑の鍵の増殖", "緑の鍵を取って部屋を出ると鍵が復活し、もう一度取れる", "同じ色の鍵を2回以上拾った"),
            new BugInfo(BugId.C1_NullOnExit, "C1", BugCategory.Crash, "クラッシュ",
                "コイン0枚でゴールすると落ちる", "コインを1枚も取らずにゴールで「調べる」", "例外（NullReferenceException）"),
            new BugInfo(BugId.C2_CoinLogOverflow, "C2", BugCategory.Crash, "クラッシュ",
                "4枚目のコインで落ちる", "コインを4枚目まで集める", "例外（IndexOutOfRangeException）"),
            new BugInfo(BugId.H1_BlueRoomTrap, "H1", BugCategory.Softlock, "ハマり",
                "青い部屋への閉じ込め", "青い扉の奥の小部屋に入ると、背後で扉が閉まる", "ゴールに到達できない状態になった"),
            new BugInfo(BugId.H2_FreezeTile, "H2", BugCategory.Softlock, "ハマり",
                "動けなくなる床", "大広間の中央付近の特定の床を踏むと、以後移動できない", "ゴールに到達できない状態になった"),
        };

        public static BugInfo Get(BugId id) { return All[(int)id]; }
        public static BugInfo Get(int index) { return All[index]; }

        /// <summary>
        /// 自動判定（オラクル）の報告を、どの仕込みバグによるものか分類する（採点用）。該当なしは -1。
        /// </summary>
        public static int Classify(GameMap map, string signature, int x, int y)
        {
            if (signature == "UNREACHABLE:void") return (int)BugId.R1_WallHole;
            if (signature == "UNREACHABLE:sealed") return (int)BugId.R2_SecretWall;
            if (signature == "UNREACHABLE:inwall")
            {
                if (x == map.HoleX && y == map.HoleY) return (int)BugId.R1_WallHole;
                if (x == map.SecretX && y == map.SecretY) return (int)BugId.R2_SecretWall;
                return -1;
            }
            if (signature == "INVARIANT:door_without_key:red") return (int)BugId.S1_RedDoorWithoutKey;
            if (signature == "INVARIANT:duplicate_key:green") return (int)BugId.S2_GreenKeyDuplicate;
            if (signature == "CRASH:NullReferenceException") return (int)BugId.C1_NullOnExit;
            if (signature == "CRASH:IndexOutOfRangeException") return (int)BugId.C2_CoinLogOverflow;
            if (signature == "SOFTLOCK")
            {
                if (x == map.FreezeX && y == map.FreezeY) return (int)BugId.H2_FreezeTile;
                if (map.IsBlueRoomInterior(x, y)) return (int)BugId.H1_BlueRoomTrap;
                return -1;
            }
            return -1;
        }
    }

    public enum BugMode { All, Single, RandomSubset }

    public struct BugConfig
    {
        public int Mask;

        public BugConfig(int mask) { Mask = mask; }

        public bool Has(BugId id) { return (Mask & (1 << (int)id)) != 0; }
        public bool Has(int index) { return (Mask & (1 << index)) != 0; }

        public static BugConfig None { get { return new BugConfig(0); } }
        public static BugConfig AllBugs { get { return new BugConfig((1 << BugCatalog.Count) - 1); } }
        public static BugConfig Single(BugId id) { return new BugConfig(1 << (int)id); }

        /// <summary>8個のうち k 個をランダムに有効化する。</summary>
        public static BugConfig RandomSubset(Random rng, int k)
        {
            var idx = new List<int>();
            for (int i = 0; i < BugCatalog.Count; i++) idx.Add(i);
            for (int i = idx.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                int t = idx[i]; idx[i] = idx[j]; idx[j] = t;
            }
            int mask = 0;
            for (int i = 0; i < Math.Min(k, idx.Count); i++) mask |= 1 << idx[i];
            return new BugConfig(mask);
        }

        public int EnabledCount
        {
            get { int n = 0; for (int i = 0; i < BugCatalog.Count; i++) if (Has(i)) n++; return n; }
        }

        public string ToCodes()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < BugCatalog.Count; i++)
            {
                if (!Has(i)) continue;
                if (sb.Length > 0) sb.Append('+');
                sb.Append(BugCatalog.All[i].Code);
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }
    }
}
