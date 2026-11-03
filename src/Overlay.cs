using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Screenshoter
{
    enum Tool { Select, Label, Frame, Marker, Arrow, Pencil }
    enum ShapeKind { Label, Frame, Marker, Arrow, Pencil }

    class Shape
    {
        public ShapeKind Kind;
        public Point A;        // Label: левый верхний угол плашки; остальные: начало
        public Point B;        // Label: куда указывает стрелка; остальные: конец
        public bool HasArrow;  // только для Label
        public bool FromArrow; // плашка появилась на хвосте нарисованной стрелки
        public bool AutoPlace; // плашка держится у хвоста стрелки, пока её не сдвинули вручную
        public Point Tail;     // хвост стрелки
        public string Text = "";
        public List<Point> Points;  // только для Pencil
    }

    // Кнопка, которая не забирает фокус у окна (чтобы Enter/Esc всегда работали).
    class FlatButton : Button
    {
        public FlatButton()
        {
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 70, 75);
            BackColor = Color.FromArgb(45, 45, 48);
            ForeColor = Color.White;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(2);
            Padding = new Padding(6, 3, 6, 3);
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
        }
    }

    class OverlayForm : Form
    {
        static readonly Color Yellow = Color.FromArgb(255, 221, 0);
        static readonly Color Dark = Color.FromArgb(25, 25, 25);
        static readonly Color Accent = Color.FromArgb(0, 120, 215);

        readonly Bitmap screen;
        readonly Bitmap dimmed;
        readonly Rectangle virt;
        int number;
        readonly bool numbering;
        readonly float scale;
        readonly Font labelFont, uiFont, badgeFont;

        Rectangle sel = Rectangle.Empty;
        Tool tool = Tool.Select;
        readonly List<Shape> shapes = new List<Shape>();

        // текущее действие мышью
        bool selecting;
        Point dragStart;
        Shape drawing;                 // фигура, которую сейчас рисуем
        Shape moving; Point moveOffset; bool moved; Point downPt;
        Shape editing; string editingOriginal; bool editingIsNew;

        TextBox editor;                 // невидимое поле ввода: текст, курсор и выделение рисуем сами на плашке
        Timer caretTimer; bool caretOn;
        FlowLayoutPanel toolbar;
        FlatButton btnSelect, btnLabel, btnFrame, btnMarker, btnArrow, btnPencil;
        Label lblInfo;

        public Bitmap Result;
        public Action ResetBatch;   // кнопка «Новая пачка»

        public static Bitmap CaptureScreen(Rectangle virt)
        {
            Bitmap b = new Bitmap(virt.Width, virt.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
                g.CopyFromScreen(virt.Location, Point.Empty, virt.Size, CopyPixelOperation.SourceCopy);
            return b;
        }

        public OverlayForm(Bitmap screen, Rectangle virt, int number, bool numbering)
        {
            this.screen = screen;
            this.virt = virt;
            this.number = number;
            this.numbering = numbering;

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = virt;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            BackColor = Color.Black;
            Text = "Скриншотер";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

            using (Graphics g = CreateGraphics()) scale = Math.Max(1f, g.DpiX / 96f);
            labelFont = new Font("Segoe UI", 17f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            badgeFont = new Font("Segoe UI", 15f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            uiFont = new Font("Segoe UI", 10f);

            dimmed = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(dimmed))
            {
                g.DrawImageUnscaled(screen, 0, 0);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(130, 0, 0, 0)))
                    g.FillRectangle(b, 0, 0, dimmed.Width, dimmed.Height);
            }

            BuildToolbar();
            BuildEditor();
        }

        // ---------- UI ----------

        void BuildToolbar()
        {
            toolbar = new FlowLayoutPanel();
            toolbar.AutoSize = true;
            toolbar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            toolbar.WrapContents = false;
            toolbar.BackColor = Color.FromArgb(30, 30, 32);
            toolbar.Padding = new Padding(4);
            toolbar.Cursor = Cursors.Default;

            btnSelect = AddButton("▭ Область", delegate { SetTool(Tool.Select); });
            btnLabel = AddButton("T Подпись", delegate { SetTool(Tool.Label); });
            btnArrow = AddButton("↗ Стрелка", delegate { SetTool(Tool.Arrow); });
            btnFrame = AddButton("□ Рамка", delegate { SetTool(Tool.Frame); });
            btnMarker = AddButton("▮ Маркер", delegate { SetTool(Tool.Marker); });
            btnPencil = AddButton("✎ Карандаш", delegate { SetTool(Tool.Pencil); });
            AddButton("⛶ Весь экран", delegate { SelectMonitor(); });
            AddButton("↶", delegate { Undo(); });
            FlatButton done = AddButton("✓ Готово", delegate { Finish(); });
            done.BackColor = Accent;
            AddButton("✕", delegate { Cancel(); });
            AddButton("⟲ Новая пачка", delegate { NewBatch(); });

            lblInfo = new Label();
            lblInfo.AutoSize = true;
            lblInfo.ForeColor = Color.FromArgb(200, 200, 200);
            lblInfo.Font = uiFont;
            lblInfo.Margin = new Padding(8, 7, 6, 0);
            lblInfo.Text = number > 1 ? ("Скрин №" + number + " в пачке") : "Новая пачка";
            toolbar.Controls.Add(lblInfo);

            Controls.Add(toolbar);
            UpdateToolButtons();
        }

        FlatButton AddButton(string text, EventHandler click)
        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.Font = uiFont;
            b.Click += click;
            toolbar.Controls.Add(b);
            return b;
        }

        void BuildEditor()
        {
            editor = new TextBox();
            editor.Multiline = true;
            editor.AcceptsReturn = true;
            editor.WordWrap = true;
            editor.BorderStyle = BorderStyle.None;
            editor.BackColor = Yellow;
            editor.ForeColor = Color.Black;
            editor.Font = labelFont;
            editor.Visible = false;
            editor.Cursor = Cursors.IBeam;
            editor.TextChanged += delegate { OnEditorChanged(); };
            // стрелки, Home/End, Shift+… двигают курсор, не меняя текст — перерисовываем после обработки клавиши
            editor.KeyDown += delegate { BeginInvoke((MethodInvoker)RefreshCaret); };
            editor.KeyUp += delegate { RefreshCaret(); };
            Controls.Add(editor);

            caretTimer = new Timer();
            caretTimer.Interval = SystemInformation.CaretBlinkTime > 0 ? SystemInformation.CaretBlinkTime : 530;
            caretTimer.Tick += delegate
            {
                if (editing == null) return;
                caretOn = !caretOn;
                Invalidate(LabelRect(editing));
            };
        }

        void SetTool(Tool t)
        {
            if (t != Tool.Select && sel.IsEmpty) SelectMonitor();
            tool = t;
            UpdateToolButtons();
        }

        void UpdateToolButtons()
        {
            Color normal = Color.FromArgb(45, 45, 48), active = Color.FromArgb(110, 90, 0);
            btnSelect.BackColor = tool == Tool.Select ? active : normal;
            btnLabel.BackColor = tool == Tool.Label ? active : normal;
            btnArrow.BackColor = tool == Tool.Arrow ? active : normal;
            btnFrame.BackColor = tool == Tool.Frame ? active : normal;
            btnMarker.BackColor = tool == Tool.Marker ? active : normal;
            btnPencil.BackColor = tool == Tool.Pencil ? active : normal;
        }

        Rectangle MonitorAt(Point clientPt)
        {
            Screen s = Screen.FromPoint(new Point(clientPt.X + virt.X, clientPt.Y + virt.Y));
            Rectangle r = s.Bounds;
            r.Offset(-virt.X, -virt.Y);
            return r;
        }

        Point CursorClient()
        {
            Point p = Cursor.Position;
            return new Point(p.X - virt.X, p.Y - virt.Y);
        }

        void LayoutToolbar()
        {
            Size ts = toolbar.PreferredSize;
            int m = (int)(8 * scale);
            int x, y;
            if (sel.IsEmpty)
            {
                Rectangle mon = MonitorAt(CursorClient());
                x = mon.Left + (mon.Width - ts.Width) / 2;
                y = mon.Top + m * 2;
            }
            else
            {
                Rectangle mon = MonitorAt(new Point(sel.Left + sel.Width / 2, sel.Top + sel.Height / 2));
                x = sel.Right - ts.Width;
                y = sel.Bottom + m;
                if (y + ts.Height > mon.Bottom) y = sel.Top - ts.Height - m;   // сверху
                if (y < mon.Top) y = sel.Bottom - ts.Height - m;                 // внутри снизу
                x = Math.Max(mon.Left + m, Math.Min(x, mon.Right - ts.Width - m));
                y = Math.Max(mon.Top + m, Math.Min(y, mon.Bottom - ts.Height - m));
            }
            toolbar.Location = new Point(x, y);
            toolbar.BringToFront();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = virt;
            LayoutToolbar();
            Native.ForceForeground(Handle);
            Activate();
            Focus();
        }

        // ---------- геометрия фигур ----------

        Size MeasureText(string text)
        {
            string t = string.IsNullOrEmpty(text) ? "W" : text;
            if (t.EndsWith("\n")) t += "W";
            return TextRenderer.MeasureText(t, labelFont, new Size(MaxTextWidth, int.MaxValue), TextFlags);
        }

        int Pad { get { return (int)(8 * scale); } }

        const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;

        // Длинный текст сам переносится на новую строку по этой ширине.
        int MaxTextWidth
        {
            get
            {
                int w = (int)(420 * scale);
                if (!sel.IsEmpty) w = Math.Min(w, sel.Width - Pad * 2 - 8);
                return Math.Max(w, (int)(80 * scale));
            }
        }

        static Rectangle PointsBounds(List<Point> pts)
        {
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            foreach (Point p in pts) { l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y); }
            return Rectangle.FromLTRB(l, t, r, b);
        }

        Rectangle LabelRect(Shape s)
        {
            Size t = MeasureText(s.Text);
            return new Rectangle(s.A.X, s.A.Y, t.Width + Pad * 2 + 4, t.Height + Pad + 4);
        }

        static Rectangle Norm(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        Rectangle ShapeArea(Shape s)
        {
            Rectangle r;
            if (s.Kind == ShapeKind.Label)
            {
                r = LabelRect(s);
                if (s.HasArrow) r = Rectangle.Union(r, Norm(s.B, s.B));
            }
            else if (s.Kind == ShapeKind.Pencil) r = PointsBounds(s.Points);
            else r = Norm(s.A, s.B);
            int inf = (int)(24 * scale);
            r.Inflate(inf, inf);
            return r;
        }

        static double DistToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
            double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return Math.Sqrt(x * x + y * y);
        }

        // Точка на краю плашки, где её пересекает линия «центр плашки → цель».
        // Отсюда начинается стрелка, поэтому она всегда смотрит прямо в блок.
        static PointF EdgePoint(Rectangle r, Point target, float inset)
        {
            float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            float dx = target.X - cx, dy = target.Y - cy;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.5f) return new PointF(cx, cy);
            float ux = dx / len, uy = dy / len;
            float hw = r.Width / 2f, hh = r.Height / 2f;
            float t = Math.Min(Math.Abs(ux) > 1e-4f ? hw / Math.Abs(ux) : float.MaxValue,
                               Math.Abs(uy) > 1e-4f ? hh / Math.Abs(uy) : float.MaxValue);
            t = Math.Max(0, t - inset);
            return new PointF(cx + ux * t, cy + uy * t);
        }

        static GraphicsPath RoundedRect(Rectangle r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        Shape HitTest(Point p)
        {
            double tol = 8 * scale;
            for (int i = shapes.Count - 1; i >= 0; i--)
            {
                Shape s = shapes[i];
                if (s == editing) continue;
                switch (s.Kind)
                {
                    case ShapeKind.Label:
                        if (LabelRect(s).Contains(p)) return s;
                        break;
                    case ShapeKind.Marker:
                        if (Norm(s.A, s.B).Contains(p)) return s;
                        break;
                    case ShapeKind.Frame:
                        {
                            Rectangle r = Norm(s.A, s.B);
                            Rectangle outer = r, inner = r;
                            outer.Inflate((int)tol, (int)tol);
                            inner.Inflate(-(int)tol, -(int)tol);
                            if (outer.Contains(p) && !(inner.Width > 0 && inner.Height > 0 && inner.Contains(p))) return s;
                            break;
                        }
                    case ShapeKind.Arrow:
                        if (DistToSegment(p, s.A, s.B) <= tol) return s;
                        break;
                    case ShapeKind.Pencil:
                        for (int j = 1; j < s.Points.Count; j++)
                            if (DistToSegment(p, s.Points[j - 1], s.Points[j]) <= tol) return s;
                        break;
                }
            }
            return null;
        }

        // ---------- рисование ----------

        void DrawArrow(Graphics g, Point a, Point b, int ox, int oy)
        {
            DrawArrow(g, new PointF(a.X, a.Y), b, ox, oy, false);
        }

        void DrawArrow(Graphics g, PointF a, Point b, int ox, int oy, bool flatStart)
        {
            PointF A = new PointF(a.X - ox, a.Y - oy), B = new PointF(b.X - ox, b.Y - oy);
            float dx = B.X - A.X, dy = B.Y - A.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            float ux = dx / len, uy = dy / len;
            float w = 4f * scale;
            float head = Math.Min(20f * scale, len * 0.6f), half = head * 0.55f;
            PointF baseP = new PointF(B.X - ux * head, B.Y - uy * head);
            PointF[] tri = new PointF[] {
                B,
                new PointF(baseP.X - uy * half, baseP.Y + ux * half),
                new PointF(baseP.X + uy * half, baseP.Y - ux * half)
            };
            using (Pen outline = new Pen(Dark, w + 3f * scale))
            using (Pen fill = new Pen(Yellow, w))
            using (SolidBrush yb = new SolidBrush(Yellow))
            using (Pen triOutline = new Pen(Dark, 1.5f * scale))
            {
                outline.StartCap = fill.StartCap = flatStart ? LineCap.Flat : LineCap.Round;
                outline.EndCap = fill.EndCap = LineCap.Round;
                g.DrawLine(outline, A, baseP);
                g.DrawLine(fill, A, baseP);
                triOutline.LineJoin = LineJoin.Round;
                g.FillPolygon(yb, tri);
                g.DrawPolygon(triOutline, tri);
            }
        }

        void DrawShape(Graphics g, Shape s, int ox, int oy, bool skipText)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            switch (s.Kind)
            {
                case ShapeKind.Marker:
                    {
                        Rectangle r = Norm(s.A, s.B); r.Offset(-ox, -oy);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(80, Yellow)))
                            g.FillRectangle(b, r);
                        using (Pen p = new Pen(Color.FromArgb(200, Yellow), 2f * scale))
                            g.DrawRectangle(p, r);
                        break;
                    }
                case ShapeKind.Frame:
                    {
                        Rectangle r = Norm(s.A, s.B); r.Offset(-ox, -oy);
                        using (Pen o = new Pen(Dark, 7f * scale))
                        using (Pen p = new Pen(Yellow, 4f * scale))
                        {
                            o.LineJoin = p.LineJoin = LineJoin.Round;
                            g.DrawRectangle(o, r);
                            g.DrawRectangle(p, r);
                        }
                        break;
                    }
                case ShapeKind.Arrow:
                    DrawArrow(g, s.A, s.B, ox, oy);
                    break;
                case ShapeKind.Pencil:
                    {
                        if (s.Points.Count < 2) break;
                        Point[] pts = new Point[s.Points.Count];
                        for (int i = 0; i < pts.Length; i++) pts[i] = new Point(s.Points[i].X - ox, s.Points[i].Y - oy);
                        using (Pen o = new Pen(Dark, 6.5f * scale))
                        using (Pen p = new Pen(Yellow, 3.5f * scale))
                        {
                            o.StartCap = o.EndCap = p.StartCap = p.EndCap = LineCap.Round;
                            o.LineJoin = p.LineJoin = LineJoin.Round;
                            g.DrawLines(o, pts);
                            g.DrawLines(p, pts);
                        }
                        break;
                    }
                case ShapeKind.Label:
                    {
                        Rectangle lr = LabelRect(s);
                        if (s.HasArrow && !lr.Contains(s.B))
                            DrawArrow(g, EdgePoint(lr, s.B, 1f), s.B, ox, oy, true);
                        Rectangle r = lr; r.Offset(-ox, -oy);
                        using (GraphicsPath path = RoundedRect(r, 7f * scale))
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(185, Yellow)))
                            g.FillPath(b, path);
                        g.SmoothingMode = SmoothingMode.None;
                        if (!skipText)
                            TextRenderer.DrawText(g, s.Text, labelFont, new Rectangle(r.X + Pad + 2, r.Y + Pad / 2 + 2, MaxTextWidth, r.Height), Color.Black, TextFlags);
                        break;
                    }
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        void DrawBadge(Graphics g, int x, int y)
        {
            string t = number.ToString();
            Size ts = TextRenderer.MeasureText(t, badgeFont, Size.Empty, TextFormatFlags.NoPadding);
            int d = Math.Max(ts.Width, ts.Height) + (int)(10 * scale);
            Rectangle r = new Rectangle(x, y, d, d);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Yellow)) g.FillEllipse(b, r);
            using (Pen p = new Pen(Dark, 2f * scale)) g.DrawEllipse(p, r);
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, t, badgeFont, r, Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        void DrawAllShapes(Graphics g, int ox, int oy, bool onScreen)
        {
            // маркеры под всем, подписи поверх всего
            foreach (Shape s in shapes) if (s.Kind == ShapeKind.Marker) DrawShape(g, s, ox, oy, false);
            foreach (Shape s in shapes) if (s.Kind == ShapeKind.Frame || s.Kind == ShapeKind.Arrow || s.Kind == ShapeKind.Pencil) DrawShape(g, s, ox, oy, false);
            foreach (Shape s in shapes) if (s.Kind == ShapeKind.Label) DrawShape(g, s, ox, oy, false);
            if (onScreen && editing != null) DrawEditorState(g);
        }

        // ---------- курсор и выделение редактируемой подписи ----------

        Point TextOrigin(Shape s)
        {
            Rectangle r = LabelRect(s);
            return new Point(r.X + Pad + 2, r.Y + Pad / 2 + 2);
        }

        int CharWidth(char c)
        {
            return TextRenderer.MeasureText(c.ToString(), labelFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        }

        // Позиция символа i внутри текста (левый верхний угол), по раскладке самого поля ввода.
        Point CharPos(int i)
        {
            string t = editor.Text;
            if (t.Length == 0) return Point.Empty;
            Point p0 = editor.GetPositionFromCharIndex(0);
            if (i < t.Length)
            {
                Point p = editor.GetPositionFromCharIndex(i);
                return new Point(p.X - p0.X, p.Y - p0.Y);
            }
            // позиция «после последнего символа» поле не отдаёт — считаем от предпоследней
            char last = t[t.Length - 1];
            Point prev = CharPos(t.Length - 1);
            if (last == '\n') return new Point(0, prev.Y + labelFont.Height);
            return new Point(prev.X + CharWidth(last), prev.Y);
        }

        void DrawEditorState(Graphics g)
        {
            Point o = TextOrigin(editing);
            int lineH = labelFont.Height;
            if (editor.SelectionLength > 0)
            {
                string t = editor.Text;
                int from = editor.SelectionStart, to = Math.Min(t.Length, from + editor.SelectionLength);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(90, Accent)))
                {
                    for (int i = from; i < to; i++)
                    {
                        if (t[i] == '\r' || t[i] == '\n') continue;
                        Point p = CharPos(i);
                        g.FillRectangle(b, o.X + p.X, o.Y + p.Y, CharWidth(t[i]), lineH);
                    }
                }
            }
            else if (caretOn)
            {
                Point p = CharPos(editor.SelectionStart);
                using (SolidBrush b = new SolidBrush(Color.Black))
                    g.FillRectangle(b, o.X + p.X, o.Y + p.Y, Math.Max(1, (int)Math.Round(1.5f * scale)), lineH);
            }
        }

        void RefreshCaret()
        {
            if (editing == null) return;
            caretOn = true;
            caretTimer.Stop(); caretTimer.Start();
            Invalidate(LabelRect(editing));
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle clip = e.ClipRectangle;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(dimmed, clip, clip, GraphicsUnit.Pixel);

            if (!sel.IsEmpty)
            {
                Rectangle r = Rectangle.Intersect(sel, clip);
                if (r.Width > 0 && r.Height > 0) g.DrawImage(screen, r, r, GraphicsUnit.Pixel);
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;

            if (!sel.IsEmpty)
            {
                using (Pen p = new Pen(Accent, Math.Max(1f, 1.5f * scale)))
                    g.DrawRectangle(p, sel.X - 1, sel.Y - 1, sel.Width + 1, sel.Height + 1);
                if (numbering) DrawBadge(g, sel.X + (int)(6 * scale), sel.Y + (int)(6 * scale));

                string size = sel.Width + " × " + sel.Height;
                Size ts = TextRenderer.MeasureText(size, uiFont);
                int ty = sel.Top - ts.Height - 4;
                if (ty < MonitorAt(sel.Location).Top) ty = sel.Top + 4;
                Rectangle tr = new Rectangle(sel.Left, ty, ts.Width, ts.Height);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 20, 20, 20))) g.FillRectangle(b, tr);
                TextRenderer.DrawText(g, size, uiFont, tr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            DrawAllShapes(g, 0, 0, true);
            if (drawing != null && !shapes.Contains(drawing)) DrawShape(g, drawing, 0, 0, false);

            if (sel.IsEmpty && !selecting)
            {
                Rectangle mon = MonitorAt(CursorClient());
                string hint = "Выдели область мышью  ·  клик — весь монитор  ·  Esc — отмена";
                Size hs = TextRenderer.MeasureText(hint, uiFont);
                int y = toolbar.Bottom + (int)(10 * scale);
                Rectangle hr = new Rectangle(mon.Left + (mon.Width - hs.Width) / 2 - 10, y, hs.Width + 20, hs.Height + 10);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(210, 20, 20, 20))) g.FillRectangle(b, hr);
                TextRenderer.DrawText(g, hint, uiFont, hr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // ---------- мышь ----------

        Rectangle hintMonitor = Rectangle.Empty;

        void InvalidateSel(Rectangle before, Rectangle after)
        {
            Rectangle u = before.IsEmpty ? after : (after.IsEmpty ? before : Rectangle.Union(before, after));
            int inf = (int)(70 * scale);
            u.Inflate(inf, inf);
            Invalidate(u);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (editing != null) CommitEdit();

            if (e.Button == MouseButtons.Right)
            {
                Shape hit = HitTest(e.Location);
                if (hit != null) { shapes.Remove(hit); Invalidate(ShapeArea(hit)); }
                else if (sel.IsEmpty) Cancel();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            downPt = e.Location;
            Shape h = HitTest(e.Location);
            if (h != null && h.Kind == ShapeKind.Label)
            {
                moving = h; moved = false;
                moveOffset = new Point(e.X - h.A.X, e.Y - h.A.Y);
                return;
            }

            bool inside = !sel.IsEmpty && sel.Contains(e.Location);
            if (inside && tool != Tool.Select)
            {
                drawing = new Shape();
                drawing.A = drawing.B = e.Location;
                switch (tool)
                {
                    case Tool.Label: drawing.Kind = ShapeKind.Label; break;
                    case Tool.Frame: drawing.Kind = ShapeKind.Frame; break;
                    case Tool.Marker: drawing.Kind = ShapeKind.Marker; break;
                    case Tool.Pencil:
                        drawing.Kind = ShapeKind.Pencil;
                        drawing.Points = new List<Point>();
                        drawing.Points.Add(e.Location);
                        break;
                    default: drawing.Kind = ShapeKind.Arrow; break;
                }
                return;
            }

            // новое выделение
            selecting = true;
            dragStart = e.Location;
            sel = Rectangle.Empty;
            toolbar.Visible = false;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (moving != null)
            {
                if (Math.Abs(e.X - downPt.X) + Math.Abs(e.Y - downPt.Y) > 3) moved = true;
                if (moved)
                {
                    Rectangle before = ShapeArea(moving);
                    moving.A = new Point(e.X - moveOffset.X, e.Y - moveOffset.Y);
                    moving.AutoPlace = false;
                    Invalidate(Rectangle.Union(before, ShapeArea(moving)));
                }
                return;
            }
            if (drawing != null)
            {
                Rectangle before = ShapeArea(drawing);
                if (drawing.Kind == ShapeKind.Label)
                {
                    // тянем от объекта к месту плашки: B — цель стрелки, плашка встаёт у курсора
                    drawing.HasArrow = Math.Abs(e.X - downPt.X) + Math.Abs(e.Y - downPt.Y) > 12 * scale;
                    drawing.B = downPt;
                    drawing.Tail = e.Location;
                    if (drawing.HasArrow) PlaceAtTail(drawing); else drawing.A = downPt;
                }
                else if (drawing.Kind == ShapeKind.Arrow)
                {
                    // стрелка тянется от объекта (остриё) к месту, где будет текст (хвост)
                    drawing.B = downPt;
                    drawing.A = e.Location;
                }
                else if (drawing.Kind == ShapeKind.Pencil)
                {
                    Point last = drawing.Points[drawing.Points.Count - 1];
                    if (Math.Abs(e.X - last.X) + Math.Abs(e.Y - last.Y) < 2) return;
                    drawing.Points.Add(e.Location);
                    // перерисовываем только последний отрезок
                    Rectangle seg = Norm(last, e.Location);
                    int inf = (int)(10 * scale);
                    seg.Inflate(inf, inf);
                    Invalidate(seg);
                    return;
                }
                else drawing.B = e.Location;
                Invalidate(Rectangle.Union(before, ShapeArea(drawing)));
                return;
            }
            if (selecting)
            {
                Rectangle before = sel;
                sel = Norm(dragStart, e.Location);
                InvalidateSel(before, sel);
                return;
            }
            Shape hit = HitTest(e.Location);
            if (hit != null && hit.Kind == ShapeKind.Label) Cursor = Cursors.SizeAll;
            else if (hit != null) Cursor = Cursors.Hand;
            else if (tool == Tool.Label && !sel.IsEmpty && sel.Contains(e.Location)) Cursor = Cursors.IBeam;
            else Cursor = Cursors.Cross;
            if (sel.IsEmpty && !selecting)
            {
                // подсказка и панель переезжают на монитор под курсором
                Rectangle mon = MonitorAt(e.Location);
                if (mon != hintMonitor) { hintMonitor = mon; LayoutToolbar(); Invalidate(); }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (moving != null)
            {
                Shape m = moving;
                moving = null;
                if (!moved) BeginEdit(m, false);
                return;
            }
            if (drawing != null)
            {
                Shape d = drawing;
                drawing = null;
                if (d.Kind == ShapeKind.Label)
                {
                    if (!d.HasArrow) d.A = downPt;
                    else d.AutoPlace = true;
                    shapes.Add(d);
                    BeginEdit(d, true);
                }
                else if (d.Kind == ShapeKind.Arrow)
                {
                    Rectangle r = Norm(d.A, d.B);
                    if (r.Width + r.Height <= 8)
                    {
                        // просто клик стрелкой — ставим обычную подпись в этом месте
                        d.Kind = ShapeKind.Label;
                        d.A = downPt;
                        d.HasArrow = false;
                        shapes.Add(d);
                        BeginEdit(d, true);
                        return;
                    }
                    // стрелка нарисована: на её хвосте сразу открываем поле для текста
                    Rectangle before = ShapeArea(d);
                    d.Kind = ShapeKind.Label;
                    d.HasArrow = true;
                    d.FromArrow = true;
                    d.AutoPlace = true;
                    d.Tail = d.A;
                    PlaceAtTail(d);
                    shapes.Add(d);
                    Invalidate(before);
                    BeginEdit(d, true);
                }
                else if (d.Kind == ShapeKind.Pencil)
                {
                    if (d.Points.Count == 1) d.Points.Add(new Point(d.Points[0].X + 1, d.Points[0].Y)); // точка
                    shapes.Add(d);
                    Invalidate(ShapeArea(d));
                }
                else
                {
                    Rectangle r = Norm(d.A, d.B);
                    if (r.Width + r.Height > 8) shapes.Add(d);
                    Invalidate(ShapeArea(d));
                }
                return;
            }
            if (selecting)
            {
                selecting = false;
                if (sel.Width < 6 || sel.Height < 6) sel = MonitorAt(e.Location);
                if (tool == Tool.Select) tool = Tool.Arrow; // после выделения сразу можно ставить стрелку с подписью
                UpdateToolButtons();
                toolbar.Visible = true;
                LayoutToolbar();
                Invalidate();
            }
        }

        // Ставит плашку так, чтобы хвост стрелки упирался в её край со стороны, противоположной острию.
        void PlaceAtTail(Shape s)
        {
            // Центр плашки лежит на продолжении стрелки, а её край касается хвоста.
            Size sz = LabelRect(s).Size;
            float dx = s.Tail.X - s.B.X, dy = s.Tail.Y - s.B.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            float ux = len < 0.5f ? 0 : dx / len, uy = len < 0.5f ? 1 : dy / len;
            float hw = sz.Width / 2f, hh = sz.Height / 2f;
            float dist = Math.Min(Math.Abs(ux) > 1e-4f ? hw / Math.Abs(ux) : float.MaxValue,
                                  Math.Abs(uy) > 1e-4f ? hh / Math.Abs(uy) : float.MaxValue);
            int x = (int)Math.Round(s.Tail.X + ux * dist - hw);
            int y = (int)Math.Round(s.Tail.Y + uy * dist - hh);
            if (!sel.IsEmpty)
            {
                // не даём плашке вылезти за край скриншота
                x = Math.Max(sel.Left, Math.Min(x, sel.Right - sz.Width));
                y = Math.Max(sel.Top, Math.Min(y, sel.Bottom - sz.Height));
            }
            s.A = new Point(x, y);
        }

        // Пустая плашка со стрелки превращается обратно в обычную стрелку.
        void RevertToArrow(Shape s)
        {
            s.Kind = ShapeKind.Arrow;
            s.A = s.Tail;
            s.HasArrow = false;
            s.FromArrow = false;
            s.AutoPlace = false;
            s.Text = "";
        }

        // ---------- редактор подписи ----------

        void BeginEdit(Shape s, bool isNew)
        {
            editing = s;
            editingIsNew = isNew;
            editingOriginal = s.Text;
            editor.Text = s.Text;
            PlaceEditor();
            editor.Visible = true;
            editor.BringToFront();
            editor.Focus();
            editor.SelectionStart = editor.Text.Length;
            caretOn = true;
            caretTimer.Start();
            Invalidate(ShapeArea(s));
        }

        void PlaceEditor()
        {
            // Само поле убрано за край формы: у TextBox нет прозрачного фона, и раньше поверх
            // закруглённой плашки торчал его прямоугольник. Ширина = ширине переноса подписи,
            // чтобы строки в поле переносились так же, как на плашке; запас по высоте — чтобы не прокручивалось.
            Size t = MeasureText(editing.Text);
            editor.Size = new Size(MaxTextWidth, t.Height + labelFont.Height * 2);
            editor.Location = new Point(-editor.Width - 10000, 0);
        }

        void OnEditorChanged()
        {
            if (editing == null) return;
            Rectangle before = ShapeArea(editing);
            editing.Text = editor.Text;
            if (editing.AutoPlace) PlaceAtTail(editing);
            PlaceEditor();
            Invalidate(Rectangle.Union(before, ShapeArea(editing)));
        }

        void CommitEdit()
        {
            if (editing == null) return;
            Shape s = editing;
            editing = null;
            s.Text = editor.Text.TrimEnd();
            editor.Visible = false;
            caretTimer.Stop();
            Rectangle area = ShapeArea(s);
            if (s.Text.Length == 0)
            {
                if (s.FromArrow) RevertToArrow(s); else shapes.Remove(s);
            }
            else if (s.AutoPlace) PlaceAtTail(s);
            ActiveControl = null;
            Focus();
            Invalidate(area);
        }

        void CancelEdit()
        {
            if (editing == null) return;
            Shape s = editing;
            editing = null;
            editor.Visible = false;
            caretTimer.Stop();
            Rectangle area = ShapeArea(s);
            if (editingIsNew || editingOriginal.Length == 0)
            {
                if (s.FromArrow) RevertToArrow(s); else shapes.Remove(s);
            }
            else
            {
                s.Text = editingOriginal;
                if (s.AutoPlace) PlaceAtTail(s);
            }
            ActiveControl = null;
            Focus();
            Invalidate(area);
        }

        // ---------- клавиатура ----------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (editing != null)
            {
                // Enter — новая строка (её обрабатывает само поле), Esc или клик мимо — закончить подпись
                if (keyData == Keys.Escape) { CommitEdit(); return true; }
                if (keyData == (Keys.Control | Keys.Enter)) { CommitEdit(); Finish(); return true; }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            switch (keyData)
            {
                case Keys.Escape: Cancel(); return true;
                case Keys.Enter:
                case Keys.Control | Keys.Enter: Finish(); return true;
                case Keys.T: SetTool(Tool.Label); return true;
                case Keys.A: SetTool(Tool.Arrow); return true;
                case Keys.R: SetTool(Tool.Frame); return true;
                case Keys.M: SetTool(Tool.Marker); return true;
                case Keys.P: SetTool(Tool.Pencil); return true;
                case Keys.N: NewBatch(); return true;
                case Keys.S: SetTool(Tool.Select); return true;
                case Keys.F: SelectMonitor(); return true;
                case Keys.Control | Keys.Z: Undo(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---------- действия ----------

        void SelectMonitor()
        {
            if (editing != null) CommitEdit();
            sel = MonitorAt(CursorClient());
            if (tool == Tool.Select) tool = Tool.Arrow;
            UpdateToolButtons();
            toolbar.Visible = true;
            LayoutToolbar();
            Invalidate();
        }

        void NewBatch()
        {
            if (ResetBatch != null) ResetBatch();
            number = 1;
            lblInfo.Text = "Пачка сброшена, этот скрин будет первым";
            LayoutToolbar();
            Invalidate();
        }

        void Undo()
        {
            if (editing != null) { CancelEdit(); return; }
            if (shapes.Count == 0) return;
            Shape s = shapes[shapes.Count - 1];
            shapes.RemoveAt(shapes.Count - 1);
            Invalidate(ShapeArea(s));
        }

        void Cancel()
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        void Finish()
        {
            if (editing != null) CommitEdit();
            if (sel.IsEmpty) sel = MonitorAt(CursorClient());
            Rectangle r = Rectangle.Intersect(sel, new Rectangle(0, 0, screen.Width, screen.Height));
            if (r.Width < 1 || r.Height < 1) { Cancel(); return; }

            Bitmap res = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(res))
            {
                g.DrawImage(screen, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
                DrawAllShapes(g, r.X, r.Y, false);
                if (numbering) DrawBadge(g, (int)(6 * scale), (int)(6 * scale));
            }
            Result = res;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                dimmed.Dispose();
                caretTimer.Dispose();
                labelFont.Dispose();
                badgeFont.Dispose();
                uiFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
