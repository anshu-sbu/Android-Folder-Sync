// Look of Folder Sync PC: theme colours, big touch-friendly controls, app names.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle(PhoneSync.Variant.Name)]
[assembly: System.Reflection.AssemblyProduct(PhoneSync.Variant.Name)]

namespace PhoneSync
{
    /// <summary>Names and settings keys of Folder Sync PC.</summary>
    static class Variant
    {
        public const bool Wifi = true;
        public const string Name = "Folder Sync PC";
        public const string Key = "PhoneSyncWifi";
        public const string MutexName = "PhoneSync.SingleInstance.wifi.v1";
        public const string RunValue = "PhoneSyncWifi";
        public const string FolderPrefix = "WiFi Sync";
        public const string Tagline = "Share a PC folder with your phone - same network or over the internet.";
    }

    // ------------------------------------------------------------------------------------
    // Modern touch-friendly look: dark theme, big targets, rounded cards
    // ------------------------------------------------------------------------------------
    static class Ui
    {
        public static float Scale = 1f;
        public static int Px(int v) { return (int)Math.Round(v * Scale); }

        // saffron
        public static readonly Color Bg = Color.FromArgb(17, 12, 8);
        public static readonly Color Card = Color.FromArgb(29, 21, 14);
        public static readonly Color Field = Color.FromArgb(43, 32, 21);
        public static readonly Color Border = Color.FromArgb(66, 50, 33);
        public static readonly Color Text = Color.FromArgb(250, 242, 232);
        public static readonly Color Muted = Color.FromArgb(166, 148, 128);
        public static readonly Color Accent = Color.FromArgb(255, 153, 51);
        public static readonly Color AccentHi = Color.FromArgb(255, 176, 92);
        public static readonly Color AccentLo = Color.FromArgb(150, 82, 14);
        public static readonly Color OnAccent = Color.FromArgb(28, 17, 4);
        public static readonly Color Surface = Color.FromArgb(66, 50, 33);
        public static readonly Color SurfaceHi = Color.FromArgb(88, 67, 44);
        public static readonly Color DangerText = Color.FromArgb(255, 120, 120);
        public static readonly Color DangerBg = Color.FromArgb(58, 30, 38);
        public static readonly Color Good = Color.FromArgb(52, 211, 153);
        public static readonly Color Warn = Color.FromArgb(251, 191, 36);

        public static Font F(float pt) { return new Font("Segoe UI", pt, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font FB(float pt) { return new Font("Segoe UI Semibold", pt, FontStyle.Regular, GraphicsUnit.Point); }

        /// <summary>App icon: a phone with Wi-Fi arcs.</summary>
        public static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(64, 64))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var path = new GraphicsPath())
                    {
                        var r = new Rectangle(14, 2, 36, 60);
                        int d = 12;
                        path.AddArc(r.X, r.Y, d, d, 180, 90);
                        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                        path.CloseFigure();
                        using (var b = new SolidBrush(Ui.Accent)) g.FillPath(b, path);
                    }
                    Color mark = Variant.Wifi ? Ui.OnAccent : Color.White;
                    using (var w = new SolidBrush(mark))
                    {
                        using (var pen = new Pen(mark, 4f))
                        {
                            pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                            g.DrawArc(pen, 20, 22, 24, 24, 225, 90);
                            g.DrawArc(pen, 14, 14, 36, 36, 225, 90);
                        }
                        g.FillEllipse(w, 29, 38, 6, 6);
                        g.FillRectangle(w, 24, 50, 16, 4);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            int d = Math.Max(1, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Label Caption(string text)
        {
            var l = new Label();
            l.Text = text.ToUpperInvariant();
            l.AutoSize = true;
            l.Font = FB(9f);
            l.ForeColor = Muted;
            l.BackColor = Color.Transparent;
            l.Margin = new Padding(Px(2), 0, 0, Px(8));
            return l;
        }
    }

    /// <summary>Rounded panel used for cards and text fields.</summary>
    class Card : Panel
    {
        public int Radius = 16;
        public Color Fill = Ui.Card;
        public Color Line = Ui.Border;
        public bool Active;

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Fill;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Round(r, Ui.Px(Radius)))
            using (var b = new SolidBrush(Fill))
            using (var pen = new Pen(Active ? Ui.Accent : Line, Active ? 2f : 1f))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>Text field inside a rounded card, 52px tall.</summary>
    class Field : Card
    {
        public readonly TextBox Box = new TextBox();

        public Field(bool readOnly)
        {
            Radius = 12;
            Fill = Ui.Field;
            BackColor = Fill;
            Height = Ui.Px(52);
            Box.BorderStyle = BorderStyle.None;
            Box.Font = Ui.F(12f);
            Box.ReadOnly = readOnly;
            Box.BackColor = Fill;
            Box.ForeColor = Ui.Text;
            Controls.Add(Box);
            Box.Enter += (o, e) => { Active = true; Invalidate(); };
            Box.Leave += (o, e) => { Active = false; Invalidate(); };
            Click += (o, e) => Box.Focus();
            Layout_();
        }

        void Layout_()
        {
            Box.SetBounds(Ui.Px(16), Math.Max(0, (Height - Box.Height) / 2), Math.Max(10, Width - Ui.Px(32)), Box.Height);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Layout_(); }
    }

    /// <summary>Large rounded button with hover / pressed states.</summary>
    class FlatButton : Button
    {
        public bool Primary, Danger;
        bool hot, down;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            Cursor = Cursors.Hand;
            Font = Ui.FB(11f);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(Ui.Px(10), 0, 0, 0);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size t = TextRenderer.MeasureText(Text, Font);
            return new Size(Math.Max(Ui.Px(110), t.Width + Ui.Px(48)), Ui.Px(52));
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill, fore = Ui.Text;
            if (Primary) { fill = down ? Ui.AccentLo : hot ? Ui.AccentHi : Ui.Accent; fore = Ui.OnAccent; }
            else if (Danger) { fill = down ? Color.FromArgb(84, 38, 48) : hot ? Color.FromArgb(72, 34, 43) : Ui.DangerBg; fore = Ui.DangerText; }
            else fill = down ? Ui.Border : hot ? Ui.SurfaceHi : Ui.Surface;
            if (!Enabled) { fill = Color.FromArgb(28, 34, 46); fore = Color.FromArgb(90, 99, 118); }
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Ui.Round(r, Ui.Px(12)))
            using (var b = new SolidBrush(fill))
            {
                g.FillPath(b, path);
                if (Focused && ShowFocusCues) using (var pen = new Pen(Ui.AccentHi, 2f)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>On/off switch (a CheckBox underneath, so Checked / CheckedChanged work as usual).</summary>
    class ToggleSwitch : CheckBox
    {
        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Font = Ui.F(11.5f);
            AutoSize = true;
            Margin = new Padding(0, 0, Ui.Px(28), 0);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size t = TextRenderer.MeasureText(Text, Font);
            return new Size(Ui.Px(64) + t.Width + Ui.Px(8), Ui.Px(48));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int tw = Ui.Px(52), th = Ui.Px(30), y = (Height - th) / 2;
            var track = new Rectangle(0, y, tw, th);
            Color on = Enabled ? Ui.Accent : Color.FromArgb(40, 60, 110);
            Color off = Enabled ? Ui.SurfaceHi : Color.FromArgb(32, 38, 52);
            using (var path = Ui.Round(track, th / 2))
            using (var b = new SolidBrush(Checked ? on : off)) g.FillPath(b, path);
            int k = th - Ui.Px(8);
            int kx = Checked ? tw - k - Ui.Px(4) : Ui.Px(4);
            using (var b = new SolidBrush(Enabled ? Color.White : Color.FromArgb(150, 158, 175)))
                g.FillEllipse(b, kx, y + Ui.Px(4), k, k);
            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Ui.AccentHi, 2f)) g.DrawEllipse(pen, kx - 1, y + Ui.Px(4) - 1, k + 2, k + 2);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.Px(64), 0, Width - Ui.Px(64), Height),
                Enabled ? Ui.Text : Ui.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }

    /// <summary>Drop-down list restyled for the dark theme (still a ComboBox, so existing code works).</summary>
    class Picker : ComboBox
    {
        public Picker()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            Font = Ui.F(12f);
            BackColor = Ui.Field;
            ForeColor = Ui.Text;
            ItemHeight = Ui.Px(44);
            Cursor = Cursors.Hand;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Ui.AccentLo : Ui.Field)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                new Rectangle(e.Bounds.X + Ui.Px(14), e.Bounds.Y, e.Bounds.Width - Ui.Px(20), e.Bounds.Height), Ui.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0xF && !DesignMode && IsHandleCreated) PaintAll();   // WM_PAINT
        }

        void PaintAll()
        {
            try
            {
                using (var g = Graphics.FromHwnd(Handle))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
                    var r = new Rectangle(0, 0, Width - 1, Height - 1);
                    bool act = Focused || DroppedDown;
                    using (var path = Ui.Round(r, Ui.Px(12)))
                    using (var b = new SolidBrush(Ui.Field))
                    using (var pen = new Pen(act ? Ui.Accent : Ui.Border, act ? 2f : 1f))
                    {
                        g.FillPath(b, path);
                        g.DrawPath(pen, path);
                    }
                    string t = SelectedItem != null ? GetItemText(SelectedItem) : "";
                    Color fore = Enabled ? Ui.Text : Ui.Muted;
                    TextRenderer.DrawText(g, t, Font, new Rectangle(Ui.Px(16), 0, Width - Ui.Px(56), Height), fore,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    int cx = Width - Ui.Px(26), cy = Height / 2, s = Ui.Px(6);
                    using (var pen = new Pen(Enabled ? Ui.Muted : Ui.Border, 2.2f))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        g.DrawLines(pen, new[] { new Point(cx - s, cy - s / 2), new Point(cx, cy + s / 2), new Point(cx + s, cy - s / 2) });
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>Activity list: tall rows, timestamp dimmed.</summary>
    class LogList : ListBox
    {
        public LogList()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DrawMode = DrawMode.OwnerDrawFixed;
            BorderStyle = BorderStyle.None;
            BackColor = Ui.Card;
            ForeColor = Ui.Text;
            Font = Ui.F(10.5f);
            ItemHeight = Ui.Px(36);
            IntegralHeight = false;
            HorizontalScrollbar = true;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            using (var b = new SolidBrush(Ui.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index < 0 || e.Index >= Items.Count) return;
            string s = Convert.ToString(Items[e.Index]);
            string stamp = s.Length >= 8 ? s.Substring(0, 8) : s;
            string msg = s.Length > 8 ? s.Substring(8).TrimStart() : "";
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, stamp, Font, new Rectangle(e.Bounds.X + Ui.Px(6), e.Bounds.Y, Ui.Px(80), e.Bounds.Height), Ui.Muted, flags);
            TextRenderer.DrawText(e.Graphics, msg, Font, new Rectangle(e.Bounds.X + Ui.Px(92), e.Bounds.Y, 4000, e.Bounds.Height), Ui.Text, flags | TextFormatFlags.NoClipping);
        }
    }
}
