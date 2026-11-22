
param(
    [string]$Path = "C:\fender-tone\OpenTone\firmware\firmware.bin",
    [int]$MinimumLength = 5
)

$data = [System.IO.File]::ReadAllBytes($Path)

$start = -1
$builder = [System.Text.StringBuilder]::new()

for ($i = 0; $i -le $data.Length; $i++) {
    $isPrintable = $i -lt $data.Length -and
        $data[$i] -ge 0x20 -and $data[$i] -le 0x7E

    if ($isPrintable) {
        if ($start -eq -1) { $start = $i }
        [void]$builder.Append([char]$data[$i])
    }
    else {
        if ($builder.Length -ge $MinimumLength) {
            [PSCustomObject]@{
                Offset = "0x{0:X8}" -f $start
                Text = $builder.ToString()
            }
        }

        $start = -1
        [void]$builder.Clear()
    }
}
