# Shared by the Typhon git hooks: the .NET 11 SDK and the CPython that CI uses as the oracle.

# dotnet: the one on PATH when it has a .NET 11 SDK, else ~/.dotnet (a user-local install)
typhon_dotnet() {
    if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^11\.'; then
        echo dotnet
    elif [ -x "$HOME/.dotnet/dotnet" ]; then
        export DOTNET_ROOT="$HOME/.dotnet"
        echo "$HOME/.dotnet/dotnet"
    else
        echo "typhon hooks: no .NET 11 SDK found (PATH or ~/.dotnet)" >&2
        return 1
    fi
}

# python: TYPHON_CPYTHON, the repo's .venv, or python3, as the conformance tests choose it; warns when it is not the
# version CI runs (expected outputs are compared with that version's CPython there)
typhon_python() {
    local py
    if [ -n "${TYPHON_CPYTHON:-}" ]; then py="$TYPHON_CPYTHON"
    elif [ -x .venv/bin/python ]; then py=.venv/bin/python
    elif [ -x .venv/Scripts/python.exe ]; then py=.venv/Scripts/python.exe
    else py=python3
    fi
    local want have
    want=$(sed -n 's/.*python-version: *"\{0,1\}\([0-9.]*\)"\{0,1\}.*/\1/p' .github/workflows/ci.yml | head -1)
    have=$("$py" -c 'import sys; print(f"{sys.version_info[0]}.{sys.version_info[1]}")' 2>/dev/null || echo none)
    if [ -n "$want" ] && [ "$have" != "$want" ]; then
        echo "typhon hooks: warning: CPython $have is used, CI uses $want (uv venv .venv --python $want && uv pip install --python .venv/bin/python numpy)" >&2
    fi
    echo "$py"
}
