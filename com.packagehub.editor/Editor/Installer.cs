using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;

namespace PackageHub
{
    /// <summary>Ba cách đưa một gói vào project.</summary>
    internal static class Installer
    {
        /// <summary>
        /// Nhờ Package Manager cài: git URL (https://github.com/o/r.git?path=/thu-muc) hoặc file:/duong/dan.
        /// Git URL dùng git của máy bạn, nên repo private cần git đã đăng nhập sẵn.
        /// Token của Package Hub KHÔNG được nhét vào URL: URL được ghi vào Packages/manifest.json,
        /// rất dễ bị commit lên repo khác và lộ token.
        /// </summary>
        public static IEnumerator AddPackage(string identifier, bool isPrivateRepo)
        {
            Job.Say("Package Manager đang cài...");

            UnityEditor.PackageManager.Requests.AddRequest request = UnityEditor.PackageManager.Client.Add(identifier);
            while (!request.IsCompleted) yield return null;

            if (request.Status != UnityEditor.PackageManager.StatusCode.Success)
            {
                string message = request.Error != null ? request.Error.message : request.Status.ToString();

                string hint = "";
                if (identifier.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    hint = "\nCài qua git URL cần đã cài git trên máy." +
                           (isPrivateRepo
                               ? " Repo private cần git đã đăng nhập (credential manager hoặc SSH). Hoặc bấm 'Nhúng' để tải bằng token của bạn."
                               : "");
                }

                throw new HubException("Package Manager báo lỗi: " + message + hint);
            }
        }

        /// <summary>Mở hộp thoại import của Unity cho một file .unitypackage.</summary>
        public static void ImportFile(string path)
        {
            AssetDatabase.ImportPackage(path, true);
        }

        /// <summary>
        /// Giải nén zipball của GitHub rồi chép thư mục gói vào Packages/ (gói "nhúng").
        /// subdir = thư mục chứa package.json trong repo, rỗng nếu ở gốc.
        /// </summary>
        public static void EmbedFromZip(string zipPath, string subdir, string packageName)
        {
            string work = Path.Combine(Path.GetTempPath(), "PackageHub", "x-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);

            try
            {
                SafeExtract(zipPath, work);

                // Zipball của GitHub luôn có đúng một thư mục gốc dạng "owner-repo-sha".
                string[] tops = Directory.GetDirectories(work);
                string root = tops.Length == 1 ? tops[0] : work;

                string src = string.IsNullOrEmpty(subdir)
                    ? root
                    : Path.Combine(root, subdir.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(Path.Combine(src, "package.json")))
                    throw new HubException("Không thấy package.json trong bản tải về" +
                                           (string.IsNullOrEmpty(subdir) ? "." : " (thư mục " + subdir + ")."));

                CopyToPackages(src, packageName);
            }
            finally
            {
                try { Directory.Delete(work, true); }
                catch { /* thư mục tạm, bỏ qua */ }
            }
        }

        /// <summary>Đường dẫn đích trong Assets/PackageHub/&lt;mục&gt;/&lt;đường dẫn&gt;.</summary>
        public static string AssetsDest(string entryName, string rel)
        {
            string root = Path.Combine(Application.dataPath, "PackageHub", HubUtil.SafeFolderName(entryName));
            return string.IsNullOrEmpty(rel) ? root : Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Hỏi trước khi ghi đè; huỷ thì ném HubException.</summary>
        public static void ConfirmOverwrite(string dest, bool isDir)
        {
            bool exists = isDir ? Directory.Exists(dest) : File.Exists(dest);
            if (!exists) return;

            if (!EditorUtility.DisplayDialog("Ghi đè?", dest + " đã tồn tại. Ghi đè?", "Ghi đè", "Huỷ"))
                throw new HubException("Đã huỷ.");
        }

        /// <summary>Lấy một thư mục con của zipball GitHub chép vào destDir (gộp, ghi đè file trùng).</summary>
        public static void ExtractFolderFromZip(string zipPath, string subdir, string destDir)
        {
            string work = Path.Combine(Path.GetTempPath(), "PackageHub", "x-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);

            try
            {
                SafeExtract(zipPath, work);

                string[] tops = Directory.GetDirectories(work);
                string root = tops.Length == 1 ? tops[0] : work;
                string src = string.IsNullOrEmpty(subdir) ? root : Path.Combine(root, subdir.Replace('/', Path.DirectorySeparatorChar));

                if (!Directory.Exists(src)) throw new HubException("Không thấy thư mục trong bản tải về: " + subdir);

                CopyDirectory(src, destDir);
                AssetDatabase.Refresh();
            }
            finally
            {
                try { Directory.Delete(work, true); }
                catch { /* thư mục tạm */ }
            }
        }

        public static void CopyFolder(string src, string dest)
        {
            CopyDirectory(src, dest);
            AssetDatabase.Refresh();
        }

        /// <summary>Chép nguyên thư mục gói có sẵn trên đĩa vào Packages/.</summary>
        public static void EmbedFromFolder(string srcDir, string packageName)
        {
            if (!File.Exists(Path.Combine(srcDir, "package.json")))
                throw new HubException("Thư mục không còn package.json: " + srcDir);

            CopyToPackages(srcDir, packageName);
        }

        private static void CopyToPackages(string srcDir, string packageName)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string target = Path.Combine(projectRoot, "Packages", HubUtil.SafeFolderName(packageName));

            if (Directory.Exists(target))
            {
                bool replace = EditorUtility.DisplayDialog(
                    "Ghi đè package?",
                    $"Packages/{Path.GetFileName(target)} đã tồn tại. Xoá bản cũ và thay bằng bản mới?",
                    "Ghi đè", "Huỷ");
                if (!replace) throw new HubException("Đã huỷ.");

                Directory.Delete(target, true);
            }

            CopyDirectory(srcDir, target);
            AssetDatabase.Refresh();
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);

            foreach (string file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);

            foreach (string dir in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(dir);
                if (name == ".git") continue;
                CopyDirectory(dir, Path.Combine(dst, name));
            }
        }

        /// <summary>Giải nén có chặn "zip-slip": entry có đường dẫn chui ra ngoài thư mục đích thì bỏ qua.</summary>
        private static void SafeExtract(string zipPath, string destDir)
        {
            string root = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string dest = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
                    if (!dest.StartsWith(root, StringComparison.Ordinal)) continue;

                    if (string.IsNullOrEmpty(entry.Name))       // entry là thư mục
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                }
            }
        }
    }
}
