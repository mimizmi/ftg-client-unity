#!/usr/bin/env bash
# FTG protobuf codegen（Linux/macOS/Git-Bash；CI 与 Go 侧用这个）
#
# 前置：protoc 在 PATH；Go 生成还需 protoc-gen-go（go install
#   google.golang.org/protobuf/cmd/protoc-gen-go@latest，且 $GOPATH/bin 在 PATH）。
# 用法：bash proto/generate.sh

set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"   # proto/
repo="$(dirname "$root")"                              # 仓库根

protos=(
  "ftg/v1/combat.proto"
  "ftg/v1/sync.proto"
  "ftg/v1/definition.proto"
)

# ---- C#（客户端）----
csharp_out="$repo/Assets/Domain/Net/Generated"
mkdir -p "$csharp_out"
echo "protoc → C#  ($csharp_out)"
protoc \
  --proto_path="$root" \
  --csharp_out="$csharp_out" \
  --csharp_opt=file_extension=.g.cs \
  "${protos[@]}"

# ---- Go（服务器/对拍；N4 才有 server/）----
go_out="$repo/server/gen"
if [ -d "$repo/server" ]; then
  mkdir -p "$go_out"
  echo "protoc → Go  ($go_out)"
  protoc \
    --proto_path="$root" \
    --go_out="$go_out" \
    --go_opt=paths=source_relative \
    "${protos[@]}"
else
  echo "跳过 Go 生成：server/ 尚未建立（N4 引入 Go 无头模拟时再启用）"
fi

echo "完成。"
