using System.Collections.Generic;
using UnityEngine;

namespace QaTestbed.UnityView
{
    /// <summary>
    /// エージェントの比較実験を Unity 上で回すためのコンポーネント。
    /// 使い方: 空の GameObject に付け、Inspector のコンポーネント名を右クリック →「Run Experiments」。
    /// （runOnStart を ON にして Play しても実行できる）
    /// 結果はプロジェクト直下の QaResults/日時/ に CSV で出力される。
    /// </summary>
    public class ExperimentRunnerBehaviour : MonoBehaviour
    {
        [Header("予算と試行回数")]
        [Tooltip("1回の実行で使う総ステップ数")]
        public long stepBudget = 20000;
        [Tooltip("手法ごとの試行回数（乱数シードの数）")]
        public int seeds = 10;
        [Tooltip("何ステップごとに網羅率・検出数を記録するか")]
        public int logInterval = 200;

        [Header("バグの入れ方")]
        public BugMode bugMode = BugMode.All;
        [Tooltip("RandomSubset のときに有効にするバグの数")]
        public int subsetSize = 4;

        [Header("比較する手法")]
        public bool includeRandom = true;
        public bool includeGoExplorePosition = true;
        public bool includeGoExplorePositionKeys = true;
        public bool includeGoExploreFull = true;

        [Header("その他")]
        public bool runOnStart = false;

        private void Start()
        {
            if (runOnStart) Run();
        }

        [ContextMenu("Run Experiments")]
        public void Run()
        {
            var kinds = new List<string>();
            if (includeRandom) kinds.Add("Random");
            if (includeGoExplorePosition) kinds.Add("GoExplore(Position)");
            if (includeGoExplorePositionKeys) kinds.Add("GoExplore(Position+Keys)");
            if (includeGoExploreFull) kinds.Add("GoExplore(Full)");

            var cfg = new ExperimentConfig
            {
                StepBudget = stepBudget,
                Seeds = seeds,
                LogInterval = logInterval,
                Mode = bugMode,
                SubsetSize = subsetSize,
                AgentKinds = kinds,
            };
            string dir = ExperimentRunner.Run(cfg, QaPaths.ResultDirectory(), msg => Debug.Log("[QaTestbed] " + msg));
            Debug.Log("[QaTestbed] done. results: " + dir);
        }
    }
}
