using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PackageHub
{
    /// <summary>
    /// Cả tài khoản GitHub: liệt kê mọi repo token nhìn thấy (cả private, cả repo của tổ chức),
    /// quét xem repo nào là gói Unity, cài về, và đẩy package mới lên (repo mới hoặc repo có sẵn).
    ///
    /// Nhận diện gói trong repo (một request git tree / repo):
    ///   - package.json kiểu Unity ở bất kỳ thư mục nào       -> UPM, cài bằng git URL (?path=...) hoặc nhúng
    ///   - file *.unitypackage nằm trong cây thư mục          -> tải file rồi import
    ///   - file *.unitypackage đính kèm trong Releases        -> tải asset rồi import
    ///
    /// Đẩy lên = tạo Release "v{phiên bản}" kèm file .unitypackage làm asset. Dùng Release thay vì
    /// commit file vào cây vì asset tới 2 GB, không phình lịch sử git, và mỗi phiên bản là một tag.
    /// </summary>
    internal sealed class GitHubSource : Source
    {
        private const string Api = "https://api.github.com";
        private const int MaxRepoPages = 10;             // 10 x 100 = 1000 repo
        private const int MaxPackageJsonPerRepo = 8;     // chặn repo monorepo làm nổ số request
        private const int MaxReleaseAssets = 12;

        private static readonly string[] IgnoredDirs =
            { "node_modules", "Library", "Temp", "Logs", "obj", ".git", ".github", "Samples~", "Documentation~" };

        // ---- DTO khớp tên trường JSON của GitHub (JsonUtility ghép theo tên) ----

        [Serializable] private class GhOwner { public string login; }
        [Serializable] private class GhPerms { public bool push; }

        [Serializable]
        private class GhRepo
        {
            public string name;
            public string full_name;
            public bool @private;
            public bool fork;
            public bool archived;
            public string description;
            public string pushed_at;
            public string default_branch;
            public string html_url;
            public List<string> topics;
            public GhOwner owner;
            public GhPerms permissions;
        }

        [Serializable] private class RepoList { public List<GhRepo> items; }

        [Serializable] private class TreeEntry { public string path; public string type; public long size; }
        [Serializable] private class TreeResponse { public List<TreeEntry> tree; public bool truncated; }

        [Serializable] private class GhAsset { public long id; public string name; public long size; public string created_at; }
        [Serializable] private class GhRelease { public string tag_name; public bool draft; public string published_at; public string created_at; public List<GhAsset> assets; }
        [Serializable] private class ReleaseList { public List<GhRelease> items; }

        [Serializable] private class PkgJson { public string name; public string displayName; public string version; public string description; public string unity; }

        [Serializable] private class CreatedRepo { public string full_name; }
        [Serializable] private class CreatedRelease { public long id; public string upload_url; }
        [Serializable] private class UserInfo { public string login; }

        // --------------------------------------------------------------------

        public GitHubSource(SourceConfig config) : base(config) { }

        public override bool CanPublish => true;

        private string Token => Config.token;

        // ------------------------------------------------------- kiểm tra ---

        public override IEnumerator Test(Box<string> message)
        {
            if (string.IsNullOrEmpty(Token)) throw new HubException("Chưa nhập token.");

            var res = new Http.Result();
            yield return Http.Get(Api + "/user", Http.Json, Token, res);
            if (!res.Ok) throw new HubException(res.Describe());

            string login = JsonUtility.FromJson<UserInfo>(res.Text).login;

            // Đếm thử trang đầu để chắc chắn token nhìn thấy repo (token chọn nhầm "Only select repositories" sẽ ra rất ít).
            var repos = new Http.Result();
            yield return Http.Get(Api + "/user/repos?per_page=100&affiliation=owner,collaborator,organization_member",
                                  Http.Json, Token, repos);
            if (!repos.Ok) throw new HubException(repos.Describe());

            RepoList list = JsonUtility.FromJson<RepoList>("{\"items\":" + repos.Text + "}");
            int n = list != null && list.items != null ? list.items.Count : 0;

            message.Value = $"Đăng nhập là @{login}. Token nhìn thấy {n}{(n >= 100 ? "+" : "")} repo (trang đầu).";
        }

        // ------------------------------------------------------- liệt kê ---

        public override IEnumerator List(List<HubEntry> into)
        {
            if (string.IsNullOrEmpty(Token)) throw new HubException("Chưa nhập token.");

            for (int page = 1; page <= MaxRepoPages; page++)
            {
                var res = new Http.Result();
                string url = $"{Api}/user/repos?per_page=100&page={page}&sort=pushed&direction=desc" +
                             "&affiliation=owner,collaborator,organization_member";

                yield return Http.Get(url, Http.Json, Token, res);
                if (!res.Ok) throw new HubException(res.Describe(), fatal: res.Status == 401);

                // JsonUtility không đọc được mảng ở gốc, nên bọc vào một đối tượng.
                RepoList list = JsonUtility.FromJson<RepoList>("{\"items\":" + res.Text + "}");
                if (list == null || list.items == null || list.items.Count == 0) break;

                foreach (GhRepo r in list.items) into.Add(ToEntry(r));

                Job.Say($"Đã tải {into.Count} repo...");
                if (list.items.Count < 100) break;
            }
        }

        private HubEntry ToEntry(GhRepo r)
        {
            var e = new HubEntry
            {
                key = Id + ":" + r.full_name,
                sourceId = Id,
                owner = r.owner != null ? r.owner.login : "",
                name = r.name,
                description = r.description,
                isPrivate = r.@private,
                isFork = r.fork,
                isArchived = r.archived,
                canPush = r.permissions != null && r.permissions.push,
                updated = r.pushed_at,
                defaultBranch = r.default_branch,
                htmlUrl = r.html_url,
                topics = r.topics ?? new List<string>(),
                stamp = r.pushed_at,
            };

            // Repo không đổi từ lần quét trước (pushed_at khớp) thì dùng lại kết quả cũ.
            if (ScanCache.TryGet(e.key, e.stamp, out List<Artifact> cached))
            {
                e.artifacts = cached;
                e.scanned = true;
            }
            return e;
        }

        // ----------------------------------------------------------- quét ---

        public override IEnumerator Scan(HubEntry e)
        {
            e.scanNote = null;
            var found = new List<Artifact>();
            string branch = BranchOf(e);

            // ---- 1. cây thư mục: package.json và *.unitypackage ----
            var tree = new Http.Result();
            yield return Http.Get($"{Api}/repos/{e.FullName}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1",
                                  Http.Json, Token, tree);

            if (tree.RateLimited || tree.Status == 401)
                throw new HubException(tree.Describe(), fatal: true);

            var packageJsonPaths = new List<string>();

            if (tree.Ok)
            {
                TreeResponse data = JsonUtility.FromJson<TreeResponse>(tree.Text);
                if (data != null && data.tree != null)
                {
                    foreach (TreeEntry t in data.tree)
                    {
                        if (t.type != "blob") continue;

                        if (IsPackageJsonPath(t.path))
                        {
                            if (packageJsonPaths.Count < MaxPackageJsonPerRepo) packageJsonPaths.Add(t.path);
                        }
                        else if (t.path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(new Artifact
                            {
                                kind = (int)ArtifactKind.PackageFile,
                                label = Path.GetFileName(t.path),
                                path = t.path,
                                size = t.size,
                                version = HubUtil.VersionFromName(Path.GetFileName(t.path)),
                            });
                        }
                    }
                    if (data.truncated) e.scanNote = "Repo quá lớn, kết quả quét có thể thiếu.";
                }
            }
            else if (tree.Status != 404 && tree.Status != 409)
            {
                // 409 = repo rỗng, 404 = nhánh mặc định không còn: coi như repo không có gì.
                // Lỗi khác (vd. token thiếu quyền Contents) thì KHÔNG đánh dấu đã quét và KHÔNG lưu cache,
                // nếu không "không có gì" sẽ bị nhớ lại cho tới khi repo có commit mới.
                e.scanNote = "Không quét được: " + tree.Describe();
                yield break;
            }

            // ---- 2. đọc từng package.json để biết tên / phiên bản và loại bỏ package.json của web ----
            foreach (string jsonPath in packageJsonPaths)
            {
                var file = new Http.Result();
                yield return Http.Get(ContentsUrl(e, jsonPath), Http.Raw, Token, file);
                if (file.RateLimited) throw new HubException(file.Describe(), fatal: true);
                if (!file.Ok) continue;

                PkgJson pj = ParsePackageJson(file.Text);
                if (pj == null || !HubUtil.LooksLikeUnityPackage(pj.name, pj.unity)) continue;

                string dir = jsonPath == "package.json" ? "" : jsonPath.Substring(0, jsonPath.Length - "/package.json".Length);

                found.Insert(0, new Artifact
                {
                    kind = (int)ArtifactKind.UpmGit,
                    label = string.IsNullOrEmpty(pj.displayName) ? pj.name : pj.displayName,
                    packageName = pj.name,
                    version = pj.version,
                    path = dir,
                });
            }

            // ---- 3. file .unitypackage đính kèm trong Releases ----
            var rel = new Http.Result();
            yield return Http.Get($"{Api}/repos/{e.FullName}/releases?per_page=5", Http.Json, Token, rel);
            if (rel.RateLimited) throw new HubException(rel.Describe(), fatal: true);

            if (rel.Ok)
            {
                ReleaseList releases = JsonUtility.FromJson<ReleaseList>("{\"items\":" + rel.Text + "}");
                int assetCount = 0;

                if (releases != null && releases.items != null)
                {
                    foreach (GhRelease r in releases.items)
                    {
                        if (r.draft || r.assets == null) continue;

                        foreach (GhAsset a in r.assets)
                        {
                            if (a.name == null || !a.name.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)) continue;
                            if (assetCount++ >= MaxReleaseAssets) break;

                            string version = HubUtil.VersionFromName(r.tag_name);
                            if (version.Length == 0) version = HubUtil.VersionFromName(a.name);

                            found.Add(new Artifact
                            {
                                kind = (int)ArtifactKind.PackageAsset,
                                label = a.name,
                                path = a.name,
                                assetId = a.id.ToString(),
                                tag = r.tag_name,
                                version = version,
                                size = a.size,
                                date = string.IsNullOrEmpty(r.published_at) ? a.created_at : r.published_at,
                            });
                        }
                    }
                }
            }

            e.artifacts = found;
            e.scanned = true;
            ScanCache.Put(e.key, e.stamp, found);
        }

        private static bool IsPackageJsonPath(string path)
        {
            if (path != "package.json" && !path.EndsWith("/package.json", StringComparison.Ordinal)) return false;

            foreach (string segment in path.Split('/'))
                foreach (string ignored in IgnoredDirs)
                    if (segment == ignored) return false;

            return true;
        }

        private static PkgJson ParsePackageJson(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return JsonUtility.FromJson<PkgJson>(text.TrimStart('﻿')); }
            catch { return null; }
        }

        // ---------------------------------------------------------- cài đặt ---

        public override IEnumerator Install(HubEntry e, Artifact a, bool embed)
        {
            switch (a.Kind)
            {
                case ArtifactKind.UpmGit:
                {
                    if (!embed)
                    {
                        yield return Installer.AddPackage(GitUrl(e, a), e.isPrivate);
                        break;
                    }

                    // Repo private mà máy chưa có git credential thì cài qua git URL sẽ hỏng.
                    // Đường này tải bằng token của bạn rồi chép thẳng vào Packages/.
                    string zip = TempPath("source.zip");
                    var res = new Http.Result();
                    Job.Say("Đang tải mã nguồn...");
                    yield return Http.Download($"{Api}/repos/{e.FullName}/zipball/{Uri.EscapeDataString(BranchOf(e))}",
                                               Http.Json, Token, zip, res);
                    if (!res.Ok)
                    {
                        TryDelete(zip);
                        throw new HubException(res.Describe());
                    }

                    try { Installer.EmbedFromZip(zip, a.path, a.packageName); }
                    finally { TryDelete(zip); }
                    break;
                }

                case ArtifactKind.PackageFile:
                {
                    string tmp = TempPath(Path.GetFileName(a.path));
                    var res = new Http.Result();
                    Job.Say("Đang tải " + a.label + "...");
                    yield return Http.Download(ContentsUrl(e, a.path), Http.Raw, Token, tmp, res);
                    if (!res.Ok)
                    {
                        TryDelete(tmp);
                        throw new HubException(res.Describe());
                    }
                    CheckSize(tmp, a.size);
                    Installer.ImportFile(tmp);
                    break;
                }

                case ArtifactKind.PackageAsset:
                {
                    string tmp = TempPath(a.path);
                    var res = new Http.Result();
                    Job.Say("Đang tải " + a.label + "...");
                    yield return Http.Download($"{Api}/repos/{e.FullName}/releases/assets/{a.assetId}",
                                               Http.Binary, Token, tmp, res);
                    if (!res.Ok)
                    {
                        TryDelete(tmp);
                        throw new HubException(res.Describe());
                    }
                    CheckSize(tmp, a.size);
                    Installer.ImportFile(tmp);
                    break;
                }

                default:
                    throw new HubException("Loại này không cài được từ GitHub.");
            }
        }

        // ------------------------------------------------- duyệt nội dung ---

        public override IEnumerator ListFiles(HubEntry e, List<FileNode> into)
        {
            var tree = new Http.Result();
            yield return Http.Get($"{Api}/repos/{e.FullName}/git/trees/{Uri.EscapeDataString(BranchOf(e))}?recursive=1",
                                  Http.Json, Token, tree);

            if (tree.Status == 409) yield break;                 // repo rỗng
            if (!tree.Ok) throw new HubException(tree.Describe(), tree.RateLimited || tree.Status == 401);

            TreeResponse data = JsonUtility.FromJson<TreeResponse>(tree.Text);
            if (data == null || data.tree == null) yield break;

            foreach (TreeEntry t in data.tree)
            {
                bool dir = t.type == "tree";
                if (!dir && t.type != "blob") continue;          // bỏ submodule (commit)
                if (into.Count >= 5000) break;
                into.Add(new FileNode { Path = t.path, IsDir = dir, Size = t.size });
            }

            if (data.truncated) Job.Say("Repo lớn, danh sách có thể thiếu.");
        }

        public override IEnumerator Fetch(HubEntry e, FileNode node)
        {
            string dest = Installer.AssetsDest(e.name, node.Path);
            Installer.ConfirmOverwrite(dest, node.IsDir);

            var res = new Http.Result();

            if (!node.IsDir)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                Job.Say("Đang tải " + node.Name + "...");
                yield return Http.Download(ContentsUrl(e, node.Path), Http.Raw, Token, dest, res);
                if (!res.Ok)
                {
                    TryDelete(dest);
                    throw new HubException(res.Describe());
                }
                UnityEditor.AssetDatabase.Refresh();
                yield break;
            }

            string zip = TempPath("folder.zip");
            Job.Say("Đang tải mã nguồn...");
            yield return Http.Download($"{Api}/repos/{e.FullName}/zipball/{Uri.EscapeDataString(BranchOf(e))}",
                                       Http.Json, Token, zip, res);
            if (!res.Ok)
            {
                TryDelete(zip);
                throw new HubException(res.Describe());
            }

            try { Installer.ExtractFolderFromZip(zip, node.Path, dest); }
            finally { TryDelete(zip); }
        }

        /// <summary>URL để dán vào Package Manager. Repo con trỏ thư mục dùng ?path=.</summary>
        public static string GitUrl(HubEntry e, Artifact a)
        {
            string url = e.htmlUrl.EndsWith(".git", StringComparison.Ordinal) ? e.htmlUrl : e.htmlUrl + ".git";
            if (!string.IsNullOrEmpty(a.path)) url += "?path=/" + a.path;
            return url;
        }

        private static void CheckSize(string file, long expected)
        {
            long got = new FileInfo(file).Length;
            if (expected > 0 && got != expected)
            {
                TryDelete(file);
                throw new HubException($"File tải về lệch kích thước (nhận {got} B, dự kiến {expected} B). Thử lại.");
            }
        }

        // --------------------------------------------------------- topics ---

        /// <summary>Ghi cả danh sách tag lên GitHub topics (cần quyền Administration: write).</summary>
        public IEnumerator SaveTopics(HubEntry e, List<string> tags)
        {
            var names = new List<string>();
            foreach (string t in tags)
            {
                string topic = HubUtil.TopicName(t);
                if (topic.Length > 0 && !names.Contains(topic)) names.Add(topic);
                if (names.Count >= 20) break;      // GitHub cho tối đa 20 topic mỗi repo
            }

            var res = new Http.Result();
            yield return Http.Send("PUT", $"{Api}/repos/{e.FullName}/topics", TopicsBody(names), Token, res);
            if (!res.Ok) throw new HubException("Không ghi được topics.\n" + res.Describe());

            e.topics = names;
        }

        private static string TopicsBody(List<string> names)
        {
            var parts = new List<string>();
            foreach (string n in names) parts.Add(HubUtil.JsonQuote(n));
            return "{\"names\":[" + string.Join(",", parts) + "]}";
        }

        // ---------------------------------------------------------- đẩy lên ---

        public override IEnumerator Publish(PublishRequest req)
        {
            string full;
            var existingTopics = new List<string>();

            // ---- 1. repo đích ----
            if (req.NewRepo)
            {
                Job.Say("Đang tạo repo " + req.NewRepoName + "...");

                string body = "{\"name\":" + HubUtil.JsonQuote(req.NewRepoName)
                            + ",\"description\":" + HubUtil.JsonQuote(req.Description ?? "")
                            + ",\"private\":" + (req.PrivateRepo ? "true" : "false")
                            + ",\"auto_init\":true}";      // có commit đầu tiên thì mới tạo Release được

                var made = new Http.Result();
                yield return Http.Send("POST", Api + "/user/repos", body, Token, made);

                if (!made.Ok)
                {
                    string extra = made.Status == 422
                        ? "\nCó thể đã có repo trùng tên này."
                        : "\nToken fine-grained thường KHÔNG tạo được repo mới cho tài khoản cá nhân (404/403). " +
                          "Dùng token classic có scope 'repo', hoặc tạo repo trên github.com rồi chọn 'Repo có sẵn'.";
                    throw new HubException("Không tạo được repo." + extra + "\n" + made.Describe());
                }

                full = JsonUtility.FromJson<CreatedRepo>(made.Text).full_name;
            }
            else
            {
                if (req.Repo == null) throw new HubException("Chưa chọn repo đích.");
                full = req.Repo.FullName;
                existingTopics = req.Repo.topics ?? new List<string>();
            }

            // ---- 2. Release v{phiên bản} ----
            string tag = "v" + req.Version;
            string fileName = $"{req.Id}-{req.Version}.unitypackage";

            Job.Say($"Đang tạo release {tag}...");

            string relBody = "{\"tag_name\":" + HubUtil.JsonQuote(tag)
                           + ",\"name\":" + HubUtil.JsonQuote(req.Title + " v" + req.Version)
                           + ",\"body\":" + HubUtil.JsonQuote(req.Notes ?? "") + "}";

            var rel = new Http.Result();
            yield return Http.Send("POST", $"{Api}/repos/{full}/releases", relBody, Token, rel);

            if (!rel.Ok)
            {
                if (rel.Status == 422)
                    throw new HubException($"Không tạo được release {tag} trong {full}: tag đã tồn tại, hoặc repo chưa có commit nào.\n" +
                                           "Tăng số phiên bản rồi thử lại.\n" + rel.Describe());
                throw new HubException("Không tạo được release.\n" + rel.Describe());
            }

            CreatedRelease release = JsonUtility.FromJson<CreatedRelease>(rel.Text);

            // upload_url có đuôi mẫu "{?name,label}" phải cắt đi.
            string uploadBase = release.upload_url;
            int brace = uploadBase != null ? uploadBase.IndexOf('{') : -1;
            if (brace >= 0) uploadBase = uploadBase.Substring(0, brace);
            if (string.IsNullOrEmpty(uploadBase))
                uploadBase = $"https://uploads.github.com/repos/{full}/releases/{release.id}/assets";

            // ---- 3. đính kèm file ----
            Job.Say($"Đang tải lên {HubUtil.FormatSize(req.Size)}...");

            var up = new Http.Result();
            yield return Http.Upload(uploadBase + "?name=" + Uri.EscapeDataString(fileName), req.ExportedFile, Token, up);

            if (!up.Ok)
            {
                // Release đã tạo mà không có file thì lần thử lại sẽ vướng "tag đã tồn tại". Dọn luôn.
                yield return Cleanup(full, release.id, tag);
                throw new HubException("Tải file lên thất bại (đã gỡ release dở dang, có thể thử lại).\n" + up.Describe());
            }

            // ---- 4. topics (không bắt buộc, lỗi thì chỉ ghi chú) ----
            string note = "";
            if (req.Tags.Count > 0)
            {
                var all = new List<string>(existingTopics);
                foreach (string t in req.Tags)
                {
                    string topic = HubUtil.TopicName(t);
                    if (topic.Length > 0 && !all.Contains(topic)) all.Add(topic);
                }

                var topicRes = new Http.Result();
                yield return Http.Send("PUT", $"{Api}/repos/{full}/topics", TopicsBody(all), Token, topicRes);
                if (!topicRes.Ok)
                    note = "\n(Chưa ghi được topics lên GitHub, token thiếu quyền Administration. Tag vẫn được lưu trên máy này.)";
            }

            req.ResultKey = Id + ":" + full;
            req.ResultMessage = $"Đã đẩy {req.Title} {tag} lên github.com/{full}." + note;
        }

        /// <summary>Gỡ release và tag vừa tạo. Cố hết sức, lỗi thì bỏ qua.</summary>
        private IEnumerator Cleanup(string full, long releaseId, string tag)
        {
            var a = new Http.Result();
            yield return Http.Send("DELETE", $"{Api}/repos/{full}/releases/{releaseId}", null, Token, a);

            var b = new Http.Result();
            yield return Http.Send("DELETE", $"{Api}/repos/{full}/git/refs/tags/{Uri.EscapeDataString(tag)}", null, Token, b);
        }

        // ------------------------------------------------------------- helper ---

        private static string BranchOf(HubEntry e) => string.IsNullOrEmpty(e.defaultBranch) ? "HEAD" : e.defaultBranch;

        private static string ContentsUrl(HubEntry e, string path) =>
            $"{Api}/repos/{e.FullName}/contents/{HubUtil.EscapePath(path)}?ref={Uri.EscapeDataString(BranchOf(e))}";

        /// <summary>Mỗi lần tải vào một thư mục riêng, khỏi đụng tên file cũ.</summary>
        private static string TempPath(string fileName)
        {
            string dir = Path.Combine(Path.GetTempPath(), "PackageHub", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* bỏ qua */ }
        }
    }
}
