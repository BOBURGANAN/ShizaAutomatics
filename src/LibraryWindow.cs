using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Image = System.Drawing.Image;

namespace ShizaAutomatics;

internal sealed class LibraryWindow : Form
{
    private static readonly Size ThumbnailSize = new(148, 111);
    private static readonly Size PreviewSize = new(380, 286);
    private static readonly Color PreviewBackground = Color.FromArgb(45, 52, 61);
    private readonly string root;
    private readonly Dictionary<string, string[]> filesByCategory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Bitmap> previews = new(StringComparer.OrdinalIgnoreCase);
    private readonly TreeView categories = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly TextBox search = new() { Dock = DockStyle.Top, PlaceholderText = "Поиск по названию блока" };
    private readonly ImageList thumbnails = new() { ImageSize = ThumbnailSize, ColorDepth = ColorDepth.Depth32Bit };
    private readonly ListView blocks = new()
    {
        Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = false, HideSelection = false, ShowItemToolTips = true
    };
    private readonly PictureBox selectedPreview = new()
    {
        Dock = DockStyle.Fill, BackColor = PreviewBackground, SizeMode = PictureBoxSizeMode.Zoom
    };
    private readonly Label selectedTitle = new()
    {
        Dock = DockStyle.Top, Height = 52, Text = "Выберите блок", TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Label details = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button insert = new() { Text = "Вставить", Enabled = false, AutoSize = true, Dock = DockStyle.Right };
    private readonly Button cancel = new() { Text = "Закрыть", DialogResult = DialogResult.Cancel, AutoSize = true, Dock = DockStyle.Right };
    internal string? ChosenFile { get; private set; }

    internal LibraryWindow(string root)
    {
        this.root = root;
        Text = "ShizaAutomatics — Библиотека блоков";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1030, 540);
        MinimumSize = new Size(720, 390);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowIcon = false;
        MinimizeBox = false;
        CancelButton = cancel;
        AcceptButton = insert;

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Width = ClientSize.Width,
            SplitterDistance = 175, Panel1MinSize = 130, Panel2MinSize = 550
        };
        var content = new SplitContainer
        {
            Dock = DockStyle.Fill, Width = ClientSize.Width - split.SplitterDistance,
            SplitterDistance = 510, Panel1MinSize = 305, Panel2MinSize = 225, FixedPanel = FixedPanel.Panel2
        };
        var bottom = new Panel { Height = 42, Dock = DockStyle.Bottom, Padding = new Padding(6) };
        bottom.Controls.Add(details);
        bottom.Controls.Add(insert);
        bottom.Controls.Add(cancel);
        split.Panel1.Controls.Add(categories);
        content.Panel1.Controls.Add(blocks);
        content.Panel1.Controls.Add(search);
        content.Panel2.Padding = new Padding(10, 0, 10, 10);
        content.Panel2.Controls.Add(selectedPreview);
        content.Panel2.Controls.Add(selectedTitle);
        split.Panel2.Controls.Add(content);
        Controls.Add(split);
        Controls.Add(bottom);
        blocks.LargeImageList = thumbnails;
        _ = thumbnails.Handle;
        var directory = new DirectoryInfo(root);
        if (directory.Exists)
        {
            var tree = AddCategory(directory, "Все блоки");
            categories.Nodes.Add(tree);
            tree.Expand();
            categories.SelectedNode = tree;
        }
        categories.AfterSelect += (_, _) => RefreshBlocks();
        search.TextChanged += (_, _) => RefreshBlocks();
        blocks.SelectedIndexChanged += (_, _) => ShowSelection();
        blocks.ItemActivate += (_, _) => SelectBlock();
        insert.Click += (_, _) => SelectBlock();
        RefreshBlocks();
    }

    private TreeNode AddCategory(DirectoryInfo directory, string title)
    {
        var node = new TreeNode(title) { Tag = directory.FullName };
        filesByCategory[directory.FullName] = directory.EnumerateFiles("*.dwg")
            .Where(file => !file.Name.StartsWith(".shiza-", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.FullName).ToArray();
        foreach (var child in directory.EnumerateDirectories()
                     .Where(folder => (folder.Attributes & FileAttributes.ReparsePoint) == 0)
                     .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase))
            node.Nodes.Add(AddCategory(child, child.Name));
        return node;
    }

    private void RefreshBlocks()
    {
        var category = categories.SelectedNode?.Tag as string;
        var available = category == root ? filesByCategory.Values.SelectMany(files => files) :
            category != null && filesByCategory.TryGetValue(category, out var list) ? list : Array.Empty<string>();
        var query = search.Text.Trim();
        blocks.BeginUpdate();
        try
        {
            blocks.Items.Clear();
            ShowSelection();
            foreach (var file in available.Where(file =>
                         Path.GetFileNameWithoutExtension(file).Contains(query, StringComparison.CurrentCultureIgnoreCase))
                         .OrderBy(file => Path.GetFileNameWithoutExtension(file), StringComparer.CurrentCultureIgnoreCase))
            {
                if (!previews.ContainsKey(file))
                {
                    using var source = Preview(file);
                    var preview = FitPreview(source);
                    previews.Add(file, preview);
                    using var thumb = new Bitmap(preview, ThumbnailSize);
                    thumbnails.Images.Add(file, thumb);
                }
                blocks.Items.Add(new ListViewItem(Path.GetFileNameWithoutExtension(file))
                {
                    Tag = file, ImageKey = file, ToolTipText = Path.GetFileNameWithoutExtension(file)
                });
            }
            details.Text = blocks.Items.Count == 0 ? "В этой категории нет подходящих блоков." :
                $"Блоков: {blocks.Items.Count}";
        }
        finally
        {
            blocks.EndUpdate();
        }
    }

    private void ShowSelection()
    {
        var selected = blocks.SelectedItems.Count == 1 ? blocks.SelectedItems[0] : null;
        insert.Enabled = selected != null;
        selectedTitle.Text = selected?.Text ?? "Выберите блок";
        selectedPreview.Image = selected?.Tag is string file ? previews[file] : null;
    }

    private static Image? Preview(string file)
    {
        var png = Path.ChangeExtension(file, ".png");
        if (File.Exists(png))
        {
            try
            {
                using var image = Image.FromFile(png);
                return new Bitmap(image);
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (OutOfMemoryException) { } // GDI+ also uses this for invalid PNG data.
        }

        try
        {
            using var source = Addon.OpenSource(file);
            using var tx = source.TransactionManager.StartOpenCloseTransaction();
            var rootBlock = Addon.FindRootBlock(source, tx);
            using var rendered = RenderPreview(SymbolUtilityServices.GetBlockModelSpaceId(source));
            if (rendered != null && !ContentBounds(rendered).IsEmpty)
                return new Bitmap(rendered);
            if (!rootBlock.IsNull && tx.GetObject(rootBlock, OpenMode.ForRead) is BlockTableRecord block &&
                block.HasPreviewIcon)
            {
                using var icon = block.PreviewIcon;
                return new Bitmap(icon);
            }
            using var thumbnail = source.ThumbnailBitmap;
            return thumbnail == null ? null : new Bitmap(thumbnail);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private static Bitmap? RenderPreview(ObjectId record)
    {
        IntPtr handle = IntPtr.Zero;
        var previousDatabase = HostApplicationServices.WorkingDatabase;
        try
        {
            HostApplicationServices.WorkingDatabase = record.Database;
            var background = Autodesk.AutoCAD.Colors.Color.FromRgb(
                PreviewBackground.R, PreviewBackground.G, PreviewBackground.B);
            handle = Autodesk.AutoCAD.Internal.Utils.GetBlockImage(
                record, PreviewSize.Width, PreviewSize.Height, background);
            return handle == IntPtr.Zero ? null : Image.FromHbitmap(handle);
        }
        catch (System.Exception)
        {
            return null;
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = previousDatabase;
            if (handle != IntPtr.Zero)
                DeleteObject(handle);
        }
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    private static Bitmap FitPreview(Image? source)
    {
        var bitmap = new Bitmap(PreviewSize.Width, PreviewSize.Height);
        using var canvas = Graphics.FromImage(bitmap);
        if (source != null)
        {
            var size = FitSize(source.Size, PreviewSize);
            using var reduced = new Bitmap(source, size);
            var content = ContentBounds(reduced);
            if (!content.IsEmpty)
            {
                canvas.Clear(reduced.GetPixel(0, 0));
                canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var fitted = FitSize(content.Size, new Size(PreviewSize.Width - 28, PreviewSize.Height - 28));
                var target = new Rectangle((PreviewSize.Width - fitted.Width) / 2,
                    (PreviewSize.Height - fitted.Height) / 2, fitted.Width, fitted.Height);
                canvas.DrawImage(reduced, target, content, GraphicsUnit.Pixel);
                return bitmap;
            }
        }

        canvas.Clear(PreviewBackground);
        TextRenderer.DrawText(canvas, "Нет предпросмотра", SystemFonts.MessageBoxFont!,
            new Rectangle(Point.Empty, PreviewSize), Color.LightSteelBlue,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        return bitmap;
    }

    private static Rectangle ContentBounds(Bitmap image)
    {
        var background = image.GetPixel(0, 0);
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            var pixel = image.GetPixel(x, y);
            if (Math.Abs(pixel.A - background.A) < 20 &&
                Math.Abs(pixel.R - background.R) < 20 &&
                Math.Abs(pixel.G - background.G) < 20 &&
                Math.Abs(pixel.B - background.B) < 20)
                continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static Size FitSize(Size source, Size bounds)
    {
        var ratio = Math.Min((double)bounds.Width / source.Width, (double)bounds.Height / source.Height);
        return new Size(Math.Max(1, (int)(source.Width * ratio)), Math.Max(1, (int)(source.Height * ratio)));
    }

    private void SelectBlock()
    {
        if (blocks.SelectedItems.Count != 1)
            return;
        ChosenFile = (string)blocks.SelectedItems[0].Tag!;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            selectedPreview.Image = null;
            foreach (var preview in previews.Values)
                preview.Dispose();
            thumbnails.Dispose();
        }
        base.Dispose(disposing);
    }
}
