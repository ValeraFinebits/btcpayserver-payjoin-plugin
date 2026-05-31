#!/usr/bin/env bash
set -euo pipefail

case "$(uname -s)" in
    Darwin)
        lib_name=libpayjoin_ffi.dylib
        ;;
    Linux)
        lib_name=libpayjoin_ffi.so
        ;;
    MINGW*|MSYS*|CYGWIN*)
        lib_name=payjoin_ffi.dll
        ;;
    *)
        echo "Unsupported OS: $(uname -s)" >&2
        exit 1
        ;;
esac

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
ffi_dir="$repo_root/rust-payjoin/payjoin-ffi"

profile="${PAYJOIN_FFI_PROFILE:-dev}"
payjoin_ffi_features="${PAYJOIN_FFI_FEATURES-_test-utils}"
generator_features="csharp"
if [[ -n "$payjoin_ffi_features" ]]; then
    generator_features="$generator_features,$payjoin_ffi_features"
fi

case "$profile" in
    dev)
        target_profile_dir=debug
        ;;
    release)
        target_profile_dir=release
        ;;
    *)
        target_profile_dir="$profile"
        ;;
esac

cd "$ffi_dir"

echo "Generating C# bindings with profile '$profile' and features '$generator_features'"
cargo build --features "$generator_features" --profile "$profile" -j2

mkdir -p csharp/src
rm -f csharp/src/*.cs

UNIFFI_BINDGEN_LANGUAGE=csharp cargo run --features "$generator_features" --profile "$profile" --bin uniffi-bindgen -- \
    --library "../target/$target_profile_dir/$lib_name" \
    --out-dir csharp/src/

mkdir -p csharp/lib
cp "../target/$target_profile_dir/$lib_name" "csharp/lib/$lib_name"
