param(
    [Parameter(Mandatory=$true)][string]$Dll,
    [string]$ShimName = "W7Compat.dll"
)

# Windows 7 缺少 GetSystemTimePreciseAsFileTime（Windows 8+ 才有）。
# 某些随带的原生 DLL（如 Magick.Native-Q8-x86.dll）的导入表中硬绑定了
# KERNEL32!GetSystemTimePreciseAsFileTime，在 Win7 上加载时加载器会直接
# 报“无法定位程序输入点”导致程序无法启动。
#
# 本脚本把目标 DLL 的导入模块名从 KERNEL32.dll 改为本仓库自带的 shim DLL
#（导出同名函数，内部回退到 GetSystemTimeAsFileTime）。两个字符串长度相同，
# 就地修改二进制，不改动 PE 结构。
$oldName = "KERNEL32.dll"
$newNameBytes = [System.Text.Encoding]::ASCII.GetBytes($ShimName + [char]0)
if ($newNameBytes.Length -gt [System.Text.Encoding]::ASCII.GetByteCount($oldName + [char]0)) {
    throw "Shim name '$ShimName' is longer than '$oldName', cannot patch in place"
}

$data = [byte[]]([IO.File]::ReadAllBytes($Dll))
$peOff = [BitConverter]::ToInt32($data, 0x3c)
if ($data.Length -lt $peOff + 24) { exit }
if ([BitConverter]::ToUInt16($data, $peOff) -ne 0x4550) { exit } # 'PE\0\0'

$opt = $peOff + 24
$magic = [BitConverter]::ToUInt16($data, $opt)
$is64 = $magic -eq 0x20b
if ($is64) {
    $dd = $opt + 112
    $secOff = $opt + 240
} else {
    $dd = $opt + 96
    $secOff = $opt + 224
}
$nsecs = [BitConverter]::ToUInt16($data, $peOff + 6)
$secs = @()
for ($i = 0; $i -lt $nsecs; $i++) {
    $s = $secOff + $i * 40
    $secs += @{
        V = [BitConverter]::ToUInt32($data, $s + 12) # VirtualAddress
        S = [BitConverter]::ToUInt32($data, $s + 16) # SizeOfRawData
        P = [BitConverter]::ToUInt32($data, $s + 20) # PointerToRawData
    }
}
function RvaToOff($rva) {
    if ($rva -eq 0) { return $null }
    foreach ($s in $secs) {
        if ($rva -ge $s.V -and $rva -lt $s.V + $s.S) { return $s.P + ($rva - $s.V) }
    }
    return $null
}
function Get-UInt32($off) { [BitConverter]::ToUInt32($data, $off) }

$patched = $false
# 1 = import directory, 13 = delay-load directory
foreach ($idx in @(1, 13)) {
    $rva = Get-UInt32($dd + $idx * 8)
    if ($rva -eq 0) { continue }
    $io = RvaToOff $rva
    while ($null -ne $io) {
        $ltid = Get-UInt32($io)
        $nameRva = Get-UInt32($io + 12)
        if ($ltid -eq 0 -and $nameRva -eq 0) { break }
        $noff = RvaToOff $nameRva
        if ($null -eq $noff) {
            Write-Host ("Skip unmapped import name in {0} at idx={1}" -f $Dll, $idx)
            $io += 20
            continue
        }
        $sb = New-Object System.Text.StringBuilder
        for ($i = $noff; ($i -lt $data.Length) -and ($data[$i] -ne 0); $i++) { $sb.Append([char]$data[$i]) }
        $mname = $sb.ToString()
        $io += 20
        if ($mname -ne $oldName) { continue }
        $currentBytes = $data[$noff..($noff + $oldName.Length - 1)]
        $current = -join ($currentBytes | ForEach-Object { [char] $_ })
        if ($current -eq $oldName) {
            [Array]::Copy($newNameBytes, 0, $data, $noff, $newNameBytes.Length)
            $patched = $true
            Write-Host ("Patched {0}: import module '{1}' -> '{2}'" -f $Dll, $oldName, $ShimName)
        }
    }
}

if ($patched) {
    [IO.File]::WriteAllBytes($Dll, $data)
} else {
    Write-Host ("{0}: no '{1}' import found (already patched or not present)" -f $Dll, $oldName)
}
