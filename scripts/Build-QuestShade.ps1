param([string]$Tag = "v1.7.41")

$ErrorActionPreference = "Continue"

$RefsRepo = "MonkeOnYT-glitch/QuestShade-refs"
$MainRepo = "MonkeOnYT-glitch/QuestShade"
$MlDir = "C:\QSBuild\quest\ml"
$IaDir = "C:\QSBuild\quest\ia"
$BoneLibPaths = @(
    "C:\Program Files (x86)\Steam\steamapps\common\BONELAB\Mods\BoneLib.dll",
    "C:\QSBuild\game\Mods\BoneLib.dll",
    "C:\QSBuild\quest\game\Mods\BoneLib.dll"
)
$WorkDir = Join-Path $env:TEMP "QuestShade-refs"

function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail "gh (GitHub CLI) is not installed. Install it from https://cli.github.com/ then run gh auth login." }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail "git is not installed." }
$null = gh auth status 2>$null
if ($LASTEXITCODE -ne 0) { Fail "gh is not logged in. Run gh auth login first." }
if (-not (Test-Path $MlDir)) { Fail "Could not find $MlDir" }
if (-not (Test-Path $IaDir)) { Fail "Could not find $IaDir" }
$BoneLib = $null
foreach ($p in $BoneLibPaths) { if (Test-Path $p) { $BoneLib = $p; break } }
if (-not $BoneLib) { Fail "Could not find BoneLib.dll. Looked in: $($BoneLibPaths -join ', ')" }

Write-Host "Setting up private refs repo $RefsRepo ..."
$repoNames = gh repo list MonkeOnYT-glitch --limit 100 --json name --jq ".[].name"
if ($repoNames -notcontains "QuestShade-refs") {
    Write-Host "Creating private repo $RefsRepo ..."
    gh repo create $RefsRepo --private --description "Private build references for QuestShade"
    if ($LASTEXITCODE -ne 0) { Fail "Could not create $RefsRepo" }
}
if (Test-Path (Join-Path $WorkDir ".git")) {
    git -C $WorkDir pull --ff-only
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Local refs copy is stale, re-cloning ..."
        Remove-Item $WorkDir -Recurse -Force -ErrorAction Stop
        gh repo clone $RefsRepo $WorkDir
    }
} else {
    if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force -ErrorAction Stop }
    gh repo clone $RefsRepo $WorkDir
}
if ($LASTEXITCODE -ne 0) { Fail "Could not clone $RefsRepo" }

Write-Host "Copying MelonLoader + game assemblies ..."
$destMl = Join-Path $WorkDir "ml"
$destIa = Join-Path $WorkDir "ia"
$destGame = Join-Path $WorkDir "game\Mods"
foreach ($d in @($destMl, $destIa, $destGame)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction Stop }
    New-Item -ItemType Directory -Force -Path $d -ErrorAction Stop | Out-Null
}
Copy-Item "$MlDir\*" $destMl -Recurse -Force -ErrorAction Stop
Copy-Item "$IaDir\*" $destIa -Recurse -Force -ErrorAction Stop
Copy-Item $BoneLib $destGame -Force -ErrorAction Stop

git -C $WorkDir add -A
if ($LASTEXITCODE -ne 0) { Fail "Could not stage the refs." }
$changes = git -C $WorkDir status --porcelain
if ($changes) {
    git -C $WorkDir commit -m "Update build references"
    if ($LASTEXITCODE -ne 0) { Fail "Could not commit the refs." }
    git -C $WorkDir push origin main
    if ($LASTEXITCODE -ne 0) { Fail "Could not push the refs repo." }
    Write-Host "Refs pushed."
} else {
    Write-Host "Refs already up to date."
}

Write-Host "Saving your GitHub login token as secret QS_REFS_PAT ..."
$token = (gh auth token).Trim()
if (-not $token) { Fail "Could not read gh token." }
gh secret set QS_REFS_PAT --repo $MainRepo --body $token
if ($LASTEXITCODE -ne 0) { Fail "Could not save the QS_REFS_PAT secret." }

Write-Host "Starting the build for $Tag ..."
gh workflow run build.yml --repo $MainRepo --field tag=$Tag
if ($LASTEXITCODE -ne 0) { Fail "Could not start the workflow." }
Write-Host "Build started. Waiting for it to finish (this takes a few minutes) ..."
Start-Sleep -Seconds 15
$runId = gh run list --workflow build.yml --repo $MainRepo --branch main --limit 1 --json databaseId --jq ".[0].databaseId"
if (-not $runId) { Fail "Could not find the workflow run. Check https://github.com/$MainRepo/actions" }
gh run watch $runId --repo $MainRepo --exit-status
if ($LASTEXITCODE -ne 0) { Fail "Build failed. See https://github.com/$MainRepo/actions/runs/$runId" }
Write-Host "Build passed. Publishing $Tag ..."
gh release edit $Tag --draft=false --repo $MainRepo
if ($LASTEXITCODE -ne 0) { Fail "Build passed but publishing failed. The DLLs are on the draft release." }
Write-Host "Published: https://github.com/$MainRepo/releases/tag/$Tag" -ForegroundColor Green
