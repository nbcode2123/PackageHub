using System;
using UnityEditor;
using UnityEngine;

namespace PackageHub
{
    /// <summary>
    /// Bảng màu + kiểu chữ dùng chung. Mỗi vai trò có một kiểu riêng để mắt phân biệt ngay:
    ///   Title   - tên repo / package (to, đậm)
    ///   Section - tiêu đề khối (nhỏ, đậm, màu nhấn, viết hoa)
    ///   Body    - nội dung chính
    ///   Meta    - thông tin phụ (ngày, dung lượng): nhỏ, màu nhạt
    ///   Mono    - phiên bản, đường dẫn, tên file: chữ đơn cách
    /// Màu mang nghĩa: xanh lá = UPM / thành công, xanh dương = .unitypackage / nhấn,
    /// hổ phách = private / cảnh báo, đỏ = lỗi / lưu trữ, tím = Release.
    /// </summary>
    internal static class HubTheme
    {
        private static bool Pro => EditorGUIUtility.isProSkin;

        private static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

        // ---- nền & chữ (đổi theo skin sáng / tối) ----
        public static Color Bg    => Pro ? C(0.14f, 0.15f, 0.17f) : C(0.84f, 0.85f, 0.88f);
        public static Color Card  => Pro ? C(0.20f, 0.21f, 0.24f) : C(0.97f, 0.97f, 0.98f);
        public static Color Panel => Pro ? C(0.11f, 0.12f, 0.14f) : C(0.78f, 0.79f, 0.83f);
        public static Color Text  => Pro ? C(0.90f, 0.91f, 0.93f) : C(0.10f, 0.11f, 0.14f);
        public static Color Muted => Pro ? C(0.58f, 0.62f, 0.68f) : C(0.36f, 0.39f, 0.45f);

        // ---- màu mang nghĩa ----
        public static readonly Color Accent = C(0.27f, 0.56f, 0.97f);
        public static readonly Color Green  = C(0.22f, 0.70f, 0.42f);
        public static readonly Color Amber  = C(0.95f, 0.66f, 0.15f);
        public static readonly Color Red    = C(0.89f, 0.30f, 0.30f);
        public static readonly Color Purple = C(0.60f, 0.43f, 0.92f);
        public static readonly Color Teal   = C(0.15f, 0.65f, 0.70f);
        public static readonly Color Gray   = C(0.50f, 0.53f, 0.58f);

        // ---- texture 1x1 ----
        private static Texture2D _white;

        private static Texture2D White
        {
            get
            {
                if (_white == null)
                {
                    _white = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                    _white.SetPixel(0, 0, Color.white);
                    _white.Apply();
                }
                return _white;
            }
        }

        private static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // ---- kiểu chữ: dựng lại khi texture mất (đổi skin, vào/ra play mode) ----
        private static Texture2D _styleTex;
        private static GUIStyle _btnText, _title, _section, _body, _sub, _meta, _mono, _big, _chip, _card, _panel, _row, _tab, _dir;

        private static void Ensure()
        {
            if (_styleTex != null && _title != null) return;

            _styleTex = White;

            Font mono = null;
            try
            {
                // Chỉ tạo font khi máy THẬT SỰ có: nếu không Unity ghi lỗi "Unable to find a font file" mỗi lần vẽ.
                string[] installed = Font.GetOSInstalledFontNames();
                foreach (string want in new[] { "Menlo", "Consolas", "DejaVu Sans Mono", "Courier New" })
                {
                    if (Array.IndexOf(installed, want) < 0) continue;
                    mono = Font.CreateDynamicFontFromOSFont(want, 11);
                    break;
                }
            }
            catch { /* không có font thì dùng mặc định */ }

            _title = new GUIStyle(EditorStyles.label)
            { fontSize = 15, fontStyle = FontStyle.Bold, richText = true, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft };
            _title.normal.textColor = Text;

            _section = new GUIStyle(EditorStyles.label) { fontSize = 11, fontStyle = FontStyle.Bold };
            _section.normal.textColor = Accent;

            _body = new GUIStyle(EditorStyles.label) { fontSize = 12, richText = true, wordWrap = true };
            _body.normal.textColor = Text;

            _sub = new GUIStyle(_body) { fontSize = 11 };
            _sub.normal.textColor = Muted;

            _meta = new GUIStyle(EditorStyles.label) { fontSize = 10 };
            _meta.normal.textColor = Muted;

            _mono = new GUIStyle(EditorStyles.label) { fontSize = 11, alignment = TextAnchor.MiddleLeft };
            if (mono != null) _mono.font = mono;
            _mono.normal.textColor = Text;

            _big = new GUIStyle(EditorStyles.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };

            _chip = new GUIStyle(EditorStyles.label)
            {
                fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(7, 7, 1, 2), margin = new RectOffset(2, 2, 3, 3), fixedHeight = 17,
            };
            _chip.normal.background = White;
            _chip.normal.textColor = Color.white;

            _card = new GUIStyle { padding = new RectOffset(14, 10, 8, 8), margin = new RectOffset(4, 4, 3, 3) };
            _card.normal.background = Solid(Card);

            _panel = new GUIStyle { padding = new RectOffset(8, 8, 6, 6), margin = new RectOffset(4, 4, 3, 3) };
            _panel.normal.background = Solid(Panel);

            _row = new GUIStyle { padding = new RectOffset(8, 6, 3, 3), margin = new RectOffset(0, 0, 2, 2) };
            _row.normal.background = Solid(Panel);

            _tab = new GUIStyle(EditorStyles.label)
            { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _tab.normal.textColor = Muted;
            _tab.hover.textColor = Text;

            _btnText = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(12, 12, 5, 6), margin = new RectOffset(2, 2, 2, 2), wordWrap = false,
            };

            _dir = new GUIStyle(EditorStyles.label) { fontSize = 12, fontStyle = FontStyle.Bold };
            _dir.normal.textColor = Amber;
        }

        public static GUIStyle Title   { get { Ensure(); return _title; } }
        public static GUIStyle Section { get { Ensure(); return _section; } }
        public static GUIStyle Body    { get { Ensure(); return _body; } }
        public static GUIStyle Sub     { get { Ensure(); return _sub; } }
        public static GUIStyle Meta    { get { Ensure(); return _meta; } }
        public static GUIStyle Mono    { get { Ensure(); return _mono; } }
        public static GUIStyle Big     { get { Ensure(); return _big; } }
        public static GUIStyle CardBox { get { Ensure(); return _card; } }
        public static GUIStyle PanelBox{ get { Ensure(); return _panel; } }
        public static GUIStyle RowBox  { get { Ensure(); return _row; } }
        public static GUIStyle TabText { get { Ensure(); return _tab; } }
        public static GUIStyle DirText { get { Ensure(); return _dir; } }

        // ------------------------------------------------------------------

        public static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("<", "<​");

        public static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        public static bool IsLight(Color c) => (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) > 0.68f;

        /// <summary>Nhãn nhỏ nền màu; chữ tự đổi đen / trắng cho dễ đọc.</summary>
        public static void Chip(string text, Color bg)
        {
            Ensure();
            Color oldBg = GUI.backgroundColor, oldFg = GUI.contentColor;
            GUI.backgroundColor = bg;
            GUI.contentColor = IsLight(bg) ? new Color(0.1f, 0.1f, 0.12f) : Color.white;
            GUILayout.Label(text, _chip, GUILayout.ExpandWidth(false));
            GUI.backgroundColor = oldBg;
            GUI.contentColor = oldFg;
        }

        /// <summary>Chip bấm được (dùng cho tag).</summary>
        public static bool ChipButton(string text, Color bg)
        {
            Ensure();
            Color oldBg = GUI.backgroundColor, oldFg = GUI.contentColor;
            GUI.backgroundColor = bg;
            GUI.contentColor = IsLight(bg) ? new Color(0.1f, 0.1f, 0.12f) : Color.white;
            bool hit = GUILayout.Button(text, _chip, GUILayout.ExpandWidth(false));
            GUI.backgroundColor = oldBg;
            GUI.contentColor = oldFg;
            return hit;
        }

        public static void BeginCard()
        {
            Ensure();
            EditorGUILayout.BeginVertical(_card);
        }

        /// <summary>Đóng thẻ và vẽ vạch màu bên trái (màu cho biết loại).</summary>
        public static void EndCard(Color accent)
        {
            EditorGUILayout.EndVertical();
            if (Event.current.type != EventType.Repaint) return;

            Rect r = GUILayoutUtility.GetLastRect();
            EditorGUI.DrawRect(new Rect(r.x, r.y, 4, r.height), accent);
        }

        /// <summary>Tiêu đề khối: chữ hoa màu nhấn + đường kẻ mảnh.</summary>
        public static void SectionHeader(string title)
        {
            Ensure();
            GUILayout.Label(title.ToUpperInvariant(), _section);
            Rect line = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(line, new Color(Accent.r, Accent.g, Accent.b, 0.45f));
            GUILayout.Space(4);
        }

        // ------------------------------------------------------------ nút ---

        public enum Look { Solid, Soft }

        private static void Round(Rect r, Color c, float radius)
        {
            GUI.DrawTexture(r, White, ScaleMode.StretchToFill, true, 0f, c, Vector4.zero, new Vector4(radius, radius, radius, radius));
        }

        /// <summary>
        /// Nút tự vẽ: bo góc, có hiệu ứng rê chuột / nhấn / vô hiệu.
        /// Solid = nền đặc (hành động chính); Soft = nền nhạt có viền (hành động phụ).
        /// </summary>
        public static bool Button(string text, Color color, Look look, bool enabled, params GUILayoutOption[] options)
        {
            Ensure();
            enabled = enabled && GUI.enabled;         // tôn trọng EditorGUI.DisabledScope bên ngoài

            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), _btnText, options);
            int id = GUIUtility.GetControlID(FocusType.Passive);
            Event ev = Event.current;
            bool clicked = false;

            switch (ev.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (enabled && ev.button == 0 && r.Contains(ev.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        ev.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        ev.Use();
                        clicked = r.Contains(ev.mousePosition);
                    }
                    break;

                case EventType.Repaint:
                    DrawButton(r, text, color, look, enabled,
                               enabled && r.Contains(ev.mousePosition),
                               enabled && GUIUtility.hotControl == id);
                    break;
            }

            return clicked;
        }

        private static void DrawButton(Rect r, string text, Color color, Look look, bool enabled, bool hover, bool down)
        {
            var label = new GUIStyle(_btnText);
            Color textColor;

            if (look == Look.Solid)
            {
                Color fill = enabled ? color : new Color(Gray.r, Gray.g, Gray.b, 0.45f);
                if (hover) fill = Color.Lerp(fill, Color.white, 0.18f);
                if (down) fill = Color.Lerp(fill, Color.black, 0.22f);

                if (!down) Round(new Rect(r.x, r.y + 1, r.width, r.height - 1), Color.Lerp(fill, Color.black, 0.35f), 6);
                Round(new Rect(r.x, down ? r.y + 1 : r.y, r.width, r.height - 1), fill, 6);

                textColor = IsLight(fill) ? new Color(0.08f, 0.09f, 0.11f) : Color.white;
            }
            else
            {
                Color edge = enabled ? new Color(color.r, color.g, color.b, hover ? 1f : 0.7f) : new Color(Gray.r, Gray.g, Gray.b, 0.4f);
                Color inner = Color.Lerp(Card, color, enabled ? (down ? 0.38f : hover ? 0.28f : 0.14f) : 0.05f);

                Round(r, edge, 6);
                Round(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), inner, 5);

                textColor = Pro ? Color.Lerp(color, Color.white, 0.4f) : Color.Lerp(color, Color.black, 0.3f);
            }

            if (!enabled) textColor.a = 0.55f;
            label.normal.textColor = textColor;
            label.hover.textColor = textColor;
            label.Draw(new Rect(r.x, r.y + (down ? 1 : 0), r.width, r.height), text, false, false, false, false);
        }

        /// <summary>Nút chọn một trong nhiều (dạng phân đoạn): mục đang chọn đặc, còn lại nhạt.</summary>
        public static bool Segment(string text, bool selected, Color color)
        {
            return Button(text, color, selected ? Look.Solid : Look.Soft, true, GUILayout.MinWidth(120), GUILayout.Height(26));
        }

        /// <summary>Số thứ tự tròn lớn cho các bước hướng dẫn.</summary>
        public static void StepNumber(int n, Color color)
        {
            Ensure();
            Rect r = GUILayoutUtility.GetRect(26, 26, GUILayout.Width(26), GUILayout.Height(26));
            if (Event.current.type != EventType.Repaint) return;

            Round(r, color, 13);
            var st = new GUIStyle(_big) { fontSize = 14 };
            st.normal.textColor = IsLight(color) ? new Color(0.08f, 0.09f, 0.11f) : Color.white;
            st.Draw(r, n.ToString(), false, false, false, false);
        }

        /// <summary>Từ khoá nổi bật trong đoạn hướng dẫn (cần richText).</summary>
        public static string Key(string text) => "<b><color=#" + Hex(Pro ? Color.Lerp(Amber, Color.white, 0.15f) : new Color(0.72f, 0.38f, 0f)) + ">" + text + "</color></b>";

        public static void Tile(string label, int value, Color color)
        {
            Ensure();
            EditorGUILayout.BeginVertical(_card, GUILayout.MinWidth(80));
            var big = new GUIStyle(_big);
            big.normal.textColor = color;
            GUILayout.Label(value.ToString(), big);
            GUILayout.Label(label, _meta);
            EditorGUILayout.EndVertical();

            if (Event.current.type == EventType.Repaint)
            {
                Rect r = GUILayoutUtility.GetLastRect();
                EditorGUI.DrawRect(new Rect(r.x, r.yMax - 3, r.width, 3), color);
            }
        }
    }
}
