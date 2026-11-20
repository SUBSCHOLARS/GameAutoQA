using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace QaTestbed
{
    /// <summary>
    /// LLM の呼び出し口。Complete() はプロンプトを渡して応答の文字列を返す。
    /// 実装は Ollama（ローカル）と、その入出力を記録・再生するキャッシュの2つ。
    /// </summary>
    public interface ILlmClient
    {
        /// <summary>モデル名（例 qwen2.5:7b-instruct）。</summary>
        string ModelId { get; }
        /// <summary>重みのハッシュ。分からない場合は空文字。</summary>
        string ModelDigest { get; }
        /// <summary>生成の設定（温度・シードなど）を JSON にしたもの。キャッシュのキーにも使う。</summary>
        string OptionsJson { get; }
        string Complete(string system, string prompt);
    }

    /// <summary>Ollama の生成設定。再生時にキーを作り直すため、記録時と同じ値を使うこと。</summary>
    public sealed class OllamaOptions
    {
        public int Seed = 0;
        public int NumCtx = 4096;
        /// <summary>理由（短い1文）＋番号が収まる長さ。</summary>
        public int NumPredict = 128;

        public string ToJson()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{{\"temperature\":0,\"top_k\":1,\"seed\":{0},\"num_ctx\":{1},\"num_predict\":{2}}}", Seed, NumCtx, NumPredict);
        }
    }

    /// <summary>
    /// Ollama（http://localhost:11434）の /api/chat を同期で呼ぶ。
    /// 再現性のため temperature=0・シード固定・文脈長固定・JSON 出力で呼ぶ。
    /// </summary>
    public sealed class OllamaClient : ILlmClient
    {
        private readonly HttpClient http;
        private readonly string baseUrl;

        public string ModelId { get; private set; }
        public string ModelDigest { get; private set; }
        public string ServerVersion { get; private set; }
        public string OptionsJson { get; private set; }

        public OllamaClient(string model, string baseUrl = "http://localhost:11434", OllamaOptions options = null)
        {
            ModelId = model;
            this.baseUrl = baseUrl.TrimEnd('/');
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            OptionsJson = (options ?? new OllamaOptions()).ToJson();

            // 実験の記録用: サーバーのバージョンとモデルの digest を取っておく
            ServerVersion = MiniJson.GetString(Get("/api/version"), "version") ?? "";
            ModelDigest = FindDigest(Get("/api/tags"), model);
            if (ModelDigest == null)
                throw new InvalidOperationException("Ollama にモデル '" + model + "' がありません。先に `ollama pull " + model + "` を実行してください。");
        }

        public string Complete(string system, string prompt)
        {
            string body = "{\"model\":" + MiniJson.Quote(ModelId)
                + ",\"stream\":false,\"format\":\"json\",\"options\":" + OptionsJson
                + ",\"messages\":[{\"role\":\"system\",\"content\":" + MiniJson.Quote(system)
                + "},{\"role\":\"user\",\"content\":" + MiniJson.Quote(prompt) + "}]}";
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var res = http.PostAsync(baseUrl + "/api/chat", content).GetAwaiter().GetResult();
            string text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Ollama error " + (int)res.StatusCode + ": " + text);
            return MiniJson.GetString(text, "content") ?? "";
        }

        private string Get(string path)
        {
            try
            {
                return http.GetStringAsync(baseUrl + path).GetAwaiter().GetResult();
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException("Ollama（" + baseUrl + "）に接続できません。`ollama serve` が動いているか確認してください。", e);
            }
        }

        /// <summary>/api/tags の一覧から、名前が一致するモデルの digest を探す。</summary>
        private static string FindDigest(string tagsJson, string model)
        {
            string[] names = { model, model.Contains(":") ? null : model + ":latest" };
            foreach (var n in names)
            {
                if (n == null) continue;
                for (int i = tagsJson.IndexOf("\"name\"", StringComparison.Ordinal); i >= 0;
                     i = tagsJson.IndexOf("\"name\"", i + 1, StringComparison.Ordinal))
                {
                    string rest = tagsJson.Substring(i);
                    if (MiniJson.GetString(rest, "name") == n) return MiniJson.GetString(rest, "digest");
                }
            }
            return null;
        }
    }

    /// <summary>
    /// LLM の入出力を JSONL に記録し、同じ入力には記録した応答を返す。
    /// replayOnly=true なら LLM を一切呼ばず、記録にない入力は例外にする（＝実験の完全な再現）。
    /// キーは SHA-256(モデル名 + 生成設定 + system + prompt)。
    /// </summary>
    public sealed class CachingLlmClient : ILlmClient, IDisposable
    {
        private readonly ILlmClient inner;
        private readonly bool replayOnly;
        private readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        private readonly StreamWriter writer;

        public string ModelId { get; private set; }
        public string ModelDigest { get; private set; }
        public string OptionsJson { get; private set; }
        public int Hits { get; private set; }
        public int Misses { get; private set; }
        public string Path { get; private set; }
        public ILlmClient Inner { get { return inner; } }

        /// <param name="inner">実際に呼ぶ LLM。replayOnly のときは null でよい。</param>
        /// <param name="modelId">replayOnly のときのモデル名（inner があればそちらを使う）。</param>
        /// <param name="optionsJson">replayOnly のときの生成設定（inner があればそちらを使う）。</param>
        public CachingLlmClient(string path, ILlmClient inner, bool replayOnly, string modelId = null, string optionsJson = null)
        {
            if (inner == null && !replayOnly) throw new ArgumentException("inner LLM is required unless replayOnly");
            this.inner = inner;
            this.replayOnly = replayOnly;
            Path = path;
            ModelId = inner != null ? inner.ModelId : modelId;
            ModelDigest = inner != null ? inner.ModelDigest : "";
            OptionsJson = inner != null ? inner.OptionsJson : optionsJson;

            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    if (line.Length == 0) continue;
                    string k = MiniJson.GetString(line, "key");
                    string r = MiniJson.GetString(line, "response");
                    if (k == null || r == null) continue;
                    cache[k] = r;
                    // 再生時は記録されている digest を引き継ぐ
                    if (inner == null && ModelDigest.Length == 0
                        && MiniJson.GetString(line, "model") == ModelId)
                        ModelDigest = MiniJson.GetString(line, "digest") ?? "";
                }
            }
            if (!replayOnly)
            {
                string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                writer = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };
            }
        }

        public int Count { get { return cache.Count; } }

        public string Complete(string system, string prompt)
        {
            string key = Key(ModelId, OptionsJson, system, prompt);
            string r;
            if (cache.TryGetValue(key, out r)) { Hits++; return r; }
            if (replayOnly)
                throw new InvalidOperationException("再生モードですが、記録にない入力がありました（key=" + key + "）。条件が記録時と違う可能性があります。");
            Misses++;
            r = inner.Complete(system, prompt);
            cache[key] = r;
            writer.WriteLine("{\"key\":" + MiniJson.Quote(key)
                + ",\"model\":" + MiniJson.Quote(ModelId)
                + ",\"digest\":" + MiniJson.Quote(ModelDigest)
                + ",\"options\":" + MiniJson.Quote(OptionsJson)
                + ",\"prompt\":" + MiniJson.Quote(prompt)
                + ",\"response\":" + MiniJson.Quote(r) + "}");
            return r;
        }

        public static string Key(string model, string options, string system, string prompt)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(model + "\n" + options + "\n" + system + "\n" + prompt));
                var sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public void Dispose() { if (writer != null) writer.Dispose(); }
    }

    /// <summary>
    /// 最小限の JSON 処理（Unity でも動くよう System.Text.Json を使わない）。
    /// 必要なのは「文字列のエスケープ」と「キーを指定して値を1つ取り出す」だけ。
    /// </summary>
    public static class MiniJson
    {
        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>最初に現れる "key": "..." の文字列値を返す。なければ null。</summary>
        public static string GetString(string json, string key)
        {
            int i = FindValue(json, key);
            if (i < 0 || json[i] != '"') return null;
            var sb = new StringBuilder();
            for (i++; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= json.Length) break;
                char e = json[i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < json.Length)
                        {
                            sb.Append((char)int.Parse(json.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            return null;
        }

        /// <summary>最初に現れる "key": 数値 の整数値を返す（"3" のような文字列も許す）。</summary>
        public static int? GetInt(string json, string key)
        {
            int i = FindValue(json, key);
            if (i < 0) return null;
            if (json[i] == '"') i++;
            int j = i;
            if (j < json.Length && json[j] == '-') j++;
            while (j < json.Length && char.IsDigit(json[j])) j++;
            int v;
            if (j > i && int.TryParse(json.Substring(i, j - i), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }

        private static int FindValue(string json, string key)
        {
            if (json == null) return -1;
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return -1;
            i += pat.Length;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != ':') return -1;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            return i < json.Length ? i : -1;
        }
    }
}
