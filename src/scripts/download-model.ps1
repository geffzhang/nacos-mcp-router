$ErrorActionPreference = 'Stop'

$Dest = if ($env:EMBEDDING_MODEL_DIR) { $env:EMBEDDING_MODEL_DIR } else { 'models/all-MiniLM-L6-v2' }
# Default to ModelScope (国内可达); HF_ENDPOINT 可覆盖 (e.g. https://hf-mirror.com 或 https://huggingface.co)
$Endpoint = if ($env:HF_ENDPOINT) { $env:HF_ENDPOINT } else { 'https://www.modelscope.cn' }
$Repo = if ($env:HF_REPO) { $env:HF_REPO } else { 'sentence-transformers/all-MiniLM-L6-v2' }
# ModelScope 用 master 分支，HF 用 main
$Branch = if ($env:HF_BRANCH) { $env:HF_BRANCH } elseif ($Endpoint -like '*modelscope.cn*') { 'master' } else { 'main' }

New-Item -ItemType Directory -Force -Path $Dest | Out-Null

# model.onnx 在 onnx/ 子目录，其它文件平铺
$files = @{
    'model.onnx'    = 'onnx/model.onnx'
    'tokenizer.json' = 'tokenizer.json'
    'vocab.txt'     = 'vocab.txt'
    'config.json'   = 'config.json'
}

foreach ($name in $files.Keys) {
    $path = $files[$name]
    $target = Join-Path $Dest $name
    if (Test-Path $target) {
        Write-Host "[skip] $target already exists"
        continue
    }
    $url = "$Endpoint/$Repo/resolve/$Branch/$path"
    Write-Host "[fetch] $url -> $target"
    Invoke-WebRequest -Uri $url -OutFile $target
}

Write-Host "Done. Model files in $Dest"