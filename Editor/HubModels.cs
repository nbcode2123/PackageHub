using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace PackageHub
{
    /// <summary>Lỗi có thông điệp đọc được, hiện thẳng cho người dùng.</summary>
    public sealed class HubException : Exception
    {
        /// <summary>Lỗi nặng (token sai, hết hạn mức request): dừng cả đợt quét thay vì bỏ qua repo hiện tại.</summary>
        public bool Fatal { get; }

        public HubException(string message, bool fatal = false) : base(message) { Fatal = fatal; }
    }

    /// <summary>Coroutine không trả giá trị được, nên dùng hộp này để mang kết quả ra ngoài.</summary>
    public sealed class Box<T>
    {
        public T Value;
    }

    // ------------------------------------------------------------------
    //  Dữ liệu hiển thị
    // ------------------------------------------------------------------

    public enum ArtifactKind
    {
        UpmGit       = 0,   // thư mục có package.json trong repo GitHub
        UpmFolder    = 1,   // thư mục có package.json trên ổ đĩa
        PackageFile  = 2,   // file .unitypackage nằm trong repo
        PackageAsset = 3,   // file .unitypackage đính kèm trong một Release
        PackageLocal = 4,   // file .unitypackage trên ổ đĩa
    }

    /// <summary>Một thứ cài được nằm trong một mục (repo hoặc file).</summary>
    [Serializable]
    public class Artifact
    {
        public int kind;             // ArtifactKind, để int cho JsonUtility lưu được
        public string label;         // dòng hiển thị
        public string version;
        public long size;
        public string path;          // UPM: thư mục chứa package.json (rỗng = gốc repo); file: đường dẫn trong repo / trên đĩa
        public string assetId;       // id asset của Release
        public string tag;           // tag của Release
        public string packageName;   // tên UPM, ví dụ com.foo.bar
        public string date;

        public ArtifactKind Kind => (ArtifactKind)kind;
        public bool IsUpm => Kind == ArtifactKind.UpmGit || Kind == ArtifactKind.UpmFolder;
    }

    /// <summary>Một dòng trong thư viện: một repo GitHub, hoặc một file / thư mục trong thư mục đồng bộ.</summary>
    [Serializable]
    public class HubEntry
    {
        public string key;           // duy nhất trên toàn bộ nguồn
        public string sourceId;
        public string owner;         // GitHub: chủ repo. Thư mục: rỗng
        public string name;
        public string description;
        public bool isPrivate;
        public bool isFork;
        public bool isArchived;
        public bool canPush;
        public string updated;       // ISO-8601
        public string defaultBranch;
        public string htmlUrl;
        public List<string> topics = new List<string>();   // tag do nguồn cung cấp (GitHub topics / tên thư mục cha)

        public bool scanned;         // đã biết trong đó có package gì chưa
        public string stamp;         // dấu để biết kết quả quét còn mới không (pushed_at)
        public string scanNote;      // lý do quét lỗi / kết quả không đầy đủ
        public List<Artifact> artifacts = new List<Artifact>();

        public string FullName => string.IsNullOrEmpty(owner) ? name : owner + "/" + name;

        public bool HasAnyPackage => artifacts.Count > 0;

        public bool HasUpm
        {
            get { foreach (var a in artifacts) if (a.IsUpm) return true; return false; }
        }

        public bool HasPackageFile
        {
            get { foreach (var a in artifacts) if (!a.IsUpm) return true; return false; }
        }
    }

    // ------------------------------------------------------------------
    //  Cấu hình nguồn: lưu theo MÁY (EditorPrefs), không nằm trong project
    // ------------------------------------------------------------------

    [Serializable]
    public class SourceConfig
    {
        public const string GitHub = "github";
        public const string Folder = "folder";

        public string id;
        public string kind;     // GitHub | Folder
        public string name;
        public string token;    // GitHub
        public string path;     // Folder

        public SourceConfig Clone() => (SourceConfig)MemberwiseClone();
    }

    [Serializable]
    internal class SourceList
    {
        public List<SourceConfig> sources = new List<SourceConfig>();
    }

    public static class SourceStore
    {
        private const string Key = "PackageHub.sources";

        public static List<SourceConfig> Load()
        {
            try
            {
                string json = EditorPrefs.GetString(Key, "");
                if (string.IsNullOrEmpty(json)) return new List<SourceConfig>();

                SourceList list = JsonUtility.FromJson<SourceList>(json);
                return list != null && list.sources != null ? list.sources : new List<SourceConfig>();
            }
            catch
            {
                return new List<SourceConfig>();
            }
        }

        public static void Save(List<SourceConfig> sources)
        {
            EditorPrefs.SetString(Key, JsonUtility.ToJson(new SourceList { sources = sources }));
        }

        public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    // ------------------------------------------------------------------
    //  Dữ liệu cục bộ dùng chung cho mọi project (cache quét, tag)
    // ------------------------------------------------------------------

    internal static class LocalData
    {
        private static string _dir;

        public static string Dir
        {
            get
            {
                if (_dir != null) return _dir;

                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(baseDir)) baseDir = Application.persistentDataPath;

                _dir = Path.Combine(baseDir, "PackageHub");
                return _dir;
            }
        }

        public static T Read<T>(string fileName) where T : class, new()
        {
            try
            {
                string path = Path.Combine(Dir, fileName);
                if (!File.Exists(path)) return new T();

                T data = JsonUtility.FromJson<T>(File.ReadAllText(path));
                return data ?? new T();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PackageHub] Không đọc được {fileName}, dùng bản rỗng: {ex.Message}");
                return new T();
            }
        }

        public static void Write(string fileName, object data)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, fileName);
                string tmp = path + ".tmp";

                File.WriteAllText(tmp, JsonUtility.ToJson(data, false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PackageHub] Không ghi được {fileName}: {ex.Message}");
            }
        }
    }

    // ---- cache kết quả quét: quét một lần, lần sau chỉ quét repo có thay đổi ----

    [Serializable]
    public class ScanRecord
    {
        public string key;
        public string stamp;
        public List<Artifact> artifacts = new List<Artifact>();
    }

    [Serializable]
    public class ScanCacheData
    {
        public List<ScanRecord> records = new List<ScanRecord>();
    }

    internal static class ScanCache
    {
        private const string FileName = "scan-cache.json";
        private static Dictionary<string, ScanRecord> _map;

        private static void Ensure()
        {
            if (_map != null) return;

            _map = new Dictionary<string, ScanRecord>();
            ScanCacheData data = LocalData.Read<ScanCacheData>(FileName);
            if (data.records == null) return;

            foreach (ScanRecord r in data.records)
                if (r != null && !string.IsNullOrEmpty(r.key)) _map[r.key] = r;
        }

        /// <summary>Chỉ trả kết quả cũ nếu dấu (pushed_at) vẫn khớp, nghĩa là repo chưa đổi.</summary>
        public static bool TryGet(string key, string stamp, out List<Artifact> artifacts)
        {
            Ensure();
            if (_map.TryGetValue(key, out ScanRecord r) && r.stamp == stamp && r.artifacts != null)
            {
                artifacts = new List<Artifact>(r.artifacts);
                return true;
            }
            artifacts = null;
            return false;
        }

        public static void Put(string key, string stamp, List<Artifact> artifacts)
        {
            Ensure();
            _map[key] = new ScanRecord { key = key, stamp = stamp, artifacts = new List<Artifact>(artifacts) };
        }

        public static void Save()
        {
            Ensure();
            LocalData.Write(FileName, new ScanCacheData { records = new List<ScanRecord>(_map.Values) });
        }
    }

    // ---- tag do bạn tự gắn (ngoài GitHub topics) ----

    [Serializable]
    public class TagRecord
    {
        public string key;
        public List<string> tags = new List<string>();
    }

    [Serializable]
    public class TagData
    {
        public List<TagRecord> records = new List<TagRecord>();
    }

    internal static class TagStore
    {
        private const string FileName = "tags.json";
        private static Dictionary<string, List<string>> _map;

        private static void Ensure()
        {
            if (_map != null) return;

            _map = new Dictionary<string, List<string>>();
            TagData data = LocalData.Read<TagData>(FileName);
            if (data.records == null) return;

            foreach (TagRecord r in data.records)
                if (r != null && !string.IsNullOrEmpty(r.key) && r.tags != null) _map[r.key] = r.tags;
        }

        private static readonly List<string> Empty = new List<string>();

        public static List<string> Get(string key)
        {
            Ensure();
            return key != null && _map.TryGetValue(key, out List<string> tags) ? tags : Empty;
        }

        public static void Set(string key, List<string> tags)
        {
            if (string.IsNullOrEmpty(key)) return;
            Ensure();

            if (tags == null || tags.Count == 0) _map.Remove(key);
            else _map[key] = new List<string>(tags);

            var data = new TagData();
            foreach (var kv in _map) data.records.Add(new TagRecord { key = kv.Key, tags = kv.Value });
            LocalData.Write(FileName, data);
        }
    }

    // ------------------------------------------------------------------
    //  Tiện ích nhỏ
    // ------------------------------------------------------------------

    public static class HubUtil
    {
        private static readonly Regex VersionRx = new Regex(@"^\d+\.\d+\.\d+$");
        private static readonly Regex VersionInName = new Regex(@"\d+\.\d+\.\d+");

        public static bool IsValidVersion(string v) => !string.IsNullOrEmpty(v) && VersionRx.IsMatch(v);

        public static int CompareVersions(string a, string b)
        {
            if (System.Version.TryParse(a, out var va) && System.Version.TryParse(b, out var vb))
                return va.CompareTo(vb);
            return string.CompareOrdinal(a, b);
        }

        /// <summary>1.2.3 -> 1.2.4. Không hợp lệ thì trả về 1.0.0.</summary>
        public static string NextPatch(string v)
        {
            if (!IsValidVersion(v)) return "1.0.0";
            string[] p = v.Split('.');
            return int.TryParse(p[2], out int patch) ? $"{p[0]}.{p[1]}.{patch + 1}" : "1.0.0";
        }

        /// <summary>Lấy số phiên bản đầu tiên dạng x.y.z trong chuỗi ("UniTask.2.5.0.unitypackage" -> "2.5.0").</summary>
        public static string VersionFromName(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            Match m = VersionInName.Match(text);
            return m.Success ? m.Value : "";
        }

        /// <summary>
        /// package.json có phải của gói Unity không (khác với package.json của npm / web).
        /// Gói Unity có tên kiểu tên miền ngược (com.xxx.yyy) và/hoặc có trường "unity".
        /// </summary>
        public static bool LooksLikeUnityPackage(string name, string unity)
        {
            if (!string.IsNullOrEmpty(unity)) return true;
            return !string.IsNullOrEmpty(name) && name.IndexOf('.') > 0 && name.IndexOf(' ') < 0 && !name.StartsWith("@");
        }

        /// <summary>"Âm thanh UI!" -> "am-thanh-ui". Dùng làm id, tên repo, tên topic.</summary>
        public static string Slug(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            string d = text.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            bool dash = false;

            foreach (char c in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                    dash = false;
                }
                else if (!dash && sb.Length > 0)
                {
                    sb.Append('-');
                    dash = true;
                }
            }
            return sb.ToString().TrimEnd('-');
        }

        /// <summary>GitHub topic: chữ thường, số, gạch ngang, tối đa 50 ký tự. Slug đã thoả sẵn.</summary>
        public static string TopicName(string tag)
        {
            string s = Slug(tag);
            if (s.Length > 50) s = s.Substring(0, 50).TrimEnd('-');
            return s;
        }

        /// <summary>Tên thư mục an toàn để đặt trong Packages/.</summary>
        public static string SafeFolderName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "")
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString().Trim('.');
            return s.Length == 0 ? "package" : s;
        }

        /// <summary>Mã hoá từng đoạn của đường dẫn trong URL, giữ nguyên dấu '/'.</summary>
        public static string EscapePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }

        /// <summary>Tách theo dấu phẩy / chấm phẩy, chữ thường, bỏ trùng.</summary>
        public static List<string> ParseTags(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return list;

            foreach (string part in raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = part.Trim().ToLowerInvariant();
                if (t.Length > 0 && !list.Contains(t)) list.Add(t);
            }
            return list;
        }

        public static string JoinTags(IEnumerable<string> tags) => string.Join(", ", tags);

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            return (mb / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }

        public static string FormatDate(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "";
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime d))
                return d.ToLocalTime().ToString("yyyy-MM-dd");
            return "";
        }

        public static string ToIso(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

        /// <summary>Mỗi tag một màu cố định (suy từ tên), để nhìn biểu đồ phân bổ là nhận ra ngay.</summary>
        public static Color ColorFor(string tag)
        {
            uint h = 2166136261;
            unchecked
            {
                foreach (char c in tag)
                {
                    h ^= c;
                    h *= 16777619;
                }
            }
            return Color.HSVToRGB((h % 360u) / 360f, 0.5f, 0.92f);
        }

        /// <summary>Chuỗi JSON thuần ASCII (ký tự ngoài ASCII thành \uXXXX).</summary>
        public static string JsonQuote(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
