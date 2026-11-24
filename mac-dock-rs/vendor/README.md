# vendor/

`windows-link/` is windows-link 0.2.1 with a patched `link!` macro (see the
comment at the top of `src/lib.rs`). On the GNU toolchain, DLLs that MinGW
ships import libraries for are linked through those instead of raw-dylib, which
avoids a broken import table when GNU ld mixes the two library formats.
It is wired in through `[patch.crates-io]` in the root `Cargo.toml`.
