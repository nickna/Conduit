[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$CandidateRevision,
    [Parameter(Mandatory)][ValidateSet('X64','Arm64')][string]$Architecture,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(5,5)][int]$Runs=5,
    [ValidateRange(60,1800)][int]$PublishTimeoutSeconds=900,
    [ValidateRange(30,900)][int]$ProcessTimeoutSeconds=300
)
$ErrorActionPreference='Stop'
$baseline='b1d49922b8dfdc6719c6d46d123f2eb5a265dcca'
if(!$IsLinux -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -cne $Architecture){throw 'Require the requested native Linux architecture'}
$machine=(& uname -m)-join ''
if(($Architecture -ceq 'X64' -and $machine.Trim() -cne 'x86_64') -or ($Architecture -ceq 'Arm64' -and $machine.Trim() -cne 'aarch64')){throw 'Process/kernel architecture mismatch; emulated results do not qualify'}
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if(!(Test-Path -LiteralPath (Join-Path $repository 'Conduit.slnx'))){throw 'Require repository-local driver'}
if($CandidateRevision -ceq $baseline){throw 'Require distinct baseline/candidate revisions'}
if((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count){throw 'Use a fresh output directory; prior evidence is never overwritten'}
New-Item -ItemType Directory -Force -Path $OutputDirectory|Out-Null
$rootOut=(Resolve-Path -LiteralPath $OutputDirectory).Path
if((Get-Item -LiteralPath $rootOut -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Output directory must not be a symbolic link'}
$work=Join-Path $rootOut 'work';New-Item -ItemType Directory -Path $work|Out-Null
$out=Join-Path $rootOut 'processes';New-Item -ItemType Directory -Path $out|Out-Null
$configuration=[pscustomobject]@{workingDirectory=$repository}
$helper=Join-Path $repository 'scripts/qualification/owned-process.ps1'
. $helper
$dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop|Select-Object -First 1).Source
$python=(Get-Command python3 -CommandType Application -ErrorAction Stop|Select-Object -First 1).Source
$bindings=[Collections.Generic.List[object]]::new()
$manifest=[Collections.Generic.List[object]]::new()
$failure=$null

function Write-Json([object]$Value,[string]$Path){ConvertTo-Json -InputObject $Value -Depth 16|Set-Content -LiteralPath $Path -Encoding utf8NoBOM}
function Step([string]$Name,[string]$Program,[string[]]$Arguments,[string]$WorkingDirectory=$repository,[int]$Timeout=120){
    $configuration.workingDirectory=$WorkingDirectory
    Invoke-Owned $Name $Program $Arguments $Timeout
}
function Files([string]$Directory){
    @(Get-ChildItem -LiteralPath $Directory -Recurse -File|Sort-Object FullName|ForEach-Object{
        [ordered]@{path=[IO.Path]::GetRelativePath($Directory,$_.FullName).Replace([char]92,[char]47);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
}
function Source-Files([string]$Directory){@(Files $Directory|Where-Object{$_.path -notmatch '(^|/)(bin|obj)/'})}
function Inventory-Delta([object[]]$Expected,[object[]]$Actual){
    $before=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $after=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach($file in $Expected){$before.Add($file.path,$file)}
    foreach($file in $Actual){$after.Add($file.path,$file)}
    $added=@($after.Keys|Where-Object{!$before.ContainsKey($_)})
    $missing=@($before.Keys|Where-Object{!$after.ContainsKey($_)})
    $modified=@($before.Keys|Where-Object{$after.ContainsKey($_) -and ($before[$_].bytes -ne $after[$_].bytes -or $before[$_].sha256 -cne $after[$_].sha256)})
    [ordered]@{passed=(!$added.Count -and !$missing.Count -and !$modified.Count);added=$added;missing=$missing;modified=$modified}
}
function Remove-MeasurementSubtree([string]$Source){
    $prefix=[IO.Path]::GetFullPath($Source).TrimEnd([char]'/')+[IO.Path]::DirectorySeparatorChar
    $target=[IO.Path]::GetFullPath((Join-Path $Source 'Tests/ConduitLLM.Benchmarks'))
    if(!$target.StartsWith($prefix,[StringComparison]::Ordinal) -or [IO.Path]::GetRelativePath($Source,$target).Replace([char]92,[char]47) -cne 'Tests/ConduitLLM.Benchmarks'){throw 'Measurement replacement escaped its new source archive'}
    foreach($path in @($Source,(Join-Path $Source 'Tests'),$target)){
        if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reject linked measurement subtree/parent'}
    }
    if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Recurse -Force}
}

try{
    $sourceSnapshot=Join-Path $rootOut 'driver-source';New-Item -ItemType Directory -Path $sourceSnapshot|Out-Null
    foreach($path in @($PSCommandPath,$helper,(Join-Path $PSScriptRoot 'analyze_micros.py'))){Copy-Item -LiteralPath $path -Destination (Join-Path $sourceSnapshot (Split-Path $path -Leaf))}
    Write-Json @(Files $sourceSnapshot) (Join-Path $rootOut 'driver-source-bindings.json')
    $gcEnvironment=@(Get-ChildItem Env:|Where-Object{$_.Name -match '^(DOTNET|COMPlus)_(GC|Heap|ServerGC|ConcurrentGC)'}|ForEach-Object{[ordered]@{name=$_.Name;value=$_.Value}})
    $context=[ordered]@{schemaVersion=1;architecture=$Architecture;processorCount=[Environment]::ProcessorCount;kernel=(& uname -a);osRelease=@(Get-Content /etc/os-release);gcEnvironment=$gcEnvironment;
        cgroup=@(foreach($path in @('/sys/fs/cgroup/cpu.max','/sys/fs/cgroup/memory.max','/sys/fs/cgroup/cpuset.cpus.effective')){if(Test-Path -LiteralPath $path){[ordered]@{path=$path;value=(Get-Content -LiteralPath $path)}}});scope='Native Linux single-VM same-.NET11 paired microbenchmarks; no production capacity, network or financial qualification'}
    Write-Json $context (Join-Path $rootOut 'context.json')
    if($gcEnvironment.Count){throw 'Default-GC comparison rejects inherited GC overrides; retained context identifies them'}
    if($env:DOTNET_STARTUP_HOOKS){throw 'Microbenchmarks require no inherited startup hook'}
    Step 'cpu-topology' 'lscpu' @()
    Step 'checkout-head' 'git' @('rev-parse','HEAD')
    Step 'baseline-ancestor' 'git' @('merge-base','--is-ancestor',$baseline,'HEAD')
    Step 'candidate-ancestor' 'git' @('merge-base','--is-ancestor',$CandidateRevision,'HEAD')
    $toolchain=Join-Path $work 'toolchain';New-Item -ItemType Directory -Path $toolchain|Out-Null
    Write-Json @{sdk=@{version='11.0.100';rollForward='disable';allowPrerelease=$false}} (Join-Path $toolchain 'global.json')
    Step 'sdk-version' $dotnet @('--version') $toolchain
    if((Get-Content -Raw -LiteralPath (Join-Path $out 'sdk-version.stdout.log')).Trim() -cne '11.0.100'){throw 'Require exact SDK11.0.100'}
    Step 'sdk-info' $dotnet @('--info') $toolchain
    Step 'runtimes' $dotnet @('--list-runtimes') $toolchain
    $overlay=Join-Path $work 'measurement-overlay.tar'
    Step 'measurement-overlay-archive' 'git' @('-c','core.autocrlf=false','-c','core.eol=lf','archive','--format=tar',"--output=$overlay",$CandidateRevision,'--','Tests/ConduitLLM.Benchmarks')
    $overlayRoot=Join-Path $work 'measurement-overlay';New-Item -ItemType Directory -Path $overlayRoot|Out-Null
    Step 'measurement-overlay-extract' 'tar' @('-xf',$overlay,'-C',$overlayRoot)
    $overlayFiles=@(Files (Join-Path $overlayRoot 'Tests/ConduitLLM.Benchmarks'))
    foreach($required in @('ConduitLLM.Benchmarks.csproj','Program.cs','Streaming/CollectorMicroHarness.cs','Streaming/WriterMicroHarness.cs','ProvidersStreamingBenchmarks.cs')){
        if($required -cnotin $overlayFiles.path){throw "Candidate measurement overlay is incomplete: $required"}
    }
    Write-Json $overlayFiles (Join-Path $rootOut 'measurement-overlay-bindings.json')
    $cohorts=@(@{id='baseline';sourceSha=$baseline},@{id='candidate';sourceSha=$CandidateRevision})
    foreach($cohort in $cohorts){
        $source=Join-Path $work "source/$($cohort.id)";New-Item -ItemType Directory -Path $source -Force|Out-Null
        $tar=Join-Path $work "$($cohort.id)-source.tar"
        Step "$($cohort.id)-source-archive" 'git' @('-c','core.autocrlf=false','-c','core.eol=lf','archive','--format=tar',"--output=$tar",$cohort.sourceSha)
        Step "$($cohort.id)-source-tree" 'git' @('rev-parse',"$($cohort.sourceSha)^{tree}")
        Step "$($cohort.id)-source-extract" 'tar' @('-xf',$tar,'-C',$source)
        Write-Json @(Files (Join-Path $source 'Tests/ConduitLLM.Benchmarks')) (Join-Path $rootOut "$($cohort.id)-original-measurement-bindings.json")
        Remove-MeasurementSubtree $source
        Step "$($cohort.id)-overlay-extract" 'tar' @('-xf',$overlay,'-C',$source)
        $overlayDelta=Inventory-Delta $overlayFiles @(Files (Join-Path $source 'Tests/ConduitLLM.Benchmarks'))
        Write-Json $overlayDelta (Join-Path $rootOut "$($cohort.id)-overlay-integrity.json")
        if(!$overlayDelta.passed){throw 'Measurement overlays differ'}
        $sourceFiles=@(Source-Files $source)
        Write-Json $sourceFiles (Join-Path $rootOut "$($cohort.id)-source-file-bindings.json")
        $published=Join-Path $work "published/$($cohort.id)"
        $project=Join-Path $source 'Tests/ConduitLLM.Benchmarks/ConduitLLM.Benchmarks.csproj'
        $arguments=@('publish',$project,'-c','Release','-o',$published,'-m:1','-nr:false','-p:UseSharedCompilation=false','-p:StreamingMicroOnly=true',"-p:ConduitCommitSha=$($cohort.sourceSha)")
        Step "$($cohort.id)-publish" $dotnet $arguments $toolchain $PublishTimeoutSeconds
        $cohort.sourceDirectory=$source;$cohort.publishedDirectory=$published;$cohort.sourceSdk='11.0.100';$cohort.expectedRuntime='11.0.0'
        $bindings.Add([ordered]@{id=$cohort.id;sourceSha=$cohort.sourceSha;sourceTree=(Get-Content -Raw -LiteralPath (Join-Path $out "$($cohort.id)-source-tree.stdout.log")).Trim();archive=([IO.Path]::GetRelativePath($rootOut,$tar));archiveSha256=(Get-FileHash -LiteralPath $tar).Hash.ToLowerInvariant();sourceFiles=$sourceFiles;publishedFiles=@(Files $published);publishArguments=$arguments})
    }
    $config=[ordered]@{schemaVersion=1;baselineRevision=$baseline;candidateRevision=$CandidateRevision;architecture=$Architecture;sourceSdk='11.0.100';runtime='11.0.0';runs=$Runs;innerSamples=5;dotnet=$dotnet;outputDirectory=$rootOut;driverRevision=(Get-Content -Raw -LiteralPath (Join-Path $out 'checkout-head.stdout.log')).Trim();cohorts=$cohorts;overlaySha256=(Get-FileHash -LiteralPath $overlay).Hash.ToLowerInvariant();queuePublishedDirectory=(Join-Path $work 'candidate-queue');performanceAcceptanceGranted=$false}
    Write-Json $config (Join-Path $rootOut 'configuration.json')
    Write-Json $bindings.ToArray() (Join-Path $rootOut 'source-bindings.json')
    $tools=@('collector','writer','provider')
    for($run=1;$run -le $Runs;$run++){
        $order=if($run%2){$cohorts}else{@($cohorts[1],$cohorts[0])}
        for($index=0;$index -lt $tools.Count;$index++){
            $tool=$tools[($index+$run-1)%$tools.Count]
            foreach($cohort in $order){
                $name="r$run-$tool-$($cohort.id)";$directory=Join-Path $rootOut "runs/$name"
                New-Item -ItemType Directory -Path $directory -Force|Out-Null
                $report=if($tool -ceq 'collector'){Join-Path $directory 'collector-micro.json'}else{Join-Path $directory "$tool-micro.json"}
                $dll=Join-Path $cohort.publishedDirectory 'ConduitLLM.Benchmarks.dll'
                $arguments=@($dll,"$tool-micro",'--output',$(if($tool -ceq 'collector'){$report}else{$directory}))
                if($tool -ceq 'collector'){$arguments+=@('--source-sha',$cohort.sourceSha,'--samples','5')}
                $configuration.workingDirectory=$cohort.sourceDirectory
                $process=Start-Owned $name $dotnet $arguments
                Finish-Owned $process $ProcessTimeoutSeconds
                if(!(Test-Path -LiteralPath $report -PathType Leaf)){throw "Missing micro report: $name"}
                $manifest.Add([ordered]@{arm=$cohort.id;sourceSha=$cohort.sourceSha;run=$run;tool=$tool;processId=$process.Process.Id;report=[IO.Path]::GetRelativePath($rootOut,$report);reportSha256=(Get-FileHash -LiteralPath $report).Hash.ToLowerInvariant();exitReceipt="processes/$name.exit.json";program=$dotnet;arguments=$arguments;workingDirectory=$cohort.sourceDirectory})
                Write-Json $manifest.ToArray() (Join-Path $rootOut 'manifest.json')
            }
        }
    }
    $microIntegrity=@(foreach($binding in $bindings){$cohort=$cohorts|Where-Object id -eq $binding.id;[ordered]@{arm=$binding.id;delta=(Inventory-Delta $binding.publishedFiles @(Files $cohort.publishedDirectory))}})
    Write-Json $microIntegrity (Join-Path $rootOut 'micro-published-integrity.json')
    if(@($microIntegrity|Where-Object{!$_.delta.passed}).Count){throw 'Published micro inputs changed during measurements'}
    # Required queue artifact is built only after every micro process and timer has finished.
    $candidate=$cohorts|Where-Object id -ceq 'candidate'
    $queueArguments=@('publish',(Join-Path $candidate.sourceDirectory 'Tests/ConduitLLM.Benchmarks/ConduitLLM.Benchmarks.csproj'),'-c','Release','-o',$config.queuePublishedDirectory,'-m:1','-nr:false','-p:UseSharedCompilation=false','-p:StreamingMicroOnly=false',"-p:ConduitCommitSha=$CandidateRevision")
    Step 'candidate-queue-publish' $dotnet $queueArguments $toolchain $PublishTimeoutSeconds
    Write-Json @{sourceSha=$CandidateRevision;sourceSdk='11.0.100';sourceDirectory=$candidate.sourceDirectory;publishedDirectory=$config.queuePublishedDirectory;arguments=$queueArguments;files=@(Files $config.queuePublishedDirectory);scope='Default Messaging-enabled candidate benchmark publish after all micro timings; not a queue throughput result'} (Join-Path $rootOut 'candidate-queue-publish.json')
    $final=@(foreach($binding in $bindings){$cohort=$cohorts|Where-Object id -eq $binding.id;[ordered]@{arm=$binding.id;source=(Inventory-Delta $binding.sourceFiles @(Source-Files $cohort.sourceDirectory));published=(Inventory-Delta $binding.publishedFiles @(Files $cohort.publishedDirectory))}})
    Write-Json $final (Join-Path $rootOut 'final-input-integrity.json')
    if(@($final|Where-Object{!$_.source.passed -or !$_.published.passed}).Count){throw 'Source or published micro inputs changed'}
}catch{$failure=$_.ToString();throw}
finally{
    $cleanup=@(Clear-OwnedProcesses)
    Write-Json @{schemaVersion=1;scope='Source build and measured-process capture phase; analysis outcome is separate';failure=$failure;cleanupFailures=$cleanup;processesClosed=@($ownedProcesses|ForEach-Object{@{name=$_.Name;processId=$_.Process.Id;closed=$_.Closed}});performanceAcceptanceGranted=$false} (Join-Path $rootOut 'execution.json')
    if($cleanup.Count -and !$failure){throw ($cleanup -join '; ')}
}
try{
    Step 'analyze-micros' $python @((Join-Path $PSScriptRoot 'analyze_micros.py'),'--directory',$rootOut,'--output',(Join-Path $rootOut 'summary.json')) $repository 180
}finally{
    $analysisCleanup=@(Clear-OwnedProcesses)
    Write-Json @{cleanupFailures=$analysisCleanup;closed=@($ownedProcesses|Where-Object Name -eq 'analyze-micros'|ForEach-Object{$_.Closed})} (Join-Path $rootOut 'analysis-execution.json')
    if($analysisCleanup.Count){throw ($analysisCleanup -join '; ')}
}
