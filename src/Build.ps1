param(
    [string]$AutoCADPath = 'C:\Program Files\Autodesk\AutoCAD 2025',
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'ShizaAutomatics.bundle\Contents\ShizaAutomatics.dll')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName Microsoft.CodeAnalysis
Add-Type -AssemblyName Microsoft.CodeAnalysis.CSharp

$frameworks = Join-Path $env:ProgramFiles 'dotnet\shared'
$coreRoot = Join-Path $frameworks 'Microsoft.NETCore.App'
$desktopRoot = Join-Path $frameworks 'Microsoft.WindowsDesktop.App'
$runtime = Get-ChildItem -LiteralPath $coreRoot -Directory -Filter '8.0.*' |
    Where-Object { Test-Path -LiteralPath (Join-Path $desktopRoot $_.Name) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$runtime) { throw 'Для сборки нужна установленная среда .NET 8 Desktop.' }

$references = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
$assemblies = @{}
foreach ($folder in $runtime.FullName, (Join-Path $desktopRoot $runtime.Name)) {
    foreach ($file in Get-ChildItem -LiteralPath $folder -Filter '*.dll' -File) {
        try { [void][Reflection.AssemblyName]::GetAssemblyName($file.FullName) }
        catch [BadImageFormatException] { continue }
        $assemblies[$file.Name] = $file.FullName
    }
}
foreach ($name in 'acmgd.dll', 'accoremgd.dll', 'acdbmgd.dll') {
    $assemblies[$name] = Join-Path $AutoCADPath $name
}
foreach ($path in $assemblies.Values) {
    $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path))
}

$trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File) {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
        [IO.File]::ReadAllText($file.FullName), $null, $file.FullName, [Text.Encoding]::UTF8,
        [Threading.CancellationToken]::None))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
    [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary
).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
    'ShizaAutomatics', $trees, $references, $options)
$resources = [System.Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
foreach ($name in 'BlockToLayer0.lsp', 'COPYKEEP.lsp', 'PDFROW.lsp', 'RecalcElevations.lsp') {
    $scriptBytes = [IO.File]::ReadAllBytes((Join-Path (Join-Path $PSScriptRoot 'Lisp') $name))
    $openResource = [Func[IO.Stream]]({ [IO.MemoryStream]::new($scriptBytes, $false) }.GetNewClosure())
    $resources.Add([Microsoft.CodeAnalysis.ResourceDescription]::new(
        "ShizaAutomatics.Lisp.$name", $openResource, $true))
}
$buffer = [IO.MemoryStream]::new()
try {
    $result = $compilation.Emit($buffer, $null, $null, $null, $resources, $null,
        [Threading.CancellationToken]::None)
    $result.Diagnostics | Where-Object Severity -in 'Error', 'Warning' | ForEach-Object ToString
    if (!$result.Success) { throw 'Сборка ShizaAutomatics завершилась ошибкой.' }
    $output = [IO.Path]::GetFullPath($OutputPath)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
    [IO.File]::WriteAllBytes($output, $buffer.ToArray())
    Get-Item -LiteralPath $output
}
finally { $buffer.Dispose() }
