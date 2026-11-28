function ConvertFrom-LinuxCpuList([string]$List) {
    foreach ($part in $List.Trim() -split ',') {
        if ($part -match '^([0-9]+)-([0-9]+)$') {
            [int]$Matches[1]..[int]$Matches[2]
        } elseif ($part -match '^[0-9]+$') {
            [int]$part
        } else {
            throw "Invalid Linux CPU list: $List"
        }
    }
}

function Get-LinuxProcessCpus([int]$ProcessId) {
    $entry = Select-String -LiteralPath "/proc/$ProcessId/status" -Pattern '^Cpus_allowed_list:\s+(\S+)$'
    if (!$entry) { throw "Could not read CPU affinity for Linux process $ProcessId" }
    @(ConvertFrom-LinuxCpuList $entry.Matches[0].Groups[1].Value | Sort-Object -Unique) -join ','
}

function Get-GatewayCpuPlacement {
    $available = @(ConvertFrom-LinuxCpuList (Get-LinuxProcessCpus $PID))
    $groups = [Collections.Generic.List[string]]::new()
    foreach ($cpu in $available) {
        $path = "/sys/devices/system/cpu/cpu$cpu/topology/thread_siblings_list"
        $siblings = @(ConvertFrom-LinuxCpuList (Get-Content -Raw -LiteralPath $path) |
            Where-Object { $_ -in $available } | Sort-Object -Unique) -join ','
        if (!$groups.Contains($siblings)) { $groups.Add($siblings) }
    }
    if ($groups.Count -lt 2) { throw 'Gateway CPU isolation requires two available physical core groups' }
    $split = [int][Math]::Floor($groups.Count / 2)
    $gateway = @($groups.GetRange(0, $split) | ForEach-Object { ConvertFrom-LinuxCpuList $_ } | Sort-Object)
    $fixture = @($available | Where-Object { $_ -notin $gateway })
    [ordered]@{ gateway = $gateway -join ','; fixture = $fixture -join ','; siblingGroups = $groups.ToArray() }
}
