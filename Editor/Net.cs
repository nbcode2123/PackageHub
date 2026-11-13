using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace PackageHub
{
    /// <summary>
    /// Bộ chạy coroutine tối giản cho Editor, để viết luồng "tải -> kiểm tra -> đẩy lên"
    /// thành code tuyến tính thay vì lồng callback. Không phụ thuộc package EditorCoroutines.
    ///
    /// Quy ước: mỗi lần `yield return null` là nhường 1 tick của Editor.
    /// `yield return <IEnumerator>` chạy coroutine con rồi mới đi tiếp.
    /// Ném exception ở đâu cũng dừng cả job và báo về onFinished.
    /// </summary>
    public sealed class Job
    {
        private static readonly List<Job> Running = new List<Job>();

        /// <summary>Job đang được Step — để Http và code bên trong báo tiến độ mà không cần truyền tham chiếu.</summary>
        internal static Job Current;

        private readonly Stack<IEnumerator> _stack = new Stack<IEnumerator>();
        private readonly Action<Exception> _onFinished;

        internal UnityWebRequest ActiveRequest;

        public string Message;

        /// <summary>Tiến độ của request đang chạy (tải lên / tải xuống), 0..1.</summary>
        public float Transfer;

        /// <summary>Tiến độ cả đợt (ví dụ đã quét 12/87 repo), 0..1. Âm = không dùng.</summary>
        public float Overall = -1f;

        public float Shown => Overall >= 0f ? Overall : Transfer;

        public bool Cancelled { get; private set; }
        public bool IsRunning { get; private set; }

        private Job(IEnumerator root, Action<Exception> onFinished)
        {
            _stack.Push(root);
            _onFinished = onFinished;
            IsRunning = true;
        }

        /// <summary>onFinished(null) khi xong, onFinished(exception) khi lỗi hoặc huỷ.</summary>
        public static Job Run(IEnumerator root, Action<Exception> onFinished = null)
        {
            var job = new Job(root, onFinished);
            Running.Add(job);

            // -= trước += để chắc chắn chỉ đăng ký một lần, kể cả khi
            // onFinished của job này lại bật một job khác.
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            return job;
        }

        public static void Say(string message)
        {
            if (Current != null) Current.Message = message;
        }

        public static void SetOverall(float value)
        {
            if (Current != null) Current.Overall = value;
        }

        public void Cancel()
        {
            Cancelled = true;
            if (ActiveRequest != null) ActiveRequest.Abort();
        }

        /// <summary>
        /// Chạy inner nhưng lỗi chỉ báo qua onError rồi đi tiếp, không làm sập cả job.
        /// Dùng khi gom nhiều việc độc lập (quét từng repo, tải từng nguồn): một cái hỏng
        /// không được kéo theo các cái còn lại.
        /// </summary>
        public static IEnumerator Guard(IEnumerator inner, Action<Exception> onError)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(inner);

            try
            {
                while (stack.Count > 0)
                {
                    bool more = false;
                    bool failed = false;
                    object current = null;

                    try
                    {
                        IEnumerator top = stack.Peek();
                        more = top.MoveNext();
                        if (more) current = top.Current;
                    }
                    catch (Exception ex)
                    {
                        if (onError != null) onError(ex);
                        failed = true;
                    }

                    if (failed) yield break;

                    if (!more)
                    {
                        stack.Pop();
                        continue;
                    }

                    if (current is IEnumerator nested)
                    {
                        stack.Push(nested);
                        continue;
                    }

                    yield return null;
                }
            }
            finally
            {
                foreach (IEnumerator it in stack)
                {
                    var d = it as IDisposable;
                    if (d != null) d.Dispose();
                }
            }
        }

        private static void Tick()
        {
            for (int i = Running.Count - 1; i >= 0; i--)
            {
                Job job = Running[i];

                Current = job;
                bool alive = job.Step(out Exception error);
                Current = null;

                if (alive) continue;

                Running.RemoveAt(i);
                job.Unwind();
                job.IsRunning = false;

                try { if (job._onFinished != null) job._onFinished(error); }
                catch (Exception ex) { Debug.LogException(ex); }
            }

            if (Running.Count == 0) EditorApplication.update -= Tick;
        }

        /// <returns>true nếu còn việc phải làm ở tick sau.</returns>
        private bool Step(out Exception error)
        {
            error = null;

            if (Cancelled)
            {
                error = new HubException("Đã huỷ.");
                return false;
            }

            while (_stack.Count > 0)
            {
                IEnumerator top = _stack.Peek();

                bool more;
                try { more = top.MoveNext(); }
                catch (Exception ex)
                {
                    error = ex;
                    return false;
                }

                if (!more)
                {
                    _stack.Pop();
                    continue;
                }

                if (top.Current is IEnumerator nested)
                {
                    _stack.Push(nested);
                    continue;
                }

                return true;   // yield return null (hoặc giá trị lạ): nhường 1 tick
            }

            return false;
        }

        /// <summary>Dọn các coroutine còn dở để chạy khối finally/using (đóng request, xoá file tạm).</summary>
        private void Unwind()
        {
            while (_stack.Count > 0)
            {
                var it = _stack.Pop() as IDisposable;
                try { if (it != null) it.Dispose(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }
    }

    // ----------------------------------------------------------------------

    [Serializable]
    internal class ApiError
    {
        public string message;
    }

    /// <summary>Gọi HTTP tới GitHub, dạng coroutine để ghép vào Job.</summary>
    internal static class Http
    {
        public const string Json   = "application/vnd.github+json";
        public const string Raw    = "application/vnd.github.raw+json";
        public const string Binary = "application/octet-stream";

        public sealed class Result
        {
            public long Status;
            public string Text;
            public string NetworkError;      // khác null = chưa tới được server / bị huỷ
            public string RateRemaining;     // header x-ratelimit-remaining

            public bool Ok => NetworkError == null && Status >= 200 && Status < 300;

            /// <summary>Hết hạn mức request của GitHub: tiếp tục chỉ tốn công, nên dừng cả đợt.</summary>
            public bool RateLimited => (Status == 403 || Status == 429) && RateRemaining == "0";

            public string Describe()
            {
                if (NetworkError != null) return "Lỗi mạng: " + NetworkError;

                string api = null;
                if (!string.IsNullOrEmpty(Text))
                {
                    try { api = JsonUtility.FromJson<ApiError>(Text).message; }
                    catch { /* thân phản hồi không phải JSON */ }
                }

                string hint = null;
                if (RateLimited) hint = "Đã hết hạn mức request của GitHub, chờ một lúc rồi thử lại.";
                else
                {
                    switch (Status)
                    {
                        case 401: hint = "Token sai hoặc đã hết hạn."; break;
                        case 403: hint = "Token thiếu quyền cho thao tác này (Contents / Administration)."; break;
                        case 404: hint = "Không thấy repo / file, hoặc token chưa được cấp quyền vào repo đó."; break;
                    }
                }

                var sb = new StringBuilder("HTTP " + Status);
                if (!string.IsNullOrEmpty(api)) sb.Append(": ").Append(api);
                if (hint != null) sb.Append('\n').Append(hint);
                return sb.ToString();
            }
        }

        public static IEnumerator Get(string url, string accept, string token, Result res)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                Prepare(req, accept, token);
                yield return Run(req, res);
            }
        }

        /// <summary>POST / PUT / PATCH / DELETE với thân JSON nhỏ (jsonBody có thể null).</summary>
        public static IEnumerator Send(string method, string url, string jsonBody, string token, Result res)
        {
            using (var req = new UnityWebRequest(url, method))
            {
                if (jsonBody != null)
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody)) { contentType = "application/json" };
                req.downloadHandler = new DownloadHandlerBuffer();
                Prepare(req, Json, token);
                yield return Run(req, res);
            }
        }

        /// <summary>Đẩy file nhị phân từ đĩa (stream, không nạp cả file vào RAM). Dùng cho asset của Release.</summary>
        public static IEnumerator Upload(string url, string filePath, string token, Result res)
        {
            using (var req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerFile(filePath) { contentType = Binary };
                req.downloadHandler = new DownloadHandlerBuffer();
                Prepare(req, Json, token);
                yield return Run(req, res);
            }
        }

        /// <summary>
        /// Tải thẳng xuống đĩa. Tự xử lý chuyển hướng (không để Unity tự theo) vì GitHub hay chuyển
        /// sang máy chủ khác bằng đường dẫn đã ký sẵn: gửi kèm token sang đó là sai (và có thể bị từ chối).
        /// Chỉ gửi token khi vẫn ở cùng host với yêu cầu đầu.
        /// </summary>
        public static IEnumerator Download(string url, string accept, string token, string toPath, Result res)
        {
            string current = url;
            bool sendAuth = true;

            for (int hop = 0; hop < 6; hop++)
            {
                using (var req = UnityWebRequest.Get(current))
                {
                    req.redirectLimit = 0;
                    req.downloadHandler = new DownloadHandlerFile(toPath) { removeFileOnAbort = true };
                    if (sendAuth) Prepare(req, accept, token);

                    yield return Run(req, res);

                    if (res.Status >= 300 && res.Status < 400)
                    {
                        string location = req.GetResponseHeader("Location");
                        if (!string.IsNullOrEmpty(location))
                        {
                            var from = new Uri(current);
                            var to = new Uri(from, location);      // Location có thể là đường dẫn tương đối

                            sendAuth = sendAuth && to.Host == from.Host;
                            current = to.AbsoluteUri;
                            res.NetworkError = null;               // 3xx do ta tự không theo, không phải lỗi mạng
                            continue;
                        }
                    }

                    yield break;
                }
            }

            res.NetworkError = "Chuyển hướng quá nhiều lần.";
        }

        private static void Prepare(UnityWebRequest req, string accept, string token)
        {
            if (!string.IsNullOrEmpty(token)) req.SetRequestHeader("Authorization", "Bearer " + token);
            req.SetRequestHeader("Accept", accept);
            req.SetRequestHeader("X-GitHub-Api-Version", "2022-11-28");
        }

        private static IEnumerator Run(UnityWebRequest req, Result res)
        {
            Job job = Job.Current;
            if (job != null) job.ActiveRequest = req;

            UnityWebRequestAsyncOperation op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (job != null)
                    job.Transfer = Mathf.Clamp01(req.method == "GET" ? req.downloadProgress : req.uploadProgress);
                yield return null;
            }

            if (job != null) job.ActiveRequest = null;

            res.Status = req.responseCode;
            res.RateRemaining = req.GetResponseHeader("x-ratelimit-remaining");
            res.Text = req.downloadHandler is DownloadHandlerBuffer ? req.downloadHandler.text : null;
            res.NetworkError = null;

            if (job != null && job.Cancelled)
                res.NetworkError = "Đã huỷ.";
            else if (req.result == UnityWebRequest.Result.ConnectionError ||
                     req.result == UnityWebRequest.Result.DataProcessingError)
                res.NetworkError = req.error;
        }
    }
}
