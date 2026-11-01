using System;
using System.Drawing;
using System.Windows.Forms;

namespace Screenshoter
{
    // Маленькое уведомление в углу экрана, не забирает фокус.
    class Toast : Form
    {
        static Toast current;
        readonly Timer timer = new Timer();

        public static void ShowText(string text)
        {
            if (current != null && !current.IsDisposed) current.Close();
            current = new Toast(text);
            current.Show();
        }

        Toast(string text)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 32, 34);
            AutoScaleMode = AutoScaleMode.None;

            Label l = new Label();
            l.AutoSize = true;
            l.Font = new Font("Segoe UI", 10.5f);
            l.ForeColor = Color.White;
            l.Text = text;
            Controls.Add(l);

            int pad;
            using (Graphics g = CreateGraphics()) pad = (int)(12 * g.DpiX / 96f);
            Size ps = l.PreferredSize;
            l.Location = new Point(pad, pad);
            ClientSize = new Size(ps.Width + pad * 2, ps.Height + pad * 2);

            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Right - Width - pad, wa.Bottom - Height - pad);

            l.Click += delegate { Close(); };
            Click += delegate { Close(); };
            timer.Interval = Math.Min(7000, 2600 + text.Length * 30);
            timer.Tick += delegate { Close(); };
            timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x80 | 0x8; // NOACTIVATE | TOOLWINDOW | TOPMOST
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Color.FromArgb(255, 221, 0), 2))
                e.Graphics.DrawRectangle(p, 1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
