# Package Hub 0.2

Cửa sổ `Window > Package Hub` trong Unity Editor. Gom mọi package của bạn từ nhiều nơi vào một danh sách, lọc theo tag, cài về, hoặc đóng gói từ project hiện tại rồi đẩy lên.

## Cài
Chép thư mục `PackageHub` vào `Packages/` của project, hoặc Package Manager > Add package from disk > chọn `package.json`. Cần Unity 2021.3 trở lên.

## Nguồn (tab "Nguồn")
**GitHub** - dán một token, Package Hub liệt kê mọi repo token thấy được (cá nhân, private, tổ chức). Không cần một repo riêng làm kho.
Token fine-grained: Repository access = All repositories; Contents = Read-only để xem và cài. Muốn đẩy lên thêm Contents = Read and write và Administration = Read and write (tạo repo, ghi topics). Token fine-grained thường KHÔNG tạo được repo mới cho tài khoản cá nhân: muốn tạo repo từ Package Hub hãy dùng token classic scope `repo`, hoặc tạo repo trên github.com rồi chọn "Repo có sẵn".

**Thư mục** - trỏ vào thư mục do Google Drive / Dropbox / OneDrive (app desktop) hoặc ổ mạng đồng bộ. Đây là cách nối Google Drive không cần OAuth. Thêm cloud khác (S3, GitLab, Drive API) = viết thêm một lớp kế thừa `Source`.

## Thư viện
- Package Hub quét repo tìm: thư mục có `package.json` kiểu Unity (UPM), file `*.unitypackage` trong repo, và file `.unitypackage` đính kèm Release.
- Quét là bước riêng (mỗi repo vài request). Kết quả được cache theo `pushed_at`, lần sau chỉ quét repo có thay đổi.
- Cột trái là phân bổ tag, số đếm tính trên các mục đã qua mọi bộ lọc khác. Bấm tag để lọc (AND).
- Tag = GitHub topics (hoặc tên thư mục cha với nguồn thư mục) + tag riêng lưu trên máy. "Sửa tag" có tuỳ chọn ghi lên GitHub topics.
- **Xem nội dung**: bấm ở mỗi mục để duyệt cây file/thư mục, bấm "Tải" để chép file hoặc cả thư mục vào `Assets/PackageHub/<tên mục>/`.
- **Cài**: UPM qua Package Manager (git URL; repo private cần git đã đăng nhập). **Nhúng**: tải bằng token rồi chép vào `Packages/`, dùng được cho repo private. **Import**: mở hộp thoại import của Unity.

## Tạo & đẩy lên
Kéo asset vào, đặt tên, phiên bản x.y.z, tag. Đích GitHub: tạo repo mới hoặc đẩy vào repo có sẵn; file thành asset của Release `vX.Y.Z`. Đích thư mục: chép `id-version.unitypackage` vào thư mục (có thể có thư mục con), app đồng bộ tự đẩy lên cloud. Phiên bản trùng bị từ chối, không ghi đè.

## Chưa kiểm chứng / giới hạn
- Chưa biên dịch trong Unity và chưa chạy với GitHub thật; mới kiểm tra cú pháp và đối chiếu tài liệu API. Có thể còn lỗi biên dịch nhỏ.
- `System.IO.Compression.ZipFile` (dùng cho "Nhúng") cần API Compatibility Level .NET Standard 2.1 / .NET Framework.
- Token lưu dạng văn bản thường trong EditorPrefs của máy bạn.
- Token chỉ chọn "Selected repositories" thì không tạo được repo mới.
- Quét giới hạn: tối đa 8 package.json mỗi repo; repo cực lớn có thể bị cắt cây thư mục.
- Chưa có chức năng xoá package trên cloud.
