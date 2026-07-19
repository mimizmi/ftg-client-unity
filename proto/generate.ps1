# FTG protobuf codegen（Windows / PowerShell）
#
# 前置：把 protoc 放进 PATH（https://github.com/protocolbuffers/protobuf/releases，
#   下载 protoc-*-win64.zip，解压后 bin\protoc.exe 加入 PATH）。
# 用法：pwsh proto/generate.ps1
#
# C# 生成落到 Assets/Domain/Net/Generated（由 FTG.Net.asmdef 编译）。
# Go 生成落到 server/gen（N4 建 server/ 后自动启用；此前跳过）。

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path   # proto/
$repo = Split-Path -Parent $root                          # 仓库根

$protos = @(
    "ftg/v1/combat.proto",
    "ftg/v1/sync.proto",
    "ftg/v1/definition.proto",
    "ftg/v1/net.proto"
)

# ---- C#（客户端）----
$csharpOut = Join-Path $repo "Assets/Domain/Net/Generated"
New-Item -ItemType Directory -Force -Path $csharpOut | Out-Null

Write-Host "protoc → C#  ($csharpOut)"
& protoc `
    --proto_path="$root" `
    --csharp_out="$csharpOut" `
    --csharp_opt=file_extension=.g.cs `
    $protos
if ($LASTEXITCODE -ne 0) { throw "protoc C# 生成失败（exit $LASTEXITCODE）" }

# ---- Go（服务器/对拍无头模拟；N4 才有 server/）----
$goOut = Join-Path $repo "server/gen"
if (Test-Path (Join-Path $repo "server")) {
    New-Item -ItemType Directory -Force -Path $goOut | Out-Null
    Write-Host "protoc → Go  ($goOut)"
    & protoc `
        --proto_path="$root" `
        --go_out="$goOut" `
        --go_opt=paths=source_relative `
        $protos
    if ($LASTEXITCODE -ne 0) { throw "protoc Go 生成失败（exit $LASTEXITCODE）" }
} else {
    Write-Host "跳过 Go 生成：server/ 尚未建立（N4 引入 Go 无头模拟时再启用）"
}

Write-Host "完成。"
