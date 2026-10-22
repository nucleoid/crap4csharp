param([string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path ([IO.Path]::GetTempPath()) ('crap4csharp-diagnostic-control-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $ResultsDirectory) { throw 'Preserve existing diagnostic controls.' }
[void][IO.Directory]::CreateDirectory($ResultsDirectory)
$testRoot = Join-Path $ResultsDirectory 'repo/tests/Crap4CSharp.Tests'
[void][IO.Directory]::CreateDirectory($testRoot)
"one`ntwo`nthree`nfour" | Set-Content (Join-Path $testRoot 'OwnedFixture.cs')
$cases = @(
    [ordered]@{ ID='one'; DisplayName='Owned.Private.Case(argument: SECRET_ARGUMENT)'; Class='Owned.Private'; Method='Case' },
    [ordered]@{ ID='two'; DisplayName='Owned.Private.Second'; Class='Owned.Private'; Method='Second' }
)
$discovery = Join-Path $ResultsDirectory 'discovery.json'
$cases | ConvertTo-Json | Set-Content $discovery
$doc = [Xml.XmlDocument]::new()
$run = $doc.CreateElement('TestRun'); [void]$doc.AppendChild($run)
$results = $doc.CreateElement('Results'); [void]$run.AppendChild($results)
foreach ($case in $cases) {
    $row = $doc.CreateElement('UnitTestResult'); $row.SetAttribute('testName',$case.DisplayName)
    $row.SetAttribute('outcome',$(if ($case.ID -eq 'one') {'Failed'} else {'Passed'})); [void]$results.AppendChild($row)
    if ($case.ID -eq 'one') {
        $output=$doc.CreateElement('Output'); $errorNode=$doc.CreateElement('ErrorInfo')
        $message=$doc.CreateElement('Message'); $message.InnerText='PRIVATE_MESSAGE C:\private\Consumer.cs run.timedOut'
        $stack=$doc.CreateElement('StackTrace'); $stack.InnerText=@'
PRIVATE_STACK at consumer in C:\private\Consumer.cs:line 2
 at test in C:\owned\tests\Crap4CSharp.Tests\OwnedFixture.cs:line 2
 at test in /owned/tests/Crap4CSharp.Tests/OwnedFixture.cs:line 3
 at test in /owned/tests/Crap4CSharp.Tests/OwnedFixture.cs:line 0
 at test in /owned/tests/Crap4CSharp.Tests/OwnedFixture.cs:line 999999
'@
        [void]$errorNode.AppendChild($message); [void]$errorNode.AppendChild($stack)
        [void]$output.AppendChild($errorNode); [void]$row.AppendChild($output)
    }
}
$trx = Join-Path $ResultsDirectory 'control.trx'
function Observe([string]$Label) {
    $path=Join-Path $ResultsDirectory "$Label.json"
    & "$PSScriptRoot/write-suite-partition-diagnostic.ps1" -DiscoveryPath $discovery -ShardCount 1 -ShardIndex 0 -TrxPath $trx -ExitCode 1 -OutputPath $path -RepositoryRoot (Join-Path $ResultsDirectory 'repo')
    $raw=Get-Content -LiteralPath $path -Raw
    foreach ($secret in @('PRIVATE_MESSAGE','PRIVATE_STACK','Consumer.cs','C:\private','SECRET_ARGUMENT','OwnedFixture.cs','Owned.Private','999999','recipe.PRIVATE_UNKNOWN_OPTION')) {
        if ($raw.Contains($secret)) { throw 'Private diagnostic details leaked.' }
    }
    return ($raw | ConvertFrom-Json)
}
$doc.Save($trx); $value=Observe 'complete'
if ($value.outcome -cne 'observed' -or $value.counts.Passed -ne 1 -or $value.counts.Failed -ne 1 -or $value.missingCases -ne 0) { throw 'Complete count mismatch.' }
$failure=$value.failures[0]
if (@($failure.observedDiagnosticTokens).Count -ne 1 -or $failure.observedDiagnosticTokens[0] -cne 'run.timedOut' -or @($failure.observedRepositoryTestSites).Count -ne 2) { throw 'Diagnostic observations missing.' }
if (@(Compare-Object @(2,3) @($failure.observedRepositoryTestSites.line)).Count) { throw 'Source line bounds or separator handling failed.' }
$expectedDigest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('tests/Crap4CSharp.Tests/OwnedFixture.cs'))).ToLowerInvariant()
foreach ($site in $failure.observedRepositoryTestSites) { if ($site.sourceDigest -cne $expectedDigest) { throw 'Source digest mismatch.' } }
# Fixed optional-recipe tokens survive; unknown/private values still cannot leave diagnostics.
$message.InnerText+=' recipe.bindingUnsupported recipe.compilerCommandPrefixUnproven diagnostic.compilerBinding.unproven diagnostic.compilerRoute.selectedSdkUnixApphost recipe.PRIVATE_UNKNOWN_OPTION'
$doc.Save($trx); $recipeValue=Observe 'recipe-binding-fixed-tokens'
$expectedTokens=@('run.timedOut','recipe.bindingUnsupported','recipe.compilerCommandPrefixUnproven','diagnostic.compilerBinding.unproven','diagnostic.compilerRoute.selectedSdkUnixApphost')
if (@(Compare-Object $expectedTokens @($recipeValue.failures[0].observedDiagnosticTokens)).Count) { throw 'Closed recipe route observations missing.' }
# Each new closed route token is independent; unknown/private values remain excluded.
$originalMessage=$message.InnerText
$routeTokens=@('diagnostic.compilerRoute.hostExecSelectedCscOtherHost','diagnostic.compilerRoute.hostExecOtherCsc','diagnostic.compilerRoute.notSelectedCscLauncher','diagnostic.compilerRoute.lateEvent','diagnostic.compilerRoute.notTransported')
for($routeIndex=0;$routeIndex -lt $routeTokens.Count;$routeIndex++){
    $message.InnerText='PRIVATE_MESSAGE recipe.PRIVATE_UNKNOWN_OPTION '+$routeTokens[$routeIndex]
    $doc.Save($trx);$routeValue=Observe ('closed-route-'+$routeIndex)
    $observedTokens=@($routeValue.failures[0].observedDiagnosticTokens)
    if($observedTokens.Count -ne 1 -or $observedTokens[0] -cne $routeTokens[$routeIndex]){throw 'Closed route distinction missing or ambiguous.'}
}
$message.InnerText=$originalMessage
$originalStack=$stack.InnerText
$stack.InnerText=(1..12 | ForEach-Object {' at test in /owned/tests/Crap4CSharp.Tests/OwnedFixture.cs:line 2'}) -join "`n"
$doc.Save($trx)
if (@((Observe 'bounded-sites').failures[0].observedRepositoryTestSites).Count -ne 8) { throw 'Source-site count bound failed.' }
$stack.InnerText=$originalStack
$unknown=$results.FirstChild.CloneNode($true); $unknown.SetAttribute('testName','SECRET_ARGUMENT'); [void]$results.AppendChild($unknown); $doc.Save($trx)
if ((Observe 'unexpected').outcome -cne 'unavailable') { throw 'Unexpected case accepted.' }
[void]$results.RemoveChild($unknown)
$duplicate=$results.FirstChild.CloneNode($true); [void]$results.AppendChild($duplicate); $doc.Save($trx)
if ((Observe 'duplicate').outcome -cne 'unavailable') { throw 'Duplicate case accepted.' }
[void]$results.RemoveChild($duplicate); [void]$results.RemoveChild($results.LastChild); $doc.Save($trx)
$partial=Observe 'partial'
if ($partial.outcome -cne 'observed' -or $partial.observedCases -ne 1 -or $partial.missingCases -ne 1) { throw 'Partial observation lost.' }
'<!DOCTYPE TestRun [<!ENTITY private SYSTEM "file:///private/credentials">]><TestRun><Results>&private;</Results></TestRun>' | Set-Content $trx
if ((Observe 'dtd').outcome -cne 'unavailable') { throw 'DTD accepted.' }
[ordered]@{privacy=$true;siteBounds=$true;siteCountBound=$true;pathSeparators=$true;unexpectedRefused=$true;duplicatesRefused=$true;partialExplicit=$true;dtdRefused=$true;qualification='Synthetic diagnostic parser/privacy controls only; no suite acceptance.'} | ConvertTo-Json | Set-Content (Join-Path $ResultsDirectory 'terminal.json')
'Suite diagnostic privacy and parser controls passed.'
