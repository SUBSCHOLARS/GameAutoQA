using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace QaTestbed.UnityView
{
    public enum ViewMode { HumanPlay, WatchAgent }

    /// <summary>
    /// テスト用ゲームの表示と操作。素材は不要（スプライトはコードで生成する）。
    /// 使い方: 空の GameObject に付けて Play。
    ///   HumanPlay  : 人間がプレイし、操作ログと「バグ報告」を CSV に残す（人間の比較実験用）
    ///   WatchAgent : エージェントの探索の様子を見る（Go-Explore の「戻る」の様子が分かる）
    /// 操作: 矢印/WASD 移動, Space/E 調べる, B バグ報告, R やり直し, Esc 終了して保存
    /// </summary>
    public class GridGameView : MonoBehaviour
    {
        [Header("モード")]
        public ViewMode mode = ViewMode.HumanPlay;

        [Header("バグ")]
        public bool enableAllBugs = true;
        [Tooltip("ON にすると、8個のうち subsetSize 個をランダムに有効化する（enableAllBugs より優先）")]
        public bool randomSubset = false;
        public int subsetSize = 4;
        public int bugSeed = 0;

        [Header("人間のプレイ（HumanPlay）")]
        public string participantId = "P01";
        [Tooltip("例: beginner / gamer / developer")]
        public string skillLevel = "unknown";
        public float timeLimitSeconds = 600f;

        [Header("エージェントの観察（WatchAgent）")]
        public string agentKind = "GoExplore(Position+Keys)";
        public float stepsPerSecond = 15f;
        public int agentSeed = 0;

        private GameMap map;
        private Harness harness;
        private HumanSession session;
        private IAgent agent;
        private float stepAccumulator;
        private string savedPath = "";

        private Sprite square, circle;
        private Material spriteMaterial;
        private SpriteRenderer[,] tiles;
        private SpriteRenderer[] keyRenderers = new SpriteRenderer[3];
        private SpriteRenderer[] coinRenderers;
        private SpriteRenderer player;
        private GUIStyle hudStyle, bigStyle;

        private static readonly Color FloorColor = new Color32(0xD9, 0xD4, 0xC7, 0xFF);
        private static readonly Color VisitedTint = new Color32(0xC9, 0xD8, 0xE8, 0xFF);
        private static readonly Color WallColor = new Color32(0x3A, 0x3F, 0x4B, 0xFF);
        private static readonly Color VoidColor = new Color32(0x15, 0x18, 0x1E, 0xFF);
        private static readonly Color ExitColor = new Color32(0x6F, 0xC2, 0x9E, 0xFF);
        private static readonly Color CoinColor = new Color32(0xF2, 0xC2, 0x3A, 0xFF);
        private static readonly Color PlayerColor = new Color32(0xF2, 0x7A, 0x2E, 0xFF);
        private static readonly Color[] KeyColors =
        {
            new Color32(0xE0, 0x3C, 0x3C, 0xFF), new Color32(0x3C, 0xB4, 0x4B, 0xFF), new Color32(0x3C, 0x6E, 0xE0, 0xFF),
        };

        private void Start()
        {
            map = GameMap.CreateDefault();
            BugConfig bugs;
            if (randomSubset) bugs = BugConfig.RandomSubset(new System.Random(bugSeed), subsetSize);
            else bugs = enableAllBugs ? BugConfig.AllBugs : BugConfig.None;

            harness = new Harness(map, bugs);
            if (mode == ViewMode.HumanPlay)
            {
                harness.SoftlockInterval = 0; // 人間のプレイでは下で一定間隔ごとに判定する
                session = new HumanSession(harness, participantId, skillLevel);
            }
            else
            {
                agent = AgentFactory.Create(agentKind);
                agent.Begin(harness, new System.Random(agentSeed));
            }

            CreateSprites();
            BuildBoard();
            SetupCamera();
            Debug.Log("[QaTestbed] bugs enabled: " + bugs.ToCodes());
        }

        private void Update()
        {
            if (mode == ViewMode.HumanPlay) UpdateHuman();
            else UpdateAgent();
            Render();
        }

        // ---------------- 人間のプレイ ----------------
        private int lastSoftlockCheckStep = -1;

        private void UpdateHuman()
        {
            if (session.Finished) return;

            if (Pressed(KeyName.Finish)) { FinishHuman("esc"); return; }
            if (session.ElapsedSeconds >= timeLimitSeconds) { FinishHuman("time_limit"); return; }
            if (Pressed(KeyName.Report)) session.Report();
            if (Pressed(KeyName.Restart)) session.Restart();

            GameAction? a = ReadAction();
            if (a.HasValue) session.Act(a.Value);

            // 10手ごとにハマり判定（本人には見せない）
            if (harness.TotalSteps % 10 == 0 && harness.TotalSteps != lastSoftlockCheckStep)
            {
                lastSoftlockCheckStep = (int)harness.TotalSteps;
                harness.CheckSoftlockNow();
            }
        }

        private void FinishHuman(string reason)
        {
            session.Finish(reason);
            savedPath = session.WriteCsv(QaPaths.ResultDirectory());
            Debug.Log("[QaTestbed] human session saved: " + savedPath);
        }

        private void OnApplicationQuit()
        {
            if (session != null && !session.Finished) FinishHuman("quit");
        }

        // ---------------- エージェントの観察 ----------------
        private void UpdateAgent()
        {
            stepAccumulator += Time.deltaTime * stepsPerSecond;
            int n = Mathf.Min((int)stepAccumulator, 1000);
            stepAccumulator -= n;
            for (int i = 0; i < n; i++) agent.Tick(harness);
        }

        // ---------------- 入力（新旧どちらの Input にも対応） ----------------
        private enum KeyName { Up, Down, Left, Right, Interact, Report, Restart, Finish }

        private GameAction? ReadAction()
        {
            if (Pressed(KeyName.Up)) return GameAction.Up;
            if (Pressed(KeyName.Down)) return GameAction.Down;
            if (Pressed(KeyName.Left)) return GameAction.Left;
            if (Pressed(KeyName.Right)) return GameAction.Right;
            if (Pressed(KeyName.Interact)) return GameAction.Interact;
            return null;
        }

#if ENABLE_INPUT_SYSTEM
        private static bool Pressed(KeyName k)
        {
            var kb = Keyboard.current;
            if (kb == null) return false;
            switch (k)
            {
                case KeyName.Up: return kb.upArrowKey.wasPressedThisFrame || kb.wKey.wasPressedThisFrame;
                case KeyName.Down: return kb.downArrowKey.wasPressedThisFrame || kb.sKey.wasPressedThisFrame;
                case KeyName.Left: return kb.leftArrowKey.wasPressedThisFrame || kb.aKey.wasPressedThisFrame;
                case KeyName.Right: return kb.rightArrowKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame;
                case KeyName.Interact: return kb.spaceKey.wasPressedThisFrame || kb.eKey.wasPressedThisFrame;
                case KeyName.Report: return kb.bKey.wasPressedThisFrame;
                case KeyName.Restart: return kb.rKey.wasPressedThisFrame;
                case KeyName.Finish: return kb.escapeKey.wasPressedThisFrame;
            }
            return false;
        }
#else
        private static bool Pressed(KeyName k)
        {
            switch (k)
            {
                case KeyName.Up: return Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W);
                case KeyName.Down: return Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S);
                case KeyName.Left: return Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A);
                case KeyName.Right: return Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D);
                case KeyName.Interact: return Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.E);
                case KeyName.Report: return Input.GetKeyDown(KeyCode.B);
                case KeyName.Restart: return Input.GetKeyDown(KeyCode.R);
                case KeyName.Finish: return Input.GetKeyDown(KeyCode.Escape);
            }
            return false;
        }
#endif

        // ---------------- 描画 ----------------
        private void CreateSprites()
        {
            var sq = new Texture2D(8, 8) { filterMode = FilterMode.Point };
            var px = new Color[64];
            for (int i = 0; i < px.Length; i++) px[i] = Color.white;
            sq.SetPixels(px);
            sq.Apply();
            square = Sprite.Create(sq, new Rect(0, 0, 8, 8), new Vector2(0.5f, 0.5f), 8f);

            const int size = 32;
            var ci = new Texture2D(size, size) { filterMode = FilterMode.Bilinear };
            var cpx = new Color[size * size];
            float r = size / 2f - 1f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - size / 2f, dy = y + 0.5f - size / 2f;
                float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                cpx[y * size + x] = new Color(1, 1, 1, a);
            }
            ci.SetPixels(cpx);
            ci.Apply();
            circle = Sprite.Create(ci, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);

            // URP の 2D ライトが無いシーンでも暗くならないよう、ライティングなしのシェーダーを使う
            Shader sh = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh != null) spriteMaterial = new Material(sh);
        }

        private SpriteRenderer MakeRenderer(string name, Sprite sprite, int x, int y, float scale, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(x, -y, 0);
            go.transform.localScale = new Vector3(scale, scale, 1);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = order;
            if (spriteMaterial != null) sr.sharedMaterial = spriteMaterial;
            return sr;
        }

        private void BuildBoard()
        {
            tiles = new SpriteRenderer[map.Width, map.Height];
            for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
                tiles[x, y] = MakeRenderer("Tile_" + x + "_" + y, square, x, y, 0.94f, 0);

            for (int c = 0; c < 3; c++)
            {
                if (map.KeyX[c] < 0) continue;
                keyRenderers[c] = MakeRenderer("Key_" + GameMap.ColorNames[c], square, map.KeyX[c], map.KeyY[c], 0.45f, 1);
                keyRenderers[c].color = KeyColors[c];
            }

            coinRenderers = new SpriteRenderer[map.CoinX.Count];
            for (int i = 0; i < coinRenderers.Length; i++)
            {
                coinRenderers[i] = MakeRenderer("Coin_" + i, circle, map.CoinX[i], map.CoinY[i], 0.45f, 1);
                coinRenderers[i].color = CoinColor;
            }

            player = MakeRenderer("Player", circle, map.StartX, map.StartY, 0.8f, 3);
            player.color = PlayerColor;
        }

        private void SetupCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
            }
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = VoidColor;
            Vector3 center = transform.position + new Vector3((map.Width - 1) / 2f, -(map.Height - 1) / 2f, -10f);
            cam.transform.position = center;
            float byHeight = map.Height / 2f + 1.5f;
            float byWidth = (map.Width / 2f + 0.5f) / Mathf.Max(cam.aspect, 0.1f);
            cam.orthographicSize = Mathf.Max(byHeight, byWidth);
        }

        private void Render()
        {
            var s = harness.State;
            bool showVisited = mode == ViewMode.WatchAgent;
            for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
                tiles[x, y].color = TileColor(map.Get(x, y), s, x, y, showVisited);

            for (int c = 0; c < 3; c++)
                if (keyRenderers[c] != null) keyRenderers[c].enabled = (s.KeyTakenMask & (1 << c)) == 0;
            for (int i = 0; i < coinRenderers.Length; i++)
                coinRenderers[i].enabled = (s.CoinTakenMask & (1 << i)) == 0;

            player.transform.localPosition = new Vector3(s.X, -s.Y, 0);
        }

        private Color TileColor(char t, GameState s, int x, int y, bool showVisited)
        {
            if (GameMap.IsWallChar(t) || t == 'X') return WallColor;   // バグの仕込み場所も見た目は壁
            if (t == ' ') return VoidColor;
            if (t == 'E') return ExitColor;
            int d = GameMap.ColorOfDoor(t);
            if (d >= 0)
            {
                var k = KeyColors[d];
                return s.DoorOpen[d] ? Color.Lerp(k, FloorColor, 0.65f) : new Color(k.r * 0.8f, k.g * 0.8f, k.b * 0.8f, 1f);
            }
            if (showVisited && harness.WasVisited(x, y)) return VisitedTint;
            return FloorColor;
        }

        private void OnGUI()
        {
            if (harness == null) return;
            if (hudStyle == null)
            {
                hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 18 };
                hudStyle.normal.textColor = Color.white;
                bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 30, alignment = TextAnchor.MiddleCenter };
                bigStyle.normal.textColor = Color.white;
            }
            var s = harness.State;
            string keys = (s.KeyHeld[0] ? "R " : "") + (s.KeyHeld[1] ? "G " : "") + (s.KeyHeld[2] ? "B" : "");

            if (mode == ViewMode.HumanPlay)
            {
                float left = Mathf.Max(0f, timeLimitSeconds - (float)session.ElapsedSeconds);
                GUI.Label(new Rect(12, 8, 900, 26),
                    string.Format("Time {0:0}s   Coins {1}   Keys {2}   Reports {3}", left, s.Coins, keys, session.Reports), hudStyle);
                GUI.Label(new Rect(12, 32, 1200, 26),
                    "Arrows/WASD: move   Space/E: interact   B: report a bug   R: restart   Esc: finish & save", hudStyle);

                GUIStyle gUIStyle = new GUIStyle(GUI.skin.label) { fontSize = 40, alignment = TextAnchor.MiddleCenter };
                gUIStyle.normal.textColor = Color.black;

                if (session.Finished)
                {
                    GUI.Label(new Rect(0, 0, Screen.width, Screen.height), "Session finished. Saved:\n" + savedPath, gUIStyle);
                }
                else if (s.Cleared)
                {
                    GUI.Label(new Rect(0, 0, Screen.width, Screen.height), "CLEAR!  (R: play again)", gUIStyle);
                }
                else if (harness.EpisodeDone)
                {
                    GUI.Label(new Rect(0, 0, Screen.width, Screen.height), "The game stopped with an error.  (R: restart)", gUIStyle);
                }
            }
            else
            {
                GUI.Label(new Rect(12, 8, 1200, 26),
                    string.Format("{0}   steps {1}   coverage {2:P0}   archive {3}   detected {4}/{5}",
                        agent.Name, harness.TotalSteps, harness.Coverage, agent.ArchiveSize,
                        harness.DetectedEnabledCount, harness.Bugs.EnabledCount), hudStyle);
                string found = "";
                foreach (var d in harness.Detections)
                    found += (d.Bug >= 0 ? BugCatalog.Get(d.Bug).Code : "?") + "@" + d.Step + "  ";
                GUI.Label(new Rect(12, 32, 1400, 26), "found: " + found, hudStyle);
            }
        }
    }
}
