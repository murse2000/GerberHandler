#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$NotesFile,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot "artifacts/$Version"),

    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = 'murse2000/GerberHandler'
$tag = "v$Version"
$notes = (Resolve-Path -LiteralPath $NotesFile).Path
if ([string]::IsNullOrWhiteSpace([IO.File]::ReadAllText($notes))) {
    throw '릴리스 변경 내용을 작성한 뒤 패키지를 만드세요.'
}

if ($Publish) {
    # 공개 릴리스와 설치 파일의 소스가 일치하도록 현재 커밋과 원격 태그를 확인한다.
    $workingChanges = git -C $PSScriptRoot status --porcelain
    if ($LASTEXITCODE -ne 0 -or $workingChanges) {
        throw '공개 릴리스 전 소스 변경을 모두 커밋하세요.'
    }
    $head = git -C $PSScriptRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw '현재 커밋을 읽을 수 없습니다.' }
    $remoteTag = gh api "repos/$repository/commits/$tag" --jq '.sha'
    if ($LASTEXITCODE -ne 0 -or $remoteTag -ne $head) {
        throw "현재 커밋에 해당하는 $tag 태그를 먼저 GitHub에 푸시하세요."
    }
}

$output = [IO.Path]::GetFullPath($OutputDirectory, (Get-Location).ProviderPath)
if (Test-Path -LiteralPath $output) {
    throw "패키지 출력 경로가 이미 존재합니다. 새 경로를 지정하세요. $output"
}
$project = Join-Path $PSScriptRoot 'GerberHandler.csproj'
$toolDirectory = Join-Path $PSScriptRoot 'obj/velopack/1.2.161'
$packager = Join-Path $toolDirectory 'vpk.exe'
if (-not (Test-Path -LiteralPath $packager)) {
    dotnet tool install vpk --tool-path $toolDirectory --version 1.2.161
    if ($LASTEXITCODE -ne 0) { throw 'Velopack 포장 도구를 설치하지 못했습니다.' }
}

$buildDirectory = Join-Path $PSScriptRoot "obj/release-staging/$([Guid]::NewGuid())"
dotnet publish $project -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $buildDirectory
if ($LASTEXITCODE -ne 0) { throw 'Windows 앱 빌드에 실패했습니다.' }

& $packager pack --packId GerberHandler --packVersion $Version --packDir $buildDirectory --mainExe GerberHandler.exe --runtime win-x64 --channel win --releaseNotes $notes --icon (Join-Path $PSScriptRoot 'Assets/GerberHandler.ico') --outputDir $output
if ($LASTEXITCODE -ne 0) { throw '설치 프로그램과 업데이트 패키지를 생성하지 못했습니다.' }

$feed = Get-Item -LiteralPath (Join-Path $output 'releases.win.json')
$installer = @(Get-ChildItem -LiteralPath $output -File -Filter '*-Setup.exe')
$packages = @(Get-ChildItem -LiteralPath $output -File -Filter '*.nupkg')
$feedEntry = @((Get-Content -LiteralPath $feed.FullName -Raw -Encoding utf8 | ConvertFrom-Json).Assets | Where-Object { $_.Version -eq $Version -and $_.Type -eq 'Full' })
if ($installer.Count -ne 1 -or $feedEntry.Count -ne 1 -or $feedEntry[0].FileName -notin $packages.Name) {
    throw '릴리스에 필요한 설치 파일 또는 업데이트 패키지가 누락되었습니다.'
}
if ([string]::IsNullOrWhiteSpace($feedEntry[0].NotesMarkdown)) {
    throw '업데이트 피드에 변경 내용이 누락되었습니다.'
}
$assets = @($installer) + @($packages) + @($feed)
$sums = Join-Path $output 'SHA256SUMS.txt'
$assets | ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" } | Set-Content -LiteralPath $sums -Encoding utf8NoBOM

if ($Publish) {
    $uploadPaths = @($assets.FullName) + @($sums)
    gh release create $tag @uploadPaths --repo $repository --verify-tag --title "GerberHandler $tag" --notes-file $notes
    if ($LASTEXITCODE -ne 0) { throw 'GitHub 릴리스 등록에 실패했습니다.' }
}

Write-Output $output
