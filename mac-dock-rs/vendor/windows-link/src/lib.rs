#![doc = include_str!("../readme.md")]
#![no_std]
// Patched for the windows-gnu toolchain without a GNU dlltool: DLLs that
// MinGW ships import libraries for are linked normally (long-format import
// libraries), so they never mix with LLVM short-format ones. Everything else
// still uses raw-dylib.

/// Defines an external function to import.
#[cfg(all(windows, target_env = "gnu", not(target_arch = "x86")))]
#[macro_export]
macro_rules! link {
    ("kernel32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "kernel32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("user32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "user32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("ntdll.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "ntdll")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("advapi32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "advapi32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("gdi32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "gdi32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("rpcrt4.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "rpcrt4")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("oleaut32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "oleaut32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("opengl32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "opengl32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("setupapi.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "setupapi")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("ole32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "ole32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("shell32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "shell32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("crypt32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "crypt32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("dbghelp.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "dbghelp")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("iphlpapi.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "iphlpapi")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("odbc32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "odbc32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("winmm.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "winmm")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("ws2_32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "ws2_32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("comctl32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "comctl32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("bcrypt.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "bcrypt")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("userenv.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "userenv")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("psapi.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "psapi")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("secur32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "secur32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("winhttp.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "winhttp")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("comdlg32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "comdlg32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("credui.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "credui")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("imagehlp.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "imagehlp")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("msimg32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "msimg32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ("wsock32.dll" $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = "wsock32")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
    ($library:literal $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = $library, kind = "raw-dylib", modifiers = "+verbatim")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    );
}

/// Defines an external function to import.
#[cfg(all(windows, target_arch = "x86"))]
#[macro_export]
macro_rules! link {
    ($library:literal $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = $library, kind = "raw-dylib", modifiers = "+verbatim", import_name_type = "undecorated")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    )
}

/// Defines an external function to import.
#[cfg(all(windows, not(target_arch = "x86"), not(target_env = "gnu")))]
#[macro_export]
macro_rules! link {
    ($library:literal $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        #[link(name = $library, kind = "raw-dylib", modifiers = "+verbatim")]
        extern $abi {
            $(#[link_name=$link_name])?
            pub fn $($function)*;
        }
    )
}

/// Defines an external function to import.
#[cfg(not(windows))]
#[macro_export]
macro_rules! link {
    ($library:literal $abi:literal $($link_name:literal)? fn $($function:tt)*) => (
        extern $abi {
            pub fn $($function)*;
        }
    )
}
