# Reproduce the existing Studio gold tile/four-point star as a Windows multi-size icon.
param([string]$OutputPath=(Join-Path $PSScriptRoot '../src/CloudScribe.App/Assets/CloudScribe.ico'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$images=@()
foreach($size in @(16,24,32,48,64,256)){
    $bitmap=[Drawing.Bitmap]::new($size,$size)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $gold=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(226,193,132))
    $ink=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(31,21,43))
    $graphics.FillEllipse($gold,0,0,$size-1,$size-1)
    $points=@(@(0.5,0.13),@(0.57,0.43),@(0.87,0.5),@(0.57,0.57),@(0.5,0.87),@(0.43,0.57),@(0.13,0.5),@(0.43,0.43))
    [Drawing.PointF[]]$polygon=@($points | ForEach-Object {[Drawing.PointF]::new([single]($_[0]*$size),[single]($_[1]*$size))})
    $graphics.FillPolygon($ink,$polygon)
    $stream=[IO.MemoryStream]::new()
    $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $images+=,[pscustomobject]@{Size=$size;Bytes=$stream.ToArray()}
    $stream.Dispose();$ink.Dispose();$gold.Dispose();$graphics.Dispose();$bitmap.Dispose()
}
$target=[IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
$file=[IO.File]::Create($target)
$writer=[IO.BinaryWriter]::new($file)
try{
    $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$images.Count)
    $offset=6+16*$images.Count
    foreach($item in $images){
        $dimension=if($item.Size -eq 256){0}else{$item.Size}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$item.Bytes.Length);$writer.Write([uint32]$offset)
        $offset+=$item.Bytes.Length
    }
    foreach($item in $images){$writer.Write([byte[]]$item.Bytes)}
}finally{$writer.Dispose();$file.Dispose()}
Write-Output "Created Windows icon: $target"
