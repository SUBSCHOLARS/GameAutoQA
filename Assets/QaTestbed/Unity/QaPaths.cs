using System.IO;
using UnityEngine;

namespace QaTestbed.UnityView
{
    public static class QaPaths
    {
        /// <summary>
        /// 結果の保存先。エディタではプロジェクト直下の QaResults フォルダ、
        /// ビルドしたアプリでは persistentDataPath/QaResults。
        /// </summary>
        public static string ResultDirectory()
        {
#if UNITY_EDITOR
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "QaResults"));
#else
            string dir = Path.Combine(Application.persistentDataPath, "QaResults");
#endif
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
