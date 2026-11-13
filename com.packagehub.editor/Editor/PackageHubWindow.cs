using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PackageHub
{
    /// <summary>Cửa sổ chính: Thư viện / Tạo &amp; đẩy lên / Nguồn.</summary>
    internal sealed class PackageHubWindow : EditorWindow
    {
        [MenuItem("Window/Package Hub")]
        private static void Open()
        {
            var w = GetWindow<PackageHubWindow>("Package Hub");
            w.minSize = new Vector2(760, 480);
        }

        private static readonly string[] Tabs = { "Thư viện", "Tạo & đẩy lên", "Nguồn" };
        private static readonly string[] KindNames =
            { "Package + chưa quét", "Tất cả", "Có UPM (package.json)", "Có .unitypackage", "Chưa quét" };

        // ---- nguồn & dữ liệu (không serialize: chứa token) ----
        [NonSerialized] private List<SourceConfig> _configs = new List<SourceConfig>();
        [NonSerialized] private Dictionary<string, Source> _sources = new Dictionary<string, Source>();
        [NonSerialized] private List<HubEntry> _entries = new List<HubEntry>();
        [NonSerialized] private bool _loaded;

        // ---- job ----
        [NonSerialized] private Job _job;
        [NonSerialized] private string _status = "";
        [NonSerialized] private bool _statusError;

        private bool Busy => _job != null && _job.IsRunning;

        // ---- giao diện ----
        private int _tab;
        private Vector2 _libScroll, _tagScroll, _createScroll, _srcScroll;

        // ---- bộ lọc ----
        private string _search = "";
        private string _sourceFilter = "";
        private string _ownerFilter = "";
        private int _kindFilter;
        private bool _showForks;
        private bool _showArchived;
        [NonSerialized] private HashSet<string> _tagFilter = new HashSet<string>();

        // ---- kết quả lọc, chỉ tính lại ở sự kiện Layout để Layout/Repaint luôn khớp nhau ----
        [NonSerialized] private List<HubEntry> _view = new List<HubEntry>();
        [NonSerialized] private List<KeyValuePair<string, int>> _facets = new List<KeyValuePair<string, int>>();
        [NonSerialized] private int _facetScope;
        [NonSerialized] private int _scanTargets;
        [NonSerialized] private int _statTotal, _statPkg, _statUnscanned, _statPrivate;
        [NonSerialized] private string[] _sourceLabels = new string[0];
        [NonSerialized] private string[] _sourceIds = new string[0];
        [NonSerialized] private string[] _owners = new string[0];

        // ---- duyệt nội dung ----
        [NonSerialized] private Dictionary<string, List<FileNode>> _trees = new Dictionary<string, List<FileNode>>();
        [NonSerialized] private HashSet<string> _openDirs = new HashSet<string>();
        private string _browseKey;
        private Vector2 _treeScroll;

        // ---- sửa tag ----
        private string _editKey;
        private string _editText = "";
        private bool _writeTopics;

        // ---- tab Nguồn ----
        [NonSerialized] private List<SourceConfig> _edit = new List<SourceConfig>();
        [NonSerialized] private Dictionary<string, string> _testMsg = new Dictionary<string, string>();
        private int _pendingRemove = -1;

        // ---- tab Tạo ----
        private List<string> _paths = new List<string>();
        private string _name = "";
        private string _version = "1.0.0";
        private string _desc = "";
        private string _notes = "";
        private string _tagsText = "";
        private bool _deps;
        private int _destIndex;
        private bool _newRepo = true;
        private string _newRepoName = "";
        private bool _private = true;
        private string _repoKey, _autoKey;
        private string _repoFilter = "";
        private string _subFolder = "";

        // =====================================================================
        //  Vòng đời
        // =====================================================================

        private void OnEnable()
        {
            wantsMouseMove = true;
            LoadConfigs();
            if (_configs.Count == 0) _tab = 2;
            else if (!_loaded) Reload(null);
        }

        private void OnInspectorUpdate()
        {
            if (Busy) Repaint();
        }

        private void LoadConfigs()
        {
            _configs = SourceStore.Load();
            _edit = _configs.Select(c => c.Clone()).ToList();

            _sources = new Dictionary<string, Source>();
            foreach (SourceConfig cfg in _configs)
            {
                Source s = Source.Create(cfg);
                if (s != null) _sources[cfg.id] = s;
            }
        }

        private Source SourceOf(HubEntry e)
        {
            return e != null && e.sourceId != null && _sources.TryGetValue(e.sourceId, out Source s) ? s : null;
        }

        // =====================================================================
        //  Chạy việc nền
        // =====================================================================

        private void Begin(string start, IEnumerator routine, string done, Action onOk)
        {
            if (Busy) return;

            _status = start;
            _statusError = false;

            _job = Job.Run(routine, error =>
            {
                _job = null;
                if (error != null)
                {
                    _status = error.Message;
                    _statusError = true;
                }
                else
                {
                    if (done != null) _status = done;
                    if (onOk != null) onOk();
                }
                Repaint();
            });
        }

        private void Reload(string thenScanKey, string successMessage = null)
        {
            var result = new List<HubEntry>();
            var errors = new List<string>();

            Begin("Đang tải danh sách...", LoadAll(result, errors), null, () =>
            {
                _entries = result;
                _loaded = true;

                if (errors.Count > 0)
                {
                    _status = string.Join("\n", errors);
                    _statusError = true;
                }
                else
                {
                    _status = successMessage ?? $"Đã tải {_entries.Count} mục.";
                }

                if (thenScanKey == null) return;

                HubEntry fresh = _entries.FirstOrDefault(x => x.key == thenScanKey);
                Source src = SourceOf(fresh);
                if (fresh != null && src != null && !fresh.scanned)
                {
                    string keep = _status;
                    Begin("Đang quét " + fresh.FullName + "...", src.Scan(fresh), keep, ScanCache.Save);
                }
            });
        }

        private IEnumerator LoadAll(List<HubEntry> result, List<string> errors)
        {
            foreach (SourceConfig cfg in _configs)
            {
                if (!_sources.TryGetValue(cfg.id, out Source src)) continue;

                var list = new List<HubEntry>();
                string label = src.Name;
                yield return Job.Guard(src.List(list), ex => errors.Add(label + ": " + ex.Message));
                result.AddRange(list);
            }
        }

        private IEnumerator ScanMany(List<HubEntry> targets)
        {
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    HubEntry entry = targets[i];
                    Source src = SourceOf(entry);
                    if (src == null) continue;

                    Job.SetOverall(i / (float)targets.Count);
                    Job.Say($"Quét {entry.FullName} ({i + 1}/{targets.Count})");

                    string problem = null;
                    bool fatal = false;

                    yield return Job.Guard(src.Scan(entry), ex =>
                    {
                        problem = ex.Message;
                        var hub = ex as HubException;
                        if (hub != null && hub.Fatal) fatal = true;
                    });

                    if (problem != null) entry.scanNote = problem;

                    if (fatal)
                        throw new HubException("Dừng quét: " + problem, true);
                }
            }
            finally
            {
                ScanCache.Save();
            }
        }

        // =====================================================================
        //  OnGUI
        // =====================================================================

        private void OnGUI()
        {
            if (Event.current.type == EventType.Layout) RecomputeView();
            if (Event.current.type == EventType.MouseMove) Repaint();      // để nút đổi màu khi rê chuột

            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), HubTheme.Bg);

            DrawHeader();

            switch (_tab)
            {
                case 0: DrawLibrary(); break;
                case 1: DrawCreate(); break;
                default: DrawSources(); break;
            }

            DrawStatusBar();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(HubTheme.PanelBox);

            GUILayout.Label("<color=#" + HubTheme.Hex(HubTheme.Accent) + ">PACKAGE</color> HUB", HubTheme.Title, GUILayout.ExpandWidth(false));
            GUILayout.Space(16);

            for (int i = 0; i < Tabs.Length; i++)
            {
                GUIStyle style = new GUIStyle(HubTheme.TabText);
                if (i == _tab) style.normal.textColor = HubTheme.Text;

                Rect r = GUILayoutUtility.GetRect(new GUIContent(Tabs[i]), style, GUILayout.Height(26), GUILayout.MinWidth(110));

                if (Event.current.type == EventType.Repaint)
                {
                    style.Draw(r, Tabs[i], r.Contains(Event.current.mousePosition), false, false, false);
                    if (i == _tab) EditorGUI.DrawRect(new Rect(r.x + 8, r.yMax - 3, r.width - 16, 3), HubTheme.Accent);
                }

                if (GUI.Button(r, GUIContent.none, GUIStyle.none)) _tab = i;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private bool Btn(string text, params GUILayoutOption[] options)
        {
            return HubTheme.Button(text, HubTheme.Accent, HubTheme.Look.Soft, !Busy, options);
        }

        /// <summary>Nút có màu nhấn (xanh lá = hành động chính, xanh dương = lấy về...).</summary>
        private bool Btn(string text, Color tint, params GUILayoutOption[] options)
        {
            return HubTheme.Button(text, tint, HubTheme.Look.Solid, !Busy, options);
        }

        private static bool Soft(string text, Color color, params GUILayoutOption[] options)
        {
            return HubTheme.Button(text, color, HubTheme.Look.Soft, true, options);
        }

        private void DrawStatusBar()
        {
            GUILayout.FlexibleSpace();

            if (Busy)
            {
                EditorGUILayout.BeginHorizontal(HubTheme.PanelBox);
                Rect r = GUILayoutUtility.GetRect(100, 20, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(r, Mathf.Clamp01(_job.Shown), _job.Message ?? "");
                if (Soft("Huỷ", HubTheme.Red, GUILayout.Width(56), GUILayout.Height(22))) _job.Cancel();
                EditorGUILayout.EndHorizontal();
                return;
            }

            if (string.IsNullOrEmpty(_status)) return;

            Color tone = _statusError ? HubTheme.Red : (_status.StartsWith("Đã") ? HubTheme.Green : HubTheme.Accent);
            string tag = _statusError ? "LỖI" : (_status.StartsWith("Đã") ? "XONG" : "INFO");

            HubTheme.BeginCard();
            EditorGUILayout.BeginHorizontal();
            HubTheme.Chip(tag, tone);
            GUILayout.Label(HubTheme.Esc(_status), HubTheme.Body);
            if (Soft("×", HubTheme.Red, GUILayout.Width(28))) _status = "";
            EditorGUILayout.EndHorizontal();
            HubTheme.EndCard(tone);
        }

        // =====================================================================
        //  Lọc
        // =====================================================================

        private static List<string> TagsOf(HubEntry e)
        {
            var all = new List<string>(e.topics);
            foreach (string t in TagStore.Get(e.key))
                if (!all.Contains(t)) all.Add(t);
            return all;
        }

        private bool PassesBase(HubEntry e)
        {
            if (_sourceFilter.Length > 0 && e.sourceId != _sourceFilter) return false;
            if (_ownerFilter.Length > 0 && e.owner != _ownerFilter) return false;
            if (!_showForks && e.isFork) return false;
            if (!_showArchived && e.isArchived) return false;

            if (_search.Length > 0)
            {
                string hay = (e.FullName + " " + e.description + " " + string.Join(" ", TagsOf(e))).ToLowerInvariant();
                if (hay.IndexOf(_search.ToLowerInvariant(), StringComparison.Ordinal) < 0) return false;
            }
            return true;
        }

        private bool PassesKind(HubEntry e)
        {
            switch (_kindFilter)
            {
                case 0: return e.HasAnyPackage || !e.scanned;
                case 2: return e.HasUpm;
                case 3: return e.HasPackageFile;
                case 4: return !e.scanned;
                default: return true;
            }
        }

        private void RecomputeView()
        {
            // nhãn nguồn & chủ repo cho popup
            _sourceIds = new[] { "" }.Concat(_configs.Select(c => c.id)).ToArray();
            _sourceLabels = new[] { "Tất cả nguồn" }.Concat(_configs.Select(c => string.IsNullOrEmpty(c.name) ? c.kind : c.name)).ToArray();
            _owners = new[] { "" }.Concat(_entries.Where(x => !string.IsNullOrEmpty(x.owner)).Select(x => x.owner).Distinct().OrderBy(x => x)).ToArray();

            if (_ownerFilter.Length > 0 && Array.IndexOf(_owners, _ownerFilter) < 0) _ownerFilter = "";
            if (_sourceFilter.Length > 0 && Array.IndexOf(_sourceIds, _sourceFilter) < 0) _sourceFilter = "";

            // phạm vi để đếm tag: qua mọi bộ lọc TRỪ bộ lọc tag (để thấy "chọn thêm tag này còn bao nhiêu")
            List<HubEntry> scope = _entries.Where(x => PassesBase(x) && PassesKind(x)).ToList();
            _facetScope = scope.Count;

            var counts = new Dictionary<string, int>();
            foreach (HubEntry e in scope)
                foreach (string t in TagsOf(e))
                    counts[t] = counts.TryGetValue(t, out int n) ? n + 1 : 1;

            _facets = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();

            _view = scope
                .Where(x => _tagFilter.All(t => TagsOf(x).Contains(t)))
                .OrderByDescending(x => x.updated ?? "")
                .ToList();

            List<HubEntry> inBase = _entries.Where(PassesBase).ToList();
            _statTotal = inBase.Count;
            _statPkg = inBase.Count(x => x.HasAnyPackage);
            _statUnscanned = inBase.Count(x => !x.scanned);
            _statPrivate = inBase.Count(x => x.isPrivate);

            _scanTargets = _entries.Count(x => PassesBase(x) && !x.scanned && SourceOf(x) != null && SourceOf(x).Config.kind == SourceConfig.GitHub);
        }

        // =====================================================================
        //  Tab Thư viện
        // =====================================================================

        private void DrawLibrary()
        {
            if (_configs.Count == 0)
            {
                EditorGUILayout.HelpBox("Chưa có nguồn nào. Sang tab 'Nguồn' để thêm tài khoản GitHub hoặc một thư mục đồng bộ.", MessageType.Info);
                return;
            }

            DrawLibraryGuide();
            DrawFilters();

            EditorGUILayout.BeginHorizontal();

            // ---- cột tag (phân bổ) ----
            EditorGUILayout.BeginVertical(GUILayout.Width(200));
            DrawFacets();
            EditorGUILayout.EndVertical();

            // ---- danh sách ----
            EditorGUILayout.BeginVertical();
            _libScroll = EditorGUILayout.BeginScrollView(_libScroll);

            if (_view.Count == 0)
            {
                EditorGUILayout.HelpBox(_loaded ? "Không có mục nào khớp bộ lọc." : "Chưa tải. Bấm 'Tải lại'.", MessageType.None);
            }

            int shown = Mathf.Min(_view.Count, 200);
            for (int i = 0; i < shown; i++) DrawEntry(_view[i]);

            if (_view.Count > shown)
                EditorGUILayout.HelpBox($"Hiện {shown}/{_view.Count} mục đầu. Lọc thêm để thu hẹp.", MessageType.None);

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilters()
        {
            EditorGUILayout.BeginHorizontal();
            HubTheme.Tile("Tổng mục", _statTotal, HubTheme.Accent);
            HubTheme.Tile("Có package", _statPkg, HubTheme.Green);
            HubTheme.Tile("Chưa quét", _statUnscanned, HubTheme.Amber);
            HubTheme.Tile("Private", _statPrivate, HubTheme.Purple);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginVertical(HubTheme.PanelBox);

            EditorGUILayout.BeginHorizontal();
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));

            int si = Mathf.Max(0, Array.IndexOf(_sourceIds, _sourceFilter));
            int newSi = EditorGUILayout.Popup(si, _sourceLabels, GUILayout.Width(130));
            _sourceFilter = _sourceIds.Length > newSi ? _sourceIds[newSi] : "";

            int oi = Mathf.Max(0, Array.IndexOf(_owners, _ownerFilter));
            string[] ownerLabels = _owners.Select(o => o.Length == 0 ? "Mọi chủ repo" : o).ToArray();
            int newOi = EditorGUILayout.Popup(oi, ownerLabels, GUILayout.Width(130));
            _ownerFilter = _owners.Length > newOi ? _owners[newOi] : "";

            _kindFilter = EditorGUILayout.Popup(_kindFilter, KindNames, GUILayout.Width(150));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _showForks = GUILayout.Toggle(_showForks, "Hiện fork", GUILayout.Width(80));
            _showArchived = GUILayout.Toggle(_showArchived, "Hiện repo lưu trữ", GUILayout.Width(130));
            GUILayout.FlexibleSpace();
            GUILayout.Label($"Đang hiện {_view.Count} / {_entries.Count}", HubTheme.Meta);

            if (Btn("Tải lại", GUILayout.Width(80))) Reload(null);

            if (_scanTargets > 0)
            {
                if (Btn($"Quét {_scanTargets} repo", HubTheme.Amber, GUILayout.Width(120)))
                {
                    List<HubEntry> targets = _entries
                        .Where(x => PassesBase(x) && !x.scanned && SourceOf(x) != null && SourceOf(x).Config.kind == SourceConfig.GitHub)
                        .ToList();
                    Begin("Đang quét...", ScanMany(targets), $"Đã quét {targets.Count} repo.", null);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawFacets()
        {
            HubTheme.SectionHeader("Phân bổ theo tag");

            if (_tagFilter.Count > 0 && Btn("Bỏ lọc tag (" + _tagFilter.Count + ")", HubTheme.Red))
                _tagFilter.Clear();

            _tagScroll = EditorGUILayout.BeginScrollView(_tagScroll);

            if (_facets.Count == 0)
                GUILayout.Label("Chưa có tag nào.", HubTheme.Meta);

            int max = _facets.Count > 0 ? _facets[0].Value : 1;

            foreach (KeyValuePair<string, int> kv in _facets)
            {
                bool on = _tagFilter.Contains(kv.Key);

                Rect r = GUILayoutUtility.GetRect(180, 24, GUILayout.ExpandWidth(true));
                Rect bar = new Rect(r.x, r.y + 2, r.width, r.height - 4);
                float frac = kv.Value / (float)max;
                Color tag = HubUtil.ColorFor(kv.Key);

                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(bar, HubTheme.Panel);
                    EditorGUI.DrawRect(new Rect(bar.x, bar.y, Mathf.Max(4, bar.width * frac), bar.height),
                                       new Color(tag.r, tag.g, tag.b, on ? 1f : 0.7f));

                    if (on)
                    {
                        Color a = HubTheme.Accent;
                        EditorGUI.DrawRect(new Rect(bar.x, bar.y, bar.width, 2), a);
                        EditorGUI.DrawRect(new Rect(bar.x, bar.yMax - 2, bar.width, 2), a);
                        EditorGUI.DrawRect(new Rect(bar.x, bar.y, 2, bar.height), a);
                        EditorGUI.DrawRect(new Rect(bar.xMax - 2, bar.y, 2, bar.height), a);
                    }
                }

                var name = new GUIStyle(HubTheme.Body) { fontStyle = on ? FontStyle.Bold : FontStyle.Normal, wordWrap = false, clipping = TextClipping.Clip };
                name.normal.textColor = HubTheme.IsLight(tag) && frac > 0.4f ? new Color(0.08f, 0.09f, 0.1f) : HubTheme.Text;
                GUI.Label(new Rect(bar.x + 6, bar.y, bar.width - 40, bar.height), kv.Key, name);

                var count = new GUIStyle(HubTheme.Mono) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
                GUI.Label(new Rect(bar.xMax - 38, bar.y, 34, bar.height), kv.Value.ToString(), count);

                if (GUI.Button(r, GUIContent.none, GUIStyle.none))
                {
                    if (!_tagFilter.Add(kv.Key)) _tagFilter.Remove(kv.Key);
                }
            }

            EditorGUILayout.EndScrollView();

            GUILayout.Label($"Đếm trên {_facetScope} mục", HubTheme.Meta);
        }

        // ---------------------------------------------------------------- thẻ ---

        private static Color AccentFor(HubEntry e)
        {
            if (!string.IsNullOrEmpty(e.scanNote)) return HubTheme.Amber;
            if (e.HasUpm) return HubTheme.Green;
            if (e.HasPackageFile) return HubTheme.Accent;
            return HubTheme.Gray;
        }

        private void DrawEntry(HubEntry e)
        {
            HubTheme.BeginCard();

            // ---- tiêu đề: chủ repo nhạt, tên repo đậm ----
            EditorGUILayout.BeginHorizontal();
            string title = string.IsNullOrEmpty(e.owner)
                ? "<b>" + HubTheme.Esc(e.name) + "</b>"
                : "<color=#" + HubTheme.Hex(HubTheme.Muted) + ">" + HubTheme.Esc(e.owner) + "/</color><b>" + HubTheme.Esc(e.name) + "</b>";
            GUILayout.Label(title, HubTheme.Title, GUILayout.ExpandWidth(false));
            GUILayout.Space(6);
            if (e.isPrivate) HubTheme.Chip("PRIVATE", HubTheme.Amber);
            if (e.isFork) HubTheme.Chip("FORK", HubTheme.Gray);
            if (e.isArchived) HubTheme.Chip("LƯU TRỮ", HubTheme.Red);
            if (!e.scanned) HubTheme.Chip("CHƯA QUÉT", HubTheme.Gray);
            GUILayout.FlexibleSpace();
            GUILayout.Label(HubUtil.FormatDate(e.updated), HubTheme.Meta);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(e.description))
                GUILayout.Label(HubTheme.Esc(e.description), HubTheme.Sub);

            // ---- tag ----
            List<string> tags = TagsOf(e);
            EditorGUILayout.BeginHorizontal();
            foreach (string t in tags.Take(8))
            {
                if (HubTheme.ChipButton("#" + t, HubUtil.ColorFor(t)))
                {
                    if (!_tagFilter.Add(t)) _tagFilter.Remove(t);
                }
            }
            if (tags.Count > 8) GUILayout.Label("+" + (tags.Count - 8), HubTheme.Meta);
            GUILayout.FlexibleSpace();
            if (Soft(_editKey == e.key ? "Đóng tag" : "Sửa tag", HubTheme.Purple, GUILayout.Width(84)))
                ToggleTagEdit(e, tags);
            EditorGUILayout.EndHorizontal();

            if (_editKey == e.key) DrawTagEditor(e);

            // ---- thứ cài được ----
            foreach (Artifact a in e.artifacts) DrawArtifact(e, a);

            if (!e.scanned && SourceOf(e) != null)
            {
                EditorGUILayout.BeginHorizontal(HubTheme.RowBox);
                GUILayout.Label("Chưa biết trong repo có package gì.", HubTheme.Sub);
                GUILayout.FlexibleSpace();
                if (Btn("Quét repo này", HubTheme.Amber, GUILayout.Width(110)))
                {
                    Source src = SourceOf(e);
                    Begin("Đang quét " + e.FullName + "...", src.Scan(e), "Đã quét " + e.FullName + ".", ScanCache.Save);
                }
                EditorGUILayout.EndHorizontal();
            }
            else if (e.scanned && e.artifacts.Count == 0 && string.IsNullOrEmpty(e.scanNote))
            {
                GUILayout.Label("Không có package.json kiểu Unity hay .unitypackage. Bấm 'Xem nội dung' để lấy từng file.", HubTheme.Meta);
            }

            if (!string.IsNullOrEmpty(e.scanNote))
            {
                EditorGUILayout.BeginHorizontal(HubTheme.RowBox);
                HubTheme.Chip("LƯU Ý", HubTheme.Amber);
                GUILayout.Label(HubTheme.Esc(e.scanNote), HubTheme.Sub);
                EditorGUILayout.EndHorizontal();
            }

            if (_browseKey == e.key) DrawTree(e);

            if (!string.IsNullOrEmpty(e.htmlUrl) || SourceOf(e) != null)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (SourceOf(e) != null &&
                    Btn(_browseKey == e.key ? "Đóng nội dung" : "Xem nội dung", HubTheme.Teal, GUILayout.Width(130)))
                    ToggleBrowse(e);
                if (!string.IsNullOrEmpty(e.htmlUrl))
                    if (Soft("Mở trên web", HubTheme.Gray, GUILayout.Width(110)))
                        Application.OpenURL(e.htmlUrl);
                EditorGUILayout.EndHorizontal();
            }

            HubTheme.EndCard(AccentFor(e));
        }

        

        private void DrawArtifact(HubEntry e, Artifact a)
        {
            EditorGUILayout.BeginHorizontal(HubTheme.RowBox);

            string kind;
            Color kindColor;
            switch (a.Kind)
            {
                case ArtifactKind.UpmGit:
                case ArtifactKind.UpmFolder: kind = "UPM"; kindColor = HubTheme.Green; break;
                case ArtifactKind.PackageAsset: kind = "RELEASE"; kindColor = HubTheme.Purple; break;
                default: kind = ".UNITYPACKAGE"; kindColor = HubTheme.Accent; break;
            }

            HubTheme.Chip(kind, kindColor);
            GUILayout.Label(HubTheme.Esc(a.label), HubTheme.Mono, GUILayout.ExpandWidth(false));
            if (!string.IsNullOrEmpty(a.version)) HubTheme.Chip("v" + a.version, HubTheme.Teal);
            if (a.size > 0) GUILayout.Label(HubUtil.FormatSize(a.size), HubTheme.Meta, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();

            Source src = SourceOf(e);

            if (a.IsUpm)
            {
                if (Btn("Cài", HubTheme.Green, GUILayout.Width(50)) && src != null)
                    Begin("Đang cài " + a.label + "...", src.Install(e, a, false), "Đã gửi cho Package Manager: " + a.label, null);

                if (Btn("Nhúng", HubTheme.Teal, GUILayout.Width(55)) && src != null)
                    Begin("Đang nhúng " + a.label + "...", src.Install(e, a, true), "Đã nhúng " + a.label + " vào Packages/.", null);

                if (a.Kind == ArtifactKind.UpmGit && Soft("URL", HubTheme.Gray, GUILayout.Width(48)))
                {
                    EditorGUIUtility.systemCopyBuffer = GitHubSource.GitUrl(e, a);
                    _status = "Đã chép git URL vào clipboard.";
                    _statusError = false;
                }
            }
            else
            {
                if (Btn("Import", HubTheme.Accent, GUILayout.Width(60)) && src != null)
                    Begin("Đang lấy " + a.label + "...", src.Install(e, a, false), null, null);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ----------------------------------------------------- hướng dẫn ---

        private static bool GuideOpen(int tab) => EditorPrefs.GetBool("PackageHub.guide." + tab, true);

        private void DrawGuide(int tab, string title, string[] steps)
        {
            bool open = GuideOpen(tab);

            Color tone = HubTheme.Amber;
            HubTheme.BeginCard();

            EditorGUILayout.BeginHorizontal();
            HubTheme.Chip("HƯỚNG DẪN", tone);
            GUILayout.Label(HubTheme.Esc(title), HubTheme.Body);
            GUILayout.FlexibleSpace();
            if (Soft(open ? "Ẩn" : "Hiện", tone, GUILayout.Width(60)))
                EditorPrefs.SetBool("PackageHub.guide." + tab, !open);
            EditorGUILayout.EndHorizontal();

            if (open)
            {
                GUILayout.Space(4);
                for (int i = 0; i < steps.Length; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    HubTheme.StepNumber(i + 1, tone);
                    GUILayout.Label(steps[i], HubTheme.Body);
                    EditorGUILayout.EndHorizontal();
                    GUILayout.Space(2);
                }
            }

            HubTheme.EndCard(tone);
        }

        private void DrawLibraryGuide()
        {
            DrawGuide(0, "Cách dùng thư viện", new[]
            {
                "Thêm tài khoản GitHub hoặc thư mục đồng bộ ở tab " + HubTheme.Key("Nguồn") + ".",
                "Bấm " + HubTheme.Key("Quét N repo") + " để biết repo nào có package (chỉ cần làm một lần, lần sau chỉ quét repo có thay đổi).",
                "Lọc bằng ô tìm, menu bộ lọc, hoặc bấm một " + HubTheme.Key("tag") + " ở cột trái.",
                HubTheme.Key("Cài") + " = qua Package Manager, " + HubTheme.Key("Nhúng") + " = chép vào Packages/, " +
                    HubTheme.Key("Import") + " = mở hộp thoại .unitypackage, " + HubTheme.Key("Xem nội dung") + " = lấy riêng từng file / thư mục.",
            });
        }

        private void DrawCreateGuide()
        {
            DrawGuide(1, "Đẩy một package từ project lên cloud", new[]
            {
                HubTheme.Key("Kéo asset") + " (hoặc thư mục script) từ cửa sổ Project vào khung bên dưới.",
                "Đặt " + HubTheme.Key("tên, phiên bản x.y.z") + " và tag.",
                "Chọn " + HubTheme.Key("Tạo repo mới") + " hoặc " + HubTheme.Key("Repo có sẵn") + ": chọn repo có sẵn thì tên, phiên bản kế tiếp, mô tả và tag được điền theo repo đó.",
                "Bấm " + HubTheme.Key("Xuất & đẩy lên") + ". Phiên bản trùng bị từ chối, không ghi đè.",
            });
        }

        private void DrawSourcesGuide()
        {
            DrawGuide(2, "Kết nối nguồn trong 4 bước", new[]
            {
                "Bấm " + HubTheme.Key("+ GitHub") + " hoặc " + HubTheme.Key("+ Thư mục") + ".",
                "GitHub: dán " + HubTheme.Key("token") + " (hướng dẫn tạo ở cuối trang). Thư mục: bấm " + HubTheme.Key("Chọn...") + " tới thư mục Drive / Dropbox đã đồng bộ.",
                "Bấm " + HubTheme.Key("Kiểm tra") + " để chắc chắn kết nối được.",
                "Bấm " + HubTheme.Key("Lưu & tải lại") + ", rồi sang tab Thư viện.",
            });
        }

        // ----------------------------------------------------- nội dung ---

        private void ToggleBrowse(HubEntry e)
        {
            if (_browseKey == e.key)
            {
                _browseKey = null;
                return;
            }

            _browseKey = e.key;
            _openDirs.Clear();
            _treeScroll = Vector2.zero;

            if (_trees.ContainsKey(e.key)) return;

            Source src = SourceOf(e);
            if (src == null) return;

            var nodes = new List<FileNode>();
            string key = e.key;
            Begin("Đang đọc nội dung " + e.FullName + "...", src.ListFiles(e, nodes), "", () => _trees[key] = nodes);
        }

        private void DrawTree(HubEntry e)
        {
            if (!_trees.TryGetValue(e.key, out List<FileNode> nodes))
            {
                GUILayout.Label(Busy ? "Đang tải danh sách file..." : "Không đọc được nội dung.", EditorStyles.miniLabel);
                return;
            }

            if (nodes.Count == 0)
            {
                GUILayout.Label("Trống.", EditorStyles.miniLabel);
                return;
            }

            GUILayout.Label("Tải về sẽ nằm trong Assets/PackageHub/" + HubUtil.SafeFolderName(e.name), HubTheme.Meta);

            _treeScroll = EditorGUILayout.BeginScrollView(_treeScroll, GUILayout.Height(Mathf.Min(260, 18 * nodes.Count + 6)));

            int rows = 0;
            foreach (FileNode n in nodes)
            {
                if (!AncestorsOpen(n.Path)) continue;
                if (rows++ >= 400) break;

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(n.Depth * 14);

                if (n.IsDir)
                {
                    bool open = _openDirs.Contains(n.Path);
                    if (GUILayout.Button((open ? "[-] " : "[+] ") + n.Name, HubTheme.DirText))
                    {
                        if (open) _openDirs.Remove(n.Path); else _openDirs.Add(n.Path);
                    }
                }
                else
                {
                    GUILayout.Label(n.Name, HubTheme.Mono);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(HubUtil.FormatSize(n.Size), HubTheme.Meta, GUILayout.Width(70));
                }

                if (n.IsDir) GUILayout.FlexibleSpace();

                if (Btn("Tải", HubTheme.Accent, GUILayout.Width(44)))
                {
                    Source src = SourceOf(e);
                    FileNode node = n;
                    if (src != null)
                        Begin("Đang tải " + node.Name + "...", src.Fetch(e, node),
                              "Đã tải " + node.Name + " vào Assets/PackageHub/" + HubUtil.SafeFolderName(e.name) + "/", null);
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        private bool AncestorsOpen(string path)
        {
            int i = path.IndexOf('/');
            while (i >= 0)
            {
                if (!_openDirs.Contains(path.Substring(0, i))) return false;
                i = path.IndexOf('/', i + 1);
            }
            return true;
        }

        // ---------------------------------------------------------------- tag ---

        private void ToggleTagEdit(HubEntry e, List<string> tags)
        {
            if (_editKey == e.key)
            {
                _editKey = null;
                return;
            }

            _editKey = e.key;
            _editText = HubUtil.JoinTags(tags);
            _writeTopics = false;
            GUI.FocusControl(null);
        }

        private void DrawTagEditor(HubEntry e)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Các tag, cách nhau bằng dấu phẩy:", EditorStyles.miniLabel);
            _editText = EditorGUILayout.TextField(_editText);

            Source src = SourceOf(e);
            bool github = src != null && src.Config.kind == SourceConfig.GitHub && e.canPush;
            if (github)
                _writeTopics = EditorGUILayout.ToggleLeft("Ghi lên GitHub topics (đồng bộ mọi máy, cần quyền Administration)", _writeTopics);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (Btn("Lưu tag", HubTheme.Green, GUILayout.Width(90)))
            {
                List<string> parsed = HubUtil.ParseTags(_editText);

                if (github && _writeTopics)
                {
                    // topics mới = danh sách vừa nhập; tag cục bộ của mục này không còn cần nữa
                    var gh = (GitHubSource)src;
                    string key = e.key;
                    Begin("Đang ghi topics...", gh.SaveTopics(e, parsed), "Đã ghi topics lên GitHub.", () => TagStore.Set(key, null));
                }
                else
                {
                    // chỉ lưu phần không thuộc topics của nguồn
                    TagStore.Set(e.key, parsed.Where(t => !e.topics.Contains(t)).ToList());
                    _status = "Đã lưu tag trên máy này.";
                    _statusError = false;
                }
                _editKey = null;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // =====================================================================
        //  Tab Tạo & đẩy lên
        // =====================================================================

        private void DrawCreate()
        {
            _createScroll = EditorGUILayout.BeginScrollView(_createScroll);

            DrawCreateGuide();

            // ---- asset ----
            HubTheme.BeginCard();
            HubTheme.SectionHeader("1 · Chọn asset trong project");
            DrawDropArea();

            EditorGUILayout.BeginHorizontal();
            if (Soft("Thêm asset đang chọn", HubTheme.Accent, GUILayout.Width(180))) AddSelection();
            if (Soft("Xoá hết", HubTheme.Red, GUILayout.Width(80))) _paths.Clear();
            EditorGUILayout.EndHorizontal();

            int remove = -1;
            for (int i = 0; i < _paths.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(_paths[i], EditorStyles.miniLabel);
                if (Soft("×", HubTheme.Red, GUILayout.Width(28))) remove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (remove >= 0) _paths.RemoveAt(remove);

            HubTheme.EndCard(HubTheme.Accent);

            // ---- thông tin ----
            HubTheme.BeginCard();
            HubTheme.SectionHeader("2 · Thông tin");
            _name = EditorGUILayout.TextField("Tên", _name);
            string id = HubUtil.Slug(_name);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField("Id", id);

            _version = EditorGUILayout.TextField("Phiên bản (x.y.z)", _version);
            _desc = EditorGUILayout.TextField("Mô tả ngắn", _desc);
            GUILayout.Label("Ghi chú phiên bản", EditorStyles.miniLabel);
            _notes = EditorGUILayout.TextArea(_notes, GUILayout.MinHeight(40));

            _tagsText = EditorGUILayout.TextField("Tag (cách nhau dấu phẩy)", _tagsText);
            DrawTagSuggestions();

            _deps = EditorGUILayout.ToggleLeft("Kéo theo asset được tham chiếu (có thể lôi cả nửa project vào)", _deps);

            HubTheme.EndCard(HubTheme.Teal);

            // ---- đích ----
            HubTheme.BeginCard();
            HubTheme.SectionHeader("3 · Đẩy lên đâu");

            List<SourceConfig> dests = _configs.Where(c => _sources.ContainsKey(c.id) && _sources[c.id].CanPublish).ToList();
            if (dests.Count == 0)
            {
                EditorGUILayout.HelpBox("Chưa có nguồn nào để đẩy lên. Thêm ở tab 'Nguồn'.", MessageType.Info);
            }

            SourceConfig dest = null;
            PublishRequest req = null;
            string problem = null;

            if (dests.Count > 0)
            {
                _destIndex = Mathf.Clamp(_destIndex, 0, dests.Count - 1);
                _destIndex = EditorGUILayout.Popup("Nguồn", _destIndex, dests.Select(c => string.IsNullOrEmpty(c.name) ? c.kind : c.name).ToArray());
                dest = dests[_destIndex];

                req = new PublishRequest
                {
                    Id = id,
                    Title = _name.Trim(),
                    Version = _version.Trim(),
                    Description = _desc,
                    Notes = _notes,
                    Tags = HubUtil.ParseTags(_tagsText),
                    Paths = new List<string>(_paths),
                    IncludeDeps = _deps,
                };

                if (dest.kind == SourceConfig.GitHub) problem = DrawGitHubDest(dest, req, id);
                else
                {
                    _subFolder = EditorGUILayout.TextField("Thư mục con (tuỳ chọn)", _subFolder);
                    req.SubFolder = _subFolder;
                }
            }

            HubTheme.EndCard(HubTheme.Green);

            string basic = ValidateBasic(id);

            GUILayout.Space(6);
            if (basic != null || problem != null)
                EditorGUILayout.HelpBox(basic ?? problem, MessageType.Warning);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (Btn("Chỉ xuất file...", GUILayout.Width(120), GUILayout.Height(30)) && basic == null) ExportOnly(id);

            using (new EditorGUI.DisabledScope(basic != null || problem != null || dest == null))
            {
                if (Btn("XUẤT & ĐẨY LÊN", HubTheme.Green, GUILayout.Width(180), GUILayout.Height(34)) && req != null)
                    StartPublish(_sources[dest.id], req);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        private string DrawGitHubDest(SourceConfig dest, PublishRequest req, string id)
        {
            EditorGUILayout.BeginHorizontal();
            if (HubTheme.Segment("+ Tạo repo mới", _newRepo, HubTheme.Accent)) _newRepo = true;
            if (HubTheme.Segment("Dùng repo có sẵn", !_newRepo, HubTheme.Accent))
            {
                if (_newRepo) _autoKey = null;        // vừa chuyển sang: điền lại thông tin theo repo đang chọn
                _newRepo = false;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(4);

            req.NewRepo = _newRepo;

            if (_newRepo)
            {
                if (string.IsNullOrEmpty(_newRepoName)) _newRepoName = id;
                _newRepoName = EditorGUILayout.TextField("Tên repo", _newRepoName);
                _private = EditorGUILayout.Toggle("Private", _private);

                req.NewRepoName = HubUtil.Slug(_newRepoName);
                req.PrivateRepo = _private;

                if (req.NewRepoName.Length == 0) return "Nhập tên repo.";
                return null;
            }

            List<HubEntry> all = _entries
                .Where(x => x.sourceId == dest.id && x.canPush && !x.isArchived)
                .OrderBy(x => x.FullName)
                .ToList();

            if (all.Count == 0) return "Không có repo nào token này được phép ghi. Bấm 'Tải lại' ở tab Thư viện.";

            _repoFilter = EditorGUILayout.TextField("Tìm repo", _repoFilter ?? "");

            string needle = _repoFilter.Trim();
            List<HubEntry> list = needle.Length == 0
                ? all
                : all.Where(x => x.FullName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (list.Count == 0) return "Không có repo nào khớp '" + needle + "'.";

            int idx = list.FindIndex(x => x.key == _repoKey);
            if (idx < 0) idx = 0;

            idx = EditorGUILayout.Popup("Repo", idx, list.Select(x => (x.isPrivate ? "[private] " : "") + x.FullName).ToArray());
            HubEntry repo = list[idx];
            _repoKey = repo.key;

            if (_autoKey != repo.key) ApplyRepoDefaults(repo);

            req.Repo = repo;
            DrawRepoInfo(repo, req);

            foreach (Artifact a in repo.artifacts)
            {
                bool sameTag = !string.IsNullOrEmpty(a.tag) && a.tag == "v" + req.Version;
                bool sameVersion = !a.IsUpm && a.version == req.Version;
                if (sameTag || sameVersion)
                    return $"Repo này đã có phiên bản {req.Version}. Tăng số phiên bản rồi thử lại.";
            }

            return null;
        }

        private static string LatestVersion(HubEntry repo)
        {
            string best = "";
            foreach (Artifact a in repo.artifacts)
            {
                if (!HubUtil.IsValidVersion(a.version)) continue;
                if (best.Length == 0 || HubUtil.CompareVersions(a.version, best) > 0) best = a.version;
            }
            return best;
        }

        /// <summary>Tên gói suy từ file đã đẩy trước đó: "foo-1.2.3.unitypackage" -> "foo".</summary>
        private static string BaseNameOf(HubEntry repo)
        {
            foreach (Artifact a in repo.artifacts)
            {
                if (a.IsUpm || string.IsNullOrEmpty(a.label)) continue;

                string n = a.label;
                if (n.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - ".unitypackage".Length);
                n = System.Text.RegularExpressions.Regex.Replace(n, @"[-_.]?v?\d+\.\d+\.\d+$", "");
                if (n.Length > 0) return n;
            }
            return repo.name;
        }

        /// <summary>Điền các ô thông tin theo repo vừa chọn: tên, phiên bản kế tiếp, mô tả, tag.</summary>
        private void ApplyRepoDefaults(HubEntry repo)
        {
            string latest = LatestVersion(repo);

            _name = BaseNameOf(repo);
            _version = latest.Length == 0 ? "1.0.0" : HubUtil.NextPatch(latest);
            _desc = repo.description ?? "";
            _tagsText = HubUtil.JoinTags(TagsOf(repo));
            _autoKey = repo.key;
            GUI.FocusControl(null);
        }

        private void DrawRepoInfo(HubEntry repo, PublishRequest req)
        {
            EditorGUILayout.BeginVertical(HubTheme.PanelBox);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("<b>" + HubTheme.Esc(repo.FullName) + "</b>", HubTheme.Body, GUILayout.ExpandWidth(false));
            if (repo.isPrivate) HubTheme.Chip("PRIVATE", HubTheme.Amber);
            if (repo.isFork) HubTheme.Chip("FORK", HubTheme.Gray);
            GUILayout.FlexibleSpace();
            if (Soft("Điền lại từ repo", HubTheme.Teal, GUILayout.Width(130))) ApplyRepoDefaults(repo);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(repo.description))
                GUILayout.Label(HubTheme.Esc(repo.description), HubTheme.Sub);

            if (repo.topics.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                foreach (string t in repo.topics.Take(8)) HubTheme.Chip("#" + t, HubUtil.ColorFor(t));
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            if (!repo.scanned)
            {
                EditorGUILayout.BeginHorizontal();
                HubTheme.Chip("CHƯA QUÉT", HubTheme.Amber);
                GUILayout.Label("Chưa biết repo đã có phiên bản nào nên chưa gợi ý được số phiên bản và chưa báo trùng.", HubTheme.Sub);
                EditorGUILayout.EndHorizontal();

                if (Btn("Quét repo này", HubTheme.Amber, GUILayout.Width(120)))
                {
                    Source src = SourceOf(repo);
                    string key = repo.key;
                    if (src != null)
                        Begin("Đang quét " + repo.FullName + "...", src.Scan(repo), "Đã quét " + repo.FullName + ".",
                              () =>
                              {
                                  ScanCache.Save();
                                  _autoKey = null;      // có dữ liệu phiên bản rồi: điền lại
                              });
                }
            }
            else if (repo.artifacts.Count == 0)
            {
                GUILayout.Label("Repo chưa có package nào, đây sẽ là bản đầu tiên.", HubTheme.Sub);
            }
            else
            {
                GUILayout.Label("Đã có trong repo:", HubTheme.Meta);
                foreach (Artifact a in repo.artifacts.Take(6))
                {
                    EditorGUILayout.BeginHorizontal();
                    HubTheme.Chip(a.IsUpm ? "UPM" : a.Kind == ArtifactKind.PackageAsset ? "RELEASE" : ".UNITYPACKAGE",
                                  a.IsUpm ? HubTheme.Green : a.Kind == ArtifactKind.PackageAsset ? HubTheme.Purple : HubTheme.Accent);
                    GUILayout.Label(HubTheme.Esc(a.label), HubTheme.Mono, GUILayout.ExpandWidth(false));
                    if (!string.IsNullOrEmpty(a.version)) HubTheme.Chip("v" + a.version, HubTheme.Teal);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
            }

            string latest = LatestVersion(repo);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Mới nhất:", HubTheme.Meta, GUILayout.ExpandWidth(false));
            HubTheme.Chip(latest.Length == 0 ? "chưa có" : "v" + latest, HubTheme.Gray);
            GUILayout.Label("→  sẽ đẩy:", HubTheme.Meta, GUILayout.ExpandWidth(false));
            HubTheme.Chip("v" + req.Version, HubTheme.Green);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private string ValidateBasic(string id)
        {
            if (_paths.Count == 0) return "Chưa chọn asset nào.";
            if (id.Length == 0) return "Nhập tên package.";
            if (!HubUtil.IsValidVersion(_version.Trim())) return "Phiên bản phải dạng x.y.z, ví dụ 1.0.0.";
            return null;
        }

        private void DrawDropArea()
        {
            Rect area = GUILayoutUtility.GetRect(0, 46, GUILayout.ExpandWidth(true));
            GUI.Box(area, "Kéo asset / thư mục từ cửa sổ Project vào đây", EditorStyles.helpBox);

            Event ev = Event.current;
            if (!area.Contains(ev.mousePosition)) return;

            if (ev.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                ev.Use();
            }
            else if (ev.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (UnityEngine.Object o in DragAndDrop.objectReferences) AddPath(AssetDatabase.GetAssetPath(o));
                ev.Use();
            }
        }

        private void AddSelection()
        {
            foreach (string guid in Selection.assetGUIDs) AddPath(AssetDatabase.GUIDToAssetPath(guid));
        }

        private void AddPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets")) return;
            if (!_paths.Contains(path)) _paths.Add(path);
        }

        private void DrawTagSuggestions()
        {
            var known = new List<string>();
            foreach (KeyValuePair<string, int> kv in _facets) known.Add(kv.Key);
            if (known.Count == 0) return;

            List<string> current = HubUtil.ParseTags(_tagsText);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Gợi ý:", EditorStyles.miniLabel, GUILayout.Width(40));
            int drawn = 0;
            foreach (string t in known)
            {
                if (current.Contains(t)) continue;
                if (drawn++ >= 8) break;

                if (HubTheme.ChipButton("+" + t, HubUtil.ColorFor(t)))
                {
                    current.Add(t);
                    _tagsText = HubUtil.JoinTags(current);
                    GUI.FocusControl(null);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void ExportOnly(string id)
        {
            string file = EditorUtility.SaveFilePanel("Lưu .unitypackage", "", $"{id}-{_version.Trim()}.unitypackage", "unitypackage");
            if (string.IsNullOrEmpty(file)) return;

            try
            {
                PackageExporter.Export(_paths, file, _deps);
                _status = "Đã xuất " + file;
                _statusError = false;
                EditorUtility.RevealInFinder(file);
            }
            catch (Exception ex)
            {
                _status = ex.Message;
                _statusError = true;
            }
        }

        private void StartPublish(Source src, PublishRequest req)
        {
            Begin("Đang đóng gói...", PublishRoutine(src, req), null, () =>
            {
                TagStore.Set(req.ResultKey, req.Tags);
                Reload(req.ResultKey, req.ResultMessage);
            });
        }

        private static IEnumerator PublishRoutine(Source src, PublishRequest req)
        {
            string dir = Path.Combine(Path.GetTempPath(), "PackageHub", "pub-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string file = Path.Combine(dir, $"{req.Id}-{req.Version}.unitypackage");

            try
            {
                Job.Say("Đang đóng gói...");
                yield return null;               // để thanh trạng thái kịp hiện trước khi Unity bận xuất file

                PackageExporter.Export(req.Paths, file, req.IncludeDeps);
                req.ExportedFile = file;
                req.Size = new FileInfo(file).Length;

                yield return src.Publish(req);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { /* thư mục tạm */ }
            }
        }

        // =====================================================================
        //  Tab Nguồn
        // =====================================================================

        private void DrawSources()
        {
            _srcScroll = EditorGUILayout.BeginScrollView(_srcScroll);

            DrawSourcesGuide();

            EditorGUILayout.HelpBox(
                "GitHub: một token cho cả tài khoản, Package Hub tự liệt kê mọi repo bạn thấy được (cá nhân, private, tổ chức).\n" +
                "Thư mục: trỏ vào thư mục do Google Drive / Dropbox / OneDrive (app desktop) hoặc ổ mạng đồng bộ sẵn về máy.\n" +
                "Cấu hình lưu theo máy (EditorPrefs), không nằm trong project nên không bị commit. Token lưu dạng văn bản thường.",
                MessageType.None);

            for (int i = 0; i < _edit.Count; i++) DrawSourceRow(i);

            if (_pendingRemove >= 0 && _pendingRemove < _edit.Count)
            {
                _edit.RemoveAt(_pendingRemove);
            }
            _pendingRemove = -1;

            GUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            if (Soft("+ GitHub", HubTheme.Purple, GUILayout.Width(100)))
                _edit.Add(new SourceConfig { id = SourceStore.NewId(), kind = SourceConfig.GitHub, name = "GitHub" });
            if (Soft("+ Thư mục", HubTheme.Amber, GUILayout.Width(100)))
                _edit.Add(new SourceConfig { id = SourceStore.NewId(), kind = SourceConfig.Folder, name = "Thư mục" });
            GUILayout.FlexibleSpace();
            if (Btn("Lưu & tải lại", HubTheme.Green, GUILayout.Width(130), GUILayout.Height(28))) SaveSources();
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "Tạo token GitHub: Settings → Developer settings → Personal access tokens → Fine-grained tokens.\n" +
                "• Repository access: All repositories (để thấy hết repo)\n" +
                "• Chỉ xem & cài: Contents = Read-only\n" +
                "• Muốn đẩy lên / tạo repo / ghi topics: Contents = Read and write, Administration = Read and write\n" +
                "Token fine-grained thường không tạo được repo mới cho tài khoản cá nhân: muốn tạo repo từ cửa sổ này hãy dùng token classic scope 'repo'; hoặc tạo repo trên github.com rồi chọn 'Repo có sẵn'.",
                MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        private void DrawSourceRow(int index)
        {
            SourceConfig c = _edit[index];

            HubTheme.BeginCard();
            EditorGUILayout.BeginHorizontal();
            HubTheme.Chip(c.kind == SourceConfig.GitHub ? "GITHUB" : "THƯ MỤC", c.kind == SourceConfig.GitHub ? HubTheme.Purple : HubTheme.Amber);
            c.name = EditorGUILayout.TextField(c.name);
            if (Soft("Xoá", HubTheme.Red, GUILayout.Width(56))) _pendingRemove = index;
            EditorGUILayout.EndHorizontal();

            if (c.kind == SourceConfig.GitHub)
            {
                c.token = EditorGUILayout.PasswordField("Token", c.token ?? "");
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                c.path = EditorGUILayout.TextField("Thư mục", c.path ?? "");
                if (Soft("Chọn...", HubTheme.Accent, GUILayout.Width(70)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Chọn thư mục", c.path ?? "", "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        c.path = picked;
                        GUI.FocusControl(null);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (Btn("Kiểm tra", HubTheme.Accent, GUILayout.Width(80))) StartTest(c);
            if (_testMsg.TryGetValue(c.id, out string msg))
            {
                bool good = msg.StartsWith("OK");
                HubTheme.Chip(good ? "OK" : "LỖI", good ? HubTheme.Green : HubTheme.Red);
                GUILayout.Label(HubTheme.Esc(msg.Substring(msg.IndexOf(':') + 1).Trim()), HubTheme.Sub);
            }
            EditorGUILayout.EndHorizontal();

            HubTheme.EndCard(c.kind == SourceConfig.GitHub ? HubTheme.Purple : HubTheme.Amber);
        }

        private void StartTest(SourceConfig cfg)
        {
            SourceConfig copy = cfg.Clone();
            Source src = Source.Create(copy);
            if (src == null) return;

            string id = copy.id;
            Begin("Đang kiểm tra...", TestRoutine(src, id), null, null);
        }

        private IEnumerator TestRoutine(Source src, string id)
        {
            var box = new Box<string>();
            string failure = null;

            yield return Job.Guard(src.Test(box), ex => failure = ex.Message);

            _testMsg[id] = failure != null ? "LỖI: " + failure : "OK: " + box.Value;
        }

        private void SaveSources()
        {
            SourceStore.Save(_edit.Select(c => c.Clone()).ToList());
            _testMsg.Clear();
            _entries = new List<HubEntry>();
            _loaded = false;
            LoadConfigs();

            if (_configs.Count > 0)
            {
                _tab = 0;
                Reload(null);
            }
            else
            {
                _status = "Đã lưu. Chưa có nguồn nào.";
                _statusError = false;
            }
        }
    }
}
