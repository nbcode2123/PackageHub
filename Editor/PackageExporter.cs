using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace PackageHub
{
    internal static class PackageExporter
    {
        /// <summary>Đóng gói các đường dẫn Assets/... thành một file .unitypackage.</summary>
        /// <param name="includeDependencies">Kéo theo mọi asset mà các mục này tham chiếu tới —
        /// tiện nhưng có thể lôi cả nửa project vào nếu tham chiếu chằng chịt.</param>
        public static void Export(IList<string> assetPaths, string outFile, bool includeDependencies)
        {
            if (assetPaths == null || assetPaths.Count == 0)
                throw new HubException("Chưa chọn asset nào để đóng gói.");

            foreach (string p in assetPaths)
            {
                bool exists = AssetDatabase.IsValidFolder(p) || AssetDatabase.LoadMainAssetAtPath(p) != null;
                if (!exists) throw new HubException("Không tìm thấy asset: " + p);
            }

            var flags = ExportPackageOptions.Recurse;
            if (includeDependencies) flags |= ExportPackageOptions.IncludeDependencies;

            string dir = Path.GetDirectoryName(outFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(outFile)) File.Delete(outFile);

            var arr = new string[assetPaths.Count];
            assetPaths.CopyTo(arr, 0);
            AssetDatabase.ExportPackage(arr, outFile, flags);

            if (!File.Exists(outFile))
                throw new HubException("Unity không tạo ra file package (xem Console để biết lý do).");
        }
    }
}
