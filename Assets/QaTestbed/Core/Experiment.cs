using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace QaTestbed
{
    public sealed class ExperimentConfig
    {
        /// <summary>1回の実行で使う予算（総ステップ数）。</summary>
        public long StepBudget = 20000;
        /// <summary>手法ごとの試行回数（乱数シードの数）。</summary>
        public int Seeds = 10;
        /// <summary>何手ごとに網羅率などを記録するか。</summary>
        public int LogInterval = 200;
        /// <summary>何手ごとにハマり判定を行うか。</summary>
        public int SoftlockInterval = 25;
        public BugMode Mode = BugMode.All;
        /// <summary>RandomSubset のときに有効にするバグの数。</summary>
        public int SubsetSize = 4;
        public List<string> AgentKinds = new List<string>(AgentFactory.DefaultKinds);
        /// <summary>LlmGoExplore(...) で使う LLM。使わないなら null。</summary>
        public ILlmClient Llm;
        /// <summary>LLM に見せる候補セルの数。</summary>
        public int LlmCandidates = 8;
        /// <summary>true なら候補の説明に周りの様子を加える（--llm-describe rich）。</summary>
        public bool LlmRichDescription;
        /// <summary>LLM を使う手法で、何手ごとに途中経過を表示するか。</summary>
        public int LlmLogInterval = 2000;
    }

    /// <summary>
    /// 実験の実行と CSV 出力。出力先フォルダに以下を書き出す:
    ///   runs.csv       1回の実行ごとの概要
    ///   progress.csv   LogInterval 手ごとの網羅率・検出数（曲線の元データ）
    ///   detections.csv オラクルが検出したバグ（気づいた）
    ///   triggers.csv   バグのコードが実行された記録（起こした／正解データ）
    ///   bugs.csv       仕込んだバグの一覧
    ///   llm_decisions.csv 候補から選んだ記録（LlmGoExplore / UniformGoExplore のみ。LLM の理由つき）
    ///   meta.json      LLM を使ったときのモデル名・digest・生成設定・プロンプト（再現用）
    /// </summary>
    public static class ExperimentRunner
    {
        public static string Run(ExperimentConfig cfg, string outputRoot, Action<string> log)
        {
            if (log == null) log = delegate { };
            string dir = Path.Combine(outputRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            var map = GameMap.CreateDefault();

            using (var runs = new CsvWriter(Path.Combine(dir, "runs.csv")))
            using (var progress = new CsvWriter(Path.Combine(dir, "progress.csv")))
            using (var detections = new CsvWriter(Path.Combine(dir, "detections.csv")))
            using (var triggers = new CsvWriter(Path.Combine(dir, "triggers.csv")))
            {
                CsvWriter decisions = null;
                runs.Row("run_id", "agent", "seed", "bug_mode", "enabled", "enabled_count", "budget", "total_ms", "detected", "final_coverage", "unintended_tiles", "archive_cells", "episodes",
                    "llm_calls", "llm_fallbacks", "llm_ms");
                progress.Row("run_id", "agent", "seed", "step", "elapsed_ms", "coverage", "unintended_tiles", "archive_cells", "detected", "enabled_count");
                detections.Row("run_id", "agent", "seed", "signature", "bug", "category", "step", "elapsed_ms", "x", "y");
                triggers.Row("run_id", "agent", "seed", "bug", "category", "step", "elapsed_ms", "x", "y");

                int runId = 0;
                var bugConfigs = new List<KeyValuePair<string, Func<int, BugConfig>>>();
                switch (cfg.Mode)
                {
                    case BugMode.All:
                        bugConfigs.Add(new KeyValuePair<string, Func<int, BugConfig>>("all", s => BugConfig.AllBugs));
                        break;
                    case BugMode.Single:
                        for (int b = 0; b < BugCatalog.Count; b++)
                        {
                            int bb = b;
                            bugConfigs.Add(new KeyValuePair<string, Func<int, BugConfig>>("single", s => BugConfig.Single((BugId)bb)));
                        }
                        break;
                    case BugMode.RandomSubset:
                        bugConfigs.Add(new KeyValuePair<string, Func<int, BugConfig>>("subset" + cfg.SubsetSize,
                            s => BugConfig.RandomSubset(new Random(s + 100000), cfg.SubsetSize)));
                        break;
                }

                foreach (var kind in cfg.AgentKinds)
                {
                    foreach (var bc in bugConfigs)
                    {
                        for (int seed = 0; seed < cfg.Seeds; seed++)
                        {
                            runId++;
                            var bugs = bc.Value(seed);
                            var agent = AgentFactory.Create(kind, cfg.Llm, cfg.LlmCandidates, cfg.LlmRichDescription);
                            var llmStats = agent as ILlmAgentStats;
                            bool usesLlm = AgentFactory.NeedsLlm(kind);
                            var h = new Harness(map, bugs) { SoftlockInterval = cfg.SoftlockInterval };
                            agent.Begin(h, new Random(seed));

                            while (h.TotalSteps < cfg.StepBudget)
                            {
                                agent.Tick(h);
                                if (h.TotalSteps % cfg.LogInterval == 0)
                                    progress.Row(runId, agent.Name, seed, h.TotalSteps, F(h.ElapsedMs), F(h.Coverage),
                                        h.UnintendedVisited, agent.ArchiveSize, h.DetectedEnabledCount, bugs.EnabledCount);
                                if (usesLlm && h.TotalSteps % cfg.LlmLogInterval == 0)
                                    log(string.Format(CultureInfo.InvariantCulture, "  [{0}] {1} seed={2}: step {3}/{4}, detected {5}, llm calls {6}, {7:F0} s",
                                        runId, agent.Name, seed, h.TotalSteps, cfg.StepBudget, h.DetectedEnabledCount, llmStats.LlmCalls, h.ElapsedMs / 1000));
                            }

                            foreach (var d in h.Detections)
                                detections.Row(runId, agent.Name, seed, d.Signature, d.Bug >= 0 ? BugCatalog.Get(d.Bug).Code : "unknown",
                                    d.Bug >= 0 ? BugCatalog.Get(d.Bug).Category.ToString() : "", d.Step, F(d.ElapsedMs), d.X, d.Y);
                            var ge = agent as GoExploreAgent;
                            if (ge != null && ge.Decisions.Count > 0)
                            {
                                if (decisions == null)
                                {
                                    decisions = new CsvWriter(Path.Combine(dir, "llm_decisions.csv"));
                                    decisions.Row("run_id", "agent", "seed", "step", "candidates", "choice", "rule_index", "picked_rule_choice",
                                        "fallback", "chosen_state", "chosen_times_chosen", "chosen_times_seen", "picked_min_seen",
                                        "min_seen", "max_seen", "reason");
                                }
                                foreach (var d in ge.Decisions)
                                    decisions.Row(runId, agent.Name, seed, d.Step, d.Candidates, d.Choice, d.RuleIndex,
                                        d.Choice == d.RuleIndex ? 1 : 0, d.Fallback ? 1 : 0, d.ChosenState, d.ChosenTimesChosen,
                                        d.ChosenTimesSeen, d.ChosenTimesSeen == d.MinSeen ? 1 : 0, d.MinSeen, d.MaxSeen, d.Reason);
                            }
                            foreach (var t in h.Triggers)
                                triggers.Row(runId, agent.Name, seed, BugCatalog.Get(t.Bug).Code, BugCatalog.Get(t.Bug).Category.ToString(),
                                    t.Step, F(t.ElapsedMs), t.X, t.Y);

                            runs.Row(runId, agent.Name, seed, bc.Key, bugs.ToCodes(), bugs.EnabledCount, cfg.StepBudget, F(h.ElapsedMs),
                                h.DetectedEnabledCount, F(h.Coverage), h.UnintendedVisited, agent.ArchiveSize, h.Episodes,
                                usesLlm ? (object)llmStats.LlmCalls : "", usesLlm ? (object)llmStats.LlmFallbacks : "",
                                usesLlm ? F(llmStats.LlmMs) : "");

                            log(string.Format(CultureInfo.InvariantCulture, "[{0}] {1} seed={2} bugs={3}: detected {4}/{5}, coverage {6:P0}, {7:F0} ms",
                                runId, agent.Name, seed, bugs.ToCodes(), h.DetectedEnabledCount, bugs.EnabledCount, h.Coverage, h.ElapsedMs));
                        }
                    }
                }
                if (decisions != null) decisions.Dispose();
            }

            WriteBugCatalog(Path.Combine(dir, "bugs.csv"));
            if (cfg.Llm != null) WriteMeta(Path.Combine(dir, "meta.json"), cfg, map);
            log("results written to: " + dir);
            return dir;
        }

        public static void WriteBugCatalog(string path)
        {
            using (var w = new CsvWriter(path))
            {
                w.Row("code", "category", "category_ja", "title", "trigger", "oracle");
                foreach (var b in BugCatalog.All)
                    w.Row(b.Code, b.Category.ToString(), b.CategoryJa, b.Title, b.Trigger, b.Oracle);
            }
        }

        /// <summary>再現に必要な LLM の条件を書き出す。</summary>
        public static void WriteMeta(string path, ExperimentConfig cfg, GameMap map)
        {
            var c = cfg.Llm as CachingLlmClient;
            var o = (c != null ? c.Inner : cfg.Llm) as OllamaClient;
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"llm_model\": ").Append(MiniJson.Quote(cfg.Llm.ModelId)).Append(",\n");
            sb.Append("  \"llm_digest\": ").Append(MiniJson.Quote(cfg.Llm.ModelDigest)).Append(",\n");
            sb.Append("  \"llm_options\": ").Append(cfg.Llm.OptionsJson).Append(",\n");
            if (o != null) sb.Append("  \"ollama_version\": ").Append(MiniJson.Quote(o.ServerVersion)).Append(",\n");
            if (c != null)
            {
                sb.Append("  \"llm_cache\": ").Append(MiniJson.Quote(Path.GetFullPath(c.Path))).Append(",\n");
                sb.Append("  \"llm_cache_hits\": ").Append(c.Hits).Append(",\n");
                sb.Append("  \"llm_cache_misses\": ").Append(c.Misses).Append(",\n");
            }
            sb.Append("  \"llm_candidates\": ").Append(cfg.LlmCandidates).Append(",\n");
            sb.Append("  \"llm_describe\": ").Append(MiniJson.Quote(cfg.LlmRichDescription ? "rich" : "basic")).Append(",\n");
            sb.Append("  \"step_budget\": ").Append(cfg.StepBudget).Append(",\n");
            sb.Append("  \"seeds\": ").Append(cfg.Seeds).Append(",\n");
            sb.Append("  \"system_prompt\": ").Append(MiniJson.Quote(LlmPrompts.System(map, new GoExploreAgent(CellMode.Position).ExploreSteps, cfg.LlmRichDescription))).Append("\n");
            sb.Append("}\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }

    /// <summary>最小限の CSV 書き出し（UTF-8 BOM 付きなので Excel でも文字化けしない）。</summary>
    public sealed class CsvWriter : IDisposable
    {
        private readonly StreamWriter w;

        public CsvWriter(string path)
        {
            w = new StreamWriter(path, false, new UTF8Encoding(true));
        }

        public void Row(params object[] cells)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) sb.Append(',');
                string s = cells[i] == null ? "" : Convert.ToString(cells[i], CultureInfo.InvariantCulture);
                if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
                sb.Append(s);
            }
            w.WriteLine(sb.ToString());
        }

        public void Dispose() { w.Dispose(); }
    }
}
