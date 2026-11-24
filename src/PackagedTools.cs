using System;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Acad = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ShizaAutomatics;

public sealed partial class Addon
{
    [CommandMethod("B0")]
    public void MoveBlockContentsToLayerZero() => QueuePackagedTool("B0");

    [CommandMethod("COPYKEEP")]
    public void CopyToLayerZero() => QueuePackagedTool("COPYKEEP");

    [CommandMethod("PDFROW")]
    public void ImportPdfPages() => QueuePackagedTool("PDFROW");

    [CommandMethod("SHIZALEVELS")]
    public void RecalculateElevations() => QueuePackagedTool("SHIZALEVELS");

    private static void QueuePackagedTool(string name)
    {
        var doc = Acad.DocumentManager.MdiActiveDocument;
        // Queue the short expression so the Lisp runs in its own AutoCAD command context.
        doc.SendStringToExecute($"(eval (read (shiza-packaged-tool \"{name}\")))\n", false, false, false);
    }

    [LispFunction("shiza-packaged-tool")]
    public static string ReadPackagedTool(ResultBuffer? arguments)
    {
        if (arguments?.AsArray() is not [{ Value: string name }])
            return "(princ)";

        var (file, entry) = name switch
        {
            "B0" => ("BlockToLayer0.lsp", "shiza:run-b0"),
            "COPYKEEP" => ("COPYKEEP.lsp", "shiza:run-copykeep"),
            "PDFROW" => ("PDFROW.lsp", "shiza:run-pdfrow"),
            "SHIZALEVELS" => ("RecalcElevations.lsp", "shiza:run-levels"),
            _ => ("", "")
        };
        if (file.Length == 0)
            return "(princ)";

        using var stream = typeof(Addon).Assembly.GetManifestResourceStream("ShizaAutomatics.Lisp." + file)
            ?? throw new InvalidOperationException($"В сборке отсутствует команда {name}.");
        using var reader = new StreamReader(stream);
        return $"(progn\n{reader.ReadToEnd()}\n({entry})\n(princ))";
    }
}
