# This wrapper does not download, install, register or alter a host profile.
param(
    [ValidateSet('export', 'remove')][string]$Operation = 'export',
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
)
& sharpdeps "agent-$Operation" @Arguments
exit $LASTEXITCODE
