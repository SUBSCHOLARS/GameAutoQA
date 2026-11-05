using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace QaTestbed
{
    /// <summary>
    /// 人間によるテストプレイの記録。
    /// ・trigger : バグのコードが実行された（＝バグを「起こした」）※本人には見せない
    /// ・oracle  : 自動判定が異常を検出した ※本人には見せない
    /// ・report  : 本人が「バグを見つけた」と思ってキーを押した（＝「気づいた」）
    /// ・restart / clear / finish : 進行の記録
    /// </summary>
    public sealed class HumanSession
    {
        public readonly Harness Harness;
        public readonly string ParticipantId;
        public readonly string SkillLevel;
        public int Reports { get; private set; }
        public bool Finished { get; private set; }

        private readonly List<object[]> rows = new List<object[]>();
        private bool clearLogged;

        public HumanSession(Harness harness, string participantId, string skillLevel)
        {
            Harness = harness;
            ParticipantId = participantId;
            SkillLevel = skillLevel;
            harness.TriggerRecorded += t => Add("trigger", BugCatalog.Get(t.Bug).Code, BugCatalog.Get(t.Bug).Code, t.X, t.Y);
            harness.DetectionRecorded += d => Add("oracle", d.Signature, d.Bug >= 0 ? BugCatalog.Get(d.Bug).Code : "unknown", d.X, d.Y);
            Add("start", "bugs=" + harness.Bugs.ToCodes(), "", harness.State.X, harness.State.Y);
        }

        public double ElapsedSeconds { get { return Harness.ElapsedMs / 1000.0; } }

        public void Act(GameAction a)
        {
            if (Finished || Harness.EpisodeDone) return;
            Harness.Step(a);
            if (Harness.State.Cleared && !clearLogged)
            {
                clearLogged = true;
                Add("clear", Harness.State.ResultText, "", Harness.State.X, Harness.State.Y);
            }
        }

        /// <summary>本人がバグ報告キーを押した。説明は後で紙やフォームに書いてもらう想定。</summary>
        public void Report()
        {
            if (Finished) return;
            Reports++;
            Add("report", "report#" + Reports, "", Harness.State.X, Harness.State.Y);
        }

        public void Restart()
        {
            if (Finished) return;
            Add("restart", "", "", Harness.State.X, Harness.State.Y);
            Harness.Reset();
            clearLogged = false;
        }

        public void Finish(string reason)
        {
            if (Finished) return;
            Add("finish", reason, "", Harness.State.X, Harness.State.Y);
            Finished = true;
        }

        public string WriteCsv(string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, string.Format(CultureInfo.InvariantCulture, "human_{0}_{1:yyyyMMdd_HHmmss}.csv", Safe(ParticipantId), DateTime.Now));
            using (var w = new CsvWriter(path))
            {
                w.Row("participant", "skill", "t_sec", "step", "event", "detail", "bug", "x", "y");
                foreach (var r in rows) w.Row(r);
            }
            return path;
        }

        private void Add(string ev, string detail, string bug, int x, int y)
        {
            rows.Add(new object[]
            {
                ParticipantId, SkillLevel, ElapsedSeconds.ToString("0.00", CultureInfo.InvariantCulture),
                Harness.TotalSteps, ev, detail, bug, x, y,
            });
        }

        private static string Safe(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
