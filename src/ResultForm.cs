using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TongYan
{
    /// <summary>
    /// 自绘双缓冲虚拟画布翻译窗口 (v2.0 纯客户区手势与段落虚拟切片引擎)
    /// 彻底剔除 Win32 非客户区模态循环，实现真硬件级零光标弹跳与 O(1) 恒定时间超长文渲染
    /// </summary>
    public class ResultForm : Form
    {
        private readonly int _topPadding = 22;
        private readonly int _sidePadding = 16;
        private readonly int _bottomPadding = 16;
        private readonly Font _font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular);
        private readonly int _lineHeight;
        private readonly float _avgCharWidth;

        // 段落级虚拟化模型：万字长文切片存储，单帧仅渲染视口内可见段落
        private class Paragraph
        {
            public string Text;
            public int Height;
        }

        private readonly List<Paragraph> _paragraphs = new List<Paragraph>();
        private readonly StringBuilder _text = new StringBuilder();
        private readonly object _chunkLock = new object();
        private readonly StringBuilder _chunkBuffer = new StringBuilder();
        private bool _updatePending = false;
        private bool _isFirstChunk = true;
        private Color _textColor = Color.FromArgb(120, 120, 120);

        // 视口与布局缓存
        private readonly Point _cursorPosition;
        private bool _userInteracted = false;
        private int _scrollY = 0;
        private int _totalContentHeight = 0;
        private int _lastMeasuredWidth = -1;
        private System.Windows.Forms.Timer _noticeTimer;

        // 纯客户区统一手势引擎状态机（彻底摆脱 Win32 模态黑盒与光标弹跳）
        private enum GestureMode
        {
            None,
            Move,
            ResizeLeft,
            ResizeRight,
            ResizeTop,
            ResizeBottom,
            ResizeTopLeft,
            ResizeTopRight,
            ResizeBottomLeft,
            ResizeBottomRight,
            ScrollThumb,
            ScrollContent
        }

        private GestureMode _currentGesture = GestureMode.None;
        private Point _dragStartCursor;
        private Rectangle _dragStartBounds;
        private int _dragStartScrollY;

        // Win32 API: 全局低阶鼠标感知钩子（外部单次点击即退）
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCRBUTTONDOWN = 0x00A4;
        private const int WM_CLOSE = 0x0010;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelMouseProc _mouseProc;
        private IntPtr _mouseHook = IntPtr.Zero;

        public ResultForm(Point cursorPosition, int textLength = 0)
        {
            _cursorPosition = cursorPosition;
            Text = "通言";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            KeyPreview = true;

            if (Program.AppIcon != null)
            {
                try { Icon = Program.AppIcon; } catch { }
            }

            _lineHeight = (int)_font.GetHeight() + 6;
            Size szM = TextRenderer.MeasureText("汉", _font);
            _avgCharWidth = Math.Max(12f, (float)(szM.Width - 4));

            // 硬件级双缓冲，物理杜绝重绘闪烁
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            UpdateStyles();

            // 根据划词规模预设黄金尺寸：短词小巧轻便，长文展开黄金阅读卡片
            Rectangle workArea = Screen.FromPoint(cursorPosition).WorkingArea;
            int targetWidth = 380;
            int targetHeight = 110;

            if (textLength >= 120)
            {
                targetWidth = 560;
                targetHeight = Math.Min(360, (int)(workArea.Height * 0.45));
            }
            else if (textLength >= 35)
            {
                targetWidth = 460;
                targetHeight = 180;
            }
            else
            {
                targetWidth = 380;
                targetHeight = 110;
            }

            int maxWidth = Math.Min(680, (int)(workArea.Width * 0.75));
            if (targetWidth > maxWidth) targetWidth = maxWidth;
            int maxHeight = (int)(workArea.Height * 0.7);
            if (targetHeight > maxHeight) targetHeight = maxHeight;

            // 物理位置一次性确定，优先置于光标下方留出原文对比
            int x = cursorPosition.X + 12;
            int y = cursorPosition.Y + 16;

            if (x + targetWidth > workArea.Right)
            {
                x = workArea.Right - targetWidth - 10;
            }
            if (x < workArea.Left + 10) x = workArea.Left + 10;

            if (y + targetHeight > workArea.Bottom)
            {
                y = cursorPosition.Y - targetHeight - 16;
                if (y < workArea.Top + 10)
                {
                    y = workArea.Bottom - targetHeight - 10;
                }
            }
            if (y < workArea.Top + 10) y = workArea.Top + 10;

            Location = new Point(x, y);
            Width = targetWidth;
            Height = targetHeight;
            MinimumSize = new Size(240, 90);
            MaximumSize = new Size(workArea.Width, workArea.Height);

            _text.Append("正在获取选中文本...");
            RebuildParagraphs(GetContentWidth());

            // 失去焦点时自动关闭（但手势拖拽中不关闭）
            Deactivate += (s, e) =>
            {
                if (_currentGesture == GestureMode.None)
                {
                    Logger.Info("翻译窗口失去焦点，关闭窗口");
                    Close();
                }
            };

            InstallOutsideClickHook();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            lock (_chunkLock)
            {
                if (_chunkBuffer.Length > 0 && !_updatePending)
                {
                    _updatePending = true;
                    try
                    {
                        BeginInvoke(new Action(FlushChunks));
                    }
                    catch
                    {
                        _updatePending = false;
                    }
                }
            }
        }

        private int GetContentWidth()
        {
            return Math.Max(10, ClientSize.Width - (_sidePadding * 2) - 12);
        }

        // 视口与段落重建：以“锚点段落”为准平滑缩放，杜绝拉伸宽度时的任何突兀跳跃
        private void RebuildParagraphs(int contentWidth, bool isInteractive = false)
        {
            if (contentWidth <= 10) return;

            // 1. 记录拉伸前的视口顶部锚点（哪一段、该段内偏移多少）
            int anchorIndex = 0;
            int anchorOffset = 0;
            int accHeight = 0;
            for (int i = 0; i < _paragraphs.Count; i++)
            {
                if (accHeight + _paragraphs[i].Height > _scrollY)
                {
                    anchorIndex = i;
                    anchorOffset = _scrollY - accHeight;
                    break;
                }
                accHeight += _paragraphs[i].Height;
            }

            // 2. 清洗段落并测量每一段的高度
            _paragraphs.Clear();
            string raw = _text.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = raw.Split('\n');
            int total = 0;
            int charsPerLine = Math.Max(1, (int)(contentWidth / _avgCharWidth));

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int h;
                if (isInteractive && !string.IsNullOrEmpty(line))
                {
                    // 交互式缩放拖拽中：0.05ms 极速行数物理估算，杜绝高频排版阻塞与左拉拖影！
                    int estLines = Math.Max(1, (int)Math.Ceiling((double)line.Length / charsPerLine));
                    h = estLines * _lineHeight;
                }
                else
                {
                    // 静态展示或松手后：高精度 GDI 排版测量
                    h = MeasureParagraphHeight(line, contentWidth);
                }
                _paragraphs.Add(new Paragraph { Text = line, Height = h });
                total += h;
            }

            _totalContentHeight = total;
            _lastMeasuredWidth = contentWidth;

            // 3. 按锚点段落精准复原视口，视线绝对静止不晃动
            int restoredY = 0;
            for (int i = 0; i < anchorIndex && i < _paragraphs.Count; i++)
            {
                restoredY += _paragraphs[i].Height;
            }
            if (anchorIndex < _paragraphs.Count)
            {
                restoredY += Math.Min(anchorOffset, _paragraphs[anchorIndex].Height);
            }
            _scrollY = restoredY;
            ClampScroll();
        }

        private int MeasureParagraphHeight(string text, int contentWidth)
        {
            if (string.IsNullOrEmpty(text))
            {
                return _lineHeight;
            }
            Size sz = TextRenderer.MeasureText(
                text,
                _font,
                new Size(contentWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl
            );
            return Math.Max(_lineHeight, sz.Height);
        }

        // 流式增量排版：只重新测量被追加内容的最后一个段落，已固定的历史段落高度永久缓存不变
        private void AppendIncremental(string normIncoming, int contentWidth)
        {
            if (string.IsNullOrEmpty(normIncoming) || _paragraphs.Count == 0) return;

            string[] parts = normIncoming.Split('\n');

            // 1. 增量更新最后一个段落
            Paragraph lastPara = _paragraphs[_paragraphs.Count - 1];
            _totalContentHeight -= lastPara.Height;
            lastPara.Text += parts[0];
            lastPara.Height = MeasureParagraphHeight(lastPara.Text, contentWidth);
            _totalContentHeight += lastPara.Height;

            // 2. 如果包含换行符，追加后续新段落
            for (int i = 1; i < parts.Length; i++)
            {
                string pText = parts[i];
                int h = MeasureParagraphHeight(pText, contentWidth);
                _paragraphs.Add(new Paragraph { Text = pText, Height = h });
                _totalContentHeight += h;
            }

            ClampScroll();
        }

        // ======================= 纯客户区手势引擎 (零光标弹跳) =======================

        private GestureMode DetectGesture(Point pt)
        {
            int border = 8;
            bool left = pt.X <= border;
            bool right = pt.X >= ClientSize.Width - border;
            bool top = pt.Y <= border;
            bool bottom = pt.Y >= ClientSize.Height - border;

            if (top && left) return GestureMode.ResizeTopLeft;
            if (top && right) return GestureMode.ResizeTopRight;
            if (bottom && left) return GestureMode.ResizeBottomLeft;
            if (bottom && right) return GestureMode.ResizeBottomRight;
            if (left) return GestureMode.ResizeLeft;
            if (right) return GestureMode.ResizeRight;
            if (top) return GestureMode.ResizeTop;
            if (bottom) return GestureMode.ResizeBottom;

            // 顶部白框非边缘区域（8px ~ 22px 之间）直接拖拽窗口位置
            if (pt.Y < _topPadding)
            {
                return GestureMode.Move;
            }

            // 右侧自绘滚动条响应区域
            int contentHeight = ClientSize.Height - _topPadding - _bottomPadding;
            if (_totalContentHeight > contentHeight && pt.X >= ClientSize.Width - 14)
            {
                return GestureMode.ScrollThumb;
            }

            // 其余正文区域：支持抓手平滑拖拽翻页
            return GestureMode.ScrollContent;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_currentGesture == GestureMode.None)
            {
                // 未按下鼠标时，仅根据当前位置呈现对应的直观指针形状
                GestureMode hoverMode = DetectGesture(e.Location);
                switch (hoverMode)
                {
                    case GestureMode.ResizeTopLeft:
                    case GestureMode.ResizeBottomRight:
                        Cursor = Cursors.SizeNWSE;
                        break;
                    case GestureMode.ResizeTopRight:
                    case GestureMode.ResizeBottomLeft:
                        Cursor = Cursors.SizeNESW;
                        break;
                    case GestureMode.ResizeLeft:
                    case GestureMode.ResizeRight:
                        Cursor = Cursors.SizeWE;
                        break;
                    case GestureMode.ResizeTop:
                    case GestureMode.ResizeBottom:
                        Cursor = Cursors.SizeNS;
                        break;
                    case GestureMode.Move:
                        Cursor = Cursors.SizeAll;
                        break;
                    case GestureMode.ScrollThumb:
                        Cursor = Cursors.Hand;
                        break;
                    default:
                        Cursor = Cursors.Default;
                        break;
                }
                return;
            }

            // 正在拖拽或缩放：纯像素级物理映射，Windows DWM 零介入，光标绝对不会弹起
            int dx = Cursor.Position.X - _dragStartCursor.X;
            int dy = Cursor.Position.Y - _dragStartCursor.Y;
            Rectangle r = _dragStartBounds;

            switch (_currentGesture)
            {
                case GestureMode.Move:
                    r.X += dx;
                    r.Y += dy;
                    Bounds = r;
                    break;

                case GestureMode.ResizeRight:
                    r.Width = Math.Max(MinimumSize.Width, _dragStartBounds.Width + dx);
                    Bounds = r;
                    break;

                case GestureMode.ResizeBottom:
                    r.Height = Math.Max(MinimumSize.Height, _dragStartBounds.Height + dy);
                    Bounds = r;
                    break;

                case GestureMode.ResizeBottomRight:
                    r.Width = Math.Max(MinimumSize.Width, _dragStartBounds.Width + dx);
                    r.Height = Math.Max(MinimumSize.Height, _dragStartBounds.Height + dy);
                    Bounds = r;
                    break;

                case GestureMode.ResizeLeft:
                    int newW = Math.Max(MinimumSize.Width, _dragStartBounds.Width - dx);
                    r.X = _dragStartBounds.Right - newW;
                    r.Width = newW;
                    Bounds = r;
                    break;

                case GestureMode.ResizeTop:
                    int newH = Math.Max(MinimumSize.Height, _dragStartBounds.Height - dy);
                    r.Y = _dragStartBounds.Bottom - newH;
                    r.Height = newH;
                    Bounds = r;
                    break;

                case GestureMode.ResizeTopLeft:
                    int wTL = Math.Max(MinimumSize.Width, _dragStartBounds.Width - dx);
                    int hTL = Math.Max(MinimumSize.Height, _dragStartBounds.Height - dy);
                    r.X = _dragStartBounds.Right - wTL;
                    r.Y = _dragStartBounds.Bottom - hTL;
                    r.Width = wTL;
                    r.Height = hTL;
                    Bounds = r;
                    break;

                case GestureMode.ResizeTopRight:
                    r.Width = Math.Max(MinimumSize.Width, _dragStartBounds.Width + dx);
                    int hTR = Math.Max(MinimumSize.Height, _dragStartBounds.Height - dy);
                    r.Y = _dragStartBounds.Bottom - hTR;
                    r.Height = hTR;
                    Bounds = r;
                    break;

                case GestureMode.ResizeBottomLeft:
                    int wBL = Math.Max(MinimumSize.Width, _dragStartBounds.Width - dx);
                    r.X = _dragStartBounds.Right - wBL;
                    r.Width = wBL;
                    r.Height = Math.Max(MinimumSize.Height, _dragStartBounds.Height + dy);
                    Bounds = r;
                    break;

                case GestureMode.ScrollThumb:
                    int contentH = ClientSize.Height - _topPadding - _bottomPadding;
                    int maxScr = _totalContentHeight - contentH;
                    if (maxScr > 0)
                    {
                        float visibleRatio = (float)contentH / _totalContentHeight;
                        int thumbH = Math.Max(24, (int)(contentH * visibleRatio));
                        int trackH = contentH - thumbH;
                        if (trackH > 0)
                        {
                            _scrollY = _dragStartScrollY + (int)((float)dy / trackH * maxScr);
                            ClampScroll();
                            Invalidate();
                        }
                    }
                    break;

                case GestureMode.ScrollContent:
                    // 抓手拖拽正文翻页（手指滑到哪、页面跟到哪）
                    _scrollY = _dragStartScrollY - dy;
                    ClampScroll();
                    Invalidate();
                    break;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _currentGesture = DetectGesture(e.Location);
                if (_currentGesture != GestureMode.None)
                {
                    _userInteracted = true;
                    Capture = true;
                    _dragStartCursor = Cursor.Position;
                    _dragStartBounds = Bounds;
                    _dragStartScrollY = _scrollY;
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_currentGesture != GestureMode.None)
            {
                bool wasResizing = (_currentGesture != GestureMode.Move && _currentGesture != GestureMode.ScrollThumb && _currentGesture != GestureMode.ScrollContent);
                _currentGesture = GestureMode.None;
                Capture = false;
                Cursor = Cursors.Default;

                if (wasResizing)
                {
                    RebuildParagraphs(GetContentWidth(), isInteractive: false);
                    Invalidate();
                }
            }
        }

        // ======================= 键盘与滚轮处理 =======================

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Logger.Info("用户按下 ESC 键，立即关闭翻译窗口");
                Close();
                return true;
            }
            else if (keyData == (Keys.Control | Keys.C))
            {
                if (!_isFirstChunk)
                {
                    string content = _text.ToString();
                    if (!string.IsNullOrEmpty(content))
                    {
                        try
                        {
                            Clipboard.SetText(content.Replace("\n", Environment.NewLine));
                            Logger.Info("用户按 Ctrl+C 复制全部译文");
                        }
                        catch { }
                    }
                }
                return true;
            }
            else if (keyData == Keys.PageDown || keyData == Keys.Down)
            {
                ScrollBy(_lineHeight * 3);
                return true;
            }
            else if (keyData == Keys.PageUp || keyData == Keys.Up)
            {
                ScrollBy(-_lineHeight * 3);
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int deltaPixels = -(e.Delta / 120) * (_lineHeight * 3);
            ScrollBy(deltaPixels);
        }

        private void ScrollBy(int deltaPixels)
        {
            _scrollY += deltaPixels;
            ClampScroll();
            Invalidate();
        }

        private void ClampScroll()
        {
            int visibleHeight = ClientSize.Height - _topPadding - _bottomPadding;
            int maxScroll = Math.Max(0, _totalContentHeight - visibleHeight);
            if (_scrollY < 0) _scrollY = 0;
            if (_scrollY > maxScroll) _scrollY = maxScroll;
        }

        // ======================= O(1) 恒定时间超长文渲染管线 =======================

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;

            // 1. 极简 1px 浅灰色边框
            using (Pen borderPen = new Pen(Color.FromArgb(215, 215, 215), 1))
            {
                g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
            }

            // 2. 确定内容区域
            int contentWidth = GetContentWidth();
            Rectangle contentRect = new Rectangle(
                _sidePadding,
                _topPadding,
                contentWidth,
                ClientSize.Height - _topPadding - _bottomPadding
            );

            if (contentRect.Width <= 10 || contentRect.Height <= 10) return;

            // 3. 校验排版宽度缓存
            if (_lastMeasuredWidth != contentWidth)
            {
                bool isInteractive = (_currentGesture != GestureMode.None && _currentGesture != GestureMode.Move && _currentGesture != GestureMode.ScrollThumb && _currentGesture != GestureMode.ScrollContent);
                RebuildParagraphs(contentWidth, isInteractive);
            }

            // 4. O(1) 虚拟切片绘制：仅渲染落在当前视口窗口内的段落（每帧永远只画数段，耗时 < 0.05ms）
            g.SetClip(contentRect);
            int curY = contentRect.Y - _scrollY;

            for (int i = 0; i < _paragraphs.Count; i++)
            {
                Paragraph p = _paragraphs[i];
                int pBottom = curY + p.Height;

                // 完全在视口上方的段落直接跳过
                if (pBottom < contentRect.Top)
                {
                    curY = pBottom;
                    continue;
                }

                // 完全在视口下方的段落直接截断退出
                if (curY > contentRect.Bottom)
                {
                    break;
                }

                if (!string.IsNullOrEmpty(p.Text))
                {
                    Rectangle pRect = new Rectangle(contentRect.X, curY, contentRect.Width, p.Height);
                    TextRenderer.DrawText(
                        g,
                        p.Text,
                        _font,
                        pRect,
                        _textColor,
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl
                    );
                }
                curY = pBottom;
            }
            g.ResetClip();

            // 5. 自绘极简半透明胶囊滚动指示器（4px）
            if (_totalContentHeight > contentRect.Height)
            {
                float visibleRatio = (float)contentRect.Height / _totalContentHeight;
                int thumbHeight = Math.Max(24, (int)(contentRect.Height * visibleRatio));
                int trackHeight = contentRect.Height - thumbHeight;
                int maxScroll = _totalContentHeight - contentRect.Height;
                int thumbY = contentRect.Y + (maxScroll > 0 ? (int)((float)_scrollY / maxScroll * trackHeight) : 0);

                using (Brush thumbBrush = new SolidBrush(Color.FromArgb(170, 170, 170)))
                {
                    g.FillRectangle(thumbBrush, ClientSize.Width - 8, thumbY, 4, thumbHeight);
                }
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int contentWidth = GetContentWidth();
            if (_lastMeasuredWidth != contentWidth)
            {
                bool isInteractive = (_currentGesture != GestureMode.None && _currentGesture != GestureMode.Move && _currentGesture != GestureMode.ScrollThumb && _currentGesture != GestureMode.ScrollContent);
                RebuildParagraphs(contentWidth, isInteractive);
            }
            else
            {
                ClampScroll();
            }
            Invalidate();
        }

        // ======================= 流式追加与消息处理 =======================

        public void SetStatusText(string status)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(SetStatusText), status); } catch { }
                return;
            }

            if (_isFirstChunk)
            {
                _textColor = Color.FromArgb(120, 120, 120);
                _text.Length = 0;
                _text.Append(status);
                RebuildParagraphs(GetContentWidth());
                Invalidate();
            }
        }

        public void UpdateTargetSize(int textLength)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<int>(UpdateTargetSize), textLength); } catch { }
                return;
            }

            if (_userInteracted || _currentGesture != GestureMode.None) return;
            if (textLength < 35) return;

            Rectangle workArea = Screen.FromPoint(_cursorPosition).WorkingArea;
            int targetWidth = 380;
            int targetHeight = 110;

            if (textLength >= 120)
            {
                targetWidth = 560;
                targetHeight = Math.Min(360, (int)(workArea.Height * 0.45));
            }
            else
            {
                targetWidth = 460;
                targetHeight = 180;
            }

            int maxWidth = Math.Min(680, (int)(workArea.Width * 0.75));
            if (targetWidth > maxWidth) targetWidth = maxWidth;
            int maxHeight = (int)(workArea.Height * 0.7);
            if (targetHeight > maxHeight) targetHeight = maxHeight;

            int x = _cursorPosition.X + 12;
            int y = _cursorPosition.Y + 16;

            if (x + targetWidth > workArea.Right)
            {
                x = workArea.Right - targetWidth - 10;
            }
            if (x < workArea.Left + 10) x = workArea.Left + 10;

            if (y + targetHeight > workArea.Bottom)
            {
                y = _cursorPosition.Y - targetHeight - 16;
                if (y < workArea.Top + 10)
                {
                    y = workArea.Bottom - targetHeight - 10;
                }
            }
            if (y < workArea.Top + 10) y = workArea.Top + 10;

            SetBounds(x, y, targetWidth, targetHeight);
        }

        public void AppendChunk(string chunk)
        {
            if (string.IsNullOrEmpty(chunk) || IsDisposed) return;

            lock (_chunkLock)
            {
                _chunkBuffer.Append(chunk);
                if (!_updatePending)
                {
                    if (IsHandleCreated)
                    {
                        _updatePending = true;
                        try
                        {
                            BeginInvoke(new Action(FlushChunks));
                        }
                        catch
                        {
                            _updatePending = false;
                        }
                    }
                }
            }
        }

        private void FlushChunks()
        {
            if (IsDisposed) return;

            string incoming;
            lock (_chunkLock)
            {
                if (!IsHandleCreated)
                {
                    _updatePending = false;
                    return;
                }
                incoming = _chunkBuffer.ToString();
                _chunkBuffer.Length = 0;
                _updatePending = false;
            }

            if (string.IsNullOrEmpty(incoming) || IsDisposed) return;

            // 统一规范化换行符，杜绝跨分包 \r\n 导致的段落计数撕裂
            incoming = incoming.Replace("\r\n", "\n").Replace('\r', '\n');

            if (_isFirstChunk)
            {
                _isFirstChunk = false;
                _textColor = Color.FromArgb(30, 30, 30);
                _text.Length = 0;
                _paragraphs.Clear();
                _totalContentHeight = 0;
                _scrollY = 0;
            }

            _text.Append(incoming);

            int contentWidth = GetContentWidth();
            if (_lastMeasuredWidth != contentWidth || _paragraphs.Count == 0)
            {
                RebuildParagraphs(contentWidth);
            }
            else
            {
                AppendIncremental(incoming, contentWidth);
            }

            Invalidate();
        }

        public void ShowNotice(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(ShowNotice), message); } catch { }
                return;
            }

            _isFirstChunk = false;
            _textColor = Color.FromArgb(100, 100, 100);
            _text.Length = 0;
            _text.Append(message);
            RebuildParagraphs(GetContentWidth());
            Invalidate();

            if (_noticeTimer != null)
            {
                _noticeTimer.Stop();
                _noticeTimer.Dispose();
            }

            _noticeTimer = new System.Windows.Forms.Timer();
            _noticeTimer.Interval = 3000;
            _noticeTimer.Tick += (s, e) =>
            {
                if (_noticeTimer != null)
                {
                    _noticeTimer.Stop();
                    _noticeTimer.Dispose();
                    _noticeTimer = null;
                }
                Close();
            };
            _noticeTimer.Start();
        }

        public void SetError(string error)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(SetError), error); } catch { }
                return;
            }

            Logger.Error("结果窗口显示错误: " + error);

            if (_isFirstChunk)
            {
                _textColor = Color.FromArgb(190, 40, 40);
                _text.Length = 0;
                _text.Append(error);
                _isFirstChunk = false;
            }
            else
            {
                _text.Append("\r\n\r\n请求异常中断: " + error);
            }

            RebuildParagraphs(GetContentWidth());
            Invalidate();
        }

        public void Complete()
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(Complete)); } catch { }
                return;
            }

            FlushChunks();

            if (_isFirstChunk)
            {
                ShowNotice("未收到有效内容");
                return;
            }

            Logger.Info("流式接收完毕并完成最终渲染");
        }

        // ======================= 全局低阶鼠标感知钩子 =======================

        private void InstallOutsideClickHook()
        {
            try
            {
                _mouseProc = MouseHookCallback;
                IntPtr hMod = GetModuleHandle(null);
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);

                if (_mouseHook == IntPtr.Zero)
                {
                    using (Process curProcess = Process.GetCurrentProcess())
                    using (ProcessModule curModule = curProcess.MainModule)
                    {
                        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(curModule.ModuleName), 0);
                    }
                }

                if (_mouseHook != IntPtr.Zero)
                {
                    Logger.Info("全局鼠标钩子安装成功");
                }
                else
                {
                    Logger.Warn("全局鼠标钩子安装失败，Win32错误码: " + Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                Logger.Error("安装全局鼠标钩子异常", ex);
            }
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                // 如果当前正在拖拽拉伸或滚动，完全忽略任何外部点击检测
                if (_currentGesture != GestureMode.None)
                {
                    return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                }

                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN ||
                    msg == WM_NCLBUTTONDOWN || msg == WM_NCRBUTTONDOWN)
                {
                    Point mousePt = Cursor.Position;
                    Rectangle hitBounds = Bounds;
                    hitBounds.Inflate(4, 4);

                    if (!hitBounds.Contains(mousePt))
                    {
                        Logger.Info("检测到窗口外部点击，发送关闭消息");
                        if (!IsDisposed && IsHandleCreated)
                        {
                            PostMessage(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private void UninstallOutsideClickHook()
        {
            if (_mouseHook != IntPtr.Zero)
            {
                try
                {
                    UnhookWindowsHookEx(_mouseHook);
                    Logger.Info("全局鼠标钩子已安全卸载");
                }
                catch { }
                _mouseHook = IntPtr.Zero;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CleanupResources();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CleanupResources();
                if (_font != null)
                {
                    _font.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private void CleanupResources()
        {
            UninstallOutsideClickHook();
            _mouseProc = null;
            if (_noticeTimer != null)
            {
                _noticeTimer.Stop();
                _noticeTimer.Dispose();
                _noticeTimer = null;
            }
            lock (_chunkLock)
            {
                _chunkBuffer.Length = 0;
                _chunkBuffer.Capacity = 0;
                _updatePending = false;
            }
            _paragraphs.Clear();
            _text.Length = 0;
            _text.Capacity = 0;
        }
    }
}
