. "$PSScriptRoot/suite-receipt-contract.ps1"
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
