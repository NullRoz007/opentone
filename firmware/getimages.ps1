
$data = [System.IO.File]::ReadAllBytes("C:\fender-tone\OpenTone\firmware\firmware.bin")

$count = 0

for ($i = 0; $i -lt $data.Length - 32; $i++) {
    if ($data[$i] -eq 0x42 -and $data[$i + 1] -eq 0x4D) {
        $hex = [BitConverter]::ToString($data, $i, 32)

        [PSCustomObject]@{
            Offset = ("0x{0:X8}" -f $i)
            Header = $hex
        }

        $count++
        if ($count -ge 30) { break }
    }
}
