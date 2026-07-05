<#
.SYNOPSIS
Creates the fixture project used by integration tests (M0).

.DESCRIPTION
Creates a project named "gpm-fixture" in the test org with every custom field type
and a few draft items. Views and Workflows cannot be created via API — the script
prints the manual one-time steps at the end.

Requires: gh CLI authenticated with an SSO-authorized token for the org.

.EXAMPLE
./scripts/setup-fixture.ps1 -Org gpm-source
#>
[CmdletBinding()]
param(
    [string]$Org = 'gpm-source',
    [string]$Title = 'gpm-fixture'
)

$ErrorActionPreference = 'Stop'

function Invoke-GraphQL([string]$Query, [hashtable]$Variables = @{}) {
    $args = @('api', 'graphql', '-f', "query=$Query")
    foreach ($key in $Variables.Keys) {
        $args += @('-f', "$key=$($Variables[$key])")
    }
    $json = gh @args
    if ($LASTEXITCODE -ne 0) { throw "GraphQL call failed: $json" }
    $json | ConvertFrom-Json
}

# --- Org & existing project lookup -------------------------------------------
$orgResult = Invoke-GraphQL 'query($login: String!) { organization(login: $login) { id projectsV2(first: 100) { nodes { id title number } } } }' @{ login = $Org }
$orgId = $orgResult.data.organization.id
$existing = $orgResult.data.organization.projectsV2.nodes | Where-Object { $_.title -eq $Title }

if ($existing) {
    Write-Host "Fixture project already exists: https://github.com/orgs/$Org/projects/$($existing.number)" -ForegroundColor Yellow
    return
}

# --- Project ------------------------------------------------------------------
$project = (Invoke-GraphQL 'mutation($ownerId: ID!, $title: String!) { createProjectV2(input: {ownerId: $ownerId, title: $title}) { projectV2 { id number } } }' @{ ownerId = $orgId; title = $Title }).data.createProjectV2.projectV2
Write-Host "Created project #$($project.number) ($($project.id))"

# --- Fields (one of each custom type) ----------------------------------------
$textField = 'mutation($projectId: ID!) { createProjectV2Field(input: {projectId: $projectId, dataType: TEXT, name: "Fixture Text"}) { projectV2Field { ... on ProjectV2Field { id } } } }'
$numberField = 'mutation($projectId: ID!) { createProjectV2Field(input: {projectId: $projectId, dataType: NUMBER, name: "Fixture Number"}) { projectV2Field { ... on ProjectV2Field { id } } } }'
$dateField = 'mutation($projectId: ID!) { createProjectV2Field(input: {projectId: $projectId, dataType: DATE, name: "Fixture Date"}) { projectV2Field { ... on ProjectV2Field { id } } } }'
$selectField = 'mutation($projectId: ID!) { createProjectV2Field(input: {projectId: $projectId, dataType: SINGLE_SELECT, name: "Fixture Select", singleSelectOptions: [{name: "Alpha", color: RED, description: "First"}, {name: "Beta", color: BLUE, description: "Second"}, {name: "Gamma", color: GREEN, description: "Third"}]}) { projectV2Field { ... on ProjectV2SingleSelectField { id } } } }'
$iterationField = 'mutation($projectId: ID!) { createProjectV2Field(input: {projectId: $projectId, dataType: ITERATION, name: "Fixture Sprint", iterationConfiguration: {duration: 14, startDate: "2026-07-06", iterations: [{title: "Sprint 1", startDate: "2026-07-06", duration: 14}, {title: "Sprint 2", startDate: "2026-07-20", duration: 14}, {title: "Sprint 3", startDate: "2026-08-03", duration: 14}]}}) { projectV2Field { ... on ProjectV2IterationField { id } } } }'

foreach ($mutation in @($textField, $numberField, $dateField, $selectField, $iterationField)) {
    Invoke-GraphQL $mutation @{ projectId = $project.id } | Out-Null
}
Write-Host 'Created fields: Text / Number / Date / Single-select (3 options) / Iteration (14d)'

# --- Draft items ---------------------------------------------------------------
foreach ($i in 1..3) {
    Invoke-GraphQL 'mutation($projectId: ID!, $title: String!) { addProjectV2DraftIssue(input: {projectId: $projectId, title: $title}) { projectItem { id } } }' @{ projectId = $project.id; title = "Fixture draft $i" } | Out-Null
}
Write-Host 'Added 3 draft items'

# --- Manual steps (no API available) ------------------------------------------
$url = "https://github.com/orgs/$Org/projects/$($project.number)"
Write-Host ''
Write-Host '=== Manual one-time steps (Views/Workflows have no write API) ===' -ForegroundColor Cyan
Write-Host "1. Open $url"
Write-Host '2. Add views: Table (default), Board (group by Status), Roadmap (date field: Fixture Date)'
Write-Host '3. Workflows: enable "Item added to project" (set Status: Todo) and one "Auto-add to project"'
Write-Host "4. Record the project number in CI if needed: GPM_FIXTURE_PROJECT=$($project.number)"
