using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

internal static class GenerateAppIcon
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
            return 2;

        using (Bitmap bitmap = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using (SolidBrush background = new SolidBrush(Color.FromArgb(255, 11, 18, 32)))
                graphics.FillEllipse(background, 2, 2, 60, 60);

            using (Pen outline = new Pen(Color.FromArgb(255, 238, 244, 255), 4.2f))
            {
                outline.LineJoin = LineJoin.Round;
                graphics.DrawRoundedRectangle(outline, new RectangleF(13, 21, 35, 24), 5);
                graphics.DrawLine(outline, 51, 28, 51, 38);
            }
            using (SolidBrush level = new SolidBrush(Color.FromArgb(255, 55, 206, 194)))
                graphics.FillRoundedRectangle(level, new RectangleF(18, 26, 24, 14), 3);

            IntPtr handle = bitmap.GetHicon();
            try
            {
                using (Icon icon = (Icon)Icon.FromHandle(handle).Clone())
                using (FileStream stream = File.Create(args[0]))
                    icon.Save(stream);
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        return 0;
    }

    private static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF rectangle, float radius)
    {
        using (GraphicsPath path = RoundedPath(rectangle, radius))
            graphics.DrawPath(pen, path);
    }

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF rectangle, float radius)
    {
        using (GraphicsPath path = RoundedPath(rectangle, radius))
            graphics.FillPath(brush, path);
    }

    private static GraphicsPath RoundedPath(RectangleF rectangle, float radius)
    {
        float diameter = radius * 2;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
