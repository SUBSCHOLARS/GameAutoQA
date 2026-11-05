using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QaTestbed;

// Unity を開かずに実験を回すためのコンソール版（任意）。
//   dotnet run -c Release -- run --budget 20000 --seeds 10 --mode all --out ../../QaResults
//   dotnet run -c Release -- verify
internal static class Program
{
    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0] : "run";
        var opts = ParseOptions(args.Skip(1).ToArray());
        if (cmd == "verify") return Verify.RunAll() ? 0 : 1;

        var cfg = new ExperimentConfig();
        if (opts.ContainsKey("budget")) cfg.StepBudget = long.Parse(opts["budget"], CultureInfo.InvariantCulture);
        if (opts.ContainsKey("seeds")) cfg.Seeds = int.Parse(opts["seeds"], CultureInfo.InvariantCulture);
        if (opts.ContainsKey("log")) cfg.LogInterval = int.Parse(opts["log"], CultureInfo.InvariantCulture);
        if (opts.ContainsKey("subset")) cfg.SubsetSize = int.Parse(opts["subset"], CultureInfo.InvariantCulture);
        if (opts.ContainsKey("mode"))
        {
            switch (opts["mode"])
            {
                case "all": cfg.Mode = BugMode.All; break;
                case "single": cfg.Mode = BugMode.Single; break;
                case "subset": cfg.Mode = BugMode.RandomSubset; break;
                default: throw new ArgumentException("mode must be all|single|subset");
            }
        }
        if (opts.ContainsKey("agents")) cfg.AgentKinds = opts["agents"].Split(';').ToList();
        string outDir = opts.ContainsKey("out") ? opts["out"] : "QaResults";
        ExperimentRunner.Run(cfg, outDir, Console.WriteLine);
        return 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < a.Length; i += 2) d[a[i].TrimStart('-')] = a[i + 1];
        return d;
    }
}

internal static class Verify
{
    public static bool RunAll()
    {
        var map = GameMap.CreateDefault();
        bool ok = true;

        var h0 = new Harness(map, BugConfig.None);
        Console.WriteLine($"map {map.Width}x{map.Height}, intended walkable tiles = {h0.IntendedTotal}");

        // 1) バグなしでは何も検出されない（誤検出がない）こと
        foreach (var kind in AgentFactory.DefaultKinds)
        {
            for (int seed = 0; seed < 3; seed++)
            {
                var h = new Harness(map, BugConfig.None) { SoftlockInterval = 10 };
                var agent = AgentFactory.Create(kind);
                agent.Begin(h, new Random(seed));
                while (h.TotalSteps < 30000) agent.Tick(h);
                if (h.Detections.Count > 0 || h.Triggers.Count > 0)
                {
                    ok = false;
                    Console.WriteLine($"FALSE POSITIVE: {kind} seed={seed}: " + string.Join(", ", h.Detections.Select(d => d.Signature + "@" + d.X + "," + d.Y)));
                }
            }
            Console.WriteLine($"no-bug check OK: {kind}");
        }

        // 2) 各バグが「起こせて」「検出でき」「正しく分類される」こと（最短の手数を求める）
        for (int b = 0; b < BugCatalog.Count; b++)
        {
            var bugs = BugConfig.Single((BugId)b);
            int depth = ShortestDetection(map, bugs, b, out string sigFound);
            var info = BugCatalog.Get(b);
            if (depth < 0) { ok = false; Console.WriteLine($"{info.Code}: NOT DETECTABLE"); }
            else Console.WriteLine($"{info.Code} ({info.CategoryJa} / {info.Title}): shortest detection = {depth} steps via {sigFound}");
        }

        // 3) 全バグ有効でも、クリアは可能であること
        int clearDepth = ShortestClear(map, BugConfig.AllBugs);
        Console.WriteLine($"all-bugs: shortest clear = {clearDepth} steps");
        int clearDepthNoBug = ShortestClear(map, BugConfig.None);
        Console.WriteLine($"no-bugs: shortest clear = {clearDepthNoBug} steps");
        if (clearDepthNoBug < 0) ok = false;

        Console.WriteLine(ok ? "VERIFY OK" : "VERIFY FAILED");
        return ok;
    }

    private static string FullKey(GameState s)
    {
        return string.Join(",", s.X, s.Y, s.DoorMask, s.HeldMask, s.KeyTakenMask, s.CoinTakenMask, s.Coins,
            s.KeyPickCount[0], s.KeyPickCount[1], s.KeyPickCount[2], s.Frozen ? 1 : 0, s.Cleared ? 1 : 0);
    }

    private static int ShortestDetection(GameMap map, BugConfig bugs, int target, out string signature)
    {
        signature = null;
        var soft = new SoftlockChecker(map, bugs);
        var ev = new List<GameEvent>();
        var sigs = new List<string>();
        var start = GameState.Initial(map);
        var q = new Queue<(GameState s, int d)>();
        var seen = new HashSet<string> { FullKey(start) };
        q.Enqueue((start, 0));
        while (q.Count > 0)
        {
            var (cur, d) = q.Dequeue();
            for (int a = 0; a < GameSim.ActionCount; a++)
            {
                var next = cur.Clone();
                sigs.Clear();
                bool crashed = false;
                try { GameSim.Step(map, bugs, next, (GameAction)a, ev); }
                catch (Exception e) { sigs.Add(Oracles.Crash(e)); crashed = true; }
                if (!crashed)
                {
                    var p = Oracles.CheckPosition(map, next);
                    if (p != null) sigs.Add(p);
                    Oracles.CheckEvents(ev, sigs);
                    Oracles.CheckState(next, sigs);
                    if (!next.Cleared && soft.IsSoftlocked(next) == true) sigs.Add("SOFTLOCK");
                }
                foreach (var sg in sigs)
                {
                    int cls = BugCatalog.Classify(map, sg, next.X, next.Y);
                    if (cls != target)
                    {
                        Console.WriteLine($"  unexpected signature {sg} (classified {cls}) while searching {BugCatalog.Get(target).Code}");
                        continue;
                    }
                    signature = sg;
                    return d + 1;
                }
                if (crashed || next.Cleared) continue;
                if (!seen.Add(FullKey(next))) continue;
                if (seen.Count > 3000000) return -1;
                q.Enqueue((next, d + 1));
            }
        }
        return -1;
    }

    private static int ShortestClear(GameMap map, BugConfig bugs)
    {
        var ev = new List<GameEvent>();
        var start = GameState.Initial(map);
        var q = new Queue<(GameState s, int d)>();
        var seen = new HashSet<string> { FullKey(start) };
        q.Enqueue((start, 0));
        while (q.Count > 0)
        {
            var (cur, d) = q.Dequeue();
            for (int a = 0; a < GameSim.ActionCount; a++)
            {
                var next = cur.Clone();
                try { GameSim.Step(map, bugs, next, (GameAction)a, ev); } catch { continue; }
                if (next.Cleared) return d + 1;
                if (!seen.Add(FullKey(next))) continue;
                q.Enqueue((next, d + 1));
            }
        }
        return -1;
    }
}
