[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CiWorkflow,
    [string] $ReleaseTag,
    [switch] $RequireTag
)

$ErrorActionPreference = 'Stop'
if ($env:DEFAULT_BRANCH -ne 'main') { throw 'Release source must be the actual default main branch.' }
if ($RequireTag) {
    if ([string]::IsNullOrWhiteSpace($ReleaseTag)) { throw 'Select an existing release tag.' }
    if ($env:GITHUB_EVENT_NAME -notin @('push', 'workflow_dispatch') -or
        $env:GITHUB_REF -cne "refs/tags/$ReleaseTag") {
        throw 'Publication must run from the matching release tag.'
    }
} elseif ($env:GITHUB_EVENT_NAME -ne 'workflow_dispatch' -or $env:GITHUB_REF -ne 'refs/heads/main') {
    throw 'Pair preparation must be dispatched from the actual default main branch.'
}
$head = git rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -ne $env:GITHUB_SHA) { throw 'Checkout does not match the dispatch SHA.' }
git fetch --no-tags origin main
if ($LASTEXITCODE -ne 0) { throw 'Could not read the current main branch.' }
$main = git rev-parse FETCH_HEAD
if ($LASTEXITCODE -ne 0 -or $main -ne $head) { throw 'Main advanced; select and verify the current main commit.' }

if ($ReleaseTag) {
    git check-ref-format "refs/tags/$ReleaseTag"
    if ($LASTEXITCODE -ne 0) { throw 'Invalid release tag.' }
    $tag = git rev-parse --verify "refs/tags/$ReleaseTag^{commit}"
    if ($LASTEXITCODE -ne 0 -or $tag -ne $head) { throw 'Release tag and current main SHA must be identical.' }
    $remoteTag = @(git ls-remote --exit-code origin "refs/tags/$ReleaseTag" "refs/tags/$ReleaseTag^{}")
    if ($LASTEXITCODE -ne 0) { throw 'Release tag is missing from origin.' }
    $peeled = @($remoteTag | Where-Object { $_.EndsWith('^{}') })
    $remoteSha = (($peeled + $remoteTag)[0] -split '\s+')[0]
    if ($remoteSha -ne $head) { throw 'Remote release tag does not match current main.' }
}

$json = gh api --method GET --paginate --slurp "repos/$env:GITHUB_REPOSITORY/actions/workflows/$CiWorkflow/runs" `
    -f branch=main -f "head_sha=$head" -f event=push -f per_page=100
if ($LASTEXITCODE -ne 0) { throw 'Could not read normal main CI results.' }
$pages = ($json -join "`n") | ConvertFrom-Json
$runs = @($pages | ForEach-Object { $_.workflow_runs } | Where-Object {
    $_.head_sha -eq $head -and $_.head_branch -eq 'main' -and $_.event -eq 'push' -and
    $_.path -eq ".github/workflows/$CiWorkflow"
} | Sort-Object id -Descending)
if (!$runs.Count -or $runs[0].status -ne 'completed' -or $runs[0].conclusion -ne 'success') {
    throw 'The latest normal push CI for this exact main SHA must pass before release operations.'
}
Write-Output "Verified main $head; normal CI run $($runs[0].id)."
