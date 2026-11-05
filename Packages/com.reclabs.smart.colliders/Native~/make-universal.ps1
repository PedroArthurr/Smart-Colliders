# Combines thin Mach-O slices into a universal (fat) binary — a minimal `lipo -create` for machines without Xcode.
# Usage: ./make-universal.ps1 -Out universal.dylib -Inputs arm64.dylib, x64.dylib
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string[]]$Inputs
)
$ErrorActionPreference = 'Stop'
$alignPow = 14                 # 2^14 = 16 KiB (arm64 page size)
$alignBytes = 1 -shl $alignPow

$slices = @()
foreach ($path in $Inputs) {
    [byte[]]$bytes = [IO.File]::ReadAllBytes((Resolve-Path $path).Path)
    $magic = [BitConverter]::ToUInt32($bytes, 0)
    if ($magic -ne [uint32]4277009103) { throw "$path is not a 64-bit little-endian Mach-O" } # 0xFEEDFACF
    $slices += , @{ Bytes = $bytes; Cpu = [BitConverter]::ToUInt32($bytes, 4); Sub = [BitConverter]::ToUInt32($bytes, 8) }
}

$ms = New-Object IO.MemoryStream
function WriteBE([IO.MemoryStream]$s, [uint32]$v) {
    $s.WriteByte([byte](($v -shr 24) -band 0xFF)); $s.WriteByte([byte](($v -shr 16) -band 0xFF))
    $s.WriteByte([byte](($v -shr 8) -band 0xFF)); $s.WriteByte([byte]($v -band 0xFF))
}

WriteBE $ms ([uint32]3405691582) # FAT_MAGIC 0xCAFEBABE
WriteBE $ms ([uint32]$slices.Count)
$offset = [long]$alignBytes
$offsets = @()
foreach ($s in $slices) {
    $offsets += $offset
    WriteBE $ms $s.Cpu
    WriteBE $ms $s.Sub
    WriteBE $ms ([uint32]$offset)
    WriteBE $ms ([uint32]$s.Bytes.Length)
    WriteBE $ms ([uint32]$alignPow)
    $offset = [long]([Math]::Ceiling(($offset + $s.Bytes.Length) / $alignBytes) * $alignBytes)
}
for ($i = 0; $i -lt $slices.Count; $i++) {
    $pad = $offsets[$i] - $ms.Length
    if ($pad -gt 0) { $ms.Write((New-Object byte[] $pad), 0, [int]$pad) }
    $ms.Write($slices[$i].Bytes, 0, $slices[$i].Bytes.Length)
}
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[IO.File]::WriteAllBytes($Out, $ms.ToArray())
"universal: $Out ($($ms.Length) bytes, $($slices.Count) slices)"
