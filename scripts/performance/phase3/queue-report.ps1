Set-StrictMode -Version Latest

function Assert-QueueCondition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Queue evidence rejected: $Message" }
}

function Get-QueueInteger([System.Text.Json.JsonElement]$Node, [string]$Name) {
    $value = $Node.GetProperty($Name)
    [long]$number = 0
    Assert-QueueCondition ($value.ValueKind -eq [System.Text.Json.JsonValueKind]::Number -and $value.TryGetInt64([ref]$number) -and $number -ge 0) "$Name must be a nonnegative JSON integer"
    return $number
}

function Get-QueueNumber([System.Text.Json.JsonElement]$Node, [string]$Name) {
    $value = $Node.GetProperty($Name)
    Assert-QueueCondition ($value.ValueKind -eq [System.Text.Json.JsonValueKind]::Number) "$Name must be a JSON number"
    $number = $value.GetDouble()
    Assert-QueueCondition ([double]::IsFinite($number) -and $number -ge 0) "$Name must be finite and nonnegative"
    return $number
}

function Assert-QueueTrue([System.Text.Json.JsonElement]$Node, [string]$Name) {
    Assert-QueueCondition ($Node.GetProperty($Name).ValueKind -eq [System.Text.Json.JsonValueKind]::True) "$Name must be true, without coercion"
}

function Assert-QueuePhase([System.Text.Json.JsonElement]$Phase, [int]$Expected) {
    foreach ($name in 'expected','registered','started','completed','settled') {
        Assert-QueueCondition ((Get-QueueInteger $Phase $name) -eq $Expected) "$name differs from the expected exact message count"
    }
    foreach ($name in 'inFlight','duplicateAttempts','orderViolations') {
        Assert-QueueCondition ((Get-QueueInteger $Phase $name) -eq 0) "$name is nonzero"
    }
    Assert-QueueTrue $Phase 'complete'
    Assert-QueueCondition ((Get-QueueInteger $Phase 'maxConcurrency') -eq 1) 'strict sequential handler policy changed'
    Assert-QueueCondition ($Phase.GetProperty('errors').GetArrayLength() -eq 0) 'collector errors remain'
    $deliveries = $Phase.GetProperty('deliveries')
    Assert-QueueCondition ($deliveries.GetArrayLength() -eq $Expected) 'delivery records are incomplete'
    $events = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $envelopes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [decimal]$amount = 0
    for ($index = 0; $index -lt $Expected; $index++) {
        $delivery = $deliveries[$index]
        Assert-QueueCondition ((Get-QueueInteger $delivery 'sequence') -eq $index) 'logical sequence differs from publication order'
        [guid]$eventId = [guid]::Empty
        [guid]$messageId = [guid]::Empty
        Assert-QueueCondition ([guid]::TryParse($delivery.GetProperty('eventId').GetString(), [ref]$eventId) -and $eventId -ne [guid]::Empty) 'missing logical event ID'
        Assert-QueueCondition ([guid]::TryParse($delivery.GetProperty('messageId').GetString(), [ref]$messageId) -and $messageId -ne [guid]::Empty) 'missing durable envelope ID'
        Assert-QueueCondition ($events.Add($eventId.ToString('N')) -and $envelopes.Add($messageId.ToString('N'))) 'duplicate event or envelope ID'
        $published = Get-QueueInteger $delivery 'publishedTicks'
        $started = Get-QueueInteger $delivery 'startedTicks'
        $finished = Get-QueueInteger $delivery 'finishedTicks'
        $settled = Get-QueueInteger $delivery 'settlementObservedTicks'
        Assert-QueueCondition ($published -gt 0 -and $started -ge $published -and $finished -ge $started -and $settled -ge $finished) 'settlement timestamp precedes publish/handler completion'
        Assert-QueueCondition ($delivery.GetProperty('error').ValueKind -eq [System.Text.Json.JsonValueKind]::Null) 'handler error remains'
        # Read monetary JSON directly as decimal; never convert a binary double to decimal.
        $wireAmount = $delivery.GetProperty('syntheticAmount').GetDecimal()
        Assert-QueueCondition ($wireAmount -eq [decimal]0.01) 'synthetic payload amount changed across transport'
        $amount += $wireAmount
    }
    Assert-QueueCondition ($amount -eq ([decimal]$Expected / [decimal]100)) 'synthetic payload amount conservation failed'
    Assert-QueueCondition ($Phase.GetProperty('registeredSyntheticAmount').GetDecimal() -eq $amount -and $Phase.GetProperty('settledSyntheticAmount').GetDecimal() -eq $amount) 'synthetic amount summary differs from exact payload sum'
    $samples = $Phase.GetProperty('storeSamples')
    Assert-QueueCondition ($samples.GetArrayLength() -gt 0) 'no independent SQL observations'
    [long]$lastTick = 0
    [long]$observedHandled = 0
    foreach ($sample in $samples.EnumerateArray()) {
        $tick = Get-QueueInteger $sample 'observedTicks'
        Assert-QueueCondition ($tick -gt 0 -and $tick -ge $lastTick) 'store samples are not monotonic'
        $lastTick = $tick
        $observedHandled += Get-QueueInteger $sample 'newlyObservedHandled'
        foreach ($name in 'ready','scheduled','incoming','outgoing','deadLetters') { $null = Get-QueueInteger $sample $name }
    }
    Assert-QueueCondition ($observedHandled -eq $Expected) 'SQL Handled observations are incomplete or duplicated'
    $last = $samples[$samples.GetArrayLength() - 1]
    foreach ($name in 'ready','scheduled','incoming','outgoing','deadLetters') {
        Assert-QueueCondition ((Get-QueueInteger $last $name) -eq 0) "active $name rows remain"
    }
}

function Assert-QueueCaseReport {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$Batch,
        [Parameter(Mandatory)][int]$Messages,
        [Parameter(Mandatory)][int]$HandlerCostMs,
        [double]$OfferedRate = 0,
        [int]$Warmup = 10,
        [ValidateSet('X64','Arm64')][string]$ExpectedArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    )
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path))
    try {
        $report = $document.RootElement
        Assert-QueueCondition ((Get-QueueInteger $report 'schemaVersion') -eq 2) 'unknown report schema'
        Assert-QueueTrue $report 'passed'
        Assert-QueueCondition ($report.GetProperty('error').ValueKind -eq [System.Text.Json.JsonValueKind]::Null) 'report contains an error'
        Assert-QueueCondition ($report.GetProperty('runtime').GetString() -ceq '.NET 11.0.0' -and $report.GetProperty('runtimeVersion').GetString() -ceq '11.0.0') 'the phase3 queue cohort requires exact released runtime11.0.0'
        Assert-QueueCondition ($report.GetProperty('architecture').GetString() -ceq $ExpectedArchitecture) 'unexpected actual process architecture'
        Assert-QueueCondition ((Get-QueueInteger $report 'processId') -gt 0) 'missing actual process ID'
        Assert-QueueCondition ($report.GetProperty('serverGc').ValueKind -in @([System.Text.Json.JsonValueKind]::True,[System.Text.Json.JsonValueKind]::False)) 'GC mode must be a boolean'
        Assert-QueueCondition ($report.GetProperty('gcConfiguration').ValueKind -eq [System.Text.Json.JsonValueKind]::Object -and @($report.GetProperty('gcConfiguration').EnumerateObject()).Count -gt 0) 'missing GC configuration'
        Assert-QueueCondition ($report.GetProperty('gcConfigurationTypes').ValueKind -eq [System.Text.Json.JsonValueKind]::Object) 'missing GC configuration value types'
        foreach ($setting in $report.GetProperty('gcConfiguration').EnumerateObject()) {
            Assert-QueueCondition ($setting.Value.ValueKind -eq [System.Text.Json.JsonValueKind]::String -and $report.GetProperty('gcConfigurationTypes').GetProperty($setting.Name).ValueKind -eq [System.Text.Json.JsonValueKind]::String) 'untyped GC configuration'
        }
        Assert-QueueCondition ($report.GetProperty('coreLibrarySha256').GetString() -cmatch '^[a-f0-9]{64}$') 'missing actual core library identity'
        Assert-QueueCondition ($report.GetProperty('results').GetArrayLength() -eq 1) 'one independently owned process/case is required'
        $result = $report.GetProperty('results')[0]
        Assert-QueueTrue $result 'complete'
        Assert-QueueTrue $result 'warmupComplete'
        Assert-QueueCondition ($result.GetProperty('queue').GetString() -ceq 'spend') 'unexpected queue'
        Assert-QueueCondition ((Get-QueueInteger $result 'receiveBatch') -eq $Batch) 'receive batch changed'
        Assert-QueueCondition ((Get-QueueInteger $result 'handlerCostMs') -eq $HandlerCostMs) 'synthetic handler delay changed'
        Assert-QueueCondition ((Get-QueueNumber $result 'offeredRate') -eq $OfferedRate) 'offered rate changed'
        $setup = $result.GetProperty('setup')
        Assert-QueueTrue $setup 'ready'
        Assert-QueueCondition ($setup.GetProperty('error').ValueKind -eq [System.Text.Json.JsonValueKind]::Null) 'schema readiness failed'
        Assert-QueueCondition ((Get-QueueInteger $setup 'polls') -gt 0) 'schema readiness was not observed'
        $null = Get-QueueNumber $setup 'elapsedMilliseconds'
        $expectedRelations = @('wolverine_queues.wolverine_queue_spend_update_events',
            'wolverine_queues.wolverine_queue_spend_update_events_scheduled',
            'wolverine_conduit_bench.wolverine_incoming_envelopes',
            'wolverine_conduit_bench.wolverine_outgoing_envelopes', 'wolverine_conduit_bench.wolverine_dead_letters') | Sort-Object
        foreach ($name in 'requiredRelations','presentRelations') {
            $relations = @($setup.GetProperty($name).EnumerateArray() | ForEach-Object { $_.GetString() })
            Assert-QueueCondition ($relations.Count -eq 5 -and (($relations | Sort-Object) -join '|') -ceq ($expectedRelations -join '|')) 'schema readiness relation inventory differs'
        }
        Assert-QueuePhase $result.GetProperty('warmup') $Warmup
        Assert-QueuePhase $result.GetProperty('measurement') $Messages
        foreach ($name in 'publishRate','handlerCompletionRate','settlementObservedRate','elapsedSeconds') {
            Assert-QueueCondition ((Get-QueueNumber $result $name) -gt 0) "$name is not positive"
        }
        $p50 = Get-QueueNumber $result 'latencyP50Ms'
        $p95 = Get-QueueNumber $result 'latencyP95Ms'
        $p99 = Get-QueueNumber $result 'latencyP99Ms'
        Assert-QueueCondition ($p50 -le $p95 -and $p95 -le $p99) 'latency quantiles are inconsistent'
        $saturation = $result.GetProperty('saturationObserved')
        Assert-QueueCondition ($saturation.ValueKind -in @([System.Text.Json.JsonValueKind]::True,[System.Text.Json.JsonValueKind]::False)) 'saturation flag must be a boolean'
        $rtt = $result.GetProperty('databaseRttMs')
        Assert-QueueCondition ($rtt.GetArrayLength() -eq 20) 'missing measured SQL round trips'
        $rtts = @($rtt.EnumerateArray() | ForEach-Object {
            Assert-QueueCondition ($_.ValueKind -eq [System.Text.Json.JsonValueKind]::Number) 'invalid RTT type'
            $number = $_.GetDouble()
            Assert-QueueCondition ([double]::IsFinite($number) -and $number -gt 0) 'invalid RTT sample'
            $number
        })
        return [pscustomobject]@{
            SettledRate = Get-QueueNumber $result 'settlementObservedRate'
            HandlerRate = Get-QueueNumber $result 'handlerCompletionRate'
            PublishRate = Get-QueueNumber $result 'publishRate'
            P50Ms = $p50; P95Ms = $p95; P99Ms = $p99
            SaturationObserved = $saturation.GetBoolean()
            SqlRttMedianMs = ((($rtts | Sort-Object)[9]) + (($rtts | Sort-Object)[10])) / 2
            SyntheticAmount = ([decimal]$Messages / [decimal]100).ToString([CultureInfo]::InvariantCulture)
            FinancialQualification = $false
        }
    }
    finally { $document.Dispose() }
}
