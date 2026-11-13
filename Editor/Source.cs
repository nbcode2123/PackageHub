using System.Collections;
using System.Collections.Generic;

namespace PackageHub
{
    /// <summary>Mọi thứ cần để đẩy một package lên một nguồn.</summary>
    internal sealed class PublishRequest
    {
        public string Id;
        public string Title;
        public string Version;
        public string Description;
        public string Notes;
        public List<string> Tags = new List<string>();
        public List<string> Paths = new List<string>();
        public bool IncludeDeps;

        // ---- đích ----
        public bool NewRepo;               // GitHub: tạo repo mới
        public string NewRepoName;
        public bool PrivateRepo = true;
        public HubEntry Repo;              // GitHub: repo có sẵn
        public string SubFolder;           // thư mục đồng bộ: thư mục con

        // ---- do cửa sổ điền sau khi xuất file ----
        public string ExportedFile;
        public long Size;

        // ---- do nguồn điền khi xong ----
        public string ResultMessage;
        public string ResultKey;           // key của mục vừa tạo, để quét lại và gắn tag
    }

    /// <summary>Một file hoặc thư mục trong nội dung của một mục (để duyệt cây).</summary>
    internal sealed class FileNode
    {
        public string Path;        // tương đối so với gốc mục, dùng '/'
        public bool IsDir;
        public long Size;

        public string Name => Path.Substring(Path.LastIndexOf('/') + 1);
        public int Depth
        {
            get { int d = 0; foreach (char c in Path) if (c == '/') d++; return d; }
        }
    }

    /// <summary>
    /// Một nơi chứa package. Thêm cloud mới (S3, GitLab, Google Drive API...) = viết thêm một lớp
    /// kế thừa lớp này rồi thêm một nhánh vào Create(); cửa sổ không phải sửa gì.
    /// Mọi hàm trả về là coroutine chạy trong Job, lỗi thì ném HubException.
    /// </summary>
    internal abstract class Source
    {
        public SourceConfig Config { get; private set; }

        protected Source(SourceConfig config)
        {
            Config = config;
        }

        public string Id => Config.id;
        public string Name => string.IsNullOrEmpty(Config.name) ? Config.kind : Config.name;

        public abstract bool CanPublish { get; }

        /// <summary>Kiểm tra cấu hình dùng được không, ghi kết quả vào message.</summary>
        public abstract IEnumerator Test(Box<string> message);

        /// <summary>Liệt kê nhanh mọi mục. Phần nặng (xem trong repo có package gì) để Scan lo.</summary>
        public abstract IEnumerator List(List<HubEntry> into);

        /// <summary>Tìm xem mục này chứa gì cài được, điền vào entry.artifacts.</summary>
        public abstract IEnumerator Scan(HubEntry entry);

        /// <param name="embed">Nhúng thẳng vào Packages/ thay vì cài tham chiếu.</param>
        public abstract IEnumerator Install(HubEntry entry, Artifact artifact, bool embed);

        public abstract IEnumerator Publish(PublishRequest request);

        /// <summary>Liệt kê file / thư mục bên trong mục để duyệt.</summary>
        public virtual IEnumerator ListFiles(HubEntry entry, List<FileNode> into)
        {
            throw new HubException("Nguồn này chưa hỗ trợ xem nội dung.");
        }

        /// <summary>Tải một file hoặc cả thư mục vào Assets/PackageHub/...</summary>
        public virtual IEnumerator Fetch(HubEntry entry, FileNode node)
        {
            throw new HubException("Nguồn này chưa hỗ trợ tải riêng lẻ.");
        }

        public static Source Create(SourceConfig config)
        {
            if (config == null) return null;
            if (config.kind == SourceConfig.GitHub) return new GitHubSource(config);
            if (config.kind == SourceConfig.Folder) return new FolderSource(config);
            return null;
        }
    }
}
