$ErrorActionPreference='Stop'
. "$PSScriptRoot/suite-discovery-contract.ps1"
$assembly=[IO.Path]::GetFullPath('Crap4CSharp.Tests.dll')
$baseline=@([pscustomobject]@{Assembly=$assembly;ID=('1'*64);DisplayName='Class.Case(first: 1)';Class='Class';Method='Case'},
    [pscustomobject]@{Assembly=$assembly;ID=('2'*64);DisplayName='Class.Case(first: 2)';Class='Class';Method='Case'})
$controls=@('positive','duplicate-id','duplicate-name','wrong-assembly','missing-class','numeric-id','extra-field','empty','invalid-id')
foreach($control in $controls){
    $cases=@($baseline|ConvertTo-Json|ConvertFrom-Json)
    switch($control){
        duplicate-id {$cases[1].ID=$cases[0].ID}
        duplicate-name {$cases[1].DisplayName=$cases[0].DisplayName}
        wrong-assembly {$cases[1].Assembly=$assembly+'.other'}
        missing-class {$cases[1].Class=$null}
        numeric-id {$cases[1].ID=1}
        extra-field {$cases[1]|Add-Member privateDetail 'canary'}
        empty {$cases=@()}
        invalid-id {$cases[1].ID='not-an-actual-xunit-id'}
    }
    $accepted=$true;try{$normal=@(Convert-SuiteDiscoveredCases $cases $assembly)}catch{$accepted=$false}
    if($accepted -ne ($control -ceq 'positive')){throw "Unexpected discovery normalization: $control"}
    if($accepted -and ($normal[0].ID -cne $baseline[0].ID -or $normal[1].ID -cne $baseline[1].ID)){throw 'Actual IDs rewritten.'}
}
$reverse=@($baseline);[Array]::Reverse($reverse)
if((@(Convert-SuiteDiscoveredCases $reverse $assembly)|ConvertTo-Json -Compress) -cne
    (@(Convert-SuiteDiscoveredCases $baseline $assembly)|ConvertTo-Json -Compress)){throw 'Discovery depends on order.'}
Write-Output 'Discovery normalization controls passed (10); actual IDs retained; synthetic schema controls only.'
$json=$baseline|ConvertTo-Json -Compress
$notice='Waiting 10 seconds for foreground threads to exit...'
$samples=@(
    @{name='plain';text=$json;accepted=$true},
    @{name='known-lf';text=$json+"`n"+$notice+"`n";accepted=$true},
    @{name='known-crlf';text=$json+"`r`n"+$notice+"`r`n";accepted=$true},
    @{name='unknown';text=$json+"`nWarning";accepted=$false},
    @{name='duplicate-notice';text=$json+"`n"+$notice+"`n"+$notice;accepted=$false},
    @{name='notice-prefix';text=$notice+"`n"+$json;accepted=$false},
    @{name='multiple-json';text=$json+"`n[]";accepted=$false},
    @{name='truncated';text=$json.Substring(0,$json.Length-1)+"`n"+$notice;accepted=$false},
    @{name='non-array';text='{}';accepted=$false},
    @{name='missing-linebreak';text=$json+$notice;accepted=$false}
)
foreach($sample in $samples){
    $accepted=$true
    try{$parsed=@(Convert-SuiteDiscoveryOutput $sample.text);$normalized=@(Convert-SuiteDiscoveredCases $parsed $assembly)}catch{$accepted=$false}
    if($accepted -ne $sample.accepted){throw "Unexpected discovery output decision: $($sample.name)"}
    if($accepted -and ($normalized|ConvertTo-Json -Compress) -cne (@(Convert-SuiteDiscoveredCases $baseline $assembly)|ConvertTo-Json -Compress)){throw 'Shutdown notice changed case identities.'}
}
Write-Output 'Discovery output controls passed (10); only one exact known shutdown notice accepted.'
