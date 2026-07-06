<#
.SYNOPSIS
Creates the fixture project used by integration tests (M0, enriched 2026-07-06).

.DESCRIPTION
Creates a project named "gpm-fixture" in the test org with:
- every custom field type, including an iteration field with one past ("Sprint 0",
  classified into completedIterations) and three future/current iterations
- three draft items carrying Text (non-ASCII), Number (3.14 / -42 / 0), Date,
  Single-select (Alpha/Beta/Gamma) and Iteration (Sprint 0/1/2) values
- an Issue item and an open Pull Request item from the fixture repository
  (the repository, its issues and the PR are created when missing)
- an archived draft item and a draft item assigned to the current viewer
- project metadata (short description + multiline README with emoji)
- the fixture repository linked to the project

Views and Workflows cannot be created via API — the script prints the manual
one-time steps at the end. If the project already exists the script skips everything.

Requires: gh CLI authenticated with an SSO-authorized token for the org.

.EXAMPLE
./scripts/setup-fixture.ps1 -Org gpm-source
#>
[CmdletBinding()]
param(
    [string]$Org = 'gpm-source',
    [string]$Title = 'gpm-fixture',
    [string]$RepoName = 'fixture-repo'
)

$ErrorActionPreference = 'Stop'

function Invoke-GraphQL([string]$Query, [hashtable]$Variables = @{}) {
    # Pass the payload as a file so non-ASCII values survive on every console encoding.
    $payload = @{ query = $Query; variables = $Variables } | ConvertTo-Json -Depth 10 -Compress
    $tmp = New-TemporaryFile
    try {
        [System.IO.File]::WriteAllText($tmp.FullName, $payload, [System.Text.UTF8Encoding]::new($false))
        $json = gh api graphql --input $tmp.FullName
        if ($LASTEXITCODE -ne 0) { throw "GraphQL call failed: $json" }
        $json | ConvertFrom-Json
    }
    finally {
        Remove-Item $tmp.FullName -ErrorAction SilentlyContinue
    }
}

# --- Org & existing project lookup -------------------------------------------
$orgResult = Invoke-GraphQL 'query($login: String!) { organization(login: $login) { id projectsV2(first: 100) { nodes { id title number } } } }' @{ login = $Org }
$orgId = $orgResult.data.organization.id
$existing = $orgResult.data.organization.projectsV2.nodes | Where-Object { $_.title -eq $Title }

if ($existing) {
    Write-Host "Fixture project already exists: https://github.com/orgs/$Org/projects/$($existing.number)" -ForegroundColor Yellow
    return
}

$viewerId = (Invoke-GraphQL 'query { viewer { id } }').data.viewer.id

# --- Fixture repository (issues + open PR) ------------------------------------
$repoFullName = "$Org/$RepoName"
gh api "/repos/$repoFullName" *> $null
if ($LASTEXITCODE -ne 0) {
    gh api -X POST "/orgs/$Org/repos" -f name=$RepoName -F private=true | Out-Null
    Write-Host "Created private repository $repoFullName"
}

# Initial commit (contents API bootstraps the default branch on an empty repository).
gh api "/repos/$repoFullName/contents/README.md" *> $null
if ($LASTEXITCODE -ne 0) {
    $readmeContent = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes("# fixture-repo`n`nPermanent fixture repository for gpm integration tests.`n"))
    gh api -X PUT "/repos/$repoFullName/contents/README.md" -f message='Initial commit' -f content=$readmeContent | Out-Null
    Write-Host 'Created initial commit (README.md)'
}

foreach ($i in 1..2) {
    $issueCount = gh api "/repos/$repoFullName/issues?state=all" --jq 'length'
    if ([int]$issueCount -lt $i) {
        gh api -X POST "/repos/$repoFullName/issues" -f title="Fixture issue $i" -f body="Permanent fixture issue $i." | Out-Null
        Write-Host "Created Fixture issue $i"
    }
}

$prNumber = gh api "/repos/$repoFullName/pulls?state=open" --jq '.[0].number'
if (-not $prNumber) {
    $defaultBranch = gh api "/repos/$repoFullName" --jq '.default_branch'
    $baseSha = gh api "/repos/$repoFullName/git/ref/heads/$defaultBranch" --jq '.object.sha'
    gh api -X POST "/repos/$repoFullName/git/refs" -f ref='refs/heads/fixture-pr-branch' -f sha=$baseSha | Out-Null
    $content = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes("fixture PR file`n"))
    gh api -X PUT "/repos/$repoFullName/contents/fixture-pr.txt" -f message='Add fixture PR file' -f content=$content -f branch=fixture-pr-branch | Out-Null
    $prNumber = gh api -X POST "/repos/$repoFullName/pulls" -f title='Fixture pull request' -f body='Permanent fixture PR (kept open for gpm integration tests).' -f head=fixture-pr-branch -f base=$defaultBranch --jq '.number'
    Write-Host "Created Fixture pull request #$prNumber"
}

# --- Project ------------------------------------------------------------------
$project = (Invoke-GraphQL 'mutation($ownerId: ID!, $title: String!) { createProjectV2(input: {ownerId: $ownerId, title: $title}) { projectV2 { id number } } }' @{ ownerId = $orgId; title = $Title }).data.createProjectV2.projectV2
Write-Host "Created project #$($project.number) ($($project.id))"

# --- Metadata (short description + multiline README with emoji) ---------------
$readme = "# gpm fixture 📦`n`nPermanent fixture project for gpm integration tests.`n`n- All custom field types (Text / Number / Date / Single-select / Iteration)`n- Drafts with 日本語 values, an Issue, a PR, an archived item and an assigned item`n- 3 views + 7 workflows (migrated by the browser module) 🚀"
Invoke-GraphQL 'mutation($projectId: ID!, $shortDescription: String!, $readme: String!) { updateProjectV2(input: {projectId: $projectId, shortDescription: $shortDescription, readme: $readme}) { projectV2 { id } } }' @{ projectId = $project.id; shortDescription = 'gpm fixture project'; readme = $readme } | Out-Null
Write-Host 'Set short description and README'

# --- Fields (one of each custom type) ----------------------------------------
# Sprint 0 starts 4 weeks ago (always classified into completedIterations);
# Sprint 1 starts today, Sprint 2/3 follow at 14-day intervals.
$sprint0Start = (Get-Date).AddDays(-28).ToString('yyyy-MM-dd')
$sprint1Start = (Get-Date).ToString('yyyy-MM-dd')
$sprint2Start = (Get-Date).AddDays(14).ToString('yyyy-MM-dd')
$sprint3Start = (Get-Date).AddDays(28).ToString('yyyy-MM-dd')

$textField = 'mutation($projectId: ID!) { createProjectV2Field(input: {projectId: $projectId, dataType: TEXT, name: "Fixture Text"}) { projectV2Field { ... on ProjectV2Field { id } } } }'
$numberField = 'mutation($projectId: ID!) { createProjectV2Field(input: {projectId: $projectId, dataType: NUMBER, name: "Fixture Number"}) { projectV2Field { ... on ProjectV2Field { id } } } }'
$dateField = 'mutation($projectId: ID!) { createProjectV2Field(input: {projectId: $projectId, dataType: DATE, name: "Fixture Date"}) { projectV2Field { ... on ProjectV2Field { id } } } }'
$selectField = 'mutation($projectId: ID!) { createProjectV2Field(input: {projectId: $projectId, dataType: SINGLE_SELECT, name: "Fixture Select", singleSelectOptions: [{name: "Alpha", color: RED, description: "First"}, {name: "Beta", color: BLUE, description: "Second"}, {name: "Gamma", color: GREEN, description: "Third"}]}) { projectV2Field { ... on ProjectV2SingleSelectField { id options { id name } } } } }'
$iterationField = 'mutation($projectId: ID!, $startDate: Date!, $iterations: [ProjectV2IterationFieldIterationInput!]!) { createProjectV2Field(input: {projectId: $projectId, dataType: ITERATION, name: "Fixture Sprint", iterationConfiguration: {duration: 14, startDate: $startDate, iterations: $iterations}}) { projectV2Field { ... on ProjectV2IterationField { id configuration { iterations { id title } completedIterations { id title } } } } } }'

$textFieldId = (Invoke-GraphQL $textField @{ projectId = $project.id }).data.createProjectV2Field.projectV2Field.id
$numberFieldId = (Invoke-GraphQL $numberField @{ projectId = $project.id }).data.createProjectV2Field.projectV2Field.id
$dateFieldId = (Invoke-GraphQL $dateField @{ projectId = $project.id }).data.createProjectV2Field.projectV2Field.id
$selectResult = (Invoke-GraphQL $selectField @{ projectId = $project.id }).data.createProjectV2Field.projectV2Field
$iterations = @(
    @{ title = 'Sprint 0'; startDate = $sprint0Start; duration = 14 },
    @{ title = 'Sprint 1'; startDate = $sprint1Start; duration = 14 },
    @{ title = 'Sprint 2'; startDate = $sprint2Start; duration = 14 },
    @{ title = 'Sprint 3'; startDate = $sprint3Start; duration = 14 }
)
$sprintResult = (Invoke-GraphQL $iterationField @{ projectId = $project.id; startDate = $sprint0Start; iterations = $iterations }).data.createProjectV2Field.projectV2Field
Write-Host 'Created fields: Text / Number / Date / Single-select (3 options) / Iteration (Sprint 0 past + Sprint 1-3)'

$selectOptions = @{}
foreach ($option in $selectResult.options) { $selectOptions[$option.name] = $option.id }
$sprintIds = @{}
foreach ($iteration in ($sprintResult.configuration.iterations + $sprintResult.configuration.completedIterations)) { $sprintIds[$iteration.title] = $iteration.id }

# --- Draft items with field values ---------------------------------------------
$setValue = 'mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $value: ProjectV2FieldValue!) { updateProjectV2ItemFieldValue(input: {projectId: $projectId, itemId: $itemId, fieldId: $fieldId, value: $value}) { projectV2Item { id } } }'
$addDraft = 'mutation($projectId: ID!, $title: String!) { addProjectV2DraftIssue(input: {projectId: $projectId, title: $title}) { projectItem { id } } }'

$draftValues = @(
    @{ Text = '日本語テキスト & <special> chars'; Number = 3.14; Date = (Get-Date).AddDays(-21).ToString('yyyy-MM-dd'); Select = 'Alpha'; Sprint = 'Sprint 0' },
    @{ Text = 'Café emoji 🚀 – em dash'; Number = -42; Date = (Get-Date).AddDays(4).ToString('yyyy-MM-dd'); Select = 'Beta'; Sprint = 'Sprint 1' },
    @{ Text = 'plain ascii text'; Number = 0; Date = (Get-Date).AddDays(26).ToString('yyyy-MM-dd'); Select = 'Gamma'; Sprint = 'Sprint 2' }
)

for ($i = 0; $i -lt 3; $i++) {
    $itemId = (Invoke-GraphQL $addDraft @{ projectId = $project.id; title = "Fixture draft $($i + 1)" }).data.addProjectV2DraftIssue.projectItem.id
    $values = $draftValues[$i]
    Invoke-GraphQL $setValue @{ projectId = $project.id; itemId = $itemId; fieldId = $textFieldId; value = @{ text = $values.Text } } | Out-Null
    Invoke-GraphQL $setValue @{ projectId = $project.id; itemId = $itemId; fieldId = $numberFieldId; value = @{ number = $values.Number } } | Out-Null
    Invoke-GraphQL $setValue @{ projectId = $project.id; itemId = $itemId; fieldId = $dateFieldId; value = @{ date = $values.Date } } | Out-Null
    Invoke-GraphQL $setValue @{ projectId = $project.id; itemId = $itemId; fieldId = $selectResult.id; value = @{ singleSelectOptionId = $selectOptions[$values.Select] } } | Out-Null
    Invoke-GraphQL $setValue @{ projectId = $project.id; itemId = $itemId; fieldId = $sprintResult.id; value = @{ iterationId = $sprintIds[$values.Sprint] } } | Out-Null
}
Write-Host 'Added 3 draft items with Text/Number/Date/Select/Sprint values'

# --- Issue and PR items ---------------------------------------------------------
$addItem = 'mutation($projectId: ID!, $contentId: ID!) { addProjectV2ItemById(input: {projectId: $projectId, contentId: $contentId}) { item { id } } }'
$issueId = (Invoke-GraphQL 'query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) { issue(number: 1) { id } } }' @{ owner = $Org; name = $RepoName }).data.repository.issue.id
Invoke-GraphQL $addItem @{ projectId = $project.id; contentId = $issueId } | Out-Null
$prId = (Invoke-GraphQL 'query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { pullRequest(number: $number) { id } } }' @{ owner = $Org; name = $RepoName; number = [int]$prNumber }).data.repository.pullRequest.id
Invoke-GraphQL $addItem @{ projectId = $project.id; contentId = $prId } | Out-Null
Write-Host "Added Issue #1 and PR #$prNumber items"

# --- Archived draft --------------------------------------------------------------
$archivedItemId = (Invoke-GraphQL $addDraft @{ projectId = $project.id; title = 'Fixture archived draft' }).data.addProjectV2DraftIssue.projectItem.id
Invoke-GraphQL 'mutation($projectId: ID!, $itemId: ID!) { archiveProjectV2Item(input: {projectId: $projectId, itemId: $itemId}) { item { id } } }' @{ projectId = $project.id; itemId = $archivedItemId } | Out-Null
Write-Host "Added and archived 'Fixture archived draft'"

# --- Assigned draft --------------------------------------------------------------
Invoke-GraphQL 'mutation($projectId: ID!, $title: String!, $assigneeIds: [ID!]) { addProjectV2DraftIssue(input: {projectId: $projectId, title: $title, assigneeIds: $assigneeIds}) { projectItem { id } } }' @{ projectId = $project.id; title = 'Fixture assigned draft'; assigneeIds = @($viewerId) } | Out-Null
Write-Host "Added 'Fixture assigned draft' assigned to the viewer"

# --- Link the fixture repository -------------------------------------------------
$repoNodeId = gh api "/repos/$repoFullName" --jq '.node_id'
Invoke-GraphQL 'mutation($projectId: ID!, $repositoryId: ID!) { linkProjectV2ToRepository(input: {projectId: $projectId, repositoryId: $repositoryId}) { repository { nameWithOwner } } }' @{ projectId = $project.id; repositoryId = $repoNodeId } | Out-Null
Write-Host "Linked $repoFullName to the project"

# --- Manual steps (no API available) ------------------------------------------
$url = "https://github.com/orgs/$Org/projects/$($project.number)"
Write-Host ''
Write-Host '=== Manual one-time steps (Views/Workflows have no write API) ===' -ForegroundColor Cyan
Write-Host "1. Open $url"
Write-Host '2. Add views: Table (default), Board (group by Status), Roadmap (date field: Fixture Date)'
Write-Host '3. Workflows: enable "Item added to project" (set Status: Todo) and one "Auto-add to project"'
Write-Host "4. Record the project number in CI if needed: GPM_FIXTURE_PROJECT=$($project.number)"
