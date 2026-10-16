. "$PSScriptRoot/suite-receipt-contract.ps1"
function Convert-SuiteDiscoveryOutput([string]$Text){
    # xUnit v3.2.2 can finish discovery successfully before a foreground thread
    # exits. Its single fixed shutdown notice is the only permitted suffix.
    $json=$Text.TrimEnd()
    $notice='Waiting 10 seconds for foreground threads to exit...'
    if($json.EndsWith("`n"+$notice,[StringComparison]::Ordinal)){
        $json=$json.Substring(0,$json.Length-$notice.Length).TrimEnd()
    }
    $document=[Text.Json.JsonDocument]::Parse($json)
    try{
        if($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Array){throw 'Discovery must contain exactly one JSON array.'}
    }finally{$document.Dispose()}
    @($json|ConvertFrom-Json)
}
function Convert-SuiteDiscoveredCases([object[]]$Cases,[string]$Assembly){
    $ids=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
    foreach($case in $Cases){
        Assert-ReceiptFields $case @('Assembly','ID','DisplayName','Class','Method')
        foreach($field in @('Assembly','ID','DisplayName','Class','Method')){
            if($case.$field -isnot [string] -or [string]::IsNullOrWhiteSpace($case.$field)){throw 'Incomplete discovered case.'}
        }
        if(!$case.Assembly.Equals($Assembly,$comparison) -or $case.ID -cnotmatch '^[0-9a-f]{64}$' -or
            !$names.Add($case.DisplayName) -or $ids.ContainsKey($case.ID)){throw 'Unexpected assembly or ambiguous case identity.'}
        $ids.Add($case.ID,[ordered]@{Assembly=[IO.Path]::GetFileName($Assembly);ID=$case.ID;DisplayName=$case.DisplayName;Class=$case.Class;Method=$case.Method})
    }
    if(!$ids.Count){throw 'Empty discovery refused.'}
    @($ids.Values)
}
