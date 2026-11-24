using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Acad = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ShizaAutomatics;

public sealed partial class Addon
{
    private const string ElevationTag = "ОТМЕТКА";

    [CommandMethod("SHIZAALIGNLEVELS")]
    public void AlignElevations()
    {
        var doc = Acad.DocumentManager.MdiActiveDocument;
        var editor = doc.Editor;
        var options = new PromptEntityOptions("\nВыберите опорную высотную отметку: ");
        options.SetRejectMessage("\nВыберите блок высотной отметки.");
        options.AddAllowedClass(typeof(BlockReference), true);
        var picked = editor.GetEntity(options);
        if (picked.Status != PromptStatus.OK)
            return;

        var database = doc.Database;
        try
        {
            using var tx = database.TransactionManager.StartTransaction();
            var source = (BlockReference)tx.GetObject(picked.ObjectId, OpenMode.ForRead);
            var sourceAttribute = FindElevationAttribute(source, tx);
            if (sourceAttribute == null)
            {
                editor.WriteMessage("\nУ опорного блока нет атрибута ОТМЕТКА.");
                return;
            }
            var selection = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nВыберите высотные отметки для выравнивания: "
            }, new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") }));
            if (selection.Status != PromptStatus.OK)
                return;

            var sourceParameters = EditableParameters(source).ToArray();
            var sourceScale = ReadBlockScale(source, tx);
            var factor = Math.Abs(source.ScaleFactors.X);
            if (sourceScale == null && factor is >= 1.0 and <= 10000.0 &&
                Math.Abs(factor - Math.Round(factor)) < 1e-9)
                sourceScale = (int)Math.Round(factor);

            int aligned = 0;
            int skipped = 0;
            foreach (var id in selection.Value.GetObjectIds())
            {
                if (id == source.ObjectId)
                    continue;
                using var targetTx = database.TransactionManager.StartTransaction();
                try
                {
                    var target = (BlockReference)targetTx.GetObject(id, OpenMode.ForRead);
                    var targetAttribute = FindElevationAttribute(target, targetTx);
                    var targetParameters = EditableParameters(target).ToArray();
                    var layer = (LayerTableRecord)targetTx.GetObject(target.LayerId, OpenMode.ForRead);
                    if (targetAttribute == null || layer.IsLocked ||
                        sourceAttribute.IsMTextAttribute != targetAttribute.IsMTextAttribute ||
                        !CompatibleParameters(sourceParameters, targetParameters))
                    {
                        skipped++;
                        continue;
                    }

                    targetTx.GetObject(id, OpenMode.ForWrite);
                    targetTx.GetObject(targetAttribute.ObjectId, OpenMode.ForWrite);
                    AlignElevation(target, targetAttribute, targetParameters, source, sourceAttribute, sourceParameters);
                    if (sourceScale.HasValue)
                        WriteBlockScale(target, targetTx, sourceScale.Value);
                    else
                        ClearBlockScale(target, targetTx);
                    targetTx.Commit();
                    aligned++;
                }
                catch (System.Exception ex)
                {
                    skipped++;
                    editor.WriteMessage($"\nОтметка {id.Handle}: {ex.Message}");
                }
            }
            tx.Commit();
            editor.WriteMessage($"\nВыровнено: {aligned}. Пропущено: {skipped}.");
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage("\nShizaAutomatics: " + ex.Message);
        }
        finally
        {
            refreshNeeded = true;
        }
    }

    private static AttributeReference? FindElevationAttribute(BlockReference block, Transaction tx)
    {
        foreach (ObjectId id in block.AttributeCollection)
        {
            var attr = (AttributeReference)tx.GetObject(id, OpenMode.ForRead);
            if (attr.Tag.Equals(ElevationTag, StringComparison.OrdinalIgnoreCase))
                return attr;
        }
        return null;
    }

    private static IEnumerable<DynamicBlockReferenceProperty> EditableParameters(BlockReference block) =>
        block.IsDynamicBlock
            ? block.DynamicBlockReferencePropertyCollection.Cast<DynamicBlockReferenceProperty>()
                .Where(property => !property.ReadOnly)
            : Enumerable.Empty<DynamicBlockReferenceProperty>();

    private static bool CompatibleParameters(DynamicBlockReferenceProperty[] a, DynamicBlockReferenceProperty[] b) =>
        a.Length == b.Length && a.All(source => b.Count(target =>
            target.PropertyName == source.PropertyName && target.PropertyTypeCode == source.PropertyTypeCode) == 1);

    private static void AlignElevation(
        BlockReference target, AttributeReference targetAttribute, DynamicBlockReferenceProperty[] targetParameters,
        BlockReference source, AttributeReference sourceAttribute, DynamicBlockReferenceProperty[] sourceParameters)
    {
        var verticalOffset = target.Position.Y - source.Position.Y;
        var displacement = new Vector3d(0, verticalOffset, 0);
        var attributeValue = targetAttribute.TextString;
        target.SetPropertiesFrom(source);
        target.Normal = source.Normal;
        target.Rotation = source.Rotation;
        target.ScaleFactors = source.ScaleFactors;
        target.Position = source.Position + displacement;
        foreach (var property in sourceParameters)
        {
            var destination = targetParameters.Single(p => p.PropertyName == property.PropertyName);
            if (!Equals(destination.Value, property.Value))
                destination.Value = property.Value;
        }
        foreach (var property in sourceParameters)
        {
            var destination = targetParameters.Single(p => p.PropertyName == property.PropertyName);
            var equal = property.Value is double a && destination.Value is double b
                ? SameLength(a, b)
                : Equals(destination.Value, property.Value);
            if (!equal)
                throw new InvalidOperationException($"Не удалось перенести параметр «{property.PropertyName}».");
        }

        targetAttribute.SetPropertiesFrom(sourceAttribute);
        targetAttribute.Normal = sourceAttribute.Normal;
        targetAttribute.TextStyleId = sourceAttribute.TextStyleId;
        targetAttribute.Height = sourceAttribute.Height;
        targetAttribute.WidthFactor = sourceAttribute.WidthFactor;
        targetAttribute.Oblique = sourceAttribute.Oblique;
        targetAttribute.IsMirroredInX = sourceAttribute.IsMirroredInX;
        targetAttribute.IsMirroredInY = sourceAttribute.IsMirroredInY;
        targetAttribute.Rotation = sourceAttribute.Rotation;
        targetAttribute.Justify = sourceAttribute.Justify;
        targetAttribute.Position = sourceAttribute.Position + displacement;
        if (!sourceAttribute.IsDefaultAlignment)
            targetAttribute.AlignmentPoint = sourceAttribute.AlignmentPoint + displacement;
        targetAttribute.Invisible = sourceAttribute.Invisible;
        if (sourceAttribute.IsMTextAttribute)
        {
            using var oldText = targetAttribute.MTextAttribute;
            using var format = sourceAttribute.MTextAttribute;
            format.Location += displacement;
            format.Contents = oldText.Contents;
            targetAttribute.MTextAttribute = format;
        }
        else
        {
            targetAttribute.TextString = attributeValue;
            targetAttribute.AdjustAlignment(target.Database);
        }
        target.RecordGraphicsModified(true);
    }

    private static void ClearBlockScale(BlockReference block, Transaction tx)
    {
        if (block.ExtensionDictionary.IsNull)
            return;
        var dictionary = (DBDictionary)tx.GetObject(block.ExtensionDictionary, OpenMode.ForRead);
        if (!dictionary.Contains(ScaleKey))
            return;
        var id = dictionary.GetAt(ScaleKey);
        tx.GetObject(dictionary.ObjectId, OpenMode.ForWrite);
        dictionary.Remove(ScaleKey);
        tx.GetObject(id, OpenMode.ForWrite).Erase();
    }
}
