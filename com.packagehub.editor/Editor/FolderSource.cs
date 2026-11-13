using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PackageHub
{
    /// <summary>
    /// Một thư mục trên máy. Đây là cách nối Google Drive / Dropbox / OneDrive mà KHÔNG cần
    /// đăng nhập hay OAuth: cài app desktop của họ (Drive for Desktop...), nó đồng bộ một thư mục
    /// về máy, rồi trỏ nguồn này vào thư mục đó. Cũng dùng được cho ổ mạng / NAS / ổ ngoài.
    ///
    /// Mục nào được liệt kê:
    ///   - file *.unitypackage                       -> import
    ///   - thư mục có package.json kiểu Unity        -> cài tham chiếu (file:) hoặc nhúng vào Packages/
    /// Tag tự lấy từ tên thư mục cha: "UI/Menu/Foo.unitypackage" có tag "ui" và "menu".
    /// </summary>
    internal sealed class FolderSource : Source
    {
        private const int MaxDepth = 6;
        private const int MaxItems = 5000;

        private static readonly HashSet<string> IgnoredDirs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", "Library", "Temp", "obj" };

        [Serializable]
        private class PkgJson
        {
            public string name;
            public string displayName;
            public string version;
            public string description;
            public string unity;
        }

        private int _budget;

        public FolderSource(SourceConfig config) : base(config) { }

        public override bool CanPublish => true;

        private string Root => Config.path;

        private void EnsureRoot()
        {
            if (string.IsNullOrEmpty(Root) || !Directory.Exists(Root))
                throw new HubException("Không thấy thư mục: " + Root);
        }

        // ------------------------------------------------------------ kiểm tra ---

        public override IEnumerator Test(Box<string> message)
        {
            EnsureRoot();

            var items = new List<HubEntry>();
            _budget = MaxItems;
            Walk(Root, 0, "", items);

            message.Value = $"Thư mục dùng được, thấy {items.Count} mục.";
            yield break;
        }

        // ------------------------------------------------------------ liệt kê ---

        public override IEnumerator List(List<HubEntry> into)
        {
            EnsureRoot();

            Job.Say("Đang đọc thư mục " + Name + "...");
            _budget = MaxItems;
            Walk(Root, 0, "", into);
            yield break;
        }

        // Thư mục cục bộ đọc xong là biết hết nội dung, không cần bước quét riêng.
        public override IEnumerator Scan(HubEntry entry)
        {
            entry.scanned = true;
            yield break;
        }

        private void Walk(string dir, int depth, string rel, List<HubEntry> into)
        {
            if (depth > MaxDepth || _budget <= 0) return;

            string packageJson = Path.Combine(dir, "package.json");
            if (File.Exists(packageJson))
            {
                HubEntry upm = UpmEntry(dir, rel, packageJson);
                if (upm != null)
                {
                    into.Add(upm);
                    _budget--;
                    return;      // đã là một gói UPM, không soi bên trong nữa
                }
            }

            string[] files;
            try { files = Directory.GetFiles(dir, "*.unitypackage"); }
            catch { files = new string[0]; }

            foreach (string file in files)
            {
                into.Add(PackageEntry(file, rel));
                if (--_budget <= 0) return;
            }

            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); }
            catch { dirs = new string[0]; }

            foreach (string sub in dirs)
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith(".") || IgnoredDirs.Contains(name)) continue;

                Walk(sub, depth + 1, rel.Length == 0 ? name : rel + "/" + name, into);
                if (_budget <= 0) return;
            }
        }

        private HubEntry UpmEntry(string dir, string rel, string packageJsonPath)
        {
            PkgJson pj;
            try { pj = JsonUtility.FromJson<PkgJson>(File.ReadAllText(packageJsonPath).TrimStart('\uFEFF')); }
            catch { return null; }

            if (pj == null || !HubUtil.LooksLikeUnityPackage(pj.name, pj.unity)) return null;

            string title = string.IsNullOrEmpty(pj.displayName) ? pj.name : pj.displayName;
            DateTime written = Directory.GetLastWriteTimeUtc(dir);

            HubEntry e = NewEntry(rel.Length == 0 ? "(gốc)" : rel, title, pj.description, written, ParentOf(rel));
            e.artifacts.Add(new Artifact
            {
                kind = (int)ArtifactKind.UpmFolder,
                label = pj.name,
                packageName = pj.name,
                version = pj.version,
                path = dir,
                date = HubUtil.ToIso(written),
            });
            return e;
        }

        private HubEntry PackageEntry(string file, string rel)
        {
            var info = new FileInfo(file);
            string relPath = rel.Length == 0 ? info.Name : rel + "/" + info.Name;

            HubEntry e = NewEntry(relPath, Path.GetFileNameWithoutExtension(info.Name), null, info.LastWriteTimeUtc, rel);
            e.artifacts.Add(new Artifact
            {
                kind = (int)ArtifactKind.PackageLocal,
                label = info.Name,
                path = file,
                size = info.Length,
                version = HubUtil.VersionFromName(info.Name),
                date = HubUtil.ToIso(info.LastWriteTimeUtc),
            });
            return e;
        }

        private HubEntry NewEntry(string relPath, string title, string description, DateTime lastWriteUtc, string parentRel)
        {
            var e = new HubEntry
            {
                key = Id + ":fs:" + relPath,
                sourceId = Id,
                name = title,
                description = description,
                updated = HubUtil.ToIso(lastWriteUtc),
                scanned = true,
            };

            // Tên thư mục cha làm tag: gom nhóm miễn phí theo cách bạn đã tự sắp xếp.
            foreach (string segment in parentRel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string topic = HubUtil.TopicName(segment);
                if (topic.Length > 0 && !e.topics.Contains(topic)) e.topics.Add(topic);
            }
            return e;
        }

        private static string ParentOf(string rel)
        {
            int i = rel.LastIndexOf('/');
            return i < 0 ? "" : rel.Substring(0, i);
        }

        // ----------------------------------------------------------- cài đặt ---

        public override IEnumerator Install(HubEntry e, Artifact a, bool embed)
        {
            switch (a.Kind)
            {
                case ArtifactKind.PackageLocal:
                    if (!File.Exists(a.path))
                        throw new HubException("Không thấy file (Drive chưa đồng bộ xong?): " + a.path);
                    Installer.ImportFile(a.path);
                    break;

                case ArtifactKind.UpmFolder:
                    if (embed) Installer.EmbedFromFolder(a.path, a.packageName);
                    else yield return Installer.AddPackage("file:" + a.path, false);
                    break;

                default:
                    throw new HubException("Loại này không cài được từ thư mục.");
            }

            yield break;
        }

        // ------------------------------------------------- duyệt nội dung ---

        private static string BrowseRoot(HubEntry e)
        {
            foreach (Artifact a in e.artifacts)
                if (a.Kind == ArtifactKind.UpmFolder) return a.path;
            return null;
        }

        public override IEnumerator ListFiles(HubEntry e, List<FileNode> into)
        {
            string root = BrowseRoot(e);
            if (root == null || !Directory.Exists(root))
                throw new HubException("Mục này là một file .unitypackage, không có nội dung để duyệt.");

            Walk(root, root, into);
            yield break;
        }

        private static void Walk(string root, string dir, List<FileNode> into)
        {
            var entries = new List<string>();
            try
            {
                entries.AddRange(Directory.GetDirectories(dir));
                entries.AddRange(Directory.GetFiles(dir));
            }
            catch { return; }

            entries.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string full in entries)
            {
                if (into.Count >= 5000) return;

                string name = Path.GetFileName(full);
                if (name.StartsWith(".") || name.EndsWith(".meta")) continue;

                string rel = full.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
                bool isDir = Directory.Exists(full);

                into.Add(new FileNode { Path = rel, IsDir = isDir, Size = isDir ? 0 : new FileInfo(full).Length });
                if (isDir) Walk(root, full, into);
            }
        }

        public override IEnumerator Fetch(HubEntry e, FileNode node)
        {
            string root = BrowseRoot(e);
            if (root == null) throw new HubException("Không có nội dung để tải.");

            string src = Path.Combine(root, node.Path.Replace('/', Path.DirectorySeparatorChar));
            string dest = Installer.AssetsDest(e.name, node.Path);
            Installer.ConfirmOverwrite(dest, node.IsDir);

            if (node.IsDir) Installer.CopyFolder(src, dest);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(src, dest, true);
                UnityEditor.AssetDatabase.Refresh();
            }
            yield break;
        }

        // ------------------------------------------------------------ đẩy lên ---

        public override IEnumerator Publish(PublishRequest req)
        {
            EnsureRoot();

            string sub = (req.SubFolder ?? "").Trim().Trim('/', '\\');
            string folder = sub.Length == 0 ? Root : Path.Combine(Root, sub);

            // Chặn "../.." chui ra ngoài thư mục gốc đã cấu hình.
            string fullRoot = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullFolder.StartsWith(fullRoot, StringComparison.Ordinal))
                throw new HubException("Thư mục con phải nằm bên trong thư mục gốc.");

            string fileName = $"{req.Id}-{req.Version}.unitypackage";
            string dest = Path.Combine(folder, fileName);

            if (File.Exists(dest))
                throw new HubException($"{fileName} đã có trong thư mục. Tăng số phiên bản rồi thử lại.");

            Job.Say("Đang chép vào thư mục...");
            yield return null;

            Directory.CreateDirectory(folder);
            File.Copy(req.ExportedFile, dest);

            string rel = sub.Length == 0 ? fileName : sub.Replace('\\', '/') + "/" + fileName;
            req.ResultKey = Id + ":fs:" + rel;
            req.ResultMessage = $"Đã chép {fileName} vào {folder}. Ứng dụng đồng bộ của bạn sẽ tự đẩy lên cloud.";
        }
    }
}
