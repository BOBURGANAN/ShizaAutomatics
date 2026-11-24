using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using Acad = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(ShizaAutomatics.Addon))]
[assembly: CommandClass(typeof(ShizaAutomatics.Addon))]

namespace ShizaAutomatics;

public sealed partial class Addon : IExtensionApplication
{
    private const string ScaleKey = "ShizaAutomatics.Scale";
    private const string LibraryDefinitionKey = "ShizaAutomatics.LibraryDefinition";
    private const string SavedBlocksCategory = "Сохраненные";
    private const int DefaultScale = 100;
    private static readonly int[] Scales = { 1, 2, 5, 10, 20, 25, 50, 75, 100, 150, 200, 250, 500 };
    private static readonly string LibraryRoot = Path.Combine(
        Path.GetDirectoryName(typeof(Addon).Assembly.Location)!, "Library");
    private static readonly Dictionary<Document, PendingChange> Pending = new();
    private static Pane? scalePane;
    private static ContextMenuStrip? scaleMenu;
    private static Timer? selectionTimer;
    private static bool refreshNeeded = true;
    private static Document? shownDocument;
    private static ObjectId[] shownSelection = Array.Empty<ObjectId>();

    public void Initialize()
    {
        Acad.Idle += OnIdle;
        Acad.DocumentManager.DocumentActivated += OnActivated;
        Acad.DocumentManager.DocumentCreated += OnCreated;
        Acad.DocumentManager.DocumentToBeDestroyed += OnClosing;
        foreach (Document doc in Acad.DocumentManager)
            Subscribe(doc);
        selectionTimer = new Timer { Interval = 300 };
        selectionTimer.Tick += OnSelectionTimer;
        selectionTimer.Start();
    }

    public void Terminate()
    {
        Acad.Idle -= OnIdle;
        Acad.DocumentManager.DocumentActivated -= OnActivated;
        Acad.DocumentManager.DocumentCreated -= OnCreated;
        Acad.DocumentManager.DocumentToBeDestroyed -= OnClosing;
        foreach (Document doc in Acad.DocumentManager)
            Unsubscribe(doc);
        if (selectionTimer != null)
        {
            selectionTimer.Stop();
            selectionTimer.Tick -= OnSelectionTimer;
            selectionTimer.Dispose();
            selectionTimer = null;
        }
        if (scalePane != null)
        {
            scalePane.MouseDown -= OnScaleClick;
            Acad.StatusBar.Panes.Remove(scalePane);
            Acad.StatusBar.Update();
            scalePane.Dispose();
            scalePane = null;
        }
        scaleMenu?.Dispose();
        Pending.Clear();
    }

    private static void Subscribe(Document doc)
    {
        doc.ImpliedSelectionChanged += OnSelectionChanged;
        doc.CommandEnded += OnCommandEnded;
        doc.LispEnded += OnSelectionChanged;
    }

    private static void Unsubscribe(Document doc)
    {
        doc.ImpliedSelectionChanged -= OnSelectionChanged;
        doc.CommandEnded -= OnCommandEnded;
        doc.LispEnded -= OnSelectionChanged;
    }

    private static void OnActivated(object sender, DocumentCollectionEventArgs args) => refreshNeeded = true;
    private static void OnCreated(object sender, DocumentCollectionEventArgs args) => Subscribe(args.Document);
    private static void OnClosing(object sender, DocumentCollectionEventArgs args)
    {
        Unsubscribe(args.Document);
        Pending.Remove(args.Document);
        refreshNeeded = true;
    }
    private static void OnSelectionChanged(object? sender, EventArgs args) => refreshNeeded = true;
    private static void OnCommandEnded(object sender, CommandEventArgs args) => refreshNeeded = true;

    private static void OnIdle(object? sender, EventArgs args) => RefreshPane(false);
    private static void OnSelectionTimer(object? sender, EventArgs args) => RefreshPane(true);

    private static void RefreshPane(bool checkSelection)
    {
        var doc = Acad.DocumentManager.MdiActiveDocument;
        if ((!refreshNeeded && !checkSelection) || doc == null || !doc.Editor.IsQuiescent)
            return;

        try
        {
            var ids = SelectionIds(doc);
            if (!refreshNeeded && doc == shownDocument && ids.SequenceEqual(shownSelection))
                return;
            refreshNeeded = false;
            if (scalePane == null)
            {
                scalePane = new Pane
                {
                    Style = PaneStyles.PopUp,
                    ToolTipText = "ShizaAutomatics — масштаб оформления",
                    MinimumWidth = 120,
                    Enabled = true,
                    Visible = true
                };
                scalePane.MouseDown += OnScaleClick;
                Acad.StatusBar.Panes.Insert(0, scalePane);
            }

            var selection = ReadSelection(doc, ids);
            var caption = selection.Caption;
            if (scalePane.Text != caption)
            {
                scalePane.Text = caption;
                Acad.StatusBar.Update();
            }
            shownDocument = doc;
            shownSelection = ids;
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\nShizaAutomatics: " + ex.Message);
        }
    }

    private static void OnScaleClick(object? sender, EventArgs args)
    {
        var doc = Acad.DocumentManager.MdiActiveDocument;
        if (doc == null || !doc.Editor.IsQuiescent)
            return;

        try
        {
            var selection = ReadSelection(doc);
            scaleMenu?.Dispose();
            scaleMenu = new ContextMenuStrip();
            foreach (var scale in Scales)
            {
                var item = new ToolStripMenuItem($"1:{scale}") { Checked = selection.DisplayScale == scale };
                item.Click += (_, _) => QueueScale(doc, selection, scale);
                scaleMenu.Items.Add(item);
            }
            scaleMenu.Items.Add(new ToolStripSeparator());
            var custom = new ToolStripMenuItem("Другой масштаб…");
            custom.Click += (_, _) => QueueScale(doc, selection, null);
            scaleMenu.Items.Add(custom);
            scaleMenu.Show(Cursor.Position);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\nShizaAutomatics: " + ex.Message);
        }
    }

    private static void QueueScale(Document doc, ScaleSelection selection, int? scale)
    {
        if (Acad.DocumentManager.MdiActiveDocument != doc || !doc.Editor.IsQuiescent)
            return;
        if (selection.HasSelection && selection.Objects.Length == 0)
        {
            doc.Editor.WriteMessage("\nВыберите размер, выноску, текст или блок оформления ShizaAutomatics.");
            return;
        }
        Pending[doc] = new PendingChange(selection.Objects, scale);
        doc.SendStringToExecute("SHIZAAPPLYSCALE ", false, false, false);
    }

    [CommandMethod("SHIZASCALE", CommandFlags.UsePickSet)]
    public void ScaleCommand()
    {
        var doc = Acad.DocumentManager.MdiActiveDocument;
        var selection = ReadSelection(doc);
        if (selection.HasSelection && selection.Objects.Length == 0)
        {
            doc.Editor.WriteMessage("\nВыберите размер, выноску, текст или блок оформления ShizaAutomatics.");
            return;
        }
        ApplyScale(doc, new PendingChange(selection.Objects, null));
    }

    [CommandMethod("SHIZAAPPLYSCALE", CommandFlags.UsePickSet)]
    public void ApplyQueuedScale()
    {
        var doc = Acad.DocumentManager.MdiActiveDocument;
        if (Pending.Remove(doc, out var change))
            ApplyScale(doc, change);
    }

    private static void ApplyScale(Document doc, PendingChange change)
    {
        try
        {
            var db = doc.Database;
            var scale = change.Scale;
            if (scale == null)
            {
                int current;
                using (var read = db.TransactionManager.StartOpenCloseTransaction())
                    current = ReadDocumentScale(db, read);
                var options = new PromptIntegerOptions("\nЗнаменатель масштаба 1:")
                {
                    DefaultValue = current, UseDefaultValue = true, LowerLimit = 1, UpperLimit = 10000
                };
                var result = doc.Editor.GetInteger(options);
                if (result.Status != PromptStatus.OK)
                    return;
                scale = result.Value;
            }

            using var tx = db.TransactionManager.StartTransaction();
            if (change.Objects.Length == 0)
            {
                SetNativeAnnotationDefaults(db, tx, scale.Value);
                var dictionary = (DBDictionary)tx.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                WriteScale(dictionary, tx, scale.Value);
            }
            foreach (var id in change.Objects)
            {
                if (id.IsErased || !id.IsValid)
                    throw new InvalidOperationException("Выбранный объект больше недоступен.");
                var entity = (Entity)tx.GetObject(id, OpenMode.ForWrite);
                if (entity is not BlockReference block)
                {
                    ResizeNativeAnnotation(entity, db, tx, scale.Value);
                    continue;
                }
                var previous = ReadBlockScale(block, tx);
                if (previous is not > 0)
                    throw new InvalidOperationException("Объект больше не является блоком оформления.");
                if (previous == scale.Value)
                    continue;
                ResizeBlock(block, previous.Value, scale.Value);
                WriteBlockScale(block, tx, scale.Value);
            }
            tx.Commit();
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\nShizaAutomatics: " + ex.Message);
        }
        finally
        {
            refreshNeeded = true;
        }
    }

    private static void ResizeBlock(BlockReference block, int previous, int next)
    {
        var factor = (double)next / previous;
        var parameters = block.IsDynamicBlock
            ? block.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>()
            : Enumerable.Empty<DynamicBlockReferenceProperty>();
        var axisLength = parameters.FirstOrDefault(p => !p.ReadOnly && p.PropertyName == "Длина линии оси");
        var length = axisLength?.Value as double?;
        block.TransformBy(Matrix3d.Scaling(factor, block.Position));
        if (length.HasValue)
        {
            if (axisLength!.Value is double scaled && !SameLength(scaled, length.Value))
                axisLength.Value = length.Value;
            if (axisLength.Value is not double updated ||
                !SameLength(updated, length.Value))
                throw new InvalidOperationException("Этот масштаб не позволяет сохранить заданную длину оси.");
        }
    }

    private static bool SameLength(double a, double b) => Math.Abs(a - b) <= 1e-8 * Math.Max(1, Math.Abs(b));

    private static ScaleSelection ReadSelection(Document doc)
        => ReadSelection(doc, SelectionIds(doc));

    private static ObjectId[] SelectionIds(Document doc)
    {
        var picked = doc.Editor.SelectImplied();
        return picked.Status == PromptStatus.OK ? picked.Value.GetObjectIds() : Array.Empty<ObjectId>();
    }

    private static ScaleSelection ReadSelection(Document doc, ObjectId[] ids)
    {
        using var tx = doc.Database.TransactionManager.StartOpenCloseTransaction();
        var objects = new List<ObjectId>();
        var scales = new HashSet<int>();
        var hasSubsystem = false;
        foreach (var id in ids)
        {
            if (id.IsErased || tx.GetObject(id, OpenMode.ForRead) is not Entity entity)
                continue;
            if (entity is BlockReference block)
            {
                var scale = ReadBlockScale(block, tx);
                if (scale is > 0)
                {
                    objects.Add(id);
                    scales.Add(scale.Value);
                }
                hasSubsystem |= scale == 0;
            }
            else if (IsNativeAnnotation(entity))
            {
                objects.Add(id);
                scales.Add(NativeAnnotationScale(entity, doc.Database, tx));
            }
        }
        return new ScaleSelection(ids.Length > 0, hasSubsystem && objects.Count == 0, objects.ToArray(), scales.Count switch
        {
            0 => ReadDocumentScale(doc.Database, tx),
            1 => scales.Single(),
            _ => null
        });
    }

    private static int ReadDocumentScale(Database db, Transaction tx) =>
        ReadScale((DBDictionary)tx.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead), tx) ?? DefaultScale;

    private static int? ReadBlockScale(BlockReference block, Transaction tx) => ReadObjectScale(block, tx);

    private static int? ReadObjectScale(DBObject obj, Transaction tx) =>
        obj.ExtensionDictionary.IsNull ? null :
        ReadScale((DBDictionary)tx.GetObject(obj.ExtensionDictionary, OpenMode.ForRead), tx);

    private static int? ReadScale(DBDictionary dictionary, Transaction tx)
    {
        if (!dictionary.Contains(ScaleKey))
            return null;
        var record = (Xrecord)tx.GetObject(dictionary.GetAt(ScaleKey), OpenMode.ForRead);
        using var data = record.Data;
        return data?.AsArray().FirstOrDefault().Value is int scale && scale >= 0 ? scale : null;
    }

    private static void WriteBlockScale(BlockReference block, Transaction tx, int scale) => WriteObjectScale(block, tx, scale);

    private static void WriteObjectScale(DBObject obj, Transaction tx, int scale)
    {
        if (obj.ExtensionDictionary.IsNull)
            obj.CreateExtensionDictionary();
        WriteScale((DBDictionary)tx.GetObject(obj.ExtensionDictionary, OpenMode.ForRead), tx, scale);
    }

    private static void WriteScale(DBDictionary dictionary, Transaction tx, int scale)
    {
        using var values = new ResultBuffer(new TypedValue((int)DxfCode.Int32, scale));
        if (dictionary.Contains(ScaleKey))
        {
            var record = (Xrecord)tx.GetObject(dictionary.GetAt(ScaleKey), OpenMode.ForWrite);
            record.Data = values;
            return;
        }
        tx.GetObject(dictionary.ObjectId, OpenMode.ForWrite);
        var added = new Xrecord { Data = values };
        dictionary.SetAt(ScaleKey, added);
        tx.AddNewlyCreatedDBObject(added, true);
    }

    [CommandMethod("SHIZALIBRARY")]
    public void OpenLibrary()
    {
        var doc = Acad.DocumentManager.MdiActiveDocument;
        try
        {
            using var dialog = new LibraryWindow(LibraryRoot);
            if (Acad.ShowModalDialog(dialog) != DialogResult.OK || dialog.ChosenFile == null)
                return;
            PromptForInsertion(doc, dialog.ChosenFile);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\nShizaAutomatics: " + ex.Message);
        }
        finally
        {
            refreshNeeded = true;
        }
    }

    [CommandMethod("SHIZASAVEBLOCK", CommandFlags.UsePickSet | CommandFlags.NoBlockEditor)]
    public void SaveLibraryBlock()
    {
        var doc = Acad.DocumentManager.MdiActiveDocument;
        var editor = doc.Editor;
        var db = doc.Database;
        try
        {
            var selected = SelectionIds(doc);
            ObjectId id = ObjectId.Null;
            if (selected.Length == 1)
            {
                using var read = db.TransactionManager.StartOpenCloseTransaction();
                if (read.GetObject(selected[0], OpenMode.ForRead) is BlockReference)
                    id = selected[0];
            }
            if (id.IsNull)
            {
                var options = new PromptEntityOptions("\nВыберите блок для сохранения: ");
                options.SetRejectMessage("\nВыберите вхождение блока.");
                options.AddAllowedClass(typeof(BlockReference), false);
                var picked = editor.GetEntity(options);
                if (picked.Status != PromptStatus.OK)
                    return;
                id = picked.ObjectId;
            }

            string title;
            Point3d origin;
            using (var read = db.TransactionManager.StartOpenCloseTransaction())
            {
                var reference = (BlockReference)read.GetObject(id, OpenMode.ForRead);
                var definitionId = reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
                var definition = (BlockTableRecord)read.GetObject(definitionId, OpenMode.ForRead);
                if (definition.IsFromExternalReference)
                    throw new InvalidOperationException("Внешнюю ссылку нельзя сохранить как блок библиотеки.");
                title = SavedBlockFileStem(definition.Name);
                origin = reference.Position;
            }

            var folder = Directory.CreateDirectory(Path.Combine(LibraryRoot, SavedBlocksCategory)).FullName;
            var temporary = Path.Combine(folder, $".shiza-{Guid.NewGuid():N}.dwg");
            try
            {
                using (var output = db.Wblock(new ObjectIdCollection(new[] { id }), origin))
                {
                    output.Insunits = db.Insunits;
                    output.SaveAs(temporary, DwgVersion.Current);
                }

                for (int index = 1; ; index++)
                {
                    var suffix = index == 1 ? "" : "_" + index;
                    var file = Path.Combine(folder, title + suffix + ".dwg");
                    try
                    {
                        File.Move(temporary, file);
                        editor.WriteMessage($"\nБлок сохранён: {Path.GetRelativePath(LibraryRoot, file)}");
                        break;
                    }
                    catch (IOException) when (File.Exists(file) || Directory.Exists(file))
                    {
                        // Another file can take this name while the drawing is being exported.
                    }
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage("\nНе удалось сохранить блок: " + ex.Message);
        }
    }

    private static string SavedBlockFileStem(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (result.Length > 120)
            result = result[..(char.IsHighSurrogate(result[119]) ? 119 : 120)].TrimEnd(' ', '.');
        if (result.Length == 0)
            return "Блок";
        var first = result.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (first is "CON" or "PRN" or "AUX" or "NUL" ||
            first.Length == 4 && (first.StartsWith("COM", StringComparison.Ordinal) ||
                                  first.StartsWith("LPT", StringComparison.Ordinal)) &&
            "123456789¹²³".Contains(first[3]))
            return "_" + result;
        return result;
    }

    [CommandMethod("-SHIZALIBRARY")]
    public void OpenLibraryAtCommandLine()
    {
        var doc = Acad.DocumentManager.MdiActiveDocument;
        var options = new PromptStringOptions("\nПуть DWG относительно папки Library: ") { AllowSpaces = true };
        var result = doc.Editor.GetString(options);
        if (result.Status != PromptStatus.OK)
            return;
        try
        {
            var file = Path.GetFullPath(result.StringResult.Trim('"'), LibraryRoot);
            var relative = Path.GetRelativePath(LibraryRoot, file);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathRooted(relative) || !Path.GetExtension(file).Equals(".dwg", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(file))
                throw new InvalidOperationException("В библиотеке нет указанного DWG.");
            PromptForInsertion(doc, file);
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\nShizaAutomatics: " + ex.Message);
        }
        finally
        {
            refreshNeeded = true;
        }
    }

    private static void PromptForInsertion(Document doc, string file)
    {
        var point = doc.Editor.GetPoint("\nУкажите точку вставки: ");
        if (point.Status == PromptStatus.OK)
            InsertLibraryBlock(doc, file, point.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem));
    }

    private static void InsertLibraryBlock(Document doc, string file, Point3d point)
    {
        var relative = Path.GetRelativePath(LibraryRoot, file);
        var category = relative.Split(Path.DirectorySeparatorChar)[0];
        var isAnnotation = category.Equals("Оформление", StringComparison.OrdinalIgnoreCase);
        var isSaved = category.Equals(SavedBlocksCategory, StringComparison.OrdinalIgnoreCase);
        using var saved = isSaved ? OpenSource(file) : null;
        using var savedTx = saved?.TransactionManager.StartOpenCloseTransaction();
        var savedReference = saved != null && savedTx != null ? FindRootReference(saved, savedTx) : null;
        var db = doc.Database;
        using var tx = db.TransactionManager.StartTransaction();
        var scale = isAnnotation ? ReadDocumentScale(db, tx) : 1;
        var definition = ImportDefinition(db, tx, file, relative);
        var space = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
        var block = new BlockReference(point, definition);
        block.SetDatabaseDefaults(db);
        block.ScaleFactors = savedReference?.ScaleFactors ?? new Scale3d(scale);
        if (savedReference != null)
        {
            block.Normal = savedReference.Normal;
            block.Rotation = savedReference.Rotation;
        }
        space.AppendEntity(block);
        tx.AddNewlyCreatedDBObject(block, true);

        var record = (BlockTableRecord)tx.GetObject(definition, OpenMode.ForRead);
        foreach (ObjectId id in record)
        {
            if (tx.GetObject(id, OpenMode.ForRead) is not AttributeDefinition attr || attr.Constant)
                continue;
            var attribute = new AttributeReference();
            attribute.SetAttributeFromBlock(attr, block.BlockTransform);
            block.AttributeCollection.AppendAttribute(attribute);
            tx.AddNewlyCreatedDBObject(attribute, true);
            if (savedReference != null && savedTx != null)
            {
                var savedAttribute = savedReference.AttributeCollection.Cast<ObjectId>()
                    .Select(key => savedTx.GetObject(key, OpenMode.ForRead) as AttributeReference)
                    .FirstOrDefault(value => value != null && value.Tag.Equals(attr.Tag, StringComparison.OrdinalIgnoreCase));
                if (savedAttribute != null)
                {
                    if (attribute.IsMTextAttribute && savedAttribute.IsMTextAttribute)
                    {
                        using var text = attribute.MTextAttribute;
                        using var savedText = savedAttribute.MTextAttribute;
                        text.Contents = savedText.Contents;
                        attribute.MTextAttribute = text;
                    }
                    else
                    {
                        attribute.TextString = savedAttribute.TextString;
                        attribute.AdjustAlignment(db);
                    }
                }
            }
        }
        if (savedReference != null)
        {
            var targetParameters = EditableParameters(block).ToLookup(property => property.PropertyName);
            foreach (var property in EditableParameters(savedReference))
            {
                var matches = targetParameters[property.PropertyName];
                if (matches.Count() != 1)
                    continue;
                var target = matches.First();
                if (!Equals(target.Value, property.Value))
                    target.Value = property.Value;
            }
        }
        WriteBlockScale(block, tx, isAnnotation ? scale : 0);
        tx.Commit();
        var title = Path.GetFileNameWithoutExtension(file);
        doc.Editor.WriteMessage(savedReference != null ? $"\n{title} вставлен." : $"\n{title} вставлен в масштабе 1:{scale}.");
    }

    internal static ObjectId FindRootBlock(Database source, Transaction tx)
    {
        var reference = FindRootReference(source, tx);
        return reference == null ? ObjectId.Null :
            reference.IsDynamicBlock ? reference.DynamicBlockTableRecord : reference.BlockTableRecord;
    }

    private static BlockReference? FindRootReference(Database source, Transaction tx)
    {
        var table = (BlockTable)tx.GetObject(source.BlockTableId, OpenMode.ForRead);
        var model = (BlockTableRecord)tx.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        var modelIds = model.Cast<ObjectId>().ToArray();
        return modelIds.Length == 1 ? tx.GetObject(modelIds[0], OpenMode.ForRead) as BlockReference : null;
    }

    internal static Database OpenSource(string file)
    {
        var source = new Database(false, true);
        try
        {
            source.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, false, null);
            source.CloseInput(true);
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static ObjectId ImportDefinition(Database destination, Transaction tx, string file, string relative)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var fileHash = Convert.ToHexString(SHA256.HashData(stream));
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(relative.ToUpperInvariant() + "\n" + fileHash)));
        var title = Path.GetFileNameWithoutExtension(file).Trim();
        var extendedNames = Convert.ToInt32(Acad.GetSystemVariable("EXTNAMES")) != 0;
        var baseName = extendedNames
            ? SymbolUtilityServices.RepairSymbolName(title, false)
            : SymbolUtilityServices.RepairPreExtendedSymbolName(title, false);
        var maxLength = extendedNames ? 255 : 31;
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "BLOCK";
        var table = (BlockTable)tx.GetObject(destination.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId id in table)
        {
            var imported = (BlockTableRecord)tx.GetObject(id, OpenMode.ForRead);
            if (!imported.IsAnonymous && ReadLibraryDefinition(imported, tx) == key)
                return id;
        }

        using var source = OpenSource(file);
        ObjectId root;
        string name;
        using (var edit = source.TransactionManager.StartTransaction())
        {
            root = FindRootBlock(source, edit);
            var sourceTable = (BlockTable)edit.GetObject(source.BlockTableId, OpenMode.ForRead);
            int index = 1;
            do
            {
                var suffix = index == 1 ? "" : "_" + index;
                name = baseName[..Math.Min(baseName.Length, maxLength - suffix.Length)] + suffix;
                index++;
            } while (table.Has(name) || (!root.IsNull && sourceTable.Has(name) && sourceTable[name] != root));
            if (!root.IsNull)
            {
                var sourceRecord = (BlockTableRecord)edit.GetObject(root, OpenMode.ForWrite);
                if (sourceRecord.Name != name)
                    sourceRecord.Name = name;
            }
            edit.Commit();
        }
        ObjectId result;
        if (root.IsNull)
            result = destination.Insert(name, source, true);
        else
        {
            var mapping = new IdMapping();
            source.WblockCloneObjects(new ObjectIdCollection(new[] { root }), destination.BlockTableId,
                mapping, DuplicateRecordCloning.Ignore, false);
            result = mapping[root].Value;
        }
        var record = (BlockTableRecord)tx.GetObject(result, OpenMode.ForWrite);
        if (record.ExtensionDictionary.IsNull)
            record.CreateExtensionDictionary();
        var dictionary = (DBDictionary)tx.GetObject(record.ExtensionDictionary, OpenMode.ForWrite);
        using var data = new ResultBuffer(new TypedValue((int)DxfCode.Text, key));
        if (dictionary.Contains(LibraryDefinitionKey))
        {
            var existing = (Xrecord)tx.GetObject(dictionary.GetAt(LibraryDefinitionKey), OpenMode.ForWrite);
            existing.Data = data;
        }
        else
        {
            var added = new Xrecord { Data = data };
            dictionary.SetAt(LibraryDefinitionKey, added);
            tx.AddNewlyCreatedDBObject(added, true);
        }
        return result;
    }

    private static string? ReadLibraryDefinition(BlockTableRecord record, Transaction tx)
    {
        if (record.ExtensionDictionary.IsNull)
            return null;
        var dictionary = (DBDictionary)tx.GetObject(record.ExtensionDictionary, OpenMode.ForRead);
        if (!dictionary.Contains(LibraryDefinitionKey))
            return null;
        var metadata = (Xrecord)tx.GetObject(dictionary.GetAt(LibraryDefinitionKey), OpenMode.ForRead);
        using var data = metadata.Data;
        return data?.AsArray().FirstOrDefault().Value as string;
    }

    private sealed record PendingChange(ObjectId[] Objects, int? Scale);
    private sealed record ScaleSelection(bool HasSelection, bool FixedScale, ObjectId[] Objects, int? DisplayScale)
    {
        public string Caption => FixedScale ? "Подсистема 1:1" :
            DisplayScale is int scale ? $"Оформление 1:{scale} ▾" : "Оформление: разные ▾";
    }
}
