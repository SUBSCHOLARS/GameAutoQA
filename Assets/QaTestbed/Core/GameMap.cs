using System;
using System.Collections.Generic;

namespace QaTestbed
{
    /// <summary>
    /// 盤面（静的な地形）。y=0 が一番上の行。
    /// 文字の意味:
    ///   '#' 壁            '.' 床            ' ' 盤外（本来は到達不能）
    ///   'S' スタート      'E' ゴール        'X' 封鎖された部屋（本来は到達不能）
    ///   'r' 'g' 'b' 鍵（赤・緑・青）      'R' 'G' 'B' 扉（同じ色の鍵で開く）
    ///   'c' コイン
    ///   バグの仕込み場所（見た目は壁 or 床として描画される）:
    ///   '1' 穴の開いた外壁（R1）  '2' すり抜けられる壁（R2）
    ///   '3' 緑の鍵が復活する床（S2）  '4' 動けなくなる床（H2）
    /// </summary>
    public sealed class GameMap
    {
        public const int Red = 0, Green = 1, Blue = 2;
        public static readonly string[] ColorNames = { "red", "green", "blue" };

        public static readonly string[] DefaultLayout =
        {
            "                          ",
            " ########    ############ ",
            " #S...c.#    #....r.#XXX# ",
            " #..g...#    #......2XXX# ",
            " ####3###    #...c..#XXX# ",
            "    #.#      ###G######## ",
            " ####.##########.######   ",
            " #.....c.........c....#   ",
            " #.b..................#   ",
            " 1..........4.........#   ",
            " #....................#   ",
            " ###B#########R########   ",
            "   #.#       #.......#    ",
            "   #c#       #...E...#    ",
            "   ###       #########    ",
            "                          ",
        };

        public readonly int Width;
        public readonly int Height;
        private readonly char[,] grid;

        public int StartX = -1, StartY = -1;
        public int ExitX = -1, ExitY = -1;
        public readonly int[] KeyX = { -1, -1, -1 };
        public readonly int[] KeyY = { -1, -1, -1 };
        public readonly int[] DoorX = { -1, -1, -1 };
        public readonly int[] DoorY = { -1, -1, -1 };
        public readonly List<int> CoinX = new List<int>();
        public readonly List<int> CoinY = new List<int>();
        public int HoleX = -1, HoleY = -1;       // '1'
        public int SecretX = -1, SecretY = -1;   // '2'
        public int RespawnX = -1, RespawnY = -1; // '3'
        public int FreezeX = -1, FreezeY = -1;   // '4'

        public GameMap(string[] rows)
        {
            if (rows == null || rows.Length == 0) throw new ArgumentException("layout is empty");
            Height = rows.Length;
            int w = 0;
            foreach (var r in rows) w = Math.Max(w, r.Length);
            Width = w;
            grid = new char[Width, Height];

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    char c = x < rows[y].Length ? rows[y][x] : ' ';
                    grid[x, y] = c;
                    switch (c)
                    {
                        case 'S': StartX = x; StartY = y; break;
                        case 'E': ExitX = x; ExitY = y; break;
                        case 'c': CoinX.Add(x); CoinY.Add(y); break;
                        case '1': HoleX = x; HoleY = y; break;
                        case '2': SecretX = x; SecretY = y; break;
                        case '3': RespawnX = x; RespawnY = y; break;
                        case '4': FreezeX = x; FreezeY = y; break;
                        default:
                            int k = ColorOfKey(c);
                            if (k >= 0) { KeyX[k] = x; KeyY[k] = y; }
                            int d = ColorOfDoor(c);
                            if (d >= 0) { DoorX[d] = x; DoorY[d] = y; }
                            break;
                    }
                }
            }

            if (StartX < 0) throw new ArgumentException("layout has no 'S'");
            if (ExitX < 0) throw new ArgumentException("layout has no 'E'");
            if (CoinX.Count > 30) throw new ArgumentException("too many coins (max 30)");
        }

        public static GameMap CreateDefault() { return new GameMap(DefaultLayout); }

        public bool InBounds(int x, int y) { return x >= 0 && y >= 0 && x < Width && y < Height; }

        /// <summary>盤面の範囲外は壁として扱う。</summary>
        public char Get(int x, int y) { return InBounds(x, y) ? grid[x, y] : '#'; }

        public static int ColorOfKey(char c)
        {
            switch (c) { case 'r': return Red; case 'g': return Green; case 'b': return Blue; default: return -1; }
        }

        public static int ColorOfDoor(char c)
        {
            switch (c) { case 'R': return Red; case 'G': return Green; case 'B': return Blue; default: return -1; }
        }

        public static bool IsWallChar(char c) { return c == '#' || c == '1' || c == '2'; }

        public int CoinIndexAt(int x, int y)
        {
            for (int i = 0; i < CoinX.Count; i++)
                if (CoinX[i] == x && CoinY[i] == y) return i;
            return -1;
        }

        /// <summary>青い扉の奥（扉の真下に続く小部屋）かどうか。H1 の判定に使う。</summary>
        public bool IsBlueRoomInterior(int x, int y)
        {
            int dx = DoorX[Blue], dy = DoorY[Blue];
            if (dx < 0) return false;
            return x == dx && y > dy && !IsWallChar(Get(x, y)) && Get(x, y) != ' ';
        }
    }
}
