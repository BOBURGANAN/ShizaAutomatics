using System;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Acad = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ShizaAutomatics;

public sealed partial class Addon
{
    private const string AnnotationScalesCollection = "ACDB_ANNOTATIONSCALES";
    private const string TextStyleSourceKey = "ShizaAutomatics.TextStyleSource";

    private static bool IsNativeAnnotation(Entity entity) =>
        entity is Dimension or MLeader or Leader or MText ||
        entity is DBText and not AttributeReference and not AttributeDefinition;

    private static void SetNativeAnnotationDefaults(Database db, Transaction tx, int scale)
    {
        var dimensionStyle = tx.GetObject(db.Dimstyle, OpenMode.ForRead);
        var leaderStyle = tx.GetObject(db.MLeaderstyle, OpenMode.ForRead);
        var textStyle = (TextStyleTableRecord)tx.GetObject(db.Textstyle, OpenMode.ForRead);
        var annotativeDimensions = dimensionStyle.Annotative == AnnotativeStates.True;
        var annotativeLeaders = leaderStyle.Annotative == AnnotativeStates.True;
        var annotativeText = textStyle.Annotative == AnnotativeStates.True;

        if (!annotativeDimensions)
            db.Dimscale = scale;
        db.Dimgap = 1;
        if (!annotativeLeaders)
            db.MLeaderscale = scale;
        if (!annotativeText)
            SetTextDefaults(db, tx, textStyle, scale);

        if (annotativeDimensions || annotativeLeaders || annotativeText)
        {
            var context = GetAnnotationScale(db, scale);
            var previousAutoScale = Acad.GetSystemVariable("ANNOAUTOSCALE");
            if (Convert.ToInt32(previousAutoScale) <= 0)
            {
                db.Cannoscale = context;
                return;
            }
            // CANNOSCALE sets the context for new objects. Keep the scale list of existing
            // annotative objects independent even when AutoScale is enabled in this profile.
            Acad.SetSystemVariable("ANNOAUTOSCALE", (short)0);
            try { db.Cannoscale = context; }
            finally { Acad.SetSystemVariable("ANNOAUTOSCALE", previousAutoScale); }
        }
    }

    private static void SetTextDefaults(Database db, Transaction tx, TextStyleTableRecord current, int scale)
    {
        var currentScale = ReadObjectScale(current, tx) ?? 1;
        var paperHeight = (current.TextSize > 0 ? current.TextSize : db.Textsize) / currentScale;
        if (currentScale == scale && ReadObjectScale(current, tx).HasValue)
            return;

        var originalId = TextStyleSource(current, tx);
        if (originalId.IsNull || originalId.IsErased)
            originalId = current.ObjectId;
        var styles = (TextStyleTable)tx.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        var prefix = $"SHIZA_TXT_{originalId.Handle}_1_{scale}";
        ObjectId styleId = ObjectId.Null;
        for (int i = 1; ; i++)
        {
            var name = i == 1 ? prefix : $"{prefix}_{i}";
            if (styles.Has(name))
            {
                var candidate = (TextStyleTableRecord)tx.GetObject(styles[name], OpenMode.ForRead);
                if (ReadObjectScale(candidate, tx) == scale && TextStyleSource(candidate, tx) == originalId)
                {
                    styleId = candidate.ObjectId;
                    break;
                }
                continue;
            }

            var original = (TextStyleTableRecord)tx.GetObject(originalId, OpenMode.ForRead);
            using var variant = (TextStyleTableRecord)original.Clone();
            variant.Name = name;
            if (variant.TextSize > 0)
                variant.TextSize = paperHeight * scale;
            tx.GetObject(styles.ObjectId, OpenMode.ForWrite);
            styleId = styles.Add(variant);
            tx.AddNewlyCreatedDBObject(variant, true);
            WriteObjectScale(variant, tx, scale);
            var dictionary = (DBDictionary)tx.GetObject(variant.ExtensionDictionary, OpenMode.ForWrite);
            using var data = new ResultBuffer(new TypedValue((int)DxfCode.SoftPointerId, originalId));
            var source = new Xrecord { Data = data };
            dictionary.SetAt(TextStyleSourceKey, source);
            tx.AddNewlyCreatedDBObject(source, true);
            break;
        }
        db.Textstyle = styleId;
        db.Textsize = paperHeight * scale;
    }

    private static ObjectId TextStyleSource(TextStyleTableRecord record, Transaction tx)
    {
        if (record.ExtensionDictionary.IsNull)
            return ObjectId.Null;
        var dictionary = (DBDictionary)tx.GetObject(record.ExtensionDictionary, OpenMode.ForRead);
        if (!dictionary.Contains(TextStyleSourceKey))
            return ObjectId.Null;
        var source = (Xrecord)tx.GetObject(dictionary.GetAt(TextStyleSourceKey), OpenMode.ForRead);
        using var data = source.Data;
        return data?.AsArray().FirstOrDefault().Value is ObjectId id ? id : ObjectId.Null;
    }

    private static int NativeAnnotationScale(Entity entity, Database db, Transaction tx)
    {
        if (entity.Annotative == AnnotativeStates.True)
        {
            var current = db.Cannoscale;
            if (entity.HasContext(current))
                return ScaleDenominator(current.DrawingUnits / current.PaperUnits);
            var assigned = AnnotationContexts(db).Cast<AnnotationScale>().FirstOrDefault(entity.HasContext);
            return assigned != null ? ScaleDenominator(assigned.DrawingUnits / assigned.PaperUnits) :
                ReadDocumentScale(db, tx);
        }
        return entity switch
        {
            Dimension dimension => ScaleDenominator(dimension.Dimscale),
            MLeader leader => ScaleDenominator(leader.Scale),
            Leader leader => ScaleDenominator(leader.Dimscale),
            MText text => ReadTextScale(text, text.TextStyleId, db, tx),
            DBText text => ReadTextScale(text, text.TextStyleId, db, tx),
            _ => ReadDocumentScale(db, tx)
        };
    }

    private static int ReadTextScale(Entity entity, ObjectId styleId, Database db, Transaction tx)
    {
        if (ReadObjectScale(entity, tx) is > 0 and int scale)
            return scale;
        if (!styleId.IsNull && ReadObjectScale(tx.GetObject(styleId, OpenMode.ForRead), tx) is > 0 and int styleScale)
            return styleScale;
        // Legacy text has no per-object scale. Interpret it at the previous drawing setting.
        return ReadDocumentScale(db, tx);
    }

    private static int ScaleDenominator(double value) =>
        double.IsFinite(value) && value >= 1 && value <= int.MaxValue
            ? (int)Math.Round(value)
            : 1;

    private static void ResizeNativeAnnotation(Entity entity, Database db, Transaction tx, int scale)
    {
        if (entity.Annotative == AnnotativeStates.True)
        {
            var selected = GetAnnotationScale(db, scale);
            if (entity.HasContext(selected))
                return;
            var previous = AnnotationContexts(db).Cast<AnnotationScale>().Where(entity.HasContext).ToArray();
            entity.AddContext(selected);
            // A single representation can be changed. Deliberate multi-scale annotations
            // retain their other viewport representations.
            if (previous.Length == 1)
                entity.RemoveContext(previous[0]);
            entity.RecordGraphicsModified(true);
            return;
        }
        switch (entity)
        {
            case Dimension dimension:
                dimension.Dimscale = scale;
                dimension.RecomputeDimensionBlock(true);
                break;
            case MLeader leader:
                leader.Scale = scale;
                break;
            case Leader leader:
                leader.Dimscale = scale;
                leader.EvaluateLeader();
                break;
            case MText text:
                text.TransformBy(Matrix3d.Scaling((double)scale / ReadTextScale(text, text.TextStyleId, db, tx), text.Location));
                WriteObjectScale(text, tx, scale);
                break;
            case DBText text:
                var point = text.IsDefaultAlignment ? text.Position : text.AlignmentPoint;
                text.TransformBy(Matrix3d.Scaling((double)scale / ReadTextScale(text, text.TextStyleId, db, tx), point));
                WriteObjectScale(text, tx, scale);
                break;
        }
        entity.RecordGraphicsModified(true);
    }

    private static ObjectContextCollection AnnotationContexts(Database db) =>
        db.ObjectContextManager.GetContextCollection(AnnotationScalesCollection);

    private static AnnotationScale GetAnnotationScale(Database db, int scale)
    {
        var contexts = AnnotationContexts(db);
        var match = contexts.Cast<AnnotationScale>()
            .FirstOrDefault(item => SameLength(item.DrawingUnits / item.PaperUnits, scale));
        if (match != null)
            return match;
        var name = $"1:{scale}";
        if (contexts.HasContext(name))
            name = $"ShizaAutomatics 1:{scale}";
        using var created = new AnnotationScale { Name = name, PaperUnits = 1, DrawingUnits = scale };
        contexts.AddContext(created);
        return (AnnotationScale)contexts.GetContext(name);
    }
}
